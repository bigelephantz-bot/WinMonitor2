# Repository Guidelines

## Project Structure & Module Organization

The main .NET 10 WinForms application lives in `src/WinMonitor`. Keep code within the existing namespaces and folders: `Core` for sensor polling and statistics, `Config` for persisted settings, `Tray` for notification-area behavior, `UI` for forms and controls, and `Localization` for user-facing text. `tools/SensorDump` is a console diagnostic that references the main project. Hardware notes belong in `docs`; `dist` is generated publish output and should not be edited manually. Read `ARCHITECTURE.md` before changing module boundaries, polling, EC access, or UI threading. If you were asked to review this repository, read `docs/REVIEW_BRIEF.md` — it scopes the review and lists the deliberate decisions that must not be "fixed".

## Build, Test, and Development Commands

- `dotnet build .\src\WinMonitor\WinMonitor.csproj -c Debug`: restore dependencies and compile the app.
- `dotnet run --project .\src\WinMonitor\WinMonitor.csproj`: launch a development build.
- `dotnet run --project .\tools\SensorDump\SensorDump.csproj`: print detected sensors for diagnostic validation.
- `dotnet run --project .\tests\WinMonitor.Tests\WinMonitor.Tests.csproj -c Release`: run the package-free regression harness.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1`: create installed and portable Release builds under `dist`.

Use Windows 10/11 x64 with the .NET 10 SDK. Run elevated when validating CPU, SMART, or fan access.

**On the maintainer's machine the `dotnet` on `PATH` is runtime-only — `dotnet --list-sdks` prints
nothing and every command above fails.** Use the user-local SDK instead, substituting it for
`dotnet` in each command:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build .\src\WinMonitor\WinMonitor.csproj -c Debug
```

`publish.ps1` already resolves an SDK on its own (system, then user-local, then a workspace SDK
under `%TEMP%\dotnet-sdk-*`), so it runs as written.

The regression harness has **no per-test filter**: it runs a hardcoded array in
`CoreRegressionTests.cs`. To run one check in isolation, temporarily trim that array.

To smoke-test without a UAC prompt from a non-elevated shell (the manifest is `highestAvailable`):

```powershell
$env:__COMPAT_LAYER = "RunAsInvoker"
Start-Process .\src\WinMonitor\bin\Debug\net10.0-windows\WinMonitor.exe
```

Such a run is degraded by design — battery and CPU load only. Force-killing the app skips the
LibreHardwareMonitor driver unload and leaves an `R0WinMonitor` service until reboot (harmless;
`sc.exe delete R0WinMonitor` clears it).

## Coding Style & Naming Conventions

Use C# 12, nullable reference types, implicit usings, four-space indentation, file-scoped namespaces, and braces on new lines. Name types, methods, and properties in `PascalCase`; locals and parameters in `camelCase`; private fields as `_camelCase`. Split large WinForms partial classes by concern, following `SettingsForm.Alerts.cs`.

Keep comments in English. Route every visible string through `Loc.T("key")` and add both English and `zh-TW` entries. Store temperatures in Celsius and convert only for display. Avoid allocations in polling paths, marshal background sensor events to the UI thread, and dispose GDI/native handles.

## Testing Guidelines

The package-free regression harness lives in `tests/WinMonitor.Tests`; it covers core configuration, history, alert, and CSV behavior. Run it before publishing; `publish.ps1` runs it by default and accepts `-SkipTests` only for an intentional bypass. No coverage threshold exists. Manually exercise affected tray, settings, logging, localization, and sensor behavior; use `SensorDump` for hardware-facing changes. Name new tests `<Type>Tests.cs` with behavior-focused names such as `Load_CorruptConfig_UsesDefaults`.

## Commit & Pull Request Guidelines

Use short, imperative, scoped subjects, matching the existing history — for example `Core: handle missing sensor values`. Work happens on `agent/*` branches; `main` is the default branch. Pull requests should explain the behavior change, list validation commands and hardware/admin conditions, link relevant issues, and include screenshots for UI changes. Call out new localization keys and configuration-schema changes. Never commit local `config.json`, `crash.log`, build artifacts, secrets, or unsigned replacement drivers.

## Codex / Claude Code Collaboration

This section is the shared Git workflow for both agents; `CLAUDE.md` supplements it without
redefining it. Keep the build/test and architecture requirements above. Branch names and path
claims are coordination rules, not GitHub permissions or an automatic file lock.

### One owner, one task, one worktree

- Use `agent/codex/<unique-task>` or `agent/claude/<unique-task>` for new work. One agent session
  owns each branch and its worktree; only that owner edits, commits, pushes, or changes its history.
  Existing `agent/*` branches are valid legacy work: identify their owner and unmerged work before
  using them. Never rename, reset, delete, or repurpose another task's branch.
