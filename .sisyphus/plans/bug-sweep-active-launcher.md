# Active Launcher Bug Sweep

## TL;DR
> **Summary**: Audit and fix the active launcher/runtime bug surface in `src/` and directly consumed `lib/` code, with priority on version switching, package deployment, backup/restore, settings persistence, and correctness-critical notification/state bugs.
> **Deliverables**:
> - bug taxonomy and diagnostic hardening for install/switch flows
> - fixes across switcher/install, backup/restore, settings persistence, notification lifecycle, and network/update/injection readiness
> - targeted build verification plus agent-executed QA evidence for each workstream
> **Effort**: Large
> **Parallel**: YES - 2 waves
> **Critical Path**: 1 -> 2 -> 3 -> 4 -> 8

## Context
### Original Request
Create a plan to find and fix all bugs, scoped to the active launcher only.

### Interview Summary
- Scope is locked to active launcher/runtime paths only: `src/`, directly consumed `lib/`, current launcher behavior, version switching/install, backups, notifications, settings, logging, and update/network/injection readiness.
- Validation strategy is build + agent-executed manual QA. Do not add new automated test infrastructure during this bug sweep.
- Validation strategy is targeted builds + scriptable runtime/file/log verification. If a bug cluster has no scriptable entrypoint, the task must first refactor the logic into a callable method or bounded verification hook within the touched code instead of relying on hand-driven WPF clicks.
- `src/` is the default code-change and build target.
- Policy choice: if uninstall succeeds and reinstall fails, backup-based data recovery plus explicit failure guidance is acceptable for this sweep; automatic package rollback is out of scope.
- Policy choice: GDK backup/restore remains limited to `%AppData%\Minecraft Bedrock` unless a concrete additional state location is proven during implementation.

### Research Summary
- Highest-risk cluster is the switcher/install path: `src/Pages/SettingsSwitcherPage.xaml.cs`, `lib/Management/Versions/InstallRequest.cs`, `lib/Management/Versions/VersionCatalog.cs`, `lib/Core/Minecraft.cs`, `lib/Core/MinecraftUWP.cs`, and `lib/Core/MinecraftGDK.cs`.
- Backup/restore has layout drift and truthfulness risks in `src/Handlers/Managers/BackupManager.cs`, `src/Pages/SettingsBackupPage.xaml.cs`, `src/Styles/BackupItem.xaml.cs`, and `src/Handlers/MinecraftGame/MinecraftGame.Backups.cs`.
- Notifications and launcher state concentrate in `src/MainWindow.xaml.cs` and `src/Styles/MessageBox.xaml.cs`.
- Settings persistence concentrates in `src/Settings.cs`, `src/Pages/SettingsGeneralPage.xaml.cs`, `src/Pages/SettingsVersionPage.xaml.cs`, and save-on-close logic in `src/MainWindow.xaml.cs`.
- Validation is `dotnet build` / `msbuild`; there is no supported automated unit/integration test suite or single-test command, and `test/` is stale.

### Oracle Review (gaps addressed)
- Add a bounded QA matrix for admin vs non-admin, UWP vs GDK, installed vs not installed, game running vs closed, and online vs offline.
- Require explicit recovery/logging acceptance criteria: no false-success messaging, no silent data loss, and every failure path logs structured deployment data.
- Prevent scope creep into dormant areas (`app/`, `test/`, CI, broad refactors, or cosmetic-only cleanup).

### Metis Review (gaps addressed)
- Treat workstreams as end-to-end user journeys, not isolated file buckets.
- Include adjacent settings writers/readers when validating persistence.
- Require restart-based verification for persistence bugs and repeated-action verification for transient UI bugs.

## Work Objectives
### Core Objective
Produce a decision-complete implementation sequence that discovers, fixes, and verifies correctness-critical bugs in the active launcher/runtime paths without drifting into inactive repo areas or new test-infrastructure work.

