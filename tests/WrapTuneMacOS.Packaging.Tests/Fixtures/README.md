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

For the MSI reader phase, also commit one or more small sample `.msi` files under
`Fixtures/msi/` with their known ProductCode / ProductVersion / UpgradeCode /
PackageCode recorded alongside, so `MsiPropertyReader` can be unit-tested.

> Never commit anything sensitive. These are throwaway test installers only.