- Run each agent in a separate sibling worktree (or a separate clone on another machine), never
  in the same working directory. Do not bypass Git's same-branch checkout guard with `--force`.
  A worktree separates files, index, `bin`, `obj`, and `dist`; refs, repository config, and stash
  are still shared. Do not alter another worktree, use a shared stash as handoff, or change global
  Git configuration. Coordinate repository-wide config changes.
- Before editing, read both guidance files, inspect `git status --short --branch`,
  `git worktree list`, remote branches, and open PRs. Preserve unrelated local edits. A dirty
  checkout may remain untouched while a new clean worktree is created from fetched `origin/main`.
  Do not automatically stash, clean, or reset it. No open PR does not prove no unpublished work exists.
- The maintainer assigns the owner and exact paths before parallel work starts. Record the claim
  in the shared task conversation, then in the draft PR body after the first scoped commit.
  Check other active claims before starting or expanding scope; ask the maintainer to resolve any
  overlap. Without a confirmed non-overlapping assignment, work sequentially. Read-only review of
  the other agent's diff is fine; fixes go through the owner or a separately assigned successor.

### File and interface claims

Claim whole files, including planned new files, tests, and documentation. Different lines in the
same file are still an overlap. Also declare shared interfaces/configuration being changed: two
different files can conflict semantically. Agree the contract and merge order before implementation.

Frequent shared files need one writer at a time: `src/WinMonitor/Program.cs`,
`src/WinMonitor/Localization/Loc.cs`, `tests/WinMonitor.Tests/CoreRegressionTests.cs`,
`src/WinMonitor/Config/AppConfig.cs`, `src/WinMonitor/Config/ConfigStore.cs`, `ARCHITECTURE.md`,
`AGENTS.md`, `CLAUDE.md`, project files, `publish.ps1`, and `.github/workflows/ci.yml`.
The assigned owner integrates required localization pairs, test registrations, and schema changes;
the other task waits or hands over its proposed changes. Do not postpone those requirements to
after merging. Separate worktrees do not prevent merge conflicts, and hardware/config/driver
state is machine-wide: serialize elevated sensor tests and use isolated test configs.

Use this small coordination block in each PR body (keep it current; no central lock file):

```text
Owner: Codex | Claude Code (one session)
Branch / worktree: <branch> / <directory>
Base: main@<full SHA>
Owned paths: <exact paths, including tests/docs>
Shared contracts: <interfaces/config changes, or none>
Depends on / merge order: <PR numbers, or independent>
Status: active | handoff-ready | superseded | ready-for-review
Handoff: <source PR/branch + full SHA, recipient, or none>
Validation: <commands, results, environment; explicit pending/blocked checks>
Remaining work: <next steps, risks, hardware checks, or none>
```

### Start and publish a task

The examples use PowerShell from an existing clone with `origin` pointing to this repository.
Replace example task names and all `<...>` placeholders before running. Run commands individually and stop on failure;
Git failures do not automatically stop a PowerShell script. `gh` examples require an authenticated
GitHub CLI; the equivalent GitHub PR UI is fine.

```powershell
git status --short --branch
git worktree list
git fetch origin --prune
git branch -r
gh pr list --repo bigelephantz-bot/WinMonitor2 --state open
git worktree add --no-track -b agent/codex/history-fix ../WinMonitor2-codex-history origin/main
git worktree add --no-track -b agent/claude/tray-fix ../WinMonitor2-claude-tray origin/main
```

Start Codex and Claude Code in their respective directories only after the path assignments are
confirmed. From the owner's worktree, stage only assigned files, validate, commit, and push an
explicit branch (never rely on an upstream that might point at `main`):

```powershell
git branch --show-current
git rev-parse origin/main
git diff --check
git add -- <assigned-file-1> <assigned-file-2>
git diff --cached --check
git diff --cached --stat
git commit -m "Core: handle missing sensor values"
git push -u origin HEAD:refs/heads/agent/codex/history-fix
gh pr create --repo bigelephantz-bot/WinMonitor2 --base main --head agent/codex/history-fix --draft --title "Core: handle missing sensor values" --body-file ../history-pr.md
```

Create the body file with the coordination block and the normal PR description. Use the Claude
branch in its own worktree's commands. Do not use `git add .`, `git push --all`, or `--mirror`.

### Sequential handoff

1. The outgoing owner commits all intended work, pushes, and records the source PR/branch,
   **full commit SHA**, base SHA, claimed paths, decisions, validation results/environment, and
   remaining work in the PR body or shared task conversation. Mark it `handoff-ready` and stop
   writing. Uncommitted edits, stashes, and generated output are not a handoff.
2. The recipient fetches and verifies that the source ref equals the recorded SHA. If it differs,
   stop and reconcile with the outgoing owner. Create a **new recipient-owned branch** from that
   SHA; do not take over the source branch. Example, from the coordination clone:

   ```powershell
   git fetch origin
   git rev-parse origin/agent/codex/history-fix
   git worktree add --no-track -b agent/claude/history-followup ../WinMonitor2-claude-history <verified-full-handoff-SHA>
   ```

