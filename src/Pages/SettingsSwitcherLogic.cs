using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Flarial.Launcher.Services.Core;

namespace Flarial.Launcher.Pages;

internal enum SwitcherStage
{
    Backup,
    Remove,
    Install,
    Verify,
    Restore
}

internal enum SwitcherFailureKind
{
    PackageConflict,
    PackageInUse,
    AccessDenied,
    DiskFull,
    Timeout,
    Canceled,
    VerificationFailed,
    RestoreFailed,
    BackupFailed,
    RemoveFailed,
    InstallFailed
}

internal enum SwitcherActionKind
{
    FreshInstall,
    Reinstall,
    Upgrade,
    Downgrade,
    PlatformSwitch
}

internal enum SwitcherRestorePolicy
{
    RestoreCreatedBackup,
    RestoreLatestUwpBackupIfAvailable,
    SkipRestore
}

internal readonly struct SwitcherPlan
{
    public SwitcherPlan(MinecraftInstallState installedState, string targetVersion, string targetPlatform, SwitcherActionKind kind)
        => (InstalledState, TargetVersion, TargetPlatform, Kind) = (installedState, targetVersion, targetPlatform, kind);

    public MinecraftInstallState InstalledState { get; }
    public string TargetVersion { get; }
    public string TargetPlatform { get; }
    public SwitcherActionKind Kind { get; }
    public bool RequiresBackup => InstalledState.IsInstalled;
    public bool RequiresRemoval => InstalledState.IsInstalled;
    public string TargetLabel => $"{TargetVersion} ({TargetPlatform})";
    public string InstalledLabel => InstalledState.IsInstalled ? $"{InstalledState.Version} ({InstalledState.Platform})" : "no installed package";
    public string ActionLabel => Kind switch
    {
        SwitcherActionKind.FreshInstall => "install",
        SwitcherActionKind.Reinstall => "reinstall",
        SwitcherActionKind.Upgrade => "upgrade",
        SwitcherActionKind.Downgrade => "downgrade",
        _ => "switch"
    };
    public string SuccessProgressText => Kind switch
    {
        SwitcherActionKind.Reinstall => $"{TargetVersion} reinstalled",
        SwitcherActionKind.Upgrade => $"Upgraded to {TargetVersion}",
        SwitcherActionKind.Downgrade => $"Downgraded to {TargetVersion}",
        SwitcherActionKind.PlatformSwitch => $"Switched to {TargetVersion} ({TargetPlatform})",
        _ => $"{TargetVersion} install finished"
    };
    public string SuccessMessage => Kind switch
    {
        SwitcherActionKind.Reinstall => $"Reinstalled {TargetLabel}.",
        SwitcherActionKind.Upgrade => $"Upgraded to {TargetLabel}.",
        SwitcherActionKind.Downgrade => $"Downgraded to {TargetLabel}.",
        SwitcherActionKind.PlatformSwitch => $"Switched from {InstalledLabel} to {TargetLabel}.",
        _ => $"Installed {TargetLabel}."
    };
}

internal readonly struct SwitcherFailureResult
{
    public SwitcherFailureResult(SwitcherFailureKind kind, string progressText, string message)
        => (Kind, ProgressText, Message) = (kind, progressText, message);

    public SwitcherFailureKind Kind { get; }
    public string ProgressText { get; }
    public string Message { get; }
}

internal sealed class SwitcherOperationGate
{
    int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public bool TryBegin() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void End() => Interlocked.Exchange(ref _busy, 0);
}

