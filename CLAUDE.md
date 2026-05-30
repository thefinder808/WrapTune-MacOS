# CLAUDE.md — WrapTune MacOS project context

> Your global `~/.claude/CLAUDE.md` rules apply (think first, check in before
> major changes, keep it simple, take the harder right, thorough security
> reviews, new repos private). This file is project-specific context.

## What this is

A macOS-native sibling to **WrapTune** (the Windows WPF app at
`/Users/thefinder808/Development/WrapTune`,
https://github.com/thefinder808/WrapTune). It builds Microsoft Intune
`.intunewin` Win32 packages from a Mac, for an admin who manages Windows fleets
but works on macOS.

This is a **separate repo**, not a fork or a change to WrapTune. WrapTune is
untouched. The two can drift; parity is maintained by hand.

Repo: https://github.com/thefinder808/WrapTune-MacOS *(private)* — created in Phase 6.

## The core problem & decision

The official `IntuneWinAppUtil.exe` is a **closed-source .NET Framework 4.7.2
Windows-only binary** — it cannot run on macOS. So this app does not wrap that
tool; it **reimplements the `.intunewin` format in-house** using only the .NET
BCL crypto (`System.Security.Cryptography`) + `System.IO.Compression`. No
third-party dependency produces the encrypted artifact (deliberate, for
auditability and supply-chain safety).

UI is **Avalonia** (.NET cross-platform XAML), ported from WrapTune's WPF
`MainWindow.xaml`.

**Target framework: `net10.0`** (the installed SDK; WrapTune uses net8.0 — this
repo is not bound to that).

## Project layout

```
WrapTuneMacOS.slnx                       .NET 10 XML solution
src/WrapTuneMacOS.Packaging/             the engine — class library, NO UI
  IntuneWinWriter.cs                       zip → AES-256-CBC → HMAC → Detection.xml → outer zip
  DetectionXml.cs                          Detection.xml model + serializer
  Msi/MsiPropertyReader.cs                 pure-managed OLE2 + MSI Property table
  InstallerExtensions.cs                   accepted setup-file extensions (single source of truth)
  PackageRequest.cs / PackageResult.cs     engine I/O records
src/WrapTuneMacOS/                       Avalonia desktop app (forthcoming, Phase 2)
tests/WrapTuneMacOS.Packaging.Tests/     round-trip + golden-fixture + MSI tests
  Fixtures/                                tiny payload + known-good .intunewin + sample .msi
tools/generate-icns.py                   builds the app .icns (shells to iconutil)
tools/verify-intunewin.py                independent .intunewin verifier (stdlib + openssl)
build-macos.sh                           publish → .app → sign → .dmg → notarize (Phase 3-4)
.github/workflows/release.yml            macos-latest CI on v* tags (Phase 3-4)
```

## The `.intunewin` format (engine MUST match exactly)

Output is an outer ZIP (OPC) containing two entries:
- `IntuneWinPackage/Contents/IntunePackage.intunewin` — the encrypted inner file
- `IntuneWinPackage/Metadata/Detection.xml` — metadata + keys

### Encryption (the part that's easy to get subtly wrong)

1. Zip the source folder → **inner ZIP**. `UnencryptedContentSize` = its byte length.
2. `FileDigest` = SHA-256 of the inner ZIP bytes **before** encryption; `FileDigestAlgorithm = "SHA256"`.
3. Generate with a CSPRNG (`RandomNumberGenerator`, never `System.Random`):
   `EncryptionKey` (32B), **distinct** `MacKey` (32B), `IV` (16B).
4. AES-256-CBC + PKCS7, key = `EncryptionKey`, the IV above.
5. `Mac = HMACSHA256(MacKey, IV || ciphertext)` — HMAC covers **IV + ciphertext**,
   not ciphertext alone, not the whole file.
6. Inner encrypted file bytes = **`Mac(32) || IV(16) || ciphertext`**.
7. All key/IV/Mac/digest fields in Detection.xml are **base64**.

### Detection.xml shape

`ApplicationInfo` (attr `ToolVersion`) → `Name`, `UnencryptedContentSize`,
`FileName` (= `IntunePackage.intunewin`), `SetupFile`, and `EncryptionInfo` →
`EncryptionKey`, `MacKey`, `InitializationVector`, `Mac`,
`ProfileIdentifier="ProfileVersion1"`, `FileDigest`, `FileDigestAlgorithm`.
For `.msi` setup files, also `MsiInfo` (ProductCode/Version/Publisher/UpgradeCode/PackageCode).

### MSI metadata

Requires reading the MSI's `Property` table — an OLE2 Structured Storage
(compound file). No Windows APIs on macOS, so we parse the compound-file format
+ MSI string-pool/columns by hand in `Msi/MsiPropertyReader.cs`.

**Install context is derived, not assumed.** `MsiExecutionContext` /
`MsiIsMachineInstall` / `MsiIsUserInstall` come from the MSI's `ALLUSERS`
property (`ResolveInstallContext`): `ALLUSERS=1` → per-machine (context **0**),
`ALLUSERS=2` → dual-purpose (context **2**, both machine+user true),
empty/absent → per-user (context **1**). The integer encoding follows Microsoft
Graph's `win32LobAppMsiPackageType` enum (perMachine=0, perUser=1,
dualPurpose=2 — confirmed in MS Learn docs). We only emit fields we can ground
from the Property table; the other `MsiInfo` booleans
(`MsiRequiresReboot/Logon`, `MsiIncludesServices`, `MsiContainsSystem*`) stay at
their `false` default until a golden MSI lets us calibrate them (don't guess —
rule #6). Unit-tested in `MsiInstallContextTests` (no fixture needed).

## Differences from WrapTune's UI (when Phase 2 lands)

The macOS window is **simpler**: no "IntuneWinAppUtil.exe path" row (engine is
built in) and **no Catalog folder field** (`-a` Win10-S-mode catalog signing is
a feature of the official tool we don't replicate). Fields: Source folder, Setup
file, Output folder, Overwrite, theme toggle, Package, output log.

Settings: `~/Library/Application Support/WrapTuneMacOS/settings.json` via
`Environment.SpecialFolder.LocalApplicationData` (already resolves correctly on
macOS). Guard against the known empty-path .NET bug on some macOS releases.

## Verification ladder (don't ship an invalid package)

1. **Round-trip self-test** — decrypt our own output, verify HMAC, `FileDigest`,
   size, and that the payload unzips identically. Runs in CI.
2. **Golden-fixture differential** — compare against a known-good `.intunewin`
   produced by the **official tool on Windows** for the same input (decrypt both,
   assert recovered inner ZIPs are byte-identical; diff Detection.xml). Fixture
   committed under `tests/.../Fixtures/` with the official tool version recorded.
3. **Independent verifier** — `python3 tools/verify-intunewin.py <file>`. Re-derives
   every value Intune's client checks (container shape, Detection.xml fields, key
   sizes, blob layout, HMAC, AES decrypt, digest, size, payload-zip + SetupFile
   present) using code paths that share **nothing** with our writer (Python stdlib
   `hmac`/`hashlib`/`zipfile` + `openssl` for AES). Exits non-zero on any failure;
   for `.msi` setup files it also checks `MsiInfo`/`MsiProductCode`. Proves
   format/crypto correctness only — **not** tenant-side rules.
4. **Real tenant upload** — the authoritative gate, run manually per
   format-affecting change.

## Build sequence (see ~/.claude/plans for the full plan)

0. ✅ Scaffold (sln + Packaging lib + tests, net10.0)
1. ✅ Engine (EXE + MSI) + round-trip/golden/structure/MSI tests (17 tests)
   — ✅ MSI/OLE2 reader (`Msi/`), calibrated against a real MSI
2. ✅ Avalonia UI (`src/WrapTuneMacOS`) + 3 headless smoke tests
3. ✅ Build/dist: `build-macos.sh` (.app → .dmg via hdiutil), committed `.icns`
      (`tools/generate-icns.py`), `macos-latest` CI in `.github/workflows/release.yml`
4. ✅ Signing + notarization WIRED (Hardened Runtime + `build/entitlements.plist`,
      codesign → notarytool → stapler); gated on `release`-env secrets, degrades
      to unsigned until added. Setup checklist: `docs/RELEASE-SIGNING.md`
6. ✅ Obsidian note + README/CLAUDE + signing docs; private GitHub repo
      (github.com/thefinder808/WrapTuneMacOS)

## Status (2026-05-29)

- **v0.1.1 shipped** — signed + notarized DMGs (arm64 + x64) on the Releases page;
  Gatekeeper accepts the install. Signed with Developer ID `Nathaniel Graham (Q6LRJQSA42)`.
- **CI signing fully wired** — all 6 `release`-env secrets set: Developer ID cert +
  App Store Connect API key `G9325XG4R4`, issuer `f479e6f9-114d-4c2c-a3c0-bff4f21d61c1`.
  `git tag v*` → signed + notarized release. (Apple creds also in the local keychain
  for local `./build-macos.sh` signing.)
- Two post-release fixes landed: codesign **`--deep`** for the flat .NET bundle (managed
  dlls/pdbs live in `Contents/MacOS`), and trailing-slash tolerance in the source-folder check.
- **Independent verifier built** (`tools/verify-intunewin.py`); first real `.intunewin` (a
  PowerShell-script package) passes format/crypto verification end-to-end.
- **First real MSI packaged + verified** (`WrapTune.msi`, 131 MB): the hand-rolled OLE2 reader
  extracted all GUIDs correctly. Verifying it surfaced a latent bug — MSI install context was
  hardcoded machine-only — now fixed to derive from `ALLUSERS` (branch
  `fix/msi-execution-context`, not yet merged to main).

## Next steps / follow-ups

- [x] **Independent verifier — `tools/verify-intunewin.py` — BUILT (2026-05-29).** Runs the
      full ladder (container → Detection.xml → key sizes → blob layout → HMAC → openssl AES
      decrypt → digest/size → payload-zip + SetupFile → MsiInfo for `.msi`). Independent of the
      engine (Python stdlib + `openssl`). Exits non-zero on any failure (CI-friendly). First
      run on `Detect-WindowsPatchHealth.ps1` package: **PASS** (491,497 B payload, 242-entry
      ZIP). Still only proves format/crypto, not tenant server-side rules.
      Note: missing `<MsiInfo>` on an `.msi` is a **WARN** not a FAIL — it's optional for a
      Win32-app upload and mirrors the engine, which only warns when its MSI reader can't
      parse the file; an empty `<MsiProductCode>` inside a present `<MsiInfo>` is still a FAIL.
- [x] **MSI install context now derived from `ALLUSERS` — FIXED (2026-05-29).** `MsiPropertyReader`
      previously hardcoded `MsiExecutionContext=0` + `MsiIsMachineInstall=true` for every MSI
      (and never set `MsiIsUserInstall`), so a per-user/dual-purpose MSI would have been mislabeled
      machine-only. Now `ResolveInstallContext(ALLUSERS)` maps `1`→per-machine (0/M), `2`→dual
      (2/M+U), empty|absent→per-user (1/U), per MS Learn `win32LobAppMsiPackageType`
      (perMachine=0,perUser=1,dualPurpose=2). Verified by re-packaging the real `WrapTune.msi`
      (ALLUSERS=1 → unchanged 0/machine — no regression) + 4 new `MsiInstallContextTests`; 23/23
      green. The non-Graph `MsiInfo` booleans (`MsiRequiresReboot/Logon`, `MsiIncludesServices`,
      `MsiContainsSystem*`) deliberately left at `false` — not derivable without a golden MSI;
      don't guess (rule #6). On branch `fix/msi-execution-context`.
- [ ] Commit a small golden `.intunewin` (from the official Windows tool) + a small `.msi`
      under `tests/.../Fixtures/` so the differential + MSI tests run in CI (they self-skip now).
- [ ] Authoritative validation: upload a Mac-built `.intunewin` to an Intune tenant.
- [ ] Optional: migrate drag-drop to Avalonia 12 `DataTransfer` (one CS0618 warning today).
- [ ] Decide if/when to make the repo public.

> Session note (2026-05-29): work repeatedly stalled because Anthropic's Bash/Edit safety
> classifier kept returning "temporarily unavailable" — retrying the same command generally
> succeeded. Infra hiccup, not a code problem.

## Gotchas

- Only the **.NET 10 SDK/runtime** is installed locally; net8 is absent — hence net10.0.
- `.slnx` (not `.sln`) — .NET 10 default solution format. Use `dotnet sln WrapTuneMacOS.slnx ...`.
- Avalonia templates are not installed; the UI project adds Avalonia via NuGet refs directly.
- **Avalonia pinned to 11.3.17** (the plan's 11.x line). 12.x removes the classic
  `DragEventArgs.Data` API in favour of `DataTransfer`; on 11.3.x `e.Data.GetFiles()`
  works but warns CS0618 — left as-is until a deliberate 12.x migration.
- The UI test project is named `WrapTuneMacOS.App.Tests` but its **namespace is
  `WrapTuneMacOS.UiTests`** — `namespace WrapTuneMacOS.App.*` would shadow the `App` class.
- Headless UI tests use `Avalonia.Headless.XUnit` (`[AvaloniaFact]`); theme-dictionary
  resources resolve only via the **variant-aware** `TryFindResource(key, variant, out _)`.
- Never commit signing material (`.p12`, `.p8`) — see `.gitignore`.
