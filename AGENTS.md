# AGENTS.md

## Repo Overview
- This repository contains a Windows launcher for Flarial Client.
- `src/` is the main WPF launcher used most often in current work and should be the default place for code changes unless the task clearly targets another project.
- `lib/` contains shared services, native/platform code, networking, version management, and installation logic.
- `app/` is a second launcher executable project with stricter compiler settings.
- `test/` is not an automated test suite; it is a manual WinForms harness and is currently stale.

## Existing Agent Rules
- No Cursor or Copilot instruction files were found (`.cursorrules`, `.cursor/rules/`, `.github/copilot-instructions.md`).
- Do not assume hidden repo rules exist; use this file plus nearby code patterns.

## Tooling And Environment
- The repo has no `.sln`; work directly against individual `csproj` files.
- `src/global.json` pins SDK `8.0.407` for `dotnet` commands run under `src/`.
- Root-level `dotnet build "app/..."` and `dotnet build "lib/..."` are not guaranteed to use that SDK pin.
- Target framework is `.NET Framework 4.8.1` (`net481`).
- WPF is used in `src/` and `app/`; WinForms is also enabled in places.
- `lib/` enables nullable and treats warnings as errors.
- `app/` also treats warnings as errors.
- `src/` is more permissive and contains most UI code-behind.

## Important Paths
- `src/Flarial.Launcher.csproj` - primary launcher build target.
- `app/Flarial.Launcher.csproj` - secondary launcher build target.
- `lib/Flarial.Launcher.Services.csproj` - shared services library.
- `src/App.xaml` - merged resource dictionaries and app startup resources.
- `src/MainWindow.xaml.cs` - main window behavior and many repo-wide patterns.
- `src/Pages/` - WPF pages, mostly code-behind driven.
- `src/Styles/` - custom controls and reusable XAML styles.
- `.github/workflows/autoupdater.yml` - closest thing to canonical CI build instructions.

## Build Commands
- Default build target for normal work: `src/Flarial.Launcher.csproj`.
- Restore + build the main launcher the way CI does:
```bash
msbuild -t:Restore "src/Flarial.Launcher.csproj"
msbuild /p:Configuration=Release /m "src/Flarial.Launcher.csproj"
```
- Local build of the main launcher:
```bash
dotnet build "src/Flarial.Launcher.csproj" -c Debug
dotnet build "src/Flarial.Launcher.csproj" -c Release
```
- Local build of the secondary app executable:
```bash
dotnet build "app/Flarial.Launcher.csproj" -c Release
```
- Only build `app/` when the work specifically targets `app/` or you need to verify the secondary executable.
- Local build of the shared library:
```bash
dotnet build "lib/Flarial.Launcher.Services.csproj" -c Release
```
- Broad validation order when shared code changes:
```bash
dotnet build "lib/Flarial.Launcher.Services.csproj" -c Release
dotnet build "src/Flarial.Launcher.csproj" -c Debug
dotnet build "app/Flarial.Launcher.csproj" -c Release
```
- Prefer targeted validation for normal work:
  - `src/`-only changes: build `src`.
  - `app/`-only changes: build `app`.
  - `lib/` changes: build `lib` and at least one consumer (`src` or `app`).

## Test Commands
- There is no working automated unit/integration test setup in this repository.
- There is no `dotnet test` target and no xUnit/NUnit/MSTest package configuration.
- There is no supported single-test command.
- If an agent is asked to run “a single test”, explain that the repo does not currently expose per-test execution.

## Manual Test Harness
- The `test/` project is a manual WinForms harness, not a test runner.
- Closest manual run command:
```bash
dotnet run --project "test/Flarial.Launcher.Client.csproj" -c Debug
```
- Current status: this project is stale and does not build cleanly because it references a missing project under `../deps/...`.
- Do not promise this harness works unless you personally rebuild and confirm it.

## Lint / Static Checks
- No dedicated lint command or formatter config was found.
- Use `dotnet build` as the main validation check.
- Treat `app/` and `lib/` as stricter than `src/` because they enable warnings-as-errors.
- If you touch `lib/`, keep nullable and compiler warnings clean.

