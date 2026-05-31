# Signing the payload

WrapTune can Authenticode-sign the Win32 installer payload — your
`.exe`/`.msi`/`.ps1` — **before** it is wrapped into the `.intunewin`. This is
optional and off by default.

## What this is (and isn't)

- The `.intunewin` format encrypts the payload (AES-256-CBC + HMAC). That is
  **confidentiality/integrity for Intune's delivery — not a publisher signature.**
- **Authenticode** code-signing is a publisher signature on the installer itself.
  Windows uses it for "Verified Publisher", to avoid SmartScreen "unknown
  publisher" blocks, and to satisfy WDAC/AppLocker policies that only run signed
  code.
- This feature is also unrelated to the app's own **Apple notarization** (that
  signs *this Mac app*, not the Windows payload it produces).

## Why it's a separate component

The `.intunewin` engine (`WrapTuneMacOS.Packaging`) is deliberately clean-room:
pure managed code, zero third-party dependencies, and **no external-process
calls**, so the encrypted artifact stays auditable. Signing would break that, so
it lives entirely outside the engine in `WrapTuneMacOS.Signing`, which shells out
to open-source signers: [`osslsigncode`](https://github.com/mtrojnar/osslsigncode)
(the cross-platform equivalent of Windows `SignTool`) for local certificates, and
[`jsign`](https://ebourg.github.io/jsign/) for Azure Trusted Signing. The app signs
the payload first, then hands the unchanged engine the folder to wrap.

## Prerequisite

WrapTune does **not** bundle the signers (that keeps the notarized app
dependency-clean — no bundled OpenSSL/JVM, no CVE-patching burden). Install the one
your certificate needs:

```bash
brew install osslsigncode   # local certs (PFX / PKCS#11)
brew install jsign          # Azure Trusted Signing
```

WrapTune auto-detects them at `/opt/homebrew/bin` or `/usr/local/bin`, or on your
`PATH`. For osslsigncode you can also set an explicit path in the **osslsigncode**
field and use **Check** to confirm it's found.

## Certificate options

You need a **Windows code-signing certificate** (this is separate from your Apple
Developer ID).

| Mode | Use it for | Inputs | Signer |
|------|-----------|--------|--------|
| **PFX / .p12** | Self-signed, test, or legacy certs | `.pfx` path + password | osslsigncode |
| **PKCS#11 / HSM** | Modern public OV/EV certs (token-backed since 2023) | PKCS#11 module path, cert URI, key URI, PIN | osslsigncode |
| **Azure Trusted Signing** | Cloud HSM certs (Azure Artifact Signing) | endpoint, account, cert profile, access token | jsign |

PKCS#11 URIs look like `pkcs11:token=<token>;object=<label>`.

### Azure Trusted Signing specifics

- **Endpoint** is the regional host, e.g. `weu.codesigning.azure.net`; **account**
  and **cert profile** combine into jsign's `<account>/<profile>` alias.
- **Token:** Trusted Signing tokens are short-lived (~1 hour). Leave the token field
  **blank** to auto-fetch one at sign time via the Azure CLI
  (`az account get-access-token --resource https://codesigning.azure.net`) — so make
  sure you've run `az login`. Or paste a token manually (still transient, never saved).
- **Timestamping is automatic** for Trusted Signing (its certs live only ~3 days), so
  the Timestamp URL field is hidden in this mode.

## Behavior

- **In place.** Files are signed directly in the source folder. (`osslsigncode`
  can't write its own input, so WrapTune signs to a temp file and atomically
  replaces the original.) Keep an unsigned copy under source control if you need
  one.
- **Already-signed files are skipped.** WrapTune runs `osslsigncode verify` first;
  if a file already carries a signature it's left untouched, so vendor-signed
  installers (Chrome, Zoom, …) are never clobbered.
- **Scope.** By default only the **setup file** is signed. Enable *"Also sign other
  signable files in the source folder"* to sign every signable file (e.g. helper
  binaries a bootstrapper invokes).
- **Signable types:** PE (`.exe`/`.dll`/`.sys`), `.msi`, and `.ps1`. Plain
  `.cmd`/`.bat` scripts are **not** Authenticode-signable and are excluded.
- **Timestamping.** Set an RFC3161 timestamp URL (e.g.
  `http://timestamp.digicert.com`) so the signature remains valid after the
  certificate expires. Requires network access; leave blank to skip.

## Security

- The **password / PIN / token is never persisted.** It's entered (or fetched) each
  run and held only in memory. It is never placed on a command line (which would be
  visible to other local users via `ps`):
  - osslsigncode (PFX/PKCS#11): passed via a `0600` temp file (`-readpass`) deleted
    immediately after signing.
  - jsign (Trusted Signing): passed via a child-process **environment variable**
    (`--storepass env:WT_TS_TOKEN`) — not in argv, not on disk.
- All other signing settings (paths, URIs, endpoint/account/profile) are saved to
  `settings.json`; the secret/token is not.
- Signing exec's a separate process and needs no extra Hardened Runtime
  entitlements.

## Verifying

After signing + wrapping, confirm on Windows with `signtool verify /pa
/v your.exe` (or the file's *Properties → Digital Signatures*), then deploy via
Intune. A **self-signed** cert proves the signature is well-formed but won't be
*trusted* unless its root is installed on the endpoint — real trust needs a
publicly-issued cert (or the test root deployed to your devices).