### Deliverables
- A structured bug-hunt and fix sequence across five workstreams: switcher/install, backup/restore, settings persistence, notification/error UX, and network/update/injection resilience.
- Structured deployment diagnostics and failure messaging for Windows package operations.
- Evidence-backed validation using targeted builds, launcher QA runs, and captured logs/artifacts.

### Definition of Done
- `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after each touched `src` workstream.
- `dotnet build "lib/Flarial.Launcher.Services.csproj" -c Release` succeeds whenever `lib/` runtime/install/network code changes.
- `msbuild -t:Restore "src/Flarial.Launcher.csproj"` and `msbuild /p:Configuration=Release /m "src/Flarial.Launcher.csproj"` succeed in the final verification wave.
- Every addressed bug class has at least one happy-path QA artifact and one failure-path QA artifact in `.sisyphus/evidence/`.
- Switcher/install failures log or surface `ActivityId`, HRESULT/extended error, and actionable remediation when deployment fails.

### Must Have
- Bug discovery and fix sequencing anchored to actual high-risk files.
- Structured failure handling for package deployment and version switching.
- Backup/restore correctness with no false success toasts.
- Settings persistence verified across restart.
- Notification lifecycle correctness verified under repeated use.

### Must NOT Have
- No work on `app/`, `test/`, CI workflows, or test-infrastructure creation unless strictly required for consumer verification.
- No architecture rewrites, no MVVM migration, and no cosmetic-only polish disconnected from bug fixes.
- No acceptance criteria that depend on human-only judgment.

## Verification Strategy
> ZERO HUMAN INTERVENTION - all verification is agent-executed.
- Test decision: build + scriptable runtime/file/log QA only; no new automated test infra.
- QA policy: every task includes at least one happy path and one failure/edge scenario.
- Evidence: `.sisyphus/evidence/task-{N}-{slug}.{ext}`
- Runtime QA rule: do not write acceptance steps that require a human to click WPF UI. Use build output, logs, filesystem/package state, PowerShell/package queries, launcher command-line entrypoints, or task-local refactors that expose the behavior to a callable path.
- Core QA matrix:
  - admin vs non-admin
  - UWP vs GDK
  - installed game vs clean/not installed
  - game running vs closed
  - online vs offline / degraded feed
  - valid backup vs missing/corrupt backup

## Execution Strategy
### Parallel Execution Waves
Wave 1: instrumentation and correctness foundations (switcher/install diagnostics, backup truthfulness, notification lifecycle, settings persistence baseline, version source/catalog hardening)

Wave 2: dependent bug sweeps and regression closure (package replacement flow, network/update resilience, end-to-end launcher-state hardening)

### Dependency Matrix
- 1 blocks 2, 3, 4, and 8 because install diagnostics and switcher state rules must be standardized first.
- 2 depends on 1 because package replacement/remediation messages need structured deployment evidence.
- 3 depends on 1 because backup UX must align with switcher/install failure semantics.
- 4 depends on 1 because notification correctness must use the agreed error/message contract.
- 5 can run in parallel with 1 but must finish before final QA for settings-related regressions.
- 6 can run in parallel with 1; it informs both 2 and 8.
- 7 can run after 1 in parallel with 3/4/5.
- 8 depends on 1, 2, and 5 because launcher-state resilience crosses install, persistence, and network/update flows.

### Agent Dispatch Summary
- Wave 1 -> 5 tasks -> `deep`, `unspecified-high`, `quick`
- Wave 2 -> 3 tasks -> `deep`, `unspecified-high`
- Final verification -> 4 tasks -> `oracle`, `unspecified-high`, `deep`

## TODOs
> Implementation + Test = ONE task. Never separate.
> EVERY task MUST have: Agent Profile + Parallelization + QA Scenarios.

- [ ] 1. Standardize Switcher Deployment Diagnostics And State Control

  **What to do**: Consolidate switcher install state management and deployment diagnostics in the active launcher. Ensure the switcher cannot be re-entered during install/remove/verify work, all deployment failures capture structured Windows deployment data, and known HRESULT buckets produce deterministic launcher-side states and messages.
  **Must NOT do**: Do not redesign the switcher UI layout beyond what is required for correctness. Do not add new installer technologies or background services.

  **Recommended Agent Profile**:
  - Category: `deep` - Reason: Cross-file async/package-deployment flow with high user impact.
  - Skills: `[]` - Existing repo and platform APIs are sufficient.
  - Omitted: `playwright` - UI browser automation is not relevant to WPF desktop verification.

  **Parallelization**: Can Parallel: NO | Wave 1 | Blocks: 2, 3, 4, 8 | Blocked By: none

  **References**:
  - Pattern: `src/Pages/SettingsSwitcherPage.xaml.cs` - current switcher install/remove/verify flow and user messaging.
  - Pattern: `lib/Management/Versions/InstallRequest.cs` - package download/deploy progress, retries, and timeout flow.
  - API/Type: `lib/Core/Minecraft.cs` - installed-package detection, platform, version, and removal abstraction.
  - Logging: `src/Logger.cs` - launcher file logging surface.
  - Startup diagnostics: `src/App.xaml.cs` - startup and unhandled exception logging pattern.
  - External: `https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.deploymentresult?view=winrt-22621` - required deployment diagnostics.

  **Acceptance Criteria**:
  - [ ] `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after the diagnostics/state changes.
  - [ ] Known deployment failures log HRESULT, `ActivityId`, and deployment text/details in launcher logs or an equivalent structured error surface.
  - [ ] While install/remove/verify is active, the switcher cannot trigger a second install or leave controls in a permanently disabled state.

  **QA Scenarios** (MANDATORY - task incomplete without these):
  ```text
  Scenario: Normal switcher install state progression
    Tool: Bash
    Steps: Execute a scriptable verification path produced by this task (for example a refactored callable method or deterministic command path) that drives one switch lifecycle without manual UI interaction, then inspect launcher logs and resulting state artifacts.
    Expected: Busy state begins once, progresses through backup/install/verify, and always returns to an enabled/idle state after success or failure.
    Evidence: .sisyphus/evidence/task-1-switcher-diagnostics.txt

  Scenario: Deployment failure captures structured diagnostics
    Tool: Bash
    Steps: Trigger a known failing install condition through the same scriptable path (non-admin or conflicting package state), then inspect `C:\Users\diamond261\AppData\Local\Flarial\Launcher\Logs\launcher.log`.
    Expected: Failure message is actionable and logs include HRESULT bucket plus deployment correlation data.
    Evidence: .sisyphus/evidence/task-1-switcher-diagnostics-error.txt
  ```

  **Commit**: YES | Message: `fix(switcher): standardize deployment diagnostics and busy state` | Files: `src/Pages/SettingsSwitcherPage.xaml.cs`, `lib/Management/Versions/InstallRequest.cs`, `lib/Core/Minecraft.cs`, `src/Logger.cs`

