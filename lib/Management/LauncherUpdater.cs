using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Flarial.Launcher.Services.Core;
using Flarial.Launcher.Services.Networking;
using Windows.Data.Json;
using static Windows.Win32.PInvoke;

namespace Flarial.Launcher.Services.Management;

public static class LauncherUpdater
{
    const string StatusArgument = "--self-update-status";

    static LauncherUpdater()
    {
        var assembly = Assembly.GetEntryAssembly();

        var path = Path.GetTempPath();
        s_source = $"{Path.Combine(path, Path.GetRandomFileName())}.exe";
        s_script = $"{Path.Combine(path, Path.GetRandomFileName())}.cmd";
        s_status = $"{Path.Combine(path, Path.GetRandomFileName())}.txt";

        s_version = assembly.GetName().Version.ToString();
        var destination = assembly.ManifestModule.FullyQualifiedName;

        path = Environment.GetFolderPath(Environment.SpecialFolder.System);

        s_filename = Path.Combine(path, "cmd.exe");
        s_arguments = string.Format(Arguments, s_script);
        s_content = string.Format(Content, Path.Combine(path, "taskkill.exe"), GetCurrentProcessId(), s_source, destination, s_status, "{0}");
    }

    const string Arguments = "/e:on /f:off /v:off /d /c call \"{0}\"";

    const string Content = @"
@echo off
chcp 65001 >nul
""{0}"" /f /pid ""{1}"" >nul 2>nul
set retries=0

:_
move /y ""{2}"" ""{3}"" >nul 2>nul
if %errorlevel%==0 goto success
set /a retries+=1
if %retries% geq 20 goto fail
timeout /t 1 /nobreak >nul
goto _

:success
>""{4}"" echo success
start "" ""{3}"" {5}
del ""%~f0""
exit /b 0

:fail
>""{4}"" echo failed:locked
if exist ""{3}"" start "" ""{3}"" {5}
del ""{2}"" >nul 2>nul
del ""%~f0""
exit /b 1
";

    const string VersionUri = "https://cdn.flarial.xyz/launcher/launcherVersion.txt";

    const string LauncherUri = "https://cdn.flarial.xyz/launcher/Flarial.Launcher.exe";

    static readonly string s_filename, s_arguments, s_version, s_source, s_script, s_status, s_content;

    public static bool TryConsumeStartupStatus(string[] arguments, out string? message, out bool failed)
    {
        message = null;
        failed = false;

        var index = Array.FindIndex(arguments, argument => argument.Equals(StatusArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= arguments.Length)
            return false;

        var path = arguments[index + 1];
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!File.Exists(path))
            {
                failed = true;
                message = "Launcher update result was unavailable. The launcher was restarted without confirmation that the update finished.";
                return true;
            }

            var status = File.ReadAllText(path).Trim();
            failed = !status.Equals("success", StringComparison.OrdinalIgnoreCase);
            message = failed
                ? "Launcher update could not replace the running executable. Close any leftover launcher processes and try updating again."
                : "Launcher updated successfully.";
            return true;
        }
        catch (Exception exception)
        {
            failed = true;
            message = $"Launcher update result could not be read. {exception.Message}";
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }

    public static async Task<bool> CheckAsync()
    {
        var input = await HttpService.GetAsync<string>(VersionUri);
        var version = JsonObject.Parse(input)["version"];
        return s_version != version.GetString();
    }

    public static async Task DownloadAsync(Action<int> action)
    {
        CleanupTemporaryFiles();
        await HttpService.DownloadAsync(LauncherUri, s_source, action);
        StringBuilder launchArguments = new();
        launchArguments.Append(StatusArgument).Append(' ').Append('"').Append(s_status).Append('"');
        if (HttpService.UseProxy) launchArguments.Append(' ').Append("--use-proxy");
        if (HttpService.UseDnsOverHttps) launchArguments.Append(' ').Append("--use-dns-over-https");

        using (StreamWriter writer = new(s_script, false, Encoding.UTF8))
            await writer.WriteAsync(string.Format(s_content, launchArguments));

        StringBuilder builder = new(s_arguments);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = s_filename,
                CreateNoWindow = true,
                UseShellExecute = false,
                Arguments = $"{builder}"
            });

            if (process is null)
                throw new InvalidOperationException("Failed to start the launcher update helper process.");
        }
        catch
        {
            CleanupTemporaryFiles();
            throw;
        }
    }

    static void CleanupTemporaryFiles()
    {
        try
        {
            if (File.Exists(s_source))
                File.Delete(s_source);
        }
        catch { }

        try
        {
            if (File.Exists(s_script))
                File.Delete(s_script);
        }
        catch { }

        try
        {
            if (File.Exists(s_status))
                File.Delete(s_status);
        }
        catch { }
    }
}
