# Issues

- The local `lsp_diagnostics` tool is not usable in this environment because `csharp-ls` is not installed, so build validation was used instead for changed C# files.
- The launcher is a WinExe, so stdout is not the primary automation contract; the verification hook mirrors to stdout when available, but scripts should trust `launcher.log` and the process exit code.
- `dotnet build "src/Flarial.Launcher.csproj" -c Debug` is currently blocked by pre-existing missing `BackupManager` references in `src/App.xaml.cs`, `src/Pages/SettingsBackupPage.xaml.cs`, `src/Styles/BackupItem.xaml.cs`, and `src/Pages/SettingsSwitcherPage.xaml.cs`, so notification verification could not be executed end-to-end in this session.
- The earlier hanging verifier left stale `Flarial.Launcher.exe` processes holding the Debug output binary open, so rebuild validation required terminating those stale verification processes before rerunning the fixed path.
- `lsp_diagnostics` is still unavailable for this C# workspace because `csharp-ls` is not installed, so the settings persistence sweep relied on a clean `dotnet build` plus the new scriptable verification hook for mechanical validation.
- Version feed hardening changes were implemented without running the new or existing catalog verification hooks because the current phase explicitly forbids verification commands; duplicate-preference and partial-failure scenarios still need deferred execution.
- `lsp_diagnostics` remains unavailable for the version catalog sweep because `csharp-ls` is still missing, so validation used clean `dotnet build` results plus the new `--verify-version-catalog all` runtime hook.
- `lsp_diagnostics` is still unavailable for this network/update sweep because `csharp-ls` is not installed, and the current phase explicitly forbids running build or runtime verification commands, so only static code review and targeted edits were completed in-session.
- Package replacement semantics were updated without running build, runtime, or switcher verification commands because this phase explicitly forbids verifications; the new replacement-plan and removed-package-conflict verification scenarios remain deferred.
