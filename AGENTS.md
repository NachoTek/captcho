## Agent skills

### Issue tracker

GitHub Issues via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context — `CONTEXT.md` at the repo root and ADRs in `docs/adr/`. See `docs/agents/domain.md`.

### Build & test

Windows-only solution. Targets `net8.0-windows` / `net8.0-windows10.0.19041.0` with
WinUI 3 (`Microsoft.WindowsAppSDK`), so it cannot build or run on Linux.

- .NET 8 SDK (Windows). No `global.json` pins a specific version.
- In WSL, `dotnet` is not on the Linux PATH. Use the **Windows** SDK by adding it to PATH:
  `export PATH="$PATH:/mnt/c/Program Files/dotnet"`
- Then run from the repo root as usual, e.g.:
  - Full suite: `dotnet test Captcho.sln -c Debug`
  - Single project/file: `dotnet test captcho-ui.Tests/captcho-ui.Tests.csproj --filter "FullyQualifiedName~GeneralTabSettingsTests"`
- The build emits pre-existing `MSB3277` assembly-version warnings; these are not failures.
- C# diagnostics come from `dotnet build`/`dotnet test`, not an LSP: the built-in csharp
  server can't start in WSL (no native Linux .NET SDK on PATH) and couldn't resolve the
  WinUI/`net8.0-windows` target anyway. `lsp` is left enabled for other file types.