3. The maintainer confirms transfer of the path claim. The recipient acknowledges the SHA and
   next steps in the shared conversation/PR, rereads the guidance and diff, and validates before
   continuing. If the source is unmerged, publish a successor PR to `main` containing the complete
   change, link both PRs, and mark/close the source PR as `superseded` before continuing edits.
   Merge only the successor; retain the source branch until the successor is safely integrated.
   If the source was already merged (including squash), start from latest `origin/main` instead
   of the old source SHA. Do not replay already-merged commits.

### Synchronize and merge in order

- Independent tasks branch from `origin/main`. Do not stack branches or cherry-pick another
  active task by default. For dependencies or shared files, merge the prerequisite PR first,
  then synchronize the dependent branch with latest `origin/main`, reconcile its contract, and
  rerun the applicable build/regression/manual checks before it is reviewed and merged.
- Rebase only a branch whose commits have **never been handed off or used as another branch's
  base**, while the same owner still exclusively controls it. With a clean worktree, fetch,
  capture the current remote tip, then rebase. Disable automatic updates to other refs:

  ```powershell
  git status --porcelain
  git fetch origin
  $branch = git branch --show-current
  if ($branch -notlike 'agent/codex/*' -and $branch -notlike 'agent/claude/*') { throw 'Stop: not an owned agent branch' }
  $expected = git rev-parse "refs/remotes/origin/$branch"
  git merge-base --is-ancestor $expected HEAD
  if ($LASTEXITCODE -ne 0) { throw 'Stop: remote work is not incorporated locally' }
  git rebase --no-update-refs --no-autostash origin/main
  # Review the rebased diff and rerun applicable validation before pushing.
  git push "--force-with-lease=refs/heads/${branch}:$expected" origin "HEAD:refs/heads/$branch"
  ```

  `git status --porcelain` must return no output; otherwise stop and preserve the edits.
  Use this only for an already-published owned `agent/*` branch; for an unpublished branch, use
  a normal first push. A lease rejection means somebody changed the remote: stop, inspect, and
  coordinate. Never refresh the expected SHA merely to overwrite their work; never use plain
  `--force`. A published PR whose diff changes needs fresh review and CI.
- After handoff or when any task depends on the branch, preserve history instead: from the clean
  owner's worktree run `git fetch origin`, `git merge --no-edit origin/main`, revalidate, then
  `git push origin HEAD:refs/heads/<owned-agent-branch>`. Apply this to an unmerged handoff
  successor too, since it contains handed-off commits. Do not rebase, amend, or reset those commits.
- Resolve conflicts only in your branch, preserving both tasks' intended behavior. Do not select
  `ours`/`theirs` wholesale or skip commits just to make Git pass. If the contract is unclear,
  stop and coordinate; `git rebase --abort` / `git merge --abort` returns to the pre-operation state.
- The maintainer integrates one PR at a time, using **Squash and merge** into `main`. Agents may
  prepare/review PRs but never merge without a separate explicit instruction. Each next PR must
  include latest `origin/main` and pass fresh `build-and-test` CI after the previous merge.
  Manual hardware checks remain required where applicable; CI is not hardware validation.
  Record documentation-only checks as such; never claim Windows/hardware tests ran elsewhere.

### Protect main and retire completed work

Never commit/push directly to `main`, force-push it, delete it, bypass failed checks, or weaken its
protection. Local hooks and guidance are not server enforcement. At the 2026-09-30 inspection,
`main` was unprotected and there were no repository rulesets; editing this file does not enable them.

The maintainer should configure GitHub **Settings → Branches → Add branch protection rule** for
`main`: require a PR, require conversation resolution, require the existing **`build-and-test`**
check from GitHub Actions, require the branch to be up to date, apply the rule to administrators
(do not allow bypass), and leave force pushes and deletion disabled. Initially leave required
approvals at **0** if both agents and the maintainer use the same GitHub account: that account
cannot approve its own PR. The maintainer still reviews and chooses the merge. Once an independent
reviewer exists, require **1** approval and dismiss stale approvals. Agent names/branch prefixes
are not independent GitHub reviewers. Configure this through a separate authorized settings
change; a documentation PR cannot install branch protection.

After a squash merge, treat the old feature branch as finished. For new work, create a fresh branch
from latest `origin/main`, not the old feature tip. Only the owner removes a clean worktree after
the maintainer confirms merge/supersession and that no handoff depends on it:

```powershell
git -C ../WinMonitor2-codex-history status --porcelain
git worktree remove ../WinMonitor2-codex-history
```

Never force removal. Leave branch deletion to the owner/maintainer; `git branch -d` can reject a
squash-merged branch because its original commits are not ancestors of `main`. Do not turn that
rejection into an automatic `-D` or delete legacy/other-agent branches.