- [ ] 2. Repair Package Replacement And Version Switching Semantics

  **What to do**: Make package replacement behavior explicit and safe for UWP and GDK. Define and implement uninstall-before-install behavior, downgrade/upgrade rules, reinstall semantics, post-install verification, and remediation messaging for conflicting or newer installed packages.
  **Policy**: If uninstall succeeds and reinstall fails, backup-based data recovery plus actionable failure guidance is acceptable for this sweep. Automatic package rollback is not required.
  **Must NOT do**: Do not silently discard backups or leave the machine without recovery guidance if uninstall succeeds but install fails.

  **Recommended Agent Profile**:
  - Category: `deep` - Reason: Platform-specific package sequencing and recovery semantics.
  - Skills: `[]` - Windows packaging APIs are already in use in the repo.
  - Omitted: `frontend-ui-ux` - This is correctness-first, not presentation-first.

  **Parallelization**: Can Parallel: NO | Wave 2 | Blocks: 8 | Blocked By: 1

  **References**:
  - Pattern: `src/Pages/SettingsSwitcherPage.xaml.cs` - current backup/remove/install/verify orchestration.
  - API/Type: `lib/Core/Minecraft.cs` - current package identity and removal entry point.
  - Pattern: `lib/Core/MinecraftUWP.cs` - UWP launch assumptions.
  - Pattern: `lib/Core/MinecraftGDK.cs` - GDK activation/watch assumptions.
  - API/Type: `lib/Management/Versions/VersionEntry.cs` - shared install abstraction.
  - External: `https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting` - downgrade/conflict/dependency HRESULT buckets.

  **Acceptance Criteria**:
  - [ ] `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after package replacement changes.
  - [ ] Switching from an installed version to another supported version follows one documented recovery-safe sequence for both UWP and GDK.
  - [ ] If install fails after removal, the UI/logs clearly explain the failure and the recovery path instead of claiming success.

  **QA Scenarios** (MANDATORY - task incomplete without these):
  ```text
  Scenario: Replace current installed version with selected version
    Tool: Bash
    Steps: Use the scriptable switch path established by Tasks 1-2 to perform backup -> remove -> install -> verify for one selected target, then query the installed package state through code/logs/PowerShell.
    Expected: Selected target version/platform becomes active, verification succeeds, and logs/state reflect the selected version.
    Evidence: .sisyphus/evidence/task-2-package-replacement.txt

  Scenario: Removal succeeds but install fails
    Tool: Bash
    Steps: Force an install failure after removal through a controlled dependency/network/package conflict condition, then inspect logs, backup artifacts, and resulting package state.
    Expected: No false success toast; recovery guidance and failure diagnostics are shown.
    Evidence: .sisyphus/evidence/task-2-package-replacement-error.txt
  ```

  **Commit**: YES | Message: `fix(switcher): make package replacement flow recoverable` | Files: `src/Pages/SettingsSwitcherPage.xaml.cs`, `lib/Core/Minecraft.cs`, `lib/Core/MinecraftUWP.cs`, `lib/Core/MinecraftGDK.cs`

- [ ] 3. Make Backup And Restore Truthful, Consistent, And Recoverable

  **What to do**: Normalize backup creation, archive listing, restore, delete, and user messaging so the launcher never reports success on partial or failed backup/restore work. Preserve compatibility with both current zip-based switcher backups and legacy layouts only where they are actually supported.
  **Policy**: GDK backup/restore stays limited to `%AppData%\Minecraft Bedrock` unless implementation uncovers a concrete additional state location that is required for correctness.
  **Must NOT do**: Do not leave optimistic UI removal or unconditional “Backup loaded.” messaging in place. Do not change backup storage roots away from the launcher data directory.

  **Recommended Agent Profile**:
  - Category: `unspecified-high` - Reason: File IO, archive handling, and user-visible recovery semantics.
  - Skills: `[]` - Repo patterns are sufficient.
  - Omitted: `oracle` - Architecture review already captured; this is implementation-focused.

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: 8 | Blocked By: 1

  **References**:
  - Pattern: `src/Handlers/Managers/BackupManager.cs` - new and legacy backup layouts, zip handling, restore, and delete logic.
  - Pattern: `src/Pages/SettingsBackupPage.xaml.cs` - backup list population.
  - Pattern: `src/Styles/BackupItem.xaml.cs` - per-item restore/delete UX.
  - Legacy: `src/Handlers/MinecraftGame/MinecraftGame.Backups.cs` - older backup descriptors and drift clues.
  - Root path: `src/Handlers/Managers/VersionManagement.cs` - launcher data root.

  **Acceptance Criteria**:
  - [ ] `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after backup fixes.
  - [ ] Backup create/load/delete only show success after the underlying operation actually succeeds.
  - [ ] Zip backups, legacy backups, and missing/corrupt backup cases each have deterministic results and user messaging.

  **QA Scenarios** (MANDATORY - task incomplete without these):
  ```text
  Scenario: Create and restore a valid backup
    Tool: Bash
    Steps: Invoke backup create/load through callable backup-manager paths or a bounded verification hook, then inspect created zip files and restored target directories.
    Expected: Backup is listed, restore completes successfully, and only a true success path shows a success toast.
    Evidence: .sisyphus/evidence/task-3-backup-truthful.txt

  Scenario: Missing or corrupt backup archive
    Tool: Bash
    Steps: Rename or corrupt a backup archive in the launcher backups directory, then invoke restore from the Backups UI.
    Expected: Restore fails gracefully, the backup item is not falsely removed, and no false “Backup loaded.” message is shown.
    Evidence: .sisyphus/evidence/task-3-backup-truthful-error.txt
  ```

  **Commit**: YES | Message: `fix(backups): make backup lifecycle truthful and recoverable` | Files: `src/Handlers/Managers/BackupManager.cs`, `src/Pages/SettingsBackupPage.xaml.cs`, `src/Styles/BackupItem.xaml.cs`

