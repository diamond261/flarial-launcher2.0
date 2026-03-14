using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Flarial.Launcher.Managers;
using Flarial.Launcher.Services.Core;
using Flarial.Launcher.Services.Management.Versions;

namespace Flarial.Launcher.Pages;

public partial class SettingsSwitcherPage : Page
{
    const int PageSize = 10;
    static readonly TimeSpan s_removeTimeout = TimeSpan.FromMinutes(2);

    sealed class VersionRow
    {
        public string Label { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public VersionEntry Entry { get; set; }
        public bool IsInstalled { get; set; }
        public string StatusText { get; set; } = "Double-click to install";
    }

    readonly List<VersionRow> _rows = [];
    readonly SwitcherOperationGate _operationGate = new();

    bool _loading;
    string _searchText = string.Empty;
    string _latestUwpVersion = "n/a";
    string _latestGdkVersion = "n/a";
    int _pageIndex;

    public SettingsSwitcherPage()
    {
        InitializeComponent();
        _ = LoadCatalogAsync();
    }

    async Task LoadCatalogAsync()
    {
        if (_loading)
            return;

        _loading = true;
        ProgressText.Text = "Loading versions...";

        try
        {
            var catalog = await VersionCatalog.GetAsync();
            var installedState = Minecraft.GetInstallState();
            var installedVersion = SettingsSwitcherLogic.NormalizeVersion(installedState.Version);

            _rows.Clear();
            _latestUwpVersion = catalog.LatestUwpVersion;
            _latestGdkVersion = catalog.LatestGdkVersion;

            foreach (var item in catalog.InstallableEntries)
            {
                var entry = item.Entry;
                var platform = item.Platform;

                _rows.Add(new VersionRow
                {
                    Label = item.Version,
                    Platform = platform,
                    Entry = entry,
                    StatusText = "Double-click to install",
                    IsInstalled = platform == installedState.Platform
                        && string.Equals(SettingsSwitcherLogic.NormalizeVersion(item.Version), installedVersion, StringComparison.OrdinalIgnoreCase)
                });
            }

            _rows.Sort((left, right) =>
            {
                var versionComparison = VersionCatalog.CompareVersions(left.Label, right.Label);
                return versionComparison != 0
                    ? versionComparison
                    : StringComparer.OrdinalIgnoreCase.Compare(left.Platform, right.Platform);
            });

            Logger.Info($"Switcher loaded UWP={_rows.Count(_ => _.Platform == "UWP")} latest={_latestUwpVersion}, GDK={_rows.Count(_ => _.Platform == "GDK")} latest={_latestGdkVersion}");

            SyncInstalledRows(installedState);
            ApplyFilter();
            ProgressText.Text = _rows.Count == 0 ? "No versions available right now" : $"{_rows.Count} versions available";
        }
        catch (Exception ex)
        {
            Logger.Error("Switcher load failed", ex);
            ProgressText.Text = "Failed to load versions";
            MainWindow.CreateMessageBox($"Switcher load failed: {ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    static bool IsRunningAsAdministrator()
    {
        var identity = WindowsIdentity.GetCurrent();
        if (identity is null)
            return false;

        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    static void CleanupStaleSwitcherTempDirectories()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var directories = Directory.GetDirectories(tempPath, "flarial-switch-*");

            foreach (var directory in directories)
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }
        catch { }
    }

    static void StopSafeInstallBlockers()
    {
        var currentProcessId = Process.GetCurrentProcess().Id;
        var safeProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Flarial.Launcher",
            "XboxPcAppFT"
        };

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentProcessId)
                    continue;

                if (!safeProcessNames.Contains(process.ProcessName))
                    continue;

                process.Kill();
                process.WaitForExit(3000);
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }
    }

    static async Task PrepareInstallEnvironmentAsync()
    {
        await Task.Run(() =>
        {
            StopSafeInstallBlockers();
            CleanupStaleSwitcherTempDirectories();
        });
    }

    void SyncInstalledRows(MinecraftInstallState state)
    {
        var installedVersion = SettingsSwitcherLogic.NormalizeVersion(state.Version);

        foreach (var item in _rows)
        {
            item.IsInstalled = state.IsInstalled
                && string.Equals(item.Platform, state.Platform, StringComparison.OrdinalIgnoreCase)
                && string.Equals(SettingsSwitcherLogic.NormalizeVersion(item.Label), installedVersion, StringComparison.OrdinalIgnoreCase);
        }
    }

