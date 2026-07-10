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

APP_NAME="WrapTune"          # user-facing: .app bundle, DMG volume, CFBundleName/DisplayName, macOS menu
EXE_NAME="WrapTuneMacOS"     # internal: executable + .icns + CFBundleExecutable (kept stable across the rename)
BUNDLE_ID="com.thefinder808.WrapTuneMacOS"   # stable bundle identity — do not change

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
    -p:UseAppHost=true -p:PublishSingleFile=false \
    -p:DebugType=none -p:DebugSymbols=false -o "$PUBLISH"

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
    echo "==> codesign (Hardened Runtime, deep)"
    # .NET's self-contained layout drops native dylibs, managed dlls and data
    # files all into Contents/MacOS — codesign treats that as the code area and
    # rejects any loose unsigned file. --deep signs all nested code in one pass
    # and seals the rest. The bundle is flat (no nested .app/.framework), so
    # --deep is safe and notarizes; entitlements apply to the main executable.
    codesign --force --deep --options runtime --timestamp \
        --entitlements "$ENTITLEMENTS" --sign "$SIGN_IDENTITY" "$APP"
    codesign --verify --deep --strict --verbose=2 "$APP"
else
    echo "==> SIGN_IDENTITY unset — UNSIGNED build (skip codesign)"
fi

# ── DMG: styled drag-to-Applications layout ──
# Build a read-write image, arrange it in Finder (window size + icon positions),
# then convert to a compressed read-only DMG. The Finder step is best-effort:
# a headless environment (CI without a window server) can't script Finder, so it
# falls back to a functional — just unstyled — DMG. Either way we always unmount
# and clean up the scratch image and staging dir.
echo "==> Building $(basename "$DMG")"
STAGE="$DIST/dmg-stage"
MOUNT="/Volumes/$APP_NAME"
RWDMG="$DIST/.rw-$RID.dmg"

cleanup_dmg() {
    hdiutil detach "$MOUNT" >/dev/null 2>&1 || true
    rm -rf "$STAGE" "$RWDMG"
}
# EXIT, not RETURN: a RETURN trap never fires at the top level of an executed
# script, so it was a no-op safety net. INT/TERM route through exit so an
# interrupted build also unmounts and cleans up.
trap cleanup_dmg EXIT
trap 'exit 130' INT TERM

rm -rf "$STAGE"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

hdiutil detach "$MOUNT" >/dev/null 2>&1 || true
rm -f "$RWDMG"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDRW "$RWDMG" >/dev/null
hdiutil attach "$RWDMG" -mountpoint "$MOUNT" >/dev/null

# Arrange in Finder. Capture the exit code explicitly (set -e + an if/heredoc
# combo is fragile), so the log message is accurate.
layout_rc=0
osascript >/dev/null 2>&1 <<OSA || layout_rc=$?
tell application "Finder"
  tell disk "$APP_NAME"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, 800, 520}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to 112
    set position of item "$APP_NAME.app" of container window to {150, 210}
    set position of item "Applications" of container window to {450, 210}
    update without registering applications
    delay 1
    close
  end tell
end tell
OSA
if [[ $layout_rc -eq 0 ]]; then
    echo "    DMG layout applied"
else
    echo "    Finder layout unavailable (headless?) — shipping unstyled DMG"
fi

sync
hdiutil detach "$MOUNT" >/dev/null 2>&1 || true
hdiutil convert "$RWDMG" -format UDZO -o "$DMG" >/dev/null
rm -f "$RWDMG"
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
