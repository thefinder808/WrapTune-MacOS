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
to the open-source [`osslsigncode`](https://github.com/mtrojnar/osslsigncode) —
the cross-platform equivalent of Windows `SignTool`. The app signs the payload
first, then hands the unchanged engine the folder to wrap.

## Prerequisite

WrapTune does **not** bundle the signer (that keeps the notarized app
dependency-clean — no bundled OpenSSL, no CVE-patching burden). Install it once:

```bash
brew install osslsigncode
```

WrapTune auto-detects it at `/opt/homebrew/bin` or `/usr/local/bin`, or on your
`PATH`. You can also set an explicit path in the **osslsigncode** field, and use
**Check** to confirm the signer is found.

## Certificate options

You need a **Windows code-signing certificate** (this is separate from your Apple
Developer ID).

| Mode | Use it for | Inputs |
|------|-----------|--------|
| **PFX / .p12** | Self-signed, test, or legacy certs | `.pfx` path + password |
| **PKCS#11 / HSM** | Modern public OV/EV certs (token-backed since 2023) | PKCS#11 module path, cert URI, key URI, PIN |

PKCS#11 URIs look like `pkcs11:token=<token>;object=<label>`.

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

- The **password / PIN is never persisted.** It's entered each run, held only in
  memory, and passed to `osslsigncode` via a `0600` temp file (`-readpass`) that
  is deleted immediately after — never on the command line (which would be visible
  to other local users via `ps`).
- All other signing settings (paths, URIs, timestamp URL) are saved to
  `settings.json`; the secret is not.
- Signing exec's a separate process and needs no extra Hardened Runtime
  entitlements.

## Verifying

After signing + wrapping, confirm on Windows with `signtool verify /pa
/v your.exe` (or the file's *Properties → Digital Signatures*), then deploy via
Intune. A **self-signed** cert proves the signature is well-formed but won't be
*trusted* unless its root is installed on the endpoint — real trust needs a
publicly-issued cert (or the test root deployed to your devices).
