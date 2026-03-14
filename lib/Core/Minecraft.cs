using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Deployment;
using Flarial.Launcher.Services.Native;
using Windows.Win32.Foundation;
using Windows.Win32.Globalization;
using static System.String;
using static System.StringComparison;
using static Windows.Win32.PInvoke;
using static Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS;

namespace Flarial.Launcher.Services.Core;

using static Native.NativeProcess;

public readonly struct MinecraftInstallState
{
    public MinecraftInstallState(bool isInstalled, string version, string platform)
        => (IsInstalled, Version, Platform) = (isInstalled, version, platform);

    public bool IsInstalled { get; }
    public string Version { get; }
    public string Platform { get; }
}

public sealed class DeploymentOperationException : Exception
{
    public DeploymentOperationException(string operation, Exception errorCode, Guid? activityId = null, Exception? extendedErrorCode = null, string? errorText = null)
        : base(BuildMessage(operation, errorCode, activityId, extendedErrorCode, errorText), errorCode)
    {
        Operation = operation;
        ActivityId = activityId;
        ExtendedErrorCode = extendedErrorCode;
        ErrorText = string.IsNullOrWhiteSpace(errorText) ? null : errorText!.Trim();
        HResult = errorCode.HResult != 0 ? errorCode.HResult : extendedErrorCode?.HResult ?? unchecked((int)0x80004005);
    }

    public string Operation { get; }
    public Guid? ActivityId { get; }
    public Exception? ExtendedErrorCode { get; }
    public string? ErrorText { get; }
    public string HResultHex => FormatHResult(HResult);
    public string? ExtendedHResultHex => ExtendedErrorCode is null ? null : FormatHResult(ExtendedErrorCode.HResult);

    public bool MatchesAny(params int[] values)
        => values.Any(value => HResult == value || ExtendedErrorCode?.HResult == value);

    public IEnumerable<KeyValuePair<string, string>> GetLogFields()
    {
        yield return new("DeploymentOperation", Operation);
        yield return new("DeploymentHResult", HResultHex);

        if (ActivityId is Guid activityId && activityId != Guid.Empty)
            yield return new("DeploymentActivityId", activityId.ToString());

        if (ExtendedHResultHex is { } extended)
            yield return new("DeploymentExtendedHResult", extended);

        if (!string.IsNullOrWhiteSpace(ErrorText))
            yield return new("DeploymentErrorText", ErrorText!);
    }

    static string BuildMessage(string operation, Exception errorCode, Guid? activityId, Exception? extendedErrorCode, string? errorText)
    {
        List<string> details = [$"operation={operation}", $"hresult={FormatHResult(errorCode.HResult)}"];

        if (activityId is Guid value && value != Guid.Empty)
            details.Add($"activityId={value}");

        if (extendedErrorCode is not null)
            details.Add($"extendedHResult={FormatHResult(extendedErrorCode.HResult)}");

        if (!string.IsNullOrWhiteSpace(errorText))
            details.Add($"errorText={errorText!.Trim()}");

        return $"Windows deployment {operation} failed ({string.Join(", ", details)})";
    }

    static string FormatHResult(int value) => $"0x{value:X8}";
}

static class DeploymentDiagnostics
{
    internal static DeploymentOperationException CreateException(string operation, Exception errorCode, DeploymentResult? result)
        => new(operation, errorCode, GetActivityId(result), GetExtendedErrorCode(result), GetErrorText(result));

    static Guid? GetActivityId(DeploymentResult? result)
    {
        if (result is null)
            return null;

        try { return result.ActivityId; }
        catch { return null; }
    }

    static Exception? GetExtendedErrorCode(DeploymentResult? result)
    {
        if (result is null)
            return null;

        try { return result.ExtendedErrorCode; }
        catch { return null; }
    }

    static string? GetErrorText(DeploymentResult? result)
    {
        if (result is null)
            return null;

        try { return result.ErrorText; }
        catch { return null; }
    }

}

