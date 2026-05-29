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
3. **Real tenant upload** — the authoritative gate, run manually per
   format-affecting change.

## Build sequence (see ~/.claude/plans for the full plan)

0. ✅ Scaffold (sln + Packaging lib + tests, net10.0)
1. ✅ Engine (EXE + MSI) + round-trip/golden/structure/MSI tests (17 tests)
   — ✅ MSI/OLE2 reader (`Msi/`), calibrated against a real MSI
2. ✅ Avalonia UI (`src/WrapTuneMacOS`) + 3 headless smoke tests
3. ⬜ Build/dist (.app/.dmg, .icns, build-macos.sh, CI)  ← next
4. ⬜ Code signing + notarization (Apple Developer ID, notarytool, stapler)
6. ⬜ Docs polish; ✅ private GitHub repo created + pushed
      (github.com/thefinder808/WrapTuneMacOS); Obsidian note still to add

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