    void SetSwitcherBusy(bool isBusy)
    {
        if (isBusy)
            Keyboard.ClearFocus();

        IsHitTestVisible = !isBusy;
        Opacity = isBusy ? 0.98 : 1.0;

        if (BusyOverlay is not null)
            BusyOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    bool TryBeginSwitchOperation()
    {
        if (!_operationGate.TryBegin())
            return false;

        SetSwitcherBusy(true);
        return true;
    }

    void EndSwitchOperation()
    {
        SetSwitcherBusy(false);
        _operationGate.End();
    }

    void ApplyFilter()
    {
        if (VersionsList is null || AllTab is null || UwpTab is null || GdkTab is null || PageInfoText is null || PreviousPageButton is null || NextPageButton is null)
            return;

        IEnumerable<VersionRow> rows = _rows;
        var selected = AllTab.IsChecked is true ? "All" : UwpTab.IsChecked is true ? "UWP" : "GDK";

        if (selected == "UWP")
            rows = rows.Where(row => row.Platform == "UWP");
        else if (selected == "GDK")
            rows = rows.Where(row => row.Platform == "GDK");

        if (_searchText.Length > 0)
            rows = rows.Where(row => row.Label.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || row.Platform.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

        var filteredRows = rows.ToList();
        var totalPages = Math.Max(1, (int)Math.Ceiling(filteredRows.Count / (double)PageSize));

        if (_pageIndex >= totalPages)
            _pageIndex = totalPages - 1;

        if (_pageIndex < 0)
            _pageIndex = 0;

        var visibleRows = filteredRows
            .Skip(_pageIndex * PageSize)
            .Take(PageSize)
            .ToList();

        VersionsList.ItemsSource = visibleRows;
        PageInfoText.Text = filteredRows.Count == 0 ? "Page 0 / 0" : $"Page {_pageIndex + 1} / {totalPages}";
        PreviousPageButton.IsEnabled = filteredRows.Count > 0 && _pageIndex > 0;
        NextPageButton.IsEnabled = filteredRows.Count > 0 && _pageIndex < totalPages - 1;
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = sender is TextBox textBox ? textBox.Text.Trim() : string.Empty;
        _pageIndex = 0;

        try { ApplyFilter(); }
        catch (Exception exception) { Logger.Error("Switcher search apply failed", exception); }
    }

    void PlatformTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tab)
            return;

        AllTab.IsChecked = ReferenceEquals(tab, AllTab);
        UwpTab.IsChecked = ReferenceEquals(tab, UwpTab);
        GdkTab.IsChecked = ReferenceEquals(tab, GdkTab);
        _pageIndex = 0;

        try { ApplyFilter(); }
        catch (Exception exception) { Logger.Error("Switcher filter apply failed", exception); }
    }

