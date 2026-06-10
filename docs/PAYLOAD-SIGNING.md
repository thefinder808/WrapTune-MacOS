# Signing the payload

WrapTune can Authenticode-sign the Win32 installer payload — your
`.exe`/`.msi`/`.ps1` — **before** it is wrapped into the `.intunewin`. This is
optional and off by default. Signing runs **in-process**: there is nothing extra
to install.

## What this is (and isn't)

- The `.intunewin` format encrypts the payload (AES-256-CBC + HMAC). That is
  **confidentiality/integrity for Intune's delivery — not a publisher signature.**
- **Authenticode** code-signing is a publisher signature on the installer itself.
  Windows uses it for "Verified Publisher", to avoid SmartScreen "unknown
  publisher" blocks, and to satisfy WDAC/AppLocker policies that only run signed
  code.
- This feature is also unrelated to the app's own **Apple notarization** (that
  signs *this Mac app*, not the Windows payload it produces).

## How it works

The `.intunewin` engine (`WrapTuneMacOS.Packaging`) is deliberately clean-room:
pure managed code, zero third-party dependencies, and **no external-process
calls**, so the encrypted artifact stays auditable. Signing lives entirely outside
the engine in `WrapTuneMacOS.Signing`, which embeds the
[MacSign signing engine](https://github.com/thefinder808/macsign) (Apache-2.0,
same author) — a cross-platform Authenticode implementation built on the .NET
BCL's CMS APIs, whose releases are cross-verified by Windows `signtool` in CI.
The app signs the payload first, then hands the unchanged engine the folder to
wrap. *(Until v1.1.x this shelled out to user-installed `osslsigncode`/`jsign`
binaries; those prerequisites are gone.)*

The only remaining external tool is the **Azure CLI**, used optionally in Azure
Artifact Signing mode to auto-fetch an access token (`az login`) — and even that
can be skipped by pasting a token.

## Certificate options

You need a **Windows code-signing certificate** (this is separate from your Apple
Developer ID).

| Mode | Use it for | Inputs |
|------|-----------|--------|
| **PFX / .p12** | Self-signed, test, or legacy certs | `.pfx` path + password |
| **PKCS#11 / HSM** | Modern public OV/EV certs (token-backed since 2023) | PKCS#11 module path, PIN, optional cert thumbprint |
| **Azure Artifact Signing** | Cloud HSM certs (formerly Trusted Signing) | endpoint, account, cert profile, access token |

For PKCS#11, the **thumbprint** is only needed when the token holds several
certificates; leave it blank when it holds exactly one. The private key never
leaves the token (or, for Artifact Signing, Azure's HSM) — the engine sends it
digests to sign, not key material.

### Azure Artifact Signing specifics

*(Azure Artifact Signing is the current name for what was Azure Trusted Signing;
the endpoints still use the older `codesigning` hostnames.)*

- **Endpoint** is the regional host, e.g. `eus.codesigning.azure.net`. It's the
  **Account URI** on your account's Overview in the Azure portal — you can paste the
  full `https://…/` form; the scheme and trailing slash are stripped.
- **Token:** tokens are short-lived (~1 hour). Leave the token field **blank** to
  auto-fetch one at sign time via the Azure CLI
  (`az account get-access-token --resource https://codesigning.azure.net`) — so make
  sure you've run `az login`. Or paste a token manually (still transient, never saved).
- **RBAC role (common gotcha):** your Azure identity must hold the **"Artifact Signing
  Certificate Profile Signer"** role on the account (or certificate profile). Without it
  the token authenticates but signing returns **403 Forbidden**. Assign it in the portal
  (account → Access control (IAM)) or via CLI (role GUID
  `2837e146-70d7-4cfd-ad55-7efa6464f958`); allow a few minutes to propagate. Also confirm
  the account's **Identity Validation Status** is *Completed*.
- **Timestamping always happens** in this mode (its certs live only ~3 days, so an
  untimestamped signature would die with them). Leave the Timestamp URL blank to use
  Microsoft's TSA (`http://timestamp.acs.microsoft.com`), or set your own.
- WrapTune shows a live prerequisites check (Azure CLI present, the role reminder)
  when you select this mode.

## Behavior

- **In place.** Files are signed directly in the source folder, written atomically
  (temp file + rename, so a crash can't leave a half-signed file). Keep an unsigned
  copy under source control if you need one.
- **Already-signed files are skipped.** The engine detects existing signatures
  in-process (all modes), so vendor-signed installers (Chrome, Zoom, …) are never
  clobbered.
- **Scope.** By default only the **setup file** is signed. Enable *"Also sign other
  signable files in the source folder"* to sign every signable file (e.g. helper
  binaries a bootstrapper invokes).
- **Signable types:** PE (`.exe`/`.dll`/`.sys`), `.msi`, and `.ps1`. Plain
  `.cmd`/`.bat` scripts are **not** Authenticode-signable and are excluded.
- **Timestamping.** Set an RFC3161 timestamp URL (e.g.
  `http://timestamp.digicert.com`; a comma-separated list is tried in order) so the
  signature remains valid after the certificate expires. Requires network access.
  Blank skips timestamping — except in Azure Artifact Signing mode (see above).

## Security

- The **password / PIN / token is never persisted.** It's entered (or fetched) each
  run and held only in memory. Signing is in-process, so the secret is never placed
  on any command line (which would be visible to other local users via `ps`) and
  never written to disk.
- All other signing settings (paths, endpoint/account/profile) are saved to
  `settings.json`; the secret/token is not.
- The Azure key and PKCS#11 token key **never enter this process** — the engine
  uses delegating signers that send digests out for signing.

## Verifying

After signing + wrapping, confirm on Windows with `signtool verify /pa
/v your.exe` (or the file's *Properties → Digital Signatures*), then deploy via
Intune. A **self-signed** cert proves the signature is well-formed but won't be
*trusted* unless its root is installed on the endpoint — real trust needs a
publicly-issued cert (or the test root deployed to your devices).