- [ ] 4. Stabilize Notification Lifecycle And Error UX Contracts

  **What to do**: Make transient notifications deterministic under rapid reuse, ensure dismissal/animation/removal always complete, and standardize when errors should use transient toast vs blocking dialog vs log-only reporting.
  **Must NOT do**: Do not turn this into a visual redesign. Do not remove required user-facing failure messages while “cleaning up” duplication.

  **Recommended Agent Profile**:
  - Category: `quick` - Reason: Focused UI-state correctness with limited file count.
  - Skills: `[]` - Existing WPF/XAML patterns are sufficient.
  - Omitted: `frontend-ui-ux` - polish is out of scope unless required for correctness.

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: 8 | Blocked By: 1

  **References**:
  - Pattern: `src/Styles/MessageBox.xaml.cs` - auto-dismiss, animation, and visual-tree removal logic.
  - Pattern: `src/Styles/MessageBox.xaml` - toast animation shell.
  - Pattern: `src/MainWindow.xaml.cs` - central `CreateMessageBox(...)` usage and launcher-wide notification entry point.
  - Pattern: `src/App.xaml.cs` - crash dialog behavior for fatal failures.

  **Acceptance Criteria**:
  - [ ] `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after notification fixes.
  - [ ] Repeated notifications do not leave orphaned visual elements or overlapping stale toasts.
  - [ ] Time-based dismissal, manual close, and failure-triggered notification flows all restore the visual tree cleanly.

  **QA Scenarios** (MANDATORY - task incomplete without these):
  ```text
  Scenario: Rapid repeated transient messages
    Tool: Bash
    Steps: Trigger multiple notification-producing code paths through callable methods or deterministic scriptable triggers created/refactored in the task.
    Expected: Each toast animates in, dismisses, and disappears within the configured timeout without leaving blank spacing or stuck elements.
    Evidence: .sisyphus/evidence/task-4-notification-lifecycle.txt

  Scenario: Manual close during auto-dismiss window
    Tool: Bash
    Steps: Invoke both auto-dismiss and explicit dismiss paths through callable notification methods within the timeout window.
    Expected: Only one close path runs, animation completes once, and no exception or orphaned control remains.
    Evidence: .sisyphus/evidence/task-4-notification-lifecycle-error.txt
  ```

  **Commit**: YES | Message: `fix(ui): stabilize transient notification lifecycle` | Files: `src/Styles/MessageBox.xaml`, `src/Styles/MessageBox.xaml.cs`, `src/MainWindow.xaml.cs`

- [ ] 5. Audit And Repair Settings Persistence Boundaries

  **What to do**: Audit all active persisted settings used by launcher behavior, verify `[DataMember]` coverage and default initialization, and eliminate bugs where settings appear to change in UI but are not reliably persisted or re-applied on restart.
  **Must NOT do**: Do not add unrelated new settings or broad settings-page redesign. Do not assume save-on-close alone is sufficient without proving restart persistence.

  **Recommended Agent Profile**:
  - Category: `unspecified-high` - Reason: Persistence boundaries span model, runtime readers, and UI writers.
  - Skills: `[]` - Repository patterns are sufficient.
  - Omitted: `quick` - Cross-file persistence and restart validation is too broad for a trivial fix pass.

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: 8 | Blocked By: none

  **References**:
  - API/Type: `src/Settings.cs` - serialized settings model, load path, defaults, and save behavior.
  - Pattern: `src/Pages/SettingsGeneralPage.xaml.cs` - active settings writers and runtime toggle interactions.
  - Pattern: `src/Pages/SettingsVersionPage.xaml.cs` - DLL list serialization/persistence.
  - Pattern: `src/MainWindow.xaml.cs` - runtime readers and save on close.

  **Acceptance Criteria**:
  - [ ] `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after persistence fixes.
  - [ ] Each active setting under bug-sweep scope persists across full launcher restart or is explicitly classified as runtime-only by design.
  - [ ] A broken/partial settings file does not silently cause unrelated fields to reset without at least a log signal.

  **QA Scenarios** (MANDATORY - task incomplete without these):
  ```text
  Scenario: Persist active settings across restart
    Tool: Bash
    Steps: Modify persisted settings through the real serialization/write path, fully relaunch the launcher process, and inspect saved files plus startup/runtime logs or computed state.
    Expected: Intended fields persist and runtime behavior matches the saved values.
    Evidence: .sisyphus/evidence/task-5-settings-persistence.txt

  Scenario: Corrupt settings input
    Tool: Bash
    Steps: Back up then corrupt the persisted settings file, relaunch the launcher, and inspect logs plus resulting defaults.
    Expected: Launcher recovers deterministically, logs the issue, and does not silently misreport persistence success.
    Evidence: .sisyphus/evidence/task-5-settings-persistence-error.txt
  ```

  **Commit**: YES | Message: `fix(settings): harden persistence and restart behavior` | Files: `src/Settings.cs`, `src/Pages/SettingsGeneralPage.xaml.cs`, `src/Pages/SettingsVersionPage.xaml.cs`, `src/MainWindow.xaml.cs`