## Known Build Caveats
- Builds can fail if the launcher executable is currently running and locking `bin/Release`.
- Antivirus/file protection can temporarily lock outputs in `obj/` or `bin/`.
- If `src` Release build fails with file lock, close the running launcher and rebuild.
- Do not edit `bin/` or `obj/` outputs manually.

## Architecture Conventions
- Prefer `src/` patterns for launcher UI work.
- Prefer `lib/` patterns for shared services, networking, platform logic, and install/version code.
- `src/Pages` is code-behind driven; do not introduce a new MVVM framework unless explicitly requested.
- `src/Styles` is the right place for reusable controls, templates, and visual behaviors.
- `Settings` state is centralized in `src/Settings.cs` and serialized with data contracts.

## C# Style
- Match the local file style instead of refactoring unrelated code.
- Most active files use file-scoped namespaces:
```csharp
namespace Flarial.Launcher.Pages;
```
- Some older files still use block-scoped namespaces; preserve that style in-place.
- Use modern C# syntax already present in the repo: target-typed `new()`, collection expressions `[]`, pattern matching, and concise expression-bodied members when they improve readability.
- Do not introduce `var` everywhere blindly; follow nearby code.
- Prefer small helper methods for repeated logic.

## Naming Conventions
- Types, methods, properties, XAML names, and events use PascalCase.
- Private instance fields usually use `_camelCase`.
- Static readonly fields usually use `s_camelCase`.
- Dependency properties follow the normal WPF `NameProperty` pattern.
- Event handlers are commonly named like `Button_Click`, `Window_ContentRendered`, `VersionsList_MouseDoubleClick`.

## Async / Threading Conventions
- Use `async Task` for real asynchronous work.
- Use `async void` only for UI event handlers.
- Marshal UI updates with `Dispatcher.Invoke` or `Dispatcher.InvokeAsync`.
- Wrap blocking IO/native-heavy work in `Task.Run` when used from UI code.
- Use `finally` to restore UI state after async operations when the UI changes during work.

## Error Handling
- In UI code, catch exceptions, log them, and show a user-facing message when the action fails.
- Existing pattern:
```csharp
catch (Exception ex)
{
    Logger.Error("Meaningful context", ex);
    MainWindow.CreateMessageBox($"Action failed: {ex.Message}");
}
```
- In infrastructure code, narrow catches are preferred when the failure mode is known.
- Some low-level/startup/media paths intentionally swallow exceptions for resilience; only follow that pattern when failure is genuinely non-critical.
- Do not add empty catches in new code without a reason.

## Nullability / Types
- `lib/` has nullable enabled; keep null handling explicit there.
- `src/` is less strict; still prefer explicit guards before dereferencing UI state.
- Prefer small DTO/view-model-like inner classes only when a page needs them.

## XAML / WPF Conventions
- Use named elements and code-behind event hooks; that is the established pattern here.
- Inline `ControlTemplate`, `Storyboard`, and `Style` usage is common.
- Reusable visual styles belong in `src/Styles/*.xaml` or `App.xaml` merged dictionaries.
- Keep fonts and visual language consistent with the existing app, especially `Space Grotesk` and dark surfaces with red accents.
- Do not move a page to a different architectural pattern unless the user asks for that refactor.

## Settings And Persistence
- Settings are serialized through `DataContractJsonSerializer`.
- Add new persisted settings with `[DataMember]` and update default initialization in `[OnDeserializing]`.
- Keep setting names stable unless you also provide migration handling.

## Logging And Diagnostics
- Use the existing `Logger` class for launcher diagnostics.
- When adding logs, include enough context to diagnose failures without dumping huge volumes of noise.

## Files To Avoid Editing Accidentally
- Do not edit `bin/`, `obj/`, or generated build output.
- Be careful with `src/AssemblyVersion.cs`; CI bumps it automatically and both `app/` and `lib/` compile that file.
- Do not change workflow files unless the task is explicitly about CI/release automation.
- Avoid changing embedded assets or icons unless the task is UI/branding related.

## Validation Before Finishing
- Always build after modifying code.
- Build the project(s) you touched.
- If you touched shared code in `lib/`, rebuild at least one consumer project too.
- If you touched UI pages or styles, make sure XAML and code-behind both compile.
- If you touched settings or serialization, verify defaults and load/save behavior logically.
- If you touched the stale `test/` harness, call out its broken dependency state explicitly.