    void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex <= 0)
            return;

        _pageIndex--;

        try { ApplyFilter(); }
        catch (Exception exception) { Logger.Error("Switcher previous page apply failed", exception); }
    }

    void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        _pageIndex++;

        try { ApplyFilter(); }
        catch (Exception exception) { Logger.Error("Switcher next page apply failed", exception); }
    }

    async void VersionsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;

        var installedState = Minecraft.GetInstallState();

        if (VersionsList.SelectedItem is not VersionRow row)
        {
            MainWindow.CreateMessageBox("Please select a version first.");
            return;
        }

        if (!TryBeginSwitchOperation())
        {
            MainWindow.CreateMessageBox("A version switch is already in progress.");
            return;
        }

        var removedCurrentVersion = false;
        string backupName = null;
        string restoreBackupName = null;
        var stage = SwitcherStage.Backup;
        var plan = SettingsSwitcherLogic.CreatePlan(installedState, row.Label, row.Platform);
        var restorePolicy = SettingsSwitcherLogic.GetRestorePolicy(plan);
        IDisposable launcherBusy = null;

        try
        {
            launcherBusy = (Application.Current.MainWindow as MainWindow)?.BeginBlockingLauncherOperation(
                "Switching...",
                "The launcher cannot be closed while a version switch is in progress.",
                "Switching Minecraft version...");

            if (!IsRunningAsAdministrator())
            {
                MainWindow.CreateMessageBox("Please run launcher as administrator first.");
                ProgressText.Text = "Run as administrator required";
                return;
            }

            var riskMessage = SettingsSwitcherLogic.GetPreSwitchRiskMessage(plan);
            if (!string.IsNullOrWhiteSpace(riskMessage))
                MainWindow.CreateMessageBox(riskMessage);

            if (restorePolicy == SwitcherRestorePolicy.RestoreLatestUwpBackupIfAvailable)
            {
                restoreBackupName = await BackupManager.FindLatestBackupAsync("UWP");
                if (string.IsNullOrWhiteSpace(restoreBackupName))
                {
                    MainWindow.CreateMessageBox("No existing UWP backup was found. The switch will continue, but there is a potential risk of losing cross-platform data.");
                }
            }

            if (plan.RequiresBackup)
            {
                ProgressText.Text = $"Creating backup before {plan.ActionLabel}...";
                backupName = await BackupManager.CreateVersionSwitchBackupAsync();
                MainWindow.CreateMessageBox($"Backup created: {backupName}");

                if (restorePolicy == SwitcherRestorePolicy.RestoreCreatedBackup)
                    restoreBackupName = backupName;
            }

            if (plan.RequiresRemoval)
            {
                stage = SwitcherStage.Remove;
                ProgressText.Text = $"Removing current {installedState.Platform} version ({installedState.Version}) before {plan.ActionLabel}...";

                await PrepareInstallEnvironmentAsync();

                var removeTask = Minecraft.RemoveAsync();
                var completed = await Task.WhenAny(removeTask, Task.Delay(s_removeTimeout));
                if (!ReferenceEquals(completed, removeTask))
                    throw new TimeoutException($"Timed out while removing current version after {s_removeTimeout.TotalMinutes:0} minutes.");

                await removeTask;
                removedCurrentVersion = true;
            }

            stage = SwitcherStage.Install;
            ProgressText.Text = $"Installing {plan.TargetLabel}...";

            await PrepareInstallEnvironmentAsync();

            var request = await row.Entry.InstallAsync(row.Label, row.Platform, value => Dispatcher.Invoke(() =>
            {
                ProgressText.Text = $"Installing {plan.TargetLabel}... {value}%";
            }));

            await request;
            stage = SwitcherStage.Verify;
            ProgressText.Text = $"Verifying {plan.TargetLabel}...";

            var verified = await SettingsSwitcherLogic.VerifyInstalledTargetAsync(plan.TargetVersion, plan.TargetPlatform, verifyException =>
            {
                Logger.Error("Switcher install verification check failed", verifyException);
            });

            if (!verified)
                throw new Exception($"Installation completed but version verification failed. Expected {plan.TargetLabel}.");

            if (!string.IsNullOrWhiteSpace(restoreBackupName))
            {
                stage = SwitcherStage.Restore;
                ProgressText.Text = $"Restoring data from backup {restoreBackupName}...";

                var restoreResult = await BackupManager.LoadBackup(restoreBackupName);
                if (!restoreResult.Success)
                {
                    if (restorePolicy == SwitcherRestorePolicy.RestoreLatestUwpBackupIfAvailable)
                    {
                        Logger.Error("Switcher optional UWP restore failed", new InvalidOperationException(restoreResult.Message));
                        MainWindow.CreateMessageBox($"Installed {plan.TargetLabel}, but applying UWP backup failed: {restoreResult.Message}");
                    }
                    else
                    {
                        throw new InvalidOperationException(restoreResult.Message);
                    }
                }
            }

            SyncInstalledRows(Minecraft.GetInstallState());
            ApplyFilter();
            ProgressText.Text = plan.SuccessProgressText;
            MainWindow.CreateMessageBox(plan.SuccessMessage);
        }
        catch (Exception ex)
        {
            if (ex is DeploymentOperationException deploymentException)
                Logger.Error("Switcher install failed", ex, SettingsSwitcherLogic.GetDeploymentLogFields(deploymentException, plan, removedCurrentVersion, backupName, stage));
            else
                Logger.Error("Switcher install failed", ex);

            var failure = SettingsSwitcherLogic.CreateFailureResult(ex, plan, removedCurrentVersion, backupName, stage);
            ProgressText.Text = failure.ProgressText;
            MainWindow.CreateMessageBox(failure.Message);
        }
        finally
        {
            launcherBusy?.Dispose();
            EndSwitchOperation();
            MainWindow.RefreshGameVersionLabel();
        }
    }
}