- [ ] 6. Harden Version Feed, Catalog, And Normalization Failures

  **What to do**: Audit remote version feeds, parsing assumptions, version normalization, sort ordering, duplicate handling, and partial-source failure behavior so the switcher remains usable and truthful when one or more remote sources drift or degrade.
  **Must NOT do**: Do not add speculative new feed providers. Do not silently swallow fetch/parse failures without logging.

  **Recommended Agent Profile**:
  - Category: `unspecified-high` - Reason: External schema drift and normalization bugs have already surfaced in active behavior.
  - Skills: `[]` - Existing version-management code is the main source of truth.
  - Omitted: `librarian` - Documentation is already gathered; implementation work is internal.

  **Parallelization**: Can Parallel: YES | Wave 1 | Blocks: 2, 8 | Blocked By: none

  **References**:
  - Pattern: `lib/Management/Versions/VersionCatalog.cs` - source merge, support matching, and sort behavior.
  - Pattern: `lib/Management/Versions/UWPVersionEntry.cs` - UWP source resolution.
  - Pattern: `lib/Management/Versions/GDKVersionEntry.cs` - GDK source resolution.
  - Pattern: `lib/Networking/HttpService.cs` - download/fetch integrity behavior.
  - UI consumer: `src/Pages/SettingsSwitcherPage.xaml.cs` - switcher list population and latest-version assumptions.

  **Acceptance Criteria**:
  - [ ] `dotnet build "lib/Flarial.Launcher.Services.csproj" -c Release` succeeds.
  - [ ] `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after catalog/source changes.
  - [ ] When one feed fails or returns malformed/partial data, the switcher remains stable, logs the problem, and shows surviving data without duplicate or misleading latest-version claims.

  **QA Scenarios** (MANDATORY - task incomplete without these):
  ```text
  Scenario: Normal feed load for UWP and GDK
    Tool: Bash
    Steps: Invoke catalog loading and switcher data preparation through callable paths, then inspect launcher logs and computed list outputs.
    Expected: UWP and GDK lists load in stable order with correct latest-version labeling and no duplicate-key failures.
    Evidence: .sisyphus/evidence/task-6-version-catalog.txt

  Scenario: Feed degradation or malformed source
    Tool: Bash
    Steps: Simulate one unavailable or malformed source through controlled network conditions or a temporary interception layer, then reopen Switcher.
    Expected: Page stays usable, failure is logged, and surviving source data still renders without crashing.
    Evidence: .sisyphus/evidence/task-6-version-catalog-error.txt
  ```

  **Commit**: YES | Message: `fix(versions): harden catalog and source resilience` | Files: `lib/Management/Versions/VersionCatalog.cs`, `lib/Management/Versions/UWPVersionEntry.cs`, `lib/Management/Versions/GDKVersionEntry.cs`, `src/Pages/SettingsSwitcherPage.xaml.cs`

- [ ] 7. Stabilize Network, Client Update, And Self-Update Failure Paths

  **What to do**: Audit and fix failure-path correctness for HTTP/download integrity, proxy/DoH behavior, client DLL update flow, and self-update lock/rollback handling so launcher state is truthful and recoverable under partial downloads or locked-file conditions.
  **Must NOT do**: Do not replace the updater architecture. Do not widen scope into general performance work.

  **Recommended Agent Profile**:
  - Category: `unspecified-high` - Reason: Mixed network, file-system, and process-state failure handling.
  - Skills: `[]` - Existing networking/updater code is self-contained.
  - Omitted: `oracle` - Architecture review already captured the scope guardrails.

  **Parallelization**: Can Parallel: YES | Wave 2 | Blocks: none | Blocked By: 1

  **References**:
  - Pattern: `lib/Networking/HttpService.cs` - request and download integrity logic.
  - Pattern: `lib/Networking/HttpServiceHandler.cs` - proxy and DoH behavior.
  - Pattern: `lib/Client/FlarialClient.cs` - client update/download/injection orchestration.
  - Pattern: `lib/Management/LauncherUpdater.cs` - self-update temp EXE/CMD replacement logic.
  - UI consumer: `src/MainWindow.xaml.cs` - launch/update status surfaces.

  **Acceptance Criteria**:
  - [ ] `dotnet build "lib/Flarial.Launcher.Services.csproj" -c Release` succeeds.
  - [ ] `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after network/update changes.
  - [ ] Partial download, proxy failure, and locked-file update cases each fail with truthful UI and log output instead of silent or misleading success.

  **QA Scenarios** (MANDATORY - task incomplete without these):
  ```text
  Scenario: Healthy update/download path
    Tool: Bash
    Steps: Invoke client update/download and updater paths through scriptable entrypoints, then inspect logs, downloaded files, and final process/file state.
    Expected: Download and update status are truthful, and successful flows end in a consistent launcher-ready state.
    Evidence: .sisyphus/evidence/task-7-network-update.txt

  Scenario: Partial download or locked output
    Tool: Bash
    Steps: Simulate interrupted download or keep a target file locked during updater/client replacement.
    Expected: Operation fails clearly, rollback/retry guidance is shown, and no false-ready state remains.
    Evidence: .sisyphus/evidence/task-7-network-update-error.txt
  ```

  **Commit**: YES | Message: `fix(update): harden network and self-update failure paths` | Files: `lib/Networking/HttpService.cs`, `lib/Networking/HttpServiceHandler.cs`, `lib/Client/FlarialClient.cs`, `lib/Management/LauncherUpdater.cs`, `src/MainWindow.xaml.cs`

