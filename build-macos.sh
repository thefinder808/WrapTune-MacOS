#!/usr/bin/env bash
#
# Build a WrapTune MacOS .app and .dmg. Signing + notarization activate only
# when the relevant env vars are set, so this also produces an unsigned build
# for local testing.
#
#   RID              target runtime id  (default osx-arm64; also osx-x64)
#   CONFIG           dotnet config      (default Release)
#   VERSION          marketing version  (default 1.0.0)
#   SIGN_IDENTITY    "Developer ID Application: NAME (TEAMID)" — enables codesign
#   NOTARY_PROFILE   xcrun notarytool keychain profile name → notarize (local Mac)
#   NOTARY_KEY_PATH  App Store Connect API key (.p8) path       \
#   NOTARY_KEY_ID    key id                                      } all three → notarize (CI)
#   NOTARY_ISSUER    issuer id                                  /
# (NOTARY_PROFILE takes precedence over the API-key trio when both are set.)
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RID="${RID:-osx-arm64}"
CONFIG="${CONFIG:-Release}"
VERSION="${VERSION:-1.0.0}"

APP_NAME="WrapTune MacOS"
EXE_NAME="WrapTuneMacOS"
BUNDLE_ID="com.thefinder808.WrapTuneMacOS"

PROJECT="$ROOT/src/WrapTuneMacOS/WrapTuneMacOS.csproj"
ICNS="$ROOT/src/WrapTuneMacOS/WrapTuneMacOS.icns"
ENTITLEMENTS="$ROOT/build/entitlements.plist"
DIST="$ROOT/dist"
PUBLISH="$DIST/publish-$RID"
APP="$DIST/$APP_NAME.app"
DMG="$DIST/WrapTuneMacOS-$VERSION-$RID.dmg"

mkdir -p "$DIST"
rm -rf "$PUBLISH" "$APP" "$DMG"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "==> dotnet publish ($RID, $CONFIG)"
dotnet publish "$PROJECT" -c "$CONFIG" -r "$RID" --self-contained true \
    -p:UseAppHost=true -p:PublishSingleFile=false -o "$PUBLISH"

echo "==> Assembling $(basename "$APP")"
cp -R "$PUBLISH"/. "$APP/Contents/MacOS/"
cp "$ICNS" "$APP/Contents/Resources/$EXE_NAME.icns"
chmod +x "$APP/Contents/MacOS/$EXE_NAME"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleExecutable</key><string>$EXE_NAME</string>
    <key>CFBundleIconFile</key><string>$EXE_NAME</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict>
</plist>
PLIST

# ── Code signing (only when SIGN_IDENTITY is set) ──
if [[ -n "${SIGN_IDENTITY:-}" ]]; then
    echo "==> codesign (Hardened Runtime)"
    # Sign nested Mach-O (native dylibs + the apphost) first, then the bundle.
    while IFS= read -r -d '' f; do
        codesign --force --options runtime --timestamp \
            --entitlements "$ENTITLEMENTS" --sign "$SIGN_IDENTITY" "$f"
    done < <(find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -name "$EXE_NAME" \) -print0)
    codesign --force --options runtime --timestamp \
        --entitlements "$ENTITLEMENTS" --sign "$SIGN_IDENTITY" "$APP"
    codesign --verify --strict --verbose=2 "$APP"
else
    echo "==> SIGN_IDENTITY unset — UNSIGNED build (skip codesign)"
fi

# ── DMG (hdiutil: reliable and headless-friendly) ──
echo "==> Building $(basename "$DMG")"
STAGE="$DIST/dmg-stage"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

# ── Notarize + staple ──
# Local: a stored keychain profile (xcrun notarytool store-credentials …).
# CI:    an App Store Connect API key (no keychain on the runner).
if [[ -n "${SIGN_IDENTITY:-}" && -n "${NOTARY_PROFILE:-}" ]]; then
    echo "==> notarize (keychain profile '$NOTARY_PROFILE') + staple"
    xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
    xcrun stapler validate "$DMG"
elif [[ -n "${SIGN_IDENTITY:-}" && -n "${NOTARY_KEY_PATH:-}" && -n "${NOTARY_KEY_ID:-}" && -n "${NOTARY_ISSUER:-}" ]]; then
    echo "==> notarize (API key) + staple"
    xcrun notarytool submit "$DMG" \
        --key "$NOTARY_KEY_PATH" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER" --wait
    xcrun stapler staple "$DMG"
    xcrun stapler validate "$DMG"
else
    echo "==> Notary creds unset — skipping notarization"
fi

echo "==> Done: $DMG"
