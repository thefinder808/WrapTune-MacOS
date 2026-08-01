# WrapTune MacOS

Build Microsoft Intune **`.intunewin`** Win32 app packages natively on a Mac — no
Windows VM, no Wine, no official tooling required.

WrapTune MacOS is the macOS-native companion to
[WrapTune](https://github.com/thefinder808/WrapTune) (the Windows app). It's for
the **Mac-based admin who manages Windows fleets** and wants to wrap installers
into `.intunewin` packages without leaving macOS.

> The packages this produces deploy to **Windows** endpoints through Intune. This
> app builds them from a Mac — it doesn't change what they target.

<img src="docs/images/hero.png" alt="WrapTune building an .intunewin package" width="792">

## Why this exists

Microsoft's official `IntuneWinAppUtil.exe` (the Win32 Content Prep Tool) is a
closed-source, **.NET Framework**, **Windows-only** binary. It cannot run on
macOS, so the usual Mac options are a Windows VM or skipping local packaging
entirely.

WrapTune MacOS instead ships its **own clean-room implementation** of the
documented `.intunewin` format. The package layout, encryption, and metadata are
produced directly:

- **AES-256-CBC** payload encryption + **HMAC-SHA256** integrity, with a distinct
  MAC key, using only the .NET base-class-library cryptography — **no third-party
  dependency produces the encrypted artifact** (deliberate, for auditability and
  supply-chain safety).
- A hand-written **OLE2 / MSI Property-table reader** so MSI metadata
  (product/upgrade/package codes, version, publisher, install context) is
  extracted on macOS without any Windows Installer APIs.
- A `Detection.xml` that matches the shape Intune's client expects.

The result has been **deployed to a real Intune tenant and installed on Windows
endpoints** (Zoom, Chrome Enterprise, PowerShell 7), with the official tool
nowhere in the chain.

## Features

- Wrap `.exe`, `.msi`, `.ps1`, `.cmd`, `.bat` installers into `.intunewin`.
- Automatic **MSI metadata** extraction, including install context
  (per-machine / per-user / dual-purpose) derived from the MSI's `ALLUSERS`
  property — the value Intune uses for the app's install behavior.
- **One-screen, three-step flow** (v2.0): grouped Files card with derived values
  (setup auto-detected, output defaulted, MSI metadata readout, live file
  count/size), signing folded in as step 2, and a **staged progress view**
  (per-stage checkmarks, live percent, elapsed time) with the raw engine log a
  click away.
- **Whole-window drag-and-drop** — drop a folder anywhere to set the source,
  drop an installer to set the setup file, drop a `.intunewin` to inspect it.
- **Optional Authenticode code-signing** of the payload before wrapping — sign
  your `.exe`/`.msi`/`.ps1` from the Mac with a local cert (PFX or PKCS#11/HSM) or
  **Azure Artifact Signing** — formerly Trusted Signing. Signing runs in-process;
  nothing extra to install. See [Signing the payload](#signing-the-payload-optional).
- **Package inspector** — **File → Inspect Package…** (⌘I) opens any `.intunewin`,
  shows its metadata, runs the HMAC / digest / size verification, lists the payload
  contents, and can extract the decrypted payload.
- **`wraptune` CLI** — the same engine, scriptable: `pack`, `inspect`, and `extract`
  for CI and automation. See [Command line](#command-line).
- Light / dark theme.
- **In-app auto-updates** — checks GitHub Releases on launch (once a day) and from
  **Help → Check for Updates…**, then one-click download → verify → install → relaunch.
  Only a notarized, Developer-ID-signed build is ever installed.
- Signed **and notarized** universal release (Apple Silicon + Intel).

<img src="docs/images/light-mode.png" alt="WrapTune in light theme" width="792">

## Install

Download the latest signed, notarized `.dmg` for your Mac from the
[**Releases**](https://github.com/thefinder808/WrapTune-MacOS/releases) page:

- Apple Silicon → `WrapTuneMacOS-<version>-osx-arm64.dmg`
- Intel → `WrapTuneMacOS-<version>-osx-x64.dmg`

Open the `.dmg`, drag **WrapTune MacOS** to Applications, and launch. Because the
build is notarized, Gatekeeper accepts it without the right-click-Open workaround.

## Usage

1. **Source folder** — the folder containing your installer and any supporting files.
2. **Setup file** — the installer within that folder (`.exe`, `.msi`, `.ps1`, …).
3. **Output folder** — where the `.intunewin` is written.
4. Click **Package** (⌘R) — a staged progress view tracks sign → zip → encrypt →
   assemble, with the raw engine log behind the *raw log* disclosure — then
   upload the result in the Intune admin center as a **Windows app (Win32)**.

<img src="docs/images/packaging.png" alt="WrapTune's staged packaging progress" width="792">

For `.msi` setup files, the package includes the MSI metadata Intune reads to
pre-fill the product code (e.g. the uninstall command) and the install behavior.

To audit an existing package — yours or anyone's — drop a `.intunewin` anywhere
on the window, or use **File → Inspect Package…** (⌘I): it shows the recorded
metadata, verifies the HMAC, digest, and size exactly the way Intune's client
would, lists the payload's files, and can save the decrypted payload zip.

## Signing the payload (optional)

Wrapping into `.intunewin` is *encryption*, not a publisher signature. If your
fleet requires **Authenticode-signed** code (Verified Publisher, no SmartScreen
block, WDAC/AppLocker "signed only" policies), WrapTune can sign the payload —
your `.exe`/`.msi`/`.ps1` — **before** it's wrapped, in one pass.

This is a clean-room-friendly add-on: signing runs entirely **outside** the
`.intunewin` engine (which stays zero-dependency and pure-managed), powered by the
[MacSign signing engine](https://github.com/thefinder808/macsign) (Apache-2.0,
same author) — a cross-platform Authenticode implementation whose releases are
cross-verified by Windows `signtool` in CI. It runs **in-process**: no
`osslsigncode`, no `jsign`, no JVM to install.

Flip the **Sign payload** switch (step 2 of the flow) and pick a mode from the
segmented control:

- **PFX / .p12** — point at a `.pfx` file and enter its password (for self-signed,
  test, or legacy certificates).
- **PKCS#11 token / HSM** — point at the vendor's PKCS#11 module and enter the PIN
  (the form modern public OV/EV certificates take); the key never leaves the token.
- **Azure Artifact Signing** (formerly Trusted Signing) — enter your endpoint, account,
  and certificate profile. The short-lived access token is fetched automatically via the
  Azure CLI (`az login`), or you can paste one. Timestamping always happens (Microsoft's
  TSA by default). Your Azure identity needs the **"Artifact Signing Certificate Profile
  Signer"** role on the account, or signing returns 403.
- Optional **RFC3161 timestamp URL** (PFX/PKCS#11 modes) so signatures stay valid
  after the cert expires.

Notes: the password/PIN/token is **entered each run and never saved**; files are
signed **in place** in the source folder; and files that already carry a signature
are **skipped** (so vendor-signed installers are never clobbered). Plain `.cmd`/`.bat`
scripts can't be Authenticode-signed and are excluded. Full details:
[`docs/PAYLOAD-SIGNING.md`](docs/PAYLOAD-SIGNING.md).

## Command line

Everything the GUI does is also scriptable — one engine, two frontends. The
`wraptune` CLI wraps, inspects, and extracts packages, so `.intunewin` builds can
run in CI (the engine is pure managed .NET; it doesn't even need macOS).

```bash
# Wrap an installer (flags mirror the official IntuneWinAppUtil)
wraptune pack -c ./source -s ./source/setup.msi -o ./out

# Inspect + verify any .intunewin (exit 0 = valid, 1 = invalid)
wraptune inspect ./out/setup.intunewin

# Recover the decrypted payload zip
wraptune extract ./out/setup.intunewin -o ./recovered
```

Payload-signing flags mirror the GUI (`--pfx`, `--pkcs11-module`, or the
`--azure-endpoint/--azure-account/--azure-profile` trio); secrets are read from
the `WRAPTUNE_SIGN_SECRET` / `WRAPTUNE_AZURE_TOKEN` environment variables — never
from argv, which other local users can see in `ps`. Full reference and build
instructions: [`docs/CLI.md`](docs/CLI.md).

## How it differs from the Windows WrapTune

The macOS app is intentionally simpler: there's **no "IntuneWinAppUtil.exe
path"** field (the engine is built in) and **no Catalog folder** field (the
official tool's `-a` Win10-S-mode catalog signing isn't reimplemented). Since
v2.0 the window is a single three-step flow — a grouped **Files** card
(source / setup / output / overwrite), an optional **Sign payload** step (see
above) that the Windows tool delegates to a separate signing tool, and one
**Package** action with a staged progress view. Light/dark theme lives under
**Window → Toggle Theme** (⌘T).

## Verifying a package

`tools/verify-intunewin.py` is an **independent** verifier that re-derives every
value Intune's client checks — container shape, `Detection.xml` fields, key
sizes, blob layout, HMAC, AES decryption, digest, size, and that the payload zip
contains the setup file — using code paths that share nothing with the engine
(Python standard library + `openssl`). It exits non-zero on any failure.

```bash
python3 tools/verify-intunewin.py path/to/package.intunewin
```

This proves format and cryptographic correctness. A real tenant upload remains
the authoritative check for tenant-side rules.

## Build from source

Requires the **.NET 10 SDK**.

```bash
# Run the engine's test suite
dotnet test

# Build a local .app + .dmg (unsigned is fine for development)
./build-macos.sh                 # Apple Silicon (osx-arm64)
RID=osx-x64 ./build-macos.sh     # Intel
```

Releasing (signing + notarization) is tag-driven via GitHub Actions; see
[`docs/RELEASE-SIGNING.md`](docs/RELEASE-SIGNING.md).

## Project layout

```
src/WrapTuneMacOS.Packaging   The .intunewin engine (class library, no UI)
src/WrapTuneMacOS.Signing     Optional payload Authenticode signing (in-process MacSign engine)
src/WrapTuneMacOS             Avalonia desktop UI
src/WrapTuneMacOS.Cli         wraptune CLI (pack / inspect / extract) on the same engine
tests/                        Engine + signing validation (round-trip, golden, MSI, sign-then-wrap)
tools/verify-intunewin.py     Independent package verifier
build-macos.sh                publish → .app → sign → .dmg → notarize
```

## Support

If WrapTune saves you from spinning up a Windows VM, you can
[**buy me a coffee**](https://www.buymeacoffee.com/thefinder808). ☕

## License

[MIT](LICENSE)
