using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Flarial.Launcher.Managers;
using Flarial.Launcher.Styles;

namespace Flarial.Launcher.Pages;

public partial class SettingsBackupPage : Page
{
    private static VirtualizingStackPanel _stackPanel;

    public SettingsBackupPage()
    {
        InitializeComponent();

        _stackPanel = BackupStackPanel;
    }

    public static void AddBackupItem(string time, string path) => _stackPanel.Children.Add(new BackupItem { Time = time, Path = path });

    //just placeholder stuff
    private void SettingsBackupPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                List<string> backups = await BackupManager.GetAllBackupsAsync();

                _stackPanel.Children.Clear();

                foreach (string backup in backups)
                {
                    var displayName = backup.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        ? System.IO.Path.GetFileNameWithoutExtension(backup)
                        : backup;
                    AddBackupItem(displayName, backup);
                }
            }
            catch (Exception exception)
            {
                MainWindow.CreateMessageBox($"Failed to load backups: {exception.Message}");
            }
        });
    }
}
