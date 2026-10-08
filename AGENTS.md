## Agent skills

### Issue tracker

GitHub Issues via the `gh` CLI. See `docs/agents/issue-tracker.md`.

This VM uses Windows PowerShell 5.1. Before each `gh` command, mint the GitHub App token:

```powershell
$env:GH_TOKEN = & "$env:USERPROFILE\.config\gh-app\gh-app-token.ps1"; gh ...
```

### Branch topology

- Resolve `BASE_BRANCH` before starting. Use the campaign/integration branch explicitly named by the parent issue or coordinator; otherwise use the repository default branch.
- Never commit or push directly to `main` or another integration base.
- Create one branch and one worktree per ticket from current `origin/$BASE_BRANCH`; name the branch `<issue-number>-<slug>`.
- Open the ticket PR against `BASE_BRANCH`, not automatically against `main`. End the PR body with `Closes #<issue-number>`.
- A campaign branch is a protected integration base. Child ticket PRs target it; only the final campaign PR targets the default branch and closes the parent epic.

For epic #24, `BASE_BRANCH` is `feat/24-spectacle-parity`. Do not implement child tickets directly on that branch.

### Review gates and closure

- Run `/code-review` before publication. CI requests `TerminalSausage` when green; do not use legacy review labels or third-party review routes.
- External CI and human review are event-driven Herdr gates. Never sleep or poll inside an agent session.
- At a gate, the child returns PR/base/branch/worktree/head receipts and ends its turn. The coordinator records the OpenCode task ID. When Herdr reports the event, verify GitHub ground truth and resume that same child with `task(task_id=...)`.
- The resumed child normally rebases if needed, merges, closes the issue when GitHub did not, and removes branch/worktree residue. A changed head means a fresh CI/review gate, so stop again instead of polling.
- The coordinator owns closure: independently verify PR and issue terminal state, refs/worktree absence, clean base, and tracker writeback.

### Domain docs

Single-context — `CONTEXT.md` at the repo root and ADRs in `docs/adr/`. See `docs/agents/domain.md`.

### Build & test

Windows-only solution. Targets `net8.0-windows` / `net8.0-windows10.0.19041.0` with
WinUI 3 (`Microsoft.WindowsAppSDK`), so it cannot build or run on Linux.

- .NET 8 SDK (Windows). No `global.json` pins a specific version.
- This VM has no WSL distro. Use the Windows SDK at `$env:USERPROFILE\.dotnet\dotnet.exe`.
- Run from the repo root:
  - Full suite: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test Captcho.sln -c Debug`
  - Single project/file: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test captcho-ui.Tests/captcho-ui.Tests.csproj --filter "FullyQualifiedName~GeneralTabSettingsTests"`
- The build emits pre-existing `MSB3277` assembly-version warnings; these are not failures.
- C# diagnostics come from `dotnet build`/`dotnet test`, not an LSP.

### Tool Calling Rules
- Execute tool calls strictly using standard JSON function signatures.
- Never wrap function calls inside standard markdown text or prose unless invoked via the tool channel.
- Complete tool calls atomically in a single turn. Do not stop generation mid-arguments.
