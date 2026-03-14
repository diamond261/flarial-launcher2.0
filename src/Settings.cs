using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Flarial.Launcher.Managers;
using Flarial.Launcher.Services.Core;

namespace Flarial.Launcher;

internal enum DllBuild { Release, Beta, Nightly, Custom }

[DataContract]
sealed partial class Settings
{
    bool _hardwareAcceleration = true;

    [DataMember]
    internal bool HardwareAcceleration
    {
        get => _hardwareAcceleration;
        set
        {
            _hardwareAcceleration = value;
            RenderOptions.ProcessRenderMode = value ? RenderMode.Default : RenderMode.SoftwareOnly;
        }
    }

    [DataMember]
    internal string CustomDllPath = null;

    [DataMember]
    internal DllBuild DllBuild = DllBuild.Release;

    [DataMember]
    internal bool WaitForInitialization = true;

    [DataMember]
    internal bool CustomTargetInjection;

    [DataMember]
    internal string CustomTargetProcessName = "Minecraft.Windows.exe";

    [DataMember]
    internal bool SaveOnTray;

    [DataMember]
    internal bool DisableAutoVoid;

    [DataMember]
    internal Dictionary<string, string> DllPresets = [];

    [DataMember]
    internal string ActiveDllPreset = "Default";

    [DataMember]
    internal bool AutoLogin = true;
}

partial class Settings
{
    [OnDeserializing]
    private void OnDeserializing(StreamingContext context)
    {
        CustomDllPath = null;
        DllBuild = DllBuild.Release;
        WaitForInitialization = true;
        CustomTargetInjection = false;
        CustomTargetProcessName = "Minecraft.Windows.exe";
        SaveOnTray = false;
        DisableAutoVoid = false;
        DllPresets = [];
        ActiveDllPreset = "Default";
        AutoLogin = true;
        HardwareAcceleration = true;
    }
}

sealed partial class Settings
{
    static readonly object _lock = new();
    const string DefaultTargetProcessName = "Minecraft.Windows.exe";

    static Settings _current;

    internal static string SettingsPath => Path.Combine(VersionManagement.launcherPath, "Flarial.Launcher.Settings.json");

    internal static Settings Current
    {
        get
        {
            if (_current is not null)
                return _current;

            lock (_lock)
            {
                _current ??= LoadCurrentSettings();

                return _current;
            }
        }
    }

    static Settings LoadCurrentSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new();

            using var stream = File.OpenRead(SettingsPath);
            var settings = _serializer.ReadObject(stream) as Settings ?? new Settings();
            Sanitize(settings, out var recoveryNotes);

            if (recoveryNotes.Count > 0)
                Logger.Info($"Settings load recovered values | path={SettingsPath} | changes={string.Join("; ", recoveryNotes)}");

            return settings;
        }
        catch (Exception ex)
        {
            RecoverFromUnreadableSettings(ex);
            return new();
        }
    }

    static void Sanitize(Settings settings, out List<string> recoveryNotes)
    {
        recoveryNotes = [];

        if (!Enum.IsDefined(typeof(DllBuild), settings.DllBuild))
        {
            settings.DllBuild = DllBuild.Release;
            recoveryNotes.Add("DllBuild=Release");
        }

        if (string.IsNullOrWhiteSpace(settings.CustomTargetProcessName))
        {
            settings.CustomTargetProcessName = DefaultTargetProcessName;
            recoveryNotes.Add($"CustomTargetProcessName={DefaultTargetProcessName}");
        }

        if (settings.DllPresets is null)
        {
            settings.DllPresets = [];
            recoveryNotes.Add("DllPresets=[]");
        }

        if (string.IsNullOrWhiteSpace(settings.ActiveDllPreset))
        {
            settings.ActiveDllPreset = "Default";
            recoveryNotes.Add("ActiveDllPreset=Default");
        }
    }

    static void RecoverFromUnreadableSettings(Exception exception)
    {
        var recoveryPath = Path.Combine(VersionManagement.launcherPath, $"Flarial.Launcher.Settings.corrupt.{DateTime.Now:yyyyMMddHHmmssfff}.json");

        try
        {
            Directory.CreateDirectory(VersionManagement.launcherPath);

            if (File.Exists(SettingsPath))
                File.Move(SettingsPath, recoveryPath);

            Logger.Error("Settings load failed; moved corrupt settings aside and restored defaults", exception,
                ("SettingsPath", SettingsPath),
                ("RecoveryPath", File.Exists(recoveryPath) ? recoveryPath : string.Empty));
        }
        catch (Exception recoveryException)
        {
            Logger.Error("Settings load failed and corrupt settings recovery also failed", recoveryException,
                ("SettingsPath", SettingsPath),
                ("RecoveryPath", recoveryPath));
            Logger.Error("Original settings load failure", exception, ("SettingsPath", SettingsPath));
        }
    }

    internal static void ResetForVerification()
    {
        lock (_lock)
            _current = null;
    }

    static readonly DataContractJsonSerializer _serializer;

    static Settings() => _serializer = new(typeof(Settings), new DataContractJsonSerializerSettings
    {
        UseSimpleDictionaryFormat = true
    });
}

sealed partial class Settings
{
    internal bool TrySave(string failureMessage)
    {
        try
        {
            Save();
            return true;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(failureMessage)
                && Application.Current?.Dispatcher is { } dispatcher)
            {
                _ = dispatcher.InvokeAsync(() => MainWindow.CreateMessageBox(failureMessage));
            }

            return false;
        }
    }

    internal void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(VersionManagement.launcherPath);
                Sanitize(this, out var recoveryNotes);

                if (recoveryNotes.Count > 0)
                    Logger.Info($"Settings save normalized values | path={SettingsPath} | changes={string.Join("; ", recoveryNotes)}");

                var temporaryPath = $"{SettingsPath}.tmp";
                using (var stream = File.Create(temporaryPath))
                    _serializer.WriteObject(stream, this);

                if (File.Exists(SettingsPath))
                    File.Replace(temporaryPath, SettingsPath, null);
                else
                    File.Move(temporaryPath, SettingsPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save launcher settings", ex, ("SettingsPath", SettingsPath));
                throw;
            }
        }
    }
}