internal static class SettingsSwitcherLogic
{
    static bool ReportsMissingFramework(string errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return false;

        return errorText.Contains("depends on a framework that could not be found", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("framework package", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("provide the framework", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("Microsoft.Services.Store.Engagement", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("Microsoft.NET.Native.Framework", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeVersion(string value)
        => TryNormalizeVersion(value, out var normalized, out _) ? normalized : string.Empty;

    static bool TryNormalizeVersion(string value, out string normalized, out Version parsedVersion)
    {
        normalized = string.Empty;
        parsedVersion = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var segments = value.Trim().Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 3 or > 4)
            return false;

        if (!int.TryParse(segments[0], out var major)
            || !int.TryParse(segments[1], out var minor)
            || !int.TryParse(segments[2], out var build))
        {
            return false;
        }

        normalized = $"{major}.{minor}.{build}";

        if (segments.Length == 4)
        {
            if (!int.TryParse(segments[3], out var revision))
                return false;

            parsedVersion = new Version(major, minor, build, revision);
            return true;
        }

        parsedVersion = new Version(major, minor, build);
        return true;
    }

    static int CompareVersions(string left, string right)
    {
        var leftValid = TryNormalizeVersion(left, out var leftNormalized, out var leftVersion);
        var rightValid = TryNormalizeVersion(right, out var rightNormalized, out var rightVersion);

        if (leftValid && rightValid)
        {
            var versionComparison = rightVersion.CompareTo(leftVersion);
            return versionComparison != 0
                ? versionComparison
                : StringComparer.OrdinalIgnoreCase.Compare(leftNormalized, rightNormalized);
        }

        if (leftValid)
            return -1;

        if (rightValid)
            return 1;

        return StringComparer.OrdinalIgnoreCase.Compare(right ?? string.Empty, left ?? string.Empty);
    }

    public static SwitcherPlan CreatePlan(MinecraftInstallState installedState, string targetVersion, string targetPlatform)
    {
        if (!installedState.IsInstalled)
            return new(installedState, targetVersion, targetPlatform, SwitcherActionKind.FreshInstall);

        var normalizedTargetVersion = NormalizeVersion(targetVersion);
        var normalizedInstalledVersion = NormalizeVersion(installedState.Version);
        var samePlatform = string.Equals(installedState.Platform, targetPlatform, StringComparison.OrdinalIgnoreCase);
        var sameVersion = normalizedTargetVersion.Length > 0
            && normalizedInstalledVersion.Length > 0
            && string.Equals(normalizedTargetVersion, normalizedInstalledVersion, StringComparison.OrdinalIgnoreCase);

        if (!samePlatform)
            return new(installedState, targetVersion, targetPlatform, SwitcherActionKind.PlatformSwitch);

        if (sameVersion)
            return new(installedState, targetVersion, targetPlatform, SwitcherActionKind.Reinstall);

        var comparison = CompareVersions(targetVersion, installedState.Version);
        var kind = comparison < 0 ? SwitcherActionKind.Upgrade : SwitcherActionKind.Downgrade;
        return new(installedState, targetVersion, targetPlatform, kind);
    }

    public static bool IsInstalledStateMatch(MinecraftInstallState state, string expectedVersion, string expectedPlatform)
        => state.IsInstalled
            && string.Equals(state.Platform, expectedPlatform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeVersion(state.Version), NormalizeVersion(expectedVersion), StringComparison.OrdinalIgnoreCase);

    public static SwitcherRestorePolicy GetRestorePolicy(SwitcherPlan plan)
    {
        if (!plan.RequiresBackup)
            return SwitcherRestorePolicy.SkipRestore;

        if (plan.Kind == SwitcherActionKind.PlatformSwitch)
        {
            var sourcePlatform = plan.InstalledState.Platform ?? string.Empty;
            var targetPlatform = plan.TargetPlatform ?? string.Empty;

            if (sourcePlatform.Equals("GDK", StringComparison.OrdinalIgnoreCase)
                && targetPlatform.Equals("UWP", StringComparison.OrdinalIgnoreCase))
            {
                return SwitcherRestorePolicy.RestoreLatestUwpBackupIfAvailable;
            }

            if (sourcePlatform.Equals("UWP", StringComparison.OrdinalIgnoreCase)
                && targetPlatform.Equals("GDK", StringComparison.OrdinalIgnoreCase))
            {
                return SwitcherRestorePolicy.SkipRestore;
            }
        }

        return SwitcherRestorePolicy.RestoreCreatedBackup;
    }

    public static string GetPreSwitchRiskMessage(SwitcherPlan plan)
    {
        if (plan.Kind != SwitcherActionKind.PlatformSwitch)
            return null;

        var sourcePlatform = plan.InstalledState.Platform ?? string.Empty;
        var targetPlatform = plan.TargetPlatform ?? string.Empty;

        if (sourcePlatform.Equals("GDK", StringComparison.OrdinalIgnoreCase)
            && targetPlatform.Equals("UWP", StringComparison.OrdinalIgnoreCase))
        {
            return "Warning: Switching from GDK to UWP can cause partial data loss if no compatible UWP backup exists. The launcher will try to apply your latest UWP backup automatically when available.";
        }

        return null;
    }

    public static async Task<bool> VerifyInstalledTargetAsync(string expectedVersion, string expectedPlatform, Action<Exception> onCheckFailed, int attempts = 14, int delayMilliseconds = 500)
    {
        for (var index = 0; index < attempts; index++)
        {
            try
            {
                if (IsInstalledStateMatch(Minecraft.GetInstallState(), expectedVersion, expectedPlatform))
                    return true;
            }
            catch (Exception exception)
            {
                onCheckFailed(exception);
            }

            await Task.Delay(delayMilliseconds);
        }

        return false;
    }

    public static SwitcherFailureResult CreateFailureResult(Exception exception, SwitcherPlan plan, bool removedInstalledPackage, string backupName, SwitcherStage stage)
    {
        const int DeploymentConflict = unchecked((int)0x80073D06);
        const int DependencyConflict = unchecked((int)0x80073CF3);
        const int PackageInUse = unchecked((int)0x80073D02);
        const int AccessDenied = unchecked((int)0x80070005);
        const int DiskFull = unchecked((int)0x80070070);

        var target = plan.TargetLabel;

        static string WithBackupGuidance(string message, bool removedInstalledPackage, string backupName)
        {
            if (!removedInstalledPackage || string.IsNullOrWhiteSpace(backupName))
                return message;

            return $"{message} You can restore backup '{backupName}' from the Backup settings page.";
        }

        static SwitcherFailureResult Present(SwitcherFailureKind kind, string progressText, string message, bool removedInstalledPackage, string backupName)
            => new(kind, progressText, WithBackupGuidance(message, removedInstalledPackage, backupName));

        if (exception is DeploymentOperationException deploymentException)
        {
            var reportsAlreadyInstalled = deploymentException.ErrorText?.Contains("already installed", StringComparison.OrdinalIgnoreCase) is true;
            var reportsNewerInstalled = deploymentException.ErrorText?.Contains("higher version", StringComparison.OrdinalIgnoreCase) is true
                || deploymentException.ErrorText?.Contains("newer package", StringComparison.OrdinalIgnoreCase) is true;
            var reportsMissingFramework = ReportsMissingFramework(deploymentException.ErrorText);

            if (reportsMissingFramework)
            {
                return Present(
                    SwitcherFailureKind.InstallFailed,
                    "Required Store framework missing",
                    $"Windows blocked installing {target} because this package depends on a required Microsoft Store framework that is not available on this system. Try a newer version or install/update Minecraft through Microsoft Store first so Windows can restore the missing dependency.",
                    removedInstalledPackage,
                    backupName);
            }

            if (deploymentException.MatchesAny(DeploymentConflict, DependencyConflict)
                || reportsAlreadyInstalled
                || reportsNewerInstalled
                || deploymentException.ErrorText?.Contains("dependency or conflict validation", StringComparison.OrdinalIgnoreCase) is true)
            {
                if (reportsNewerInstalled && plan.InstalledState.IsInstalled && !removedInstalledPackage)
                {
                    return Present(
                        SwitcherFailureKind.PackageConflict,
                        "Install blocked by newer installed package",
                        $"Windows reported that {plan.InstalledLabel} is newer than {target}. Remove the newer package before retrying this downgrade or reinstall.",
                        removedInstalledPackage,
                        backupName);
                }

                if (removedInstalledPackage)
                {
                    return Present(
                        SwitcherFailureKind.PackageConflict,
                        reportsNewerInstalled ? "Install blocked by lingering newer package state" : "Install blocked by package conflict",
                        $"Windows blocked installing {target} even after removing {plan.InstalledLabel}. Reopen Microsoft Store and Xbox, confirm which Minecraft package is currently registered, then retry the switch.",
                        removedInstalledPackage,
                        backupName);
                }

                return Present(
                    SwitcherFailureKind.PackageConflict,
                    reportsAlreadyInstalled ? "Install blocked by existing package" : "Install blocked by package conflict",
                    $"Windows blocked installing {target} because another Minecraft package or dependency conflicts with it. Remove the conflicting package or update through Microsoft Store before retrying.",
                    removedInstalledPackage,
                    backupName);
            }

            if (deploymentException.MatchesAny(PackageInUse))
            {
                return Present(
                    SwitcherFailureKind.PackageInUse,
                    deploymentException.Operation == "remove" ? "Removal blocked by running app" : "Install blocked by running app",
                    "Close Minecraft, Microsoft Store, Xbox, and any launcher windows that may still be using the package, then try the switch again.",
                    removedInstalledPackage,
                    backupName);
            }

            if (deploymentException.MatchesAny(AccessDenied))
            {
                return Present(
                    SwitcherFailureKind.AccessDenied,
                    "Administrator access required",
                    "Windows denied the package deployment request. Make sure the launcher is running as administrator and try again.",
                    removedInstalledPackage,
                    backupName);
            }

            if (deploymentException.MatchesAny(DiskFull))
            {
                return Present(
                    SwitcherFailureKind.DiskFull,
                    "Not enough disk space",
                    "Windows could not finish the package deployment because the drive is out of space. Free some space and try again.",
                    removedInstalledPackage,
                    backupName);
            }

            if (deploymentException.Operation == "remove")
            {
                return Present(
                    SwitcherFailureKind.RemoveFailed,
                    "Removal failed",
                    "Windows could not remove the current Minecraft package. Close Minecraft-related apps and retry. Check launcher.log if the problem keeps happening.",
                    removedInstalledPackage,
                    backupName);
            }

            return Present(
                SwitcherFailureKind.InstallFailed,
                "Install failed",
                $"Windows could not deploy {target}. Check launcher.log for the deployment HRESULT, ActivityId, and Windows error details before retrying.",
                removedInstalledPackage,
                backupName);
        }

        if (exception is TimeoutException)
        {
            var timedOutDuringRemoval = stage == SwitcherStage.Remove;
            return Present(
                SwitcherFailureKind.Timeout,
                timedOutDuringRemoval ? "Removal timed out" : "Install timed out",
                timedOutDuringRemoval
                    ? $"Windows took too long to remove the current Minecraft package before switching to {target}. Close Minecraft, Xbox, and Microsoft Store, then retry the switch."
                    : $"Windows took too long to deploy {target}. Wait a moment to see if the package finishes in the background, then reopen the launcher and verify the installed version.",
                removedInstalledPackage,
                backupName);
        }

        if (exception is OperationCanceledException)
        {
            return Present(
                SwitcherFailureKind.Canceled,
                stage == SwitcherStage.Remove ? "Removal canceled" : stage == SwitcherStage.Restore ? "Restore canceled" : "Install canceled",
                $"Windows canceled the switch to {target}. Retry the switch after closing Minecraft-related apps.",
                removedInstalledPackage,
                backupName);
        }

        if (exception is System.IO.IOException ioException
            && ioException.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase))
        {
            return Present(
                SwitcherFailureKind.InstallFailed,
                "Install blocked by locked package file",
                $"Windows could not finish installing {target} because the downloaded package file is locked by another process. Close antivirus scans, Explorer previews, and any launcher instances, then retry the switch.",
                removedInstalledPackage,
                backupName);
        }

        if (exception is HttpRequestException
            || exception.Message.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("connection reset", StringComparison.OrdinalIgnoreCase))
        {
            return Present(
                SwitcherFailureKind.InstallFailed,
                "Install download failed",
                $"The download for {target} was interrupted by the remote server or your network connection. Retry the switch, and if it keeps failing, disable VPN/proxy tools and try again on a more stable connection.",
                removedInstalledPackage,
                backupName);
        }

        if (exception is System.IO.InvalidDataException)
        {
            return Present(
                SwitcherFailureKind.InstallFailed,
                "Install package validation failed",
                $"The downloaded package for {target} was incomplete or invalid. Retry the switch, and if it fails again, wait a moment for the download mirror to recover before trying again.",
                removedInstalledPackage,
                backupName);
        }

        if (stage == SwitcherStage.Verify)
        {
            return Present(
                SwitcherFailureKind.VerificationFailed,
                "Verification failed",
                $"The launcher could not verify that {target} finished registering. Reopen the launcher and confirm the installed version before trying again.",
                removedInstalledPackage,
                backupName);
        }

        if (stage == SwitcherStage.Restore)
        {
            return Present(
                SwitcherFailureKind.RestoreFailed,
                "Restore failed",
                $"Installed {target}, but automatic data restore failed. Restore backup '{backupName}' manually from the Backup settings page.",
                removedInstalledPackage,
                backupName);
        }

        return Present(
            stage == SwitcherStage.Backup ? SwitcherFailureKind.BackupFailed : stage == SwitcherStage.Remove ? SwitcherFailureKind.RemoveFailed : SwitcherFailureKind.InstallFailed,
            stage == SwitcherStage.Remove ? "Removal failed" : stage == SwitcherStage.Backup ? "Backup failed" : "Install failed",
            $"Failed to switch to {target}: {exception.Message}",
            removedInstalledPackage,
            backupName);
    }

    public static (string Key, object Value)[] GetDeploymentLogFields(DeploymentOperationException exception, SwitcherPlan plan, bool removedInstalledPackage, string backupName, SwitcherStage stage)
    {
        var fields = new List<(string Key, object Value)>
        {
            ("SwitcherStage", stage.ToString()),
            ("SwitcherActionKind", plan.Kind.ToString()),
            ("RemovedInstalledPackage", removedInstalledPackage),
            ("TargetVersion", plan.TargetVersion),
            ("TargetPlatform", plan.TargetPlatform),
            ("InstalledVersionBeforeSwitch", plan.InstalledState.Version),
            ("InstalledPlatformBeforeSwitch", plan.InstalledState.Platform)
        };

        if (!string.IsNullOrWhiteSpace(backupName))
            fields.Add(("BackupName", backupName));

        foreach (var field in exception.GetLogFields())
            fields.Add((field.Key, field.Value));

        return [.. fields];
    }
}

internal static class SettingsSwitcherVerificationRunner
{
    public static int Run(string scenario)
    {
        var normalized = string.IsNullOrWhiteSpace(scenario) ? "all" : scenario.Trim().ToLowerInvariant();

        return normalized switch
        {
            "all" => RunAll(),
            "busy" => RunScenario("busy", VerifyBusyStateTransitions),
            "conflict" => RunScenario("conflict", VerifyConflictClassification),
            "missing-framework" => RunScenario("missing-framework", VerifyMissingFrameworkClassification),
            "package-in-use" => RunScenario("package-in-use", VerifyPackageInUseClassification),
            "access-denied" => RunScenario("access-denied", VerifyAccessDeniedClassification),
            "disk-full" => RunScenario("disk-full", VerifyDiskFullClassification),
            "timeout" => RunScenario("timeout", VerifyTimeoutClassification),
            "remove-timeout" => RunScenario("remove-timeout", VerifyRemoveTimeoutClassification),
            "canceled" => RunScenario("canceled", VerifyCanceledClassification),
            "verification-failed" => RunScenario("verification-failed", VerifyVerificationFailureClassification),
            "replacement-plan" => RunScenario("replacement-plan", VerifyReplacementPlanClassification),
            "restore-policy" => RunScenario("restore-policy", VerifyRestorePolicyClassification),
            "removed-package-conflict" => RunScenario("removed-package-conflict", VerifyRemovedPackageConflictGuidance),
            _ => Fail($"SWITCHER_VERIFY scenario={normalized} result=invalid message=UnknownScenario")
        };
    }

    static int RunAll()
    {
        var results = new[]
        {
            RunScenario("busy", VerifyBusyStateTransitions),
            RunScenario("conflict", VerifyConflictClassification),
            RunScenario("missing-framework", VerifyMissingFrameworkClassification),
            RunScenario("package-in-use", VerifyPackageInUseClassification),
            RunScenario("access-denied", VerifyAccessDeniedClassification),
            RunScenario("disk-full", VerifyDiskFullClassification),
            RunScenario("timeout", VerifyTimeoutClassification),
            RunScenario("remove-timeout", VerifyRemoveTimeoutClassification),
            RunScenario("canceled", VerifyCanceledClassification),
            RunScenario("verification-failed", VerifyVerificationFailureClassification),
            RunScenario("replacement-plan", VerifyReplacementPlanClassification),
            RunScenario("restore-policy", VerifyRestorePolicyClassification),
            RunScenario("removed-package-conflict", VerifyRemovedPackageConflictGuidance)
        };

        var failed = results.Any(result => result != 0);
        WriteLine($"SWITCHER_VERIFY scenario=all result={(failed ? "fail" : "pass")}");
        return failed ? 1 : 0;
    }

    static int RunScenario(string scenario, Action action)
    {
        try
        {
            action();
            WriteLine($"SWITCHER_VERIFY scenario={scenario} result=pass");
            return 0;
        }
        catch (Exception exception)
        {
            return Fail($"SWITCHER_VERIFY scenario={scenario} result=fail message={Sanitize(exception.Message)}");
        }
    }

    static int Fail(string message)
    {
        Logger.Error(message, new InvalidOperationException(message));
        TryWriteToConsole(message);
        return message.Contains("result=invalid", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    static void WriteLine(string message)
    {
        Logger.Info(message);
        TryWriteToConsole(message);
    }

    static void TryWriteToConsole(string message)
    {
        try { Console.WriteLine(message); }
        catch { }
    }

    static string Sanitize(string value) => value.Replace(' ', '_');

    static void VerifyBusyStateTransitions()
    {
        var gate = new SwitcherOperationGate();

        Assert(!gate.IsBusy, "InitialBusyStateShouldBeFalse");
        Assert(gate.TryBegin(), "FirstAcquireShouldSucceed");
        Assert(gate.IsBusy, "BusyStateShouldBeTrueAfterAcquire");
        Assert(!gate.TryBegin(), "SecondAcquireShouldFail");
        Assert(gate.IsBusy, "BusyStateShouldRemainTrueWhileHeld");
        gate.End();
        Assert(!gate.IsBusy, "BusyStateShouldResetAfterEnd");

        WriteLine("SWITCHER_VERIFY scenario=busy step=transitions firstAcquire=true secondAcquire=false finalBusy=false");
    }

    static void VerifyConflictClassification()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            CreateDeploymentException("install", unchecked((int)0x80073D06), "A newer package is already installed."),
            new SwitcherPlan(new MinecraftInstallState(true, "1.21.60", "UWP"), "1.21.50", "UWP", SwitcherActionKind.Downgrade),
            false,
            string.Empty,
            SwitcherStage.Install);

        Assert(result.Kind == SwitcherFailureKind.PackageConflict, "ConflictShouldMapToPackageConflict");
        Assert(result.ProgressText == "Install blocked by newer installed package", "ConflictProgressTextMismatch");
        Assert(result.Message.Contains("1.21.60 (UWP)", StringComparison.Ordinal), "ConflictMessageMismatch");
        WriteLine($"SWITCHER_VERIFY scenario=conflict classification={result.Kind} progress={Sanitize(result.ProgressText)}");
    }

    static void VerifyPackageInUseClassification()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            CreateDeploymentException("install", unchecked((int)0x80073D02), "Package is in use."),
            new SwitcherPlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.50", "GDK", SwitcherActionKind.FreshInstall),
            false,
            string.Empty,
            SwitcherStage.Install);

        Assert(result.Kind == SwitcherFailureKind.PackageInUse, "PackageInUseShouldMapCorrectly");
        Assert(result.ProgressText == "Install blocked by running app", "PackageInUseProgressTextMismatch");
        WriteLine($"SWITCHER_VERIFY scenario=package-in-use classification={result.Kind} progress={Sanitize(result.ProgressText)}");
    }

    static void VerifyMissingFrameworkClassification()
    {
        const string errorText = "Package failed updates, dependency or conflict validation. Windows cannot install package Microsoft.WindowsStore because this package depends on a framework that could not be found. Provide the framework \"Microsoft.UI.Xaml.2.8\" published by \"CN=Microsoft Corporation\" with neutral or x64 processor architecture and minimum version 8.2501.31001.0 along with this package to install.";

        var result = SettingsSwitcherLogic.CreateFailureResult(
            CreateDeploymentException("install", unchecked((int)0x80073CF3), errorText),
            new SwitcherPlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.50", "UWP", SwitcherActionKind.FreshInstall),
            false,
            string.Empty,
            SwitcherStage.Install);

        Assert(result.Kind == SwitcherFailureKind.InstallFailed, "MissingFrameworkShouldRemainInstallFailed");
        Assert(result.ProgressText == "Required Store framework missing", "MissingFrameworkProgressTextMismatch");
        Assert(result.Message.Contains("required Microsoft Store framework", StringComparison.Ordinal), "MissingFrameworkMessageMismatch");
        WriteLine($"SWITCHER_VERIFY scenario=missing-framework classification={result.Kind} progress={Sanitize(result.ProgressText)}");
    }

    static void VerifyAccessDeniedClassification()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            CreateDeploymentException("install", unchecked((int)0x80070005), "Access denied."),
            new SwitcherPlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.50", "UWP", SwitcherActionKind.FreshInstall),
            false,
            string.Empty,
            SwitcherStage.Install);

        Assert(result.Kind == SwitcherFailureKind.AccessDenied, "AccessDeniedShouldMapCorrectly");
        Assert(result.ProgressText == "Administrator access required", "AccessDeniedProgressTextMismatch");
        WriteLine($"SWITCHER_VERIFY scenario=access-denied classification={result.Kind} progress={Sanitize(result.ProgressText)}");
    }

