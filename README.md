# WrapTune MacOS

A macOS-native companion to [WrapTune](https://github.com/thefinder808/WrapTune)
(the Windows app). WrapTune MacOS lets a **Mac-based Intune admin who manages
Windows fleets** build `.intunewin` packages locally — no Windows VM required.

> The `.intunewin` packages this produces still deploy to **Windows** endpoints
> via Intune. This app is about building them from a Mac, not changing what they
> target.

## Why this is a separate app, not a recompile

Microsoft's official `IntuneWinAppUtil.exe` (the Win32 Content Prep Tool) is a
closed-source **.NET Framework** Windows-only binary — it cannot run on macOS.
WrapTune MacOS therefore ships its **own in-house implementation** of the
documented `.intunewin` format (AES-256-CBC + HMAC-SHA256 + a `Detection.xml`
metadata file), built with the .NET cryptography libraries only — no third-party
dependency produces the encrypted package. The UI is rebuilt in
[Avalonia](https://avaloniaui.net/) (the cross-platform analogue of WrapTune's WPF UI).

## Status

🚧 Early development. See `CLAUDE.md` for architecture and the build plan.

## Layout

```
src/WrapTuneMacOS.Packaging   The .intunewin engine (no UI) — see CLAUDE.md
src/WrapTuneMacOS             Avalonia desktop UI (forthcoming)
tests/                        Engine validation (round-trip + golden-fixture)
```

## Build & test (engine)

```bash
dotnet test
```

## License

MIT
