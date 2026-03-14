using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using Flarial.Launcher.Managers;
using Flarial.Launcher.Pages;
using Flarial.Launcher.Services.Modding;
using Flarial.Launcher.Services.Networking;
using Flarial.Launcher.Styles;

namespace Flarial.Launcher;

public partial class App : Application
{
    // ↓ Do not modify this initialization code. ↓

    const string Format = @"Looks like the launcher crashed! 

• Please take a screenshot of this.
• Create a new support post & send the screenshot.

Version: {0}
Exception: {1}

{2}

{3}";

    const string Name = "54874D29-646C-4536-B6D1-8E05053BE00E";

    static readonly Mutex _mutex;

    static bool IsSwitcherVerificationMode(string[] arguments)
        => arguments.Any(argument => argument.Equals("--verify-switcher", StringComparison.OrdinalIgnoreCase));

    static bool IsBackupVerificationMode(string[] arguments)
        => arguments.Any(argument => argument.Equals("--verify-backups", StringComparison.OrdinalIgnoreCase));

    static bool IsNotificationVerificationMode(string[] arguments)
        => arguments.Any(argument => argument.Equals("--verify-notifications", StringComparison.OrdinalIgnoreCase));

    static bool IsSettingsVerificationMode(string[] arguments)
        => arguments.Any(argument => argument.Equals("--verify-settings-persistence", StringComparison.OrdinalIgnoreCase));

    static bool IsVersionCatalogVerificationMode(string[] arguments)
        => arguments.Any(argument => argument.Equals("--verify-version-catalog", StringComparison.OrdinalIgnoreCase));

    static bool TryConsumeSelfUpdateStatus(string[] arguments, out string message, out bool failed)
    {
        const string statusArgument = "--self-update-status";

        message = string.Empty;
        failed = false;

        var index = Array.FindIndex(arguments, argument => argument.Equals(statusArgument, StringComparison.OrdinalIgnoreCase));
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

    static void UnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var version = $"{Assembly.GetEntryAssembly().GetName().Version}";

        var exception = (Exception)args.ExceptionObject;
        var trace = $"{exception.StackTrace}".Trim();

        while (exception.InnerException is not null)
            exception = exception.InnerException;

        var name = exception.GetType().Name;
        var message = exception.Message;

        var text = string.Format(Format, version, name, message, trace);
        Logger.Error("Unhandled exception", (Exception)args.ExceptionObject);
        System.Windows.MessageBox.Show(text, "Flarial Launcher: Error", MessageBoxButton.OK, MessageBoxImage.Error);

        Environment.Exit(1);
    }

    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += UnhandledException;

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        _mutex = new(false, Name, out var value);
        if (!value
            && !IsSwitcherVerificationMode(Environment.GetCommandLineArgs())
            && !IsBackupVerificationMode(Environment.GetCommandLineArgs())
            && !IsNotificationVerificationMode(Environment.GetCommandLineArgs())
            && !IsSettingsVerificationMode(Environment.GetCommandLineArgs())
            && !IsVersionCatalogVerificationMode(Environment.GetCommandLineArgs()))
            using (_mutex) Environment.Exit(0);
    }

    // ↓ Start writing code from here. ↓

    void ApplicationStartup(object sender, StartupEventArgs args)
    {
        var arguments = args.Args;

        Environment.CurrentDirectory = Directory.CreateDirectory(VersionManagement.launcherPath).FullName;
        Directory.CreateDirectory(BackupManager.backupDirectory);
        Directory.CreateDirectory(@$"{VersionManagement.launcherPath}\Versions");
        Directory.CreateDirectory(@$"{VersionManagement.launcherPath}\Logs");
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "unknown";
        Logger.Info($"Application startup | exe={assembly.Location} | version={version}");
        Logger.Info($"Install pipeline revision | value={HttpService.DownloadPipelineRevision}");

        string path = @$"{VersionManagement.launcherPath}\cachedToken.txt";
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);

        var verificationIndex = Array.FindIndex(arguments, argument => argument.Equals("--verify-switcher", StringComparison.OrdinalIgnoreCase));
        if (verificationIndex >= 0)
        {
            var scenario = verificationIndex + 1 < arguments.Length ? arguments[verificationIndex + 1] : "all";
            Logger.Info($"Switcher verification startup | scenario={scenario}");
            var exitCode = SettingsSwitcherVerificationRunner.Run(scenario);
            Environment.Exit(exitCode);
            return;
        }

        verificationIndex = Array.FindIndex(arguments, argument => argument.Equals("--verify-backups", StringComparison.OrdinalIgnoreCase));
        if (verificationIndex >= 0)
        {
            var scenario = verificationIndex + 1 < arguments.Length ? arguments[verificationIndex + 1] : "all";
            Logger.Info($"Backup verification startup | scenario={scenario}");
            var exitCode = BackupVerificationRunner.Run(scenario);
            Environment.Exit(exitCode);
            return;
        }

        verificationIndex = Array.FindIndex(arguments, argument => argument.Equals("--verify-notifications", StringComparison.OrdinalIgnoreCase));
        if (verificationIndex >= 0)
        {
            Logger.Info("Notification verification startup");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(RunNotificationVerificationAsync);
            return;
        }

        verificationIndex = Array.FindIndex(arguments, argument => argument.Equals("--verify-settings-persistence", StringComparison.OrdinalIgnoreCase));
        if (verificationIndex >= 0)
        {
            var scenario = verificationIndex + 1 < arguments.Length ? arguments[verificationIndex + 1] : "all";
            Logger.Info($"Settings persistence verification startup | scenario={scenario}");
            var exitCode = SettingsPersistenceVerificationRunner.Run(scenario);
            Environment.Exit(exitCode);
            return;
        }

        verificationIndex = Array.FindIndex(arguments, argument => argument.Equals("--verify-version-catalog", StringComparison.OrdinalIgnoreCase));
        if (verificationIndex >= 0)
        {
            var scenario = verificationIndex + 1 < arguments.Length ? arguments[verificationIndex + 1] : "all";
            Logger.Info($"Version catalog verification startup | scenario={scenario}");
            var exitCode = VersionCatalogVerificationRunner.Run(scenario);
            Environment.Exit(exitCode);
            return;
        }

        var length = arguments.Length;
        var settings = Settings.Current;

        for (var index = 0; index < length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--inject":
                    if (!(index + 1 < length))
                        continue;

                    var offset = index + 1; var count = length - offset;
                    ArraySegment<string> segment = new(arguments, offset, count);

                    Injector.Launch(true, segment.First());
                    Environment.Exit(0);
                    break;

                case "--no-hardware-acceleration":
                    settings.HardwareAcceleration = false;
                    break;

                case "--use-proxy":
                    HttpService.UseProxy = true;
                    break;

                case "--use-dns-over-https":
                    HttpService.UseDnsOverHttps = true;
                    break;
            }
        }

        if (TryConsumeSelfUpdateStatus(arguments, out var selfUpdateMessage, out var selfUpdateFailed)
            && !string.IsNullOrWhiteSpace(selfUpdateMessage))
        {
            Logger.Info($"Self-update status | failed={selfUpdateFailed} | message={selfUpdateMessage}");
            global::Flarial.Launcher.MainWindow.SetPendingStartupMessage(selfUpdateMessage, selfUpdateFailed);
        }

        global::Flarial.Launcher.MainWindow mainWindow = new();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    async void RunNotificationVerificationAsync()
    {
        var exitCode = await NotificationVerificationRunner.RunAsync();
        Shutdown(exitCode);
    }
}