- [ ] 8. Run Cross-Workstream Regression Closure For Active Launcher Journeys

  **What to do**: Execute a final active-launcher bug hunt across the integrated user journeys after tasks 1-7 land. Focus on state restoration, version label correctness, launcher close/restart behavior, backup recovery after failed switch, notification overlap, and launch/injection readiness after settings or package changes.
  **Must NOT do**: Do not add new feature work discovered during QA unless it is a real bug in the active launcher journey. Spin any optional enhancement out of scope.

  **Recommended Agent Profile**:
  - Category: `deep` - Reason: End-to-end regression pass spanning all major workstreams.
  - Skills: `[]` - Repository-local behavior is the focus.
  - Omitted: `quick` - Cross-journey regression closure requires broader reasoning.

  **Parallelization**: Can Parallel: NO | Wave 2 | Blocks: Final Verification Wave | Blocked By: 1, 2, 3, 4, 5, 6, 7

  **References**:
  - Pattern: `src/MainWindow.xaml.cs` - launcher state, status text, tray/save/close flow, launch orchestration.
  - Pattern: `src/Pages/SettingsPage.xaml.cs` - settings shell and lazy loading.
  - Pattern: `src/Pages/SettingsSwitcherPage.xaml.cs` - end-to-end switcher flow.
  - Pattern: `src/Pages/SettingsBackupPage.xaml.cs` - backup page refresh.
  - Pattern: `src/Settings.cs` - persisted state boundaries.

  **Acceptance Criteria**:
  - [ ] `dotnet build "src/Flarial.Launcher.csproj" -c Debug` succeeds after regression fixes.
  - [ ] No active-launcher journey under the QA matrix leaves the app in a false-success, permanently-disabled, or inconsistent-version-label state.
  - [ ] Final release-parity validation succeeds with `msbuild -t:Restore "src/Flarial.Launcher.csproj"` and `msbuild /p:Configuration=Release /m "src/Flarial.Launcher.csproj"`.

  **QA Scenarios** (MANDATORY - task incomplete without these):
  ```text
  Scenario: Integrated active launcher journey
    Tool: Bash
    Steps: Execute a scripted end-to-end verification sequence using the callable paths and runtime hooks produced in prior tasks, then inspect final state/logs/artifacts across switch, backup, restart, persistence, and launch readiness.
    Expected: State remains consistent across the entire journey with correct version labels, enabled controls, and truthful messages.
    Evidence: .sisyphus/evidence/task-8-regression-closure.txt

  Scenario: Failure-path regression matrix
    Tool: Bash
    Steps: Repeat selected scripted journeys under non-admin, offline, corrupt-backup, and conflicting-package conditions.
    Expected: Every failure path surfaces a bounded, actionable message and leaves the launcher recoverable.
    Evidence: .sisyphus/evidence/task-8-regression-closure-error.txt
  ```

  **Commit**: YES | Message: `fix(launcher): close regressions across active journeys` | Files: `src/MainWindow.xaml.cs`, `src/Pages/SettingsPage.xaml.cs`, `src/Pages/SettingsSwitcherPage.xaml.cs`, `src/Pages/SettingsBackupPage.xaml.cs`, `src/Settings.cs`

## Final Verification Wave (4 parallel agents, ALL must APPROVE)
- [ ] F1. Plan Compliance Audit - oracle
- [ ] F2. Code Quality Review - unspecified-high
- [ ] F3. Scriptable Runtime QA - unspecified-high
- [ ] F4. Scope Fidelity Check - deep

## Commit Strategy
- Keep commits scoped by workstream.
- Prefer one commit per task unless a task naturally splits into a safe diagnostics-first commit and a follow-up correctness commit.
- Do not mix unrelated UI polish with install/backup/state correctness changes.

## Success Criteria
- Active launcher flows stop surfacing raw/ambiguous failures for known bug buckets.
- Bug-prone flows (switch/install, backup/restore, settings persistence, notifications, network/update/injection) are validated with artifacts and targeted builds.
- No critical bug class in active paths remains without explicit diagnosis, acceptance criteria, and remediation sequencing.
