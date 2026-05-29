# Golden fixtures

These let the `GoldenFixtureTests` cross-check our engine against the **official**
Microsoft Content Prep Tool. They are optional: the golden tests self-skip when
the files are absent, so the suite is green without them. Add them to raise
confidence before any release that touches the format.

## How to generate (on Windows)

1. Pick a tiny, deterministic source folder — e.g. a `setup.cmd` plus one or two
   small text files. Keep it small so the repo stays light.
2. Run the official tool (record its version):

   ```cmd
   IntuneWinAppUtil.exe -c <source> -s <source>\setup.cmd -o <out> -q
   IntuneWinAppUtil.exe -v
   ```

3. Commit into this folder:

   ```
   Fixtures/golden/<name>.intunewin     the official output
   Fixtures/golden/source/...           the EXACT input folder used
   Fixtures/golden/TOOLVERSION.txt      the version string from `-v`
   ```

## What the tests then check

- `Official_package_decrypts_and_validates_under_our_reader` — our reader
  validates the official package's HMAC, FileDigest, and size. This is the
  strongest single offline cross-check: if our reader accepts official output,
  our writer (its exact inverse) is producing the same structure.
- `Our_output_payload_matches_official_for_the_same_source` — packaging the same
  source with our engine yields a payload whose **recovered files** match the
  official one's (compared by content, not raw ZIP bytes — zip metadata/ordering
  legitimately differs across implementations).

## MSI fixtures

`MsiReaderTests` validates the OLE2/MSI parser against a real `.msi`. It looks for one:

1. the `WRAPTUNE_MSI_FIXTURE` env var (an absolute path to any `.msi`), or
2. any `*.msi` under `Fixtures/msi/`.

If neither is present it self-skips. For CI, commit a **small** sample `.msi`
under `Fixtures/msi/` (record its known ProductCode / ProductVersion /
UpgradeCode / PackageCode in a sibling note). For local runs against a large MSI,
point the env var at it, e.g.:

```bash
WRAPTUNE_MSI_FIXTURE=~/Downloads/WrapTune.msi dotnet test
```

The parser was calibrated against WrapTune's own MSI (publisher `thefinder808`,
upgrade code `{B7E4F831-…}`); the test asserts those values when the fixture is
named `WrapTune.msi`.

> Never commit anything sensitive. These are throwaway test installers only.
