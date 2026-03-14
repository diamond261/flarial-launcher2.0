using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Media;

namespace Flarial.Launcher;

internal static class SettingsPersistenceVerificationRunner
{
    static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    const string Flag = "--verify-settings-persistence";

    internal static int Run(string scenario)
    {
        Logger.Info($"Settings persistence verification started | scenario={scenario}");

        try
        {
            return scenario.Equals("assert-roundtrip", StringComparison.OrdinalIgnoreCase)
                ? RunRoundTripAssertion()
                : scenario.Equals("assert-corruption", StringComparison.OrdinalIgnoreCase)
                    ? RunCorruptionAssertion()
                    : RunAllScenarios();
        }
        catch (Exception ex)
        {
            Logger.Error("Settings persistence verification failed", ex, ("Scenario", scenario));
            Console.Error.WriteLine($"Settings persistence verification failed: {ex.Message}");
            return 1;
        }
    }

    static int RunAllScenarios()
    {
        var backupPath = $"{Settings.SettingsPath}.verification-backup";
        var hadOriginalSettings = File.Exists(Settings.SettingsPath);
        HashSet<string> existingRecoveryFiles = [.. Directory.GetFiles(Path.GetDirectoryName(Settings.SettingsPath), "Flarial.Launcher.Settings.corrupt.*.json")];

        Directory.CreateDirectory(Path.GetDirectoryName(Settings.SettingsPath));

        if (File.Exists(backupPath))
            File.Delete(backupPath);

        if (hadOriginalSettings)
            File.Copy(Settings.SettingsPath, backupPath, true);

        try
        {
            var sample = new Settings
            {
                HardwareAcceleration = false,
                CustomDllPath = @"1|C:\verification\first.dll;0|C:\verification\second.dll",
                DllBuild = DllBuild.Custom,
                WaitForInitialization = false,
                CustomTargetInjection = true,
                CustomTargetProcessName = "notepad.exe",
                SaveOnTray = true,
                DisableAutoVoid = true,
                DllPresets = new() { ["Default"] = @"C:\verification\first.dll" },
                ActiveDllPreset = "Default",
                AutoLogin = false
            };

            sample.Save();
            Settings.ResetForVerification();
            RunChildScenario("assert-roundtrip");

            File.WriteAllText(Settings.SettingsPath, "{ definitely not valid json }");
            Settings.ResetForVerification();
            RunChildScenario("assert-corruption");

            const string success = "Settings persistence verification passed.";
            Logger.Info(success);
            Console.WriteLine(success);
            return 0;
        }
        finally
        {
            RestoreOriginalSettings(hadOriginalSettings, backupPath);
            DeleteCorruptRecoveryFiles(existingRecoveryFiles);
            Settings.ResetForVerification();
        }
    }

    static int RunRoundTripAssertion()
    {
        Settings.ResetForVerification();
        var settings = Settings.Current;
        var startup = MainWindow.ReadStartupSettings(settings);

        Assert(settings.DllBuild == DllBuild.Custom, "DllBuild did not survive restart.");
        Assert(string.Equals(settings.CustomDllPath, @"1|C:\verification\first.dll;0|C:\verification\second.dll", StringComparison.Ordinal), "Custom DLL paths did not survive restart.");
        Assert(!settings.WaitForInitialization, "WaitForInitialization did not survive restart.");
        Assert(settings.CustomTargetInjection, "CustomTargetInjection did not survive restart.");
        Assert(string.Equals(settings.CustomTargetProcessName, "notepad.exe", StringComparison.Ordinal), "CustomTargetProcessName did not survive restart.");
        Assert(settings.SaveOnTray, "SaveOnTray did not survive restart.");
        Assert(settings.DisableAutoVoid, "DisableAutoVoid did not survive restart.");
        Assert(settings.DllPresets.Count == 1 && settings.DllPresets.ContainsKey("Default"), "DllPresets did not survive restart.");
        Assert(string.Equals(settings.ActiveDllPreset, "Default", StringComparison.Ordinal), "ActiveDllPreset did not survive restart.");
        Assert(!settings.AutoLogin, "AutoLogin did not survive restart.");
        Assert(RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly, "HardwareAcceleration was not re-applied on restart.");
        Assert(startup.AutoVoidDisabled, "DisableAutoVoid startup state was not re-applied on restart.");
        Assert(startup.HardwareAccelerationDisabled, "HardwareAcceleration startup state was not re-applied on restart.");
        Console.WriteLine("Settings persistence round-trip assertion passed.");
        return 0;
    }

    static int RunCorruptionAssertion()
    {
        Settings.ResetForVerification();
        var settings = Settings.Current;

        Assert(settings.DllBuild == DllBuild.Release, "Corrupt settings did not recover to default DllBuild.");
        Assert(settings.WaitForInitialization, "Corrupt settings did not recover default WaitForInitialization.");
        Assert(string.Equals(settings.CustomTargetProcessName, "Minecraft.Windows.exe", StringComparison.Ordinal), "Corrupt settings did not recover default CustomTargetProcessName.");
        Assert(settings.DllPresets.Count == 0, "Corrupt settings did not recover empty DllPresets.");
        Assert(string.Equals(settings.ActiveDllPreset, "Default", StringComparison.Ordinal), "Corrupt settings did not recover default ActiveDllPreset.");
        Assert(Directory.GetFiles(Path.GetDirectoryName(Settings.SettingsPath), "Flarial.Launcher.Settings.corrupt.*.json").Length > 0, "Corrupt settings file was not moved aside.");
        Console.WriteLine("Settings corruption assertion passed.");
        return 0;
    }

    static void RunChildScenario(string scenario)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = $"{Flag} {scenario}",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start settings verification child process for scenario '{scenario}'.");

        if (!process.WaitForExit((int)s_timeout.TotalMilliseconds))
        {
            process.Kill();
            throw new TimeoutException($"Settings verification child process timed out for scenario '{scenario}'.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Settings verification child process failed for scenario '{scenario}' with exit code {process.ExitCode}.");
    }

    static void RestoreOriginalSettings(bool hadOriginalSettings, string backupPath)
    {
        if (File.Exists(Settings.SettingsPath))
            File.Delete(Settings.SettingsPath);

        if (!hadOriginalSettings)
            return;

        File.Move(backupPath, Settings.SettingsPath);
    }

    static void DeleteCorruptRecoveryFiles(HashSet<string> existingRecoveryFiles)
    {
        var directory = Path.GetDirectoryName(Settings.SettingsPath);
        foreach (var file in Directory.GetFiles(directory, "Flarial.Launcher.Settings.corrupt.*.json"))
            if (!existingRecoveryFiles.Contains(file))
                File.Delete(file);
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
