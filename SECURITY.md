# Security Policy

WrapTune MacOS builds Intune `.intunewin` packages with its own clean-room
crypto, opens `.intunewin` files it did not create, signs code, and updates
itself. Each of those is a place where a bug could matter, so vulnerability
reports are genuinely welcome.

## Supported versions

Only the latest release gets fixes. The app has a built-in updater
(**Help → Check for Updates…**), so please update before reporting.

| Version | Supported |
| ------- | --------- |
| 2.0.x   | ✅ |
| < 2.0   | ❌ |

## Reporting a vulnerability

**Report privately through GitHub — do not open a public issue.**

Go to the [**Security**](https://github.com/thefinder808/WrapTune-MacOS/security)
tab → **Report a vulnerability**. That opens a private advisory only the
maintainer can see.

Please include:

- Which component (packaging engine, inspector/CLI, updater, payload signing, UI).
- Version (**Help → About WrapTune**, or `wraptune --version`) and macOS version.
- Steps to reproduce, and a proof of concept if you have one.
- A sample file if the bug involves parsing (`.intunewin`, `.msi`) — a minimal
  crafted file is far more useful than a description.
- What you think an attacker gains.

This is a single-maintainer side project, so response times are best-effort, not
contractual:

| Stage | Target |
| ----- | ------ |
| Acknowledge the report | 5 business days |
| Initial assessment (valid / not, rough severity) | 10 business days |
| Fix released for a confirmed issue | 90 days, sooner when severity warrants |

You'll be credited in the advisory and release notes unless you'd rather not be.
Please hold public disclosure until a fix ships or 90 days pass, whichever comes
first.

## In scope

The parts most worth your time:

**Packaging engine** (`src/WrapTuneMacOS.Packaging`) — anything that weakens the
`.intunewin` artifact: predictable or reused keys/IVs, a key/MAC-key collision,
HMAC covering the wrong bytes, an encryption or digest mismatch that would let a
tampered payload verify, or a `Detection.xml` that misrepresents the payload.

**Untrusted-file parsing** — the app parses files it did not create in the
package inspector (⌘I), on drag-and-drop, and in `wraptune inspect` / `extract`.
Reports welcome for path traversal or symlink escape on extract ("zip slip"),
XML entity expansion or external-entity fetches in `Detection.xml`, and
memory/CPU exhaustion or crashes from a crafted `.intunewin` or `.msi` — the
hand-written OLE2/MSI reader in `Msi/` is a deliberate target.

**Updater** (`src/WrapTuneMacOS/Services/UpdateService.cs`) — the highest-value
target here, since it installs code. Anything that gets an unverified or
downgraded build installed: bypassing the Developer ID / Team ID
(`Q6LRJQSA42`) / notarization checks on the app inside the DMG, a TOCTOU or
symlink race in the download-verify-swap sequence, or a transport weakness.

**Payload signing** (`src/WrapTuneMacOS.Signing`) — secret handling above all:
a PFX password, PKCS#11 PIN, or Azure token reaching argv, a temp file, a log,
the crash log, or `settings.json`. Also signing the wrong bytes, or silently
producing a signature that doesn't verify.

**Local exposure** — secrets or sensitive paths written to
`~/Library/Application Support/WrapTuneMacOS/settings.json`,
`~/Library/Logs/WrapTuneMacOS/`, or world-readable temp files.

**Supply chain / build** — a flaw in `build-macos.sh` or the release workflow
that could ship an unsigned, un-notarized, or substituted artifact.

## Out of scope

**The encryption keys in `Detection.xml` are not a vulnerability.** The
`.intunewin` format stores `EncryptionKey`, `MacKey`, and the IV in base64 inside
the package, next to the ciphertext, by design — that is how Intune's client
decrypts the payload after download. WrapTune implements the documented format;
it cannot deviate and still produce packages Intune accepts. The encryption
protects the payload in transit and at rest in the service, not from someone
holding the `.intunewin` file. Treat a `.intunewin` as being as sensitive as its
payload.

Also out of scope:

- Microsoft Intune, Graph, Azure Artifact Signing, or Windows itself — report
  those to Microsoft.
- Vulnerabilities in an installer *you* wrapped. WrapTune packages what it's
  pointed at; it doesn't audit it.
- Bugs in the [MacSign](https://github.com/thefinder808/macsign) signing engine —
  report them there. If the flaw is in how WrapTune *uses* it, report it here.
- Anything that requires an attacker who already has code execution or admin on
  the Mac, or write access to the source folder being packaged.
- Unsigned local development builds (`./build-macos.sh` without signing) behaving
  differently from a notarized release.
- Missing hardening with no demonstrated impact, and automated-scanner output
  with no working proof of concept.
- Social engineering, physical access, or DoS against GitHub.

## Verifying a release

Every release DMG is signed with an Apple Developer ID and notarized, and ships
with a `checksums.txt`. Before installing, you can confirm you have the genuine
artifact:

```bash
shasum -a 256 -c checksums.txt
```

```bash
spctl --assess --type open --context context:primary-signature -v WrapTuneMacOS-<version>-osx-arm64.dmg
```

Expect `source=Notarized Developer ID`. To check the app itself after installing:

```bash
codesign --verify --deep --strict --verbose=2 /Applications/WrapTune.app
```

```bash
codesign -dv --verbose=4 /Applications/WrapTune.app 2>&1 | grep TeamIdentifier
```

The Team Identifier must be `Q6LRJQSA42`. The in-app updater performs these same
checks on the app *inside* the downloaded DMG before installing anything.

To verify a package you built, `tools/verify-intunewin.py` re-derives every value
Intune's client checks using code that shares nothing with the engine:

```bash
python3 tools/verify-intunewin.py path/to/package.intunewin
```

## Safe harbor

Good-faith security research on your own systems and your own packages is
welcome, and no legal action will be pursued over it. Please don't access data
that isn't yours, degrade anyone's service, or test against third-party Intune
tenants you don't administer.
