# Releasing & code signing (macOS)

## How a release happens

Push a SemVer tag and CI does the rest:

```bash
git tag v1.0.0
git push origin v1.0.0
```

`.github/workflows/release.yml` (on `macos-latest`) then:

1. runs the tests,
2. publishes self-contained `osx-arm64` **and** `osx-x64` builds,
3. assembles each into a `WrapTune MacOS.app`,
4. **signs + notarizes** them (only if the secrets below exist — otherwise it
   produces an *unsigned* `.dmg` so the pipeline still works pre-enrollment),
5. builds a `.dmg` per arch and attaches both to a GitHub Release for the tag.

Until the signing secrets are added, releases are **unsigned** — they run, but
Gatekeeper warns on first launch. Add the secrets to flip on signing; no code
changes needed.

## One-time Apple setup

Requires a paid **Apple Developer Program** membership (~$99/yr).

### 1. Developer ID Application certificate

Create a **Developer ID Application** certificate (Xcode → Settings → Accounts →
Manage Certificates, or the Apple Developer portal). Export it from Keychain
Access as a `.p12` **with a password** (this bundles the private key).

```bash
base64 -i DeveloperID.p12 | pbcopy        # → APPLE_CERT_P12_BASE64
```

Find the identity string (used as `APPLE_SIGN_IDENTITY`):

```bash
security find-identity -v -p codesigning
# → "Developer ID Application: Your Name (TEAMID)"
```

### 2. App Store Connect API key (for notarization)

App Store Connect → **Users and Access → Integrations → App Store Connect API**
→ generate a key (role: *Developer* is sufficient for notarization). Download
the `.p8` (one-time download). Note the **Key ID** and the **Issuer ID**.

```bash
base64 -i AuthKey_XXXXXXXXXX.p8 | pbcopy   # → APPLE_API_KEY_P8_BASE64
```

### 3. Add the secrets

Repo → Settings → **Environments → `release`** (create it) → add these secrets:

| Secret | Value |
|---|---|
| `APPLE_SIGN_IDENTITY` | `Developer ID Application: Your Name (TEAMID)` |
| `APPLE_CERT_P12_BASE64` | base64 of the `.p12` |
| `APPLE_CERT_PASSWORD` | the `.p12` export password |
| `APPLE_API_KEY_P8_BASE64` | base64 of the `.p8` |
| `APPLE_API_KEY_ID` | the key id (e.g. `ABC123DEF4`) |
| `APPLE_API_ISSUER` | the issuer id (a UUID) |

Scoping to the `release` environment (rather than repo-wide) limits blast radius
— it mirrors how WrapTune scopes its Azure signing secrets.

## Building locally

Unsigned (default — good for dev):

```bash
./build-macos.sh                      # osx-arm64
RID=osx-x64 ./build-macos.sh          # osx-x64
```

Signed + notarized locally:

```bash
export SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
export NOTARY_KEY_PATH=~/private/AuthKey_XXXX.p8
export NOTARY_KEY_ID=ABC123DEF4
export NOTARY_ISSUER=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
VERSION=1.0.0 ./build-macos.sh
```

## Why these entitlements

`build/entitlements.plist` enables `com.apple.security.cs.allow-jit` (+ unsigned
executable memory + library validation off). A self-contained, non-AOT .NET app
JITs managed code and loads its own dylibs; under the Hardened Runtime (required
for notarization) it crashes at launch without these.

## Verifying a signed build

```bash
codesign --verify --deep --strict --verbose=2 "dist/WrapTune MacOS.app"
spctl -a -vvv --type install "dist/WrapTuneMacOS-1.0.0-osx-arm64.dmg"
xcrun stapler validate "dist/WrapTuneMacOS-1.0.0-osx-arm64.dmg"
```

## Refreshing the icon

`src/WrapTuneMacOS/WrapTuneMacOS.icns` is committed (source of truth). Regenerate
after design tweaks with:

```bash
python3 tools/generate-icns.py
```
