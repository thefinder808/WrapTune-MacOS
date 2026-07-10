# `wraptune` — command-line reference

The CLI frontend to the same engine the GUI uses (`WrapTuneMacOS.Packaging` +
`WrapTuneMacOS.Signing`). One core, two frontends: anything the CLI builds, the
GUI can inspect, and vice versa.

```
usage:
  wraptune pack -c <source-folder> -s <setup-file> -o <output-folder> [options]
  wraptune inspect <package.intunewin>
  wraptune extract <package.intunewin> [-o <folder>] [--overwrite]
```

`wraptune -c … -s … -o …` (no subcommand, official-IntuneWinAppUtil style) is
accepted and means `pack`.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | success / package is valid |
| 1 | operation failed / package is invalid |
| 2 | usage error (bad flags, missing file) |

## `pack`

Wraps a source folder into a `.intunewin`. Flags mirror the official
IntuneWinAppUtil where they overlap:

| Flag | Meaning |
|------|---------|
| `-c, --source <dir>` | folder whose contents get packaged (required) |
| `-s, --setup <file>` | the installer inside the source folder (required) |
| `-o, --output <dir>` | where the `.intunewin` is written (required) |
| `-q, --quiet` | suppress progress output; silently overwrites (official `-q` semantics) |
| `--overwrite` | replace an existing `.intunewin` |

The output path is printed on stdout (also in quiet mode), so scripts can
capture it: `PKG=$(wraptune pack -q -c … -s … -o …)`.

### Payload signing (optional)

Signing is enabled by the presence of a mode flag — pick exactly one mode:

| Flag | Mode |
|------|------|
| `--pfx <path>` | PFX / .p12 file |
| `--pkcs11-module <path>` (+ optional `--pkcs11-thumbprint <hex>`) | PKCS#11 token / HSM |
| `--azure-endpoint <host>` + `--azure-account <name>` + `--azure-profile <name>` | Azure Artifact Signing (formerly Trusted Signing) |

Common signing options: `--timestamp-url <url>` (Azure mode defaults to
Microsoft's TSA — Artifact Signing certs are short-lived, so timestamping is
never skipped), `--description <text>`, `--sign-url <url>`, and `--sign-all` to
also sign every other signable file in the source folder. Already-signed files
are always skipped.

**Secrets come from the environment, never argv** (argv is visible to every
local user via `ps`):

| Variable | Used for |
|----------|----------|
| `WRAPTUNE_SIGN_SECRET` | PFX password / PKCS#11 PIN |
| `WRAPTUNE_AZURE_TOKEN` | pasted Azure access token; when unset, a fresh token is fetched via the Azure CLI (`az login` first) |

Example — sign with Azure Artifact Signing, then wrap:

```bash
az login   # once; the token is auto-fetched per run
wraptune pack \
  -c ./source -s ./source/Deploy-App.ps1 -o ./out \
  --azure-endpoint eus.codesigning.azure.net \
  --azure-account  my-signing-account \
  --azure-profile  public-signing
```

## `inspect`

Prints the package's Detection.xml metadata (including `MsiInfo` when present),
runs the same HMAC / digest / size verification Intune's client performs, and
lists the payload's contents. Exit 0 when everything checks out, 1 otherwise —
CI-friendly:

```bash
wraptune inspect ./out/setup.intunewin || echo "package invalid!"
```

## `extract`

Decrypts the payload back to a plain zip
(`<package-name>-payload.zip`, written to `-o <folder>`, default `.`).
Verify-then-decrypt: if the HMAC fails, nothing is written. If the payload
decrypts but its digest/size don't match Detection.xml, the file is kept but
the exit code is 1 and a warning goes to stderr.

## Building / running

```bash
# From the repo (framework-dependent; needs the .NET 10 runtime):
dotnet run --project src/WrapTuneMacOS.Cli -- pack -c … -s … -o …

# Self-contained single-folder binary (no runtime needed on the target):
dotnet publish src/WrapTuneMacOS.Cli -c Release -r osx-arm64 --self-contained -o ./dist-cli
./dist-cli/wraptune --help
```

The engine is pure managed .NET with no macOS dependency, so the same project
publishes for `linux-x64` / `win-x64` too — `.intunewin` packaging can run on
any CI runner.