public unsafe abstract class Minecraft
{
    internal Minecraft() { }

    static readonly PackageManager s_manager = new();
    protected const string PackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    static Package? TryGetPackage() => s_manager.FindPackagesForUser(Empty, PackageFamilyName).FirstOrDefault();
    protected static Package Package => TryGetPackage() ?? throw new InvalidOperationException("Minecraft is not installed.");

    public static Minecraft Current => UsingGameDevelopmentKit ? s_gdk : s_uwp;
    static readonly Minecraft s_uwp = new MinecraftUWP(), s_gdk = new MinecraftGDK();


    public bool IsRunning => Window is { };
    protected abstract string WindowClass { get; }

    protected abstract uint? Activate();
    public abstract uint? Launch(bool initialized);

    static bool IsGameDevelopmentKitPackage(Package package)
    {
        var aumid = package.GetAppListEntries().FirstOrDefault()?.AppUserModelId;
        return aumid?.Equals("Microsoft.MinecraftUWP_8wekyb3d8bbwe!Game", OrdinalIgnoreCase) is true;
    }

    static string FormatVersion(Package package)
    {
        var version = package.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build / 100}";
    }

    static DeploymentResult? TryGetResults(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> operation)
    {
        try { return operation.GetResults(); }
        catch { return null; }
    }

    public static bool IsSigned => TryGetPackage()?.SignatureKind is PackageSignatureKind.Store;
    public static bool IsInstalled => s_manager.FindPackagesForUser(Empty, PackageFamilyName).Any();

    public static MinecraftInstallState GetInstallState()
    {
        var package = TryGetPackage();
        if (package is null)
            return new(false, string.Empty, string.Empty);

        return new(true, FormatVersion(package), IsGameDevelopmentKitPackage(package) ? "GDK" : "UWP");
    }

    public static bool UsingGameDevelopmentKit
    {
        get
        {
            return TryGetPackage() is { } package && IsGameDevelopmentKitPackage(package);
        }
    }

    public static string Version
    {
        get
        {
            return TryGetPackage() is { } package ? FormatVersion(package) : "Not installed";
        }
    }

    public static string FullName => TryGetPackage()?.Id.FullName ?? string.Empty;

    public static Task RemoveAsync()
    {
        if (!IsInstalled)
            return Task.CompletedTask;

        TaskCompletionSource<bool> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = s_manager.RemovePackageAsync(FullName);

        operation.Completed += (sender, _) =>
        {
            if (sender.Status is AsyncStatus.Error)
            {
                source.TrySetException(DeploymentDiagnostics.CreateException("remove", sender.ErrorCode, TryGetResults(sender)));
                return;
            }

            if (sender.Status is AsyncStatus.Canceled)
            {
                source.TrySetException(new OperationCanceledException("Windows package removal was canceled."));
                return;
            }

            source.TrySetResult(true);
        };

        return source.Task;
    }

    public static uint? RunningProcessId => Current.Window is { } window ? window.ProcessId : null;

    private protected NativeWindow? Window
    {
        get
        {
            fixed (char* @class = WindowClass)
            fixed (char* pfn = PackageFamilyName)
            {
                NativeWindow window = HWND.Null;
                var length = PACKAGE_FAMILY_NAME_MAX_LENGTH + 1;
                var buffer = stackalloc char[(int)length];

                while ((window = FindWindowEx(HWND.Null, window, @class, null)) != HWND.Null)
                {
                    if (Open(PROCESS_QUERY_LIMITED_INFORMATION, window.ProcessId) is not { } process)
                        continue;

                    using (process)
                    {
                        var error = GetPackageFamilyName(process, &length, buffer);
                        if (error is not WIN32_ERROR.ERROR_SUCCESS) continue;

                        var result = CompareStringOrdinal(pfn, -1, buffer, -1, true);
                        if (result is not COMPARESTRING_RESULT.CSTR_EQUAL) continue;

                        return window;
                    }
                }

                return null;
            }
        }
    }
}