    static void VerifyDiskFullClassification()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            CreateDeploymentException("install", unchecked((int)0x80070070), "Disk full."),
            new SwitcherPlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.50", "UWP", SwitcherActionKind.FreshInstall),
            false,
            string.Empty,
            SwitcherStage.Install);

        Assert(result.Kind == SwitcherFailureKind.DiskFull, "DiskFullShouldMapCorrectly");
        Assert(result.ProgressText == "Not enough disk space", "DiskFullProgressTextMismatch");
        WriteLine($"SWITCHER_VERIFY scenario=disk-full classification={result.Kind} progress={Sanitize(result.ProgressText)}");
    }

    static void VerifyTimeoutClassification()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            new TimeoutException("Synthetic timeout"),
            new SwitcherPlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.50", "UWP", SwitcherActionKind.FreshInstall),
            false,
            string.Empty,
            SwitcherStage.Install);

        Assert(result.Kind == SwitcherFailureKind.Timeout, "TimeoutShouldMapCorrectly");
        Assert(result.ProgressText == "Install timed out", "TimeoutProgressTextMismatch");
        WriteLine($"SWITCHER_VERIFY scenario=timeout classification={result.Kind} progress={Sanitize(result.ProgressText)}");
    }

    static void VerifyRemoveTimeoutClassification()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            new TimeoutException("Synthetic remove timeout"),
            new SwitcherPlan(new MinecraftInstallState(true, "1.21.60", "UWP"), "1.21.50", "UWP", SwitcherActionKind.Downgrade),
            false,
            string.Empty,
            SwitcherStage.Remove);

        Assert(result.Kind == SwitcherFailureKind.Timeout, "RemoveTimeoutShouldMapCorrectly");
        Assert(result.ProgressText == "Removal timed out", "RemoveTimeoutProgressTextMismatch");
        Assert(result.Message.Contains("too long to remove", StringComparison.OrdinalIgnoreCase), "RemoveTimeoutMessageMismatch");
        WriteLine($"SWITCHER_VERIFY scenario=remove-timeout classification={result.Kind} progress={Sanitize(result.ProgressText)}");
    }

    static void VerifyCanceledClassification()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            new OperationCanceledException("Synthetic cancel"),
            new SwitcherPlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.50", "UWP", SwitcherActionKind.FreshInstall),
            false,
            string.Empty,
            SwitcherStage.Install);

        Assert(result.Kind == SwitcherFailureKind.Canceled, "CanceledShouldMapCorrectly");
        Assert(result.ProgressText == "Install canceled", "CanceledProgressTextMismatch");
        WriteLine($"SWITCHER_VERIFY scenario=canceled classification={result.Kind} progress={Sanitize(result.ProgressText)}");
    }

    static void VerifyVerificationFailureClassification()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            new Exception("Synthetic verification failure"),
            new SwitcherPlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.50", "UWP", SwitcherActionKind.FreshInstall),
            true,
            "switch-backup",
            SwitcherStage.Verify);

        Assert(result.Kind == SwitcherFailureKind.VerificationFailed, "VerificationFailureShouldMapCorrectly");
        Assert(result.ProgressText == "Verification failed", "VerificationProgressTextMismatch");
        Assert(result.Message.Contains("switch-backup", StringComparison.Ordinal), "VerificationFailureShouldIncludeBackupGuidance");
        WriteLine($"SWITCHER_VERIFY scenario=verification-failed classification={result.Kind} progress={Sanitize(result.ProgressText)} backupGuidance=true");
    }

    static void VerifyReplacementPlanClassification()
    {
        var reinstall = SettingsSwitcherLogic.CreatePlan(new MinecraftInstallState(true, "1.21.50", "UWP"), "1.21.50", "UWP");
        var upgrade = SettingsSwitcherLogic.CreatePlan(new MinecraftInstallState(true, "1.21.40", "UWP"), "1.21.50", "UWP");
        var downgrade = SettingsSwitcherLogic.CreatePlan(new MinecraftInstallState(true, "1.21.60", "UWP"), "1.21.50", "UWP");
        var platformSwitch = SettingsSwitcherLogic.CreatePlan(new MinecraftInstallState(true, "1.21.50", "UWP"), "1.21.50", "GDK");
        var freshInstall = SettingsSwitcherLogic.CreatePlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.50", "GDK");

        Assert(reinstall.Kind == SwitcherActionKind.Reinstall, "ReinstallShouldClassifyCorrectly");
        Assert(upgrade.Kind == SwitcherActionKind.Upgrade, "UpgradeShouldClassifyCorrectly");
        Assert(downgrade.Kind == SwitcherActionKind.Downgrade, "DowngradeShouldClassifyCorrectly");
        Assert(platformSwitch.Kind == SwitcherActionKind.PlatformSwitch, "PlatformSwitchShouldClassifyCorrectly");
        Assert(freshInstall.Kind == SwitcherActionKind.FreshInstall, "FreshInstallShouldClassifyCorrectly");
        Assert(!freshInstall.RequiresBackup && !freshInstall.RequiresRemoval, "FreshInstallShouldNotRequireReplacement");
        Assert(reinstall.RequiresBackup && reinstall.RequiresRemoval, "InstalledReplacementShouldRequireBackupAndRemoval");

        WriteLine("SWITCHER_VERIFY scenario=replacement-plan kinds=reinstall,upgrade,downgrade,platform-switch,fresh-install replacementPolicy=backup-remove-install-verify");
    }

    static void VerifyRestorePolicyClassification()
    {
        var reinstall = SettingsSwitcherLogic.GetRestorePolicy(
            new SwitcherPlan(new MinecraftInstallState(true, "1.21.60", "UWP"), "1.21.60", "UWP", SwitcherActionKind.Reinstall));

        var gdkToUwp = SettingsSwitcherLogic.GetRestorePolicy(
            new SwitcherPlan(new MinecraftInstallState(true, "1.21.60", "GDK"), "1.21.50", "UWP", SwitcherActionKind.PlatformSwitch));

        var uwpToGdk = SettingsSwitcherLogic.GetRestorePolicy(
            new SwitcherPlan(new MinecraftInstallState(true, "1.21.50", "UWP"), "1.21.60", "GDK", SwitcherActionKind.PlatformSwitch));

        var fresh = SettingsSwitcherLogic.GetRestorePolicy(
            new SwitcherPlan(new MinecraftInstallState(false, string.Empty, string.Empty), "1.21.60", "UWP", SwitcherActionKind.FreshInstall));

        Assert(reinstall == SwitcherRestorePolicy.RestoreCreatedBackup, "ReinstallRestorePolicyMismatch");
        Assert(gdkToUwp == SwitcherRestorePolicy.RestoreLatestUwpBackupIfAvailable, "GdkToUwpRestorePolicyMismatch");
        Assert(uwpToGdk == SwitcherRestorePolicy.SkipRestore, "UwpToGdkRestorePolicyMismatch");
        Assert(fresh == SwitcherRestorePolicy.SkipRestore, "FreshInstallRestorePolicyMismatch");

        var riskMessage = SettingsSwitcherLogic.GetPreSwitchRiskMessage(
            new SwitcherPlan(new MinecraftInstallState(true, "1.21.60", "GDK"), "1.21.50", "UWP", SwitcherActionKind.PlatformSwitch));
        Assert(!string.IsNullOrWhiteSpace(riskMessage), "GdkToUwpRiskMessageMissing");

        WriteLine($"SWITCHER_VERIFY scenario=restore-policy policies={reinstall},{gdkToUwp},{uwpToGdk},{fresh}");
    }

    static void VerifyRemovedPackageConflictGuidance()
    {
        var result = SettingsSwitcherLogic.CreateFailureResult(
            CreateDeploymentException("install", unchecked((int)0x80073D06), "A newer package is already installed."),
            new SwitcherPlan(new MinecraftInstallState(true, "1.21.60", "GDK"), "1.21.50", "UWP", SwitcherActionKind.PlatformSwitch),
            true,
            "switch-backup",
            SwitcherStage.Install);

        Assert(result.Kind == SwitcherFailureKind.PackageConflict, "RemovedPackageConflictShouldMapToPackageConflict");
        Assert(result.Message.Contains("even after removing 1.21.60 (GDK)", StringComparison.Ordinal), "RemovedPackageConflictMessageMismatch");
        Assert(result.Message.Contains("switch-backup", StringComparison.Ordinal), "RemovedPackageConflictShouldIncludeBackupGuidance");

        WriteLine($"SWITCHER_VERIFY scenario=removed-package-conflict classification={result.Kind} backupGuidance=true");
    }

    static DeploymentOperationException CreateDeploymentException(string operation, int hresult, string errorText)
        => new(operation, new VerificationException(hresult, errorText), Guid.Empty, null, errorText);

    static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    sealed class VerificationException : Exception
    {
        public VerificationException(int hresult, string message)
            : base(message) => HResult = hresult;
    }
}
