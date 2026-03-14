using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Flarial.Launcher.Services.Core;
using Flarial.Launcher.Services.Management;
using static System.Environment;

namespace Flarial.Launcher.Pages;

/// <summary>
/// Interaction logic for SettingsGeneralPage.xaml
/// </summary>
public partial class SettingsGeneralPage : Page
{
    // ReSharper disable once MemberCanBePrivate.Global
    public double DllSelectionItemWidth { get; } = 120;
    // ReSharper disable once MemberCanBePrivate.Global
    public double DllSelectionItemMargin { get; } = 10;

    public static ToggleButton saveButton;

    const string SaveFailureMessage = "Failed to save launcher settings.";

    readonly Settings _settings = Settings.Current;

    //    readonly TextBlock _launcherFolderButtonTextBlock, _clientFolderButtonTextBlock;

    // readonly Border _launcherFolderButtonBorder, _clientFolderButtonBorder;

    //  static readonly string _clientPath;

    static readonly ProcessStartInfo s_gdk;

    static readonly ProcessStartInfo s_uwp;

    static SettingsGeneralPage()
    {
        var localAppDataPath = GetFolderPath(SpecialFolder.LocalApplicationData);

        s_gdk = new()
        {
            UseShellExecute = true,
            FileName = Path.Combine(localAppDataPath, @"Flarial\Client")
        };

        s_uwp = new()
        {
            UseShellExecute = true,
            FileName = Path.Combine(localAppDataPath, @"Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\RoamingState\Flarial")
        };
    }

    readonly static Brush s_brush = (Brush)new BrushConverter().ConvertFromString("#3F2A2D");

    static void OverrideLaunchButtonTemplate(Button button, string text)
    {
        button.ApplyTemplate();

        var launcherFolderButtonTextBlock = (TextBlock)button.Template.FindName("LaunchText", button);
        launcherFolderButtonTextBlock.Margin = new();
        launcherFolderButtonTextBlock.Text = text;

        var launcherFolderButtonBorder = (Border)button.Template.FindName("MainBorder", button);
        launcherFolderButtonBorder.Background = s_brush;

        var launcherFolderButtonIcon = (System.Windows.Shapes.Path)button.Template.FindName("LaunchIcon", button);
        launcherFolderButtonIcon.Data = null;
    }

    public SettingsGeneralPage()
    {
        InitializeComponent();
        DataContext = this;

        // Start - Overrides some stuff from the Launch Button template.

        OverrideLaunchButtonTemplate(ClientFolderButton, "📁 Client Folder");
        OverrideLaunchButtonTemplate(LauncherFolderButton, "📁 Launcher Folder");

        // End - Overrides some stuff from the Launch Button template.

        saveButton = SaveButton;

        /*tb1.Checked += (_, _) =>
        {
            if ((bool)tb2.IsChecked)
            {
                tb2.IsChecked = false;
                Config.UseBetaDLL = false;
            }
        };

        tb2.Checked += (_, _) =>
        {
            if ((bool)tb1.IsChecked)
            {
                tb1.IsChecked = false;
                Config.UseCustomDLL = false;
            }
        };*/

        LauncherFolderButton.Click += (_, _) =>
        {
            using (Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = CurrentDirectory
            })) { }
        };

        ClientFolderButton.Click += (_, _) =>
        {
            if (!Minecraft.IsInstalled)
            {
                MainWindow.CreateMessageBox("Please install the game.");
                return;
            }

            var startInfo = Minecraft.UsingGameDevelopmentKit ? s_gdk : s_uwp;

            if (!Directory.Exists(startInfo.FileName))
            {
                MainWindow.CreateMessageBox("Please launch the client at least once to generate its folder.");
                return;
            }

            using (Process.Start(startInfo)) { }
        };

        if (string.IsNullOrWhiteSpace(CustomTargetTextBox.Text))
            CustomTargetTextBox.Text = "Minecraft.Windows.exe";

        Loaded += Window_ContentRendered;
    }

    void Window_ContentRendered(object sender, RoutedEventArgs args)
    {
        switch (_settings.DllBuild)
        {
            case DllBuild.Release:
                ReleaseRadioButton.IsChecked = true;
                break;

            case DllBuild.Beta:
                if (BetaRadioButton.IsEnabled) BetaRadioButton.IsChecked = true;
                else ReleaseRadioButton.IsChecked = true;
                break;

            case DllBuild.Nightly:
                if (NightlyRadioButton.IsEnabled) NightlyRadioButton.IsChecked = true;
                else ReleaseRadioButton.IsChecked = true;
                break;

            case DllBuild.Custom:
                CustomRadioButton.IsChecked = true;
                break;
        }

        AutoLogin.IsChecked = _settings.AutoLogin;
        HardwareAcceleration.IsChecked = _settings.HardwareAcceleration;
        SaveOnTray.IsChecked = _settings.SaveOnTray;

        WaitForInitialization.IsChecked = _settings.WaitForInitialization;
        CustomTargetInjection.IsChecked = _settings.CustomTargetInjection;
        CustomTargetTextBox.Text = string.IsNullOrWhiteSpace(_settings.CustomTargetProcessName)
            ? "Minecraft.Windows.exe"
            : _settings.CustomTargetProcessName;

        SettingsPage.SetDllPageVisibility(_settings.DllBuild is DllBuild.Custom);

        if (_settings.CustomTargetInjection)
            Animations.ToggleButtonTransitions.CheckedAnimation(CustomTargetGrid);

        CustomTargetTextBox.IsEnabled = _settings.CustomTargetInjection;


        var window = (MainWindow)Application.Current.MainWindow;
        if (window is not null)
        {
            DisableAutoVoid.IsChecked = _settings.DisableAutoVoid;
            window.SetAutoVoidDisabled(_settings.DisableAutoVoid);
        }

        Loaded -= Window_ContentRendered;
    }

    void WaitForInitializationClick(object sender, RoutedEventArgs args)
    {
        var button = (ToggleButton)sender;
        if (button.IsChecked is not bool @checked) return;
        _settings.WaitForInitialization = @checked;
        _settings.TrySave(SaveFailureMessage);
    }

    void HardwareAcceleration_Click(object sender, RoutedEventArgs e)
    {
        var settings = Settings.Current;
        var button = (ToggleButton)sender;

        if (button.IsChecked is not bool @checked)
            return;

        settings.HardwareAcceleration = @checked;
        settings.TrySave(SaveFailureMessage);
    }

    void SaveOnTray_Click(object sender, RoutedEventArgs e)
    {
        var settings = Settings.Current;
        var button = (ToggleButton)sender;

        if (button.IsChecked is not bool @checked)
            return;

        settings.SaveOnTray = @checked;
        settings.TrySave(SaveFailureMessage);
    }

    void DisableAutoVoid_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is not MainWindow window)
            return;

        var button = (ToggleButton)sender;
        if (button.IsChecked is not bool @checked)
            return;

        _settings.DisableAutoVoid = @checked;
        window.SetAutoVoidDisabled(@checked);
        _settings.TrySave(SaveFailureMessage);
    }

    private void ToggleButton_Click_1(object sender, RoutedEventArgs e)
    {
        var settings = Settings.Current;
        var button = (ToggleButton)sender;

        if (button.IsChecked is not bool @checked)
            return;

        settings.AutoLogin = @checked;
        settings.TrySave(SaveFailureMessage);
    }

    private void BuildChanged(object sender, RoutedEventArgs e)
    {
        int.TryParse((sender as RadioButton)?.Tag.ToString(), out var num);
        var animation = new ThicknessAnimation
        {
            Duration = TimeSpan.FromMilliseconds(250),
            To = new Thickness(num * DllSelectionItemWidth + num * DllSelectionItemMargin, 0, 0, 0),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        BuildSelectedBorder.BeginAnimation(MarginProperty, animation);

        var settings = Settings.Current;
        var button = (RadioButton)sender;
        var content = $"{button.Content}";

        if (Enum.TryParse<DllBuild>(content, out var build))
            settings.DllBuild = build;

        SettingsPage.SetDllPageVisibility(build is DllBuild.Custom);

        if (build is DllBuild.Custom)
            Animations.ToggleButtonTransitions.CheckedAnimation(CustomTargetGrid);
        else
        {
            settings.CustomTargetInjection = false;
            CustomTargetInjection.IsChecked = false;
            CustomTargetTextBox.IsEnabled = false;
            Animations.ToggleButtonTransitions.UnCheckedAnimation(CustomTargetGrid);
        }

        settings.TrySave(SaveFailureMessage);
    }

    void CustomTargetInjection_Click(object sender, RoutedEventArgs args)
    {
        var button = (ToggleButton)sender;
        if (button.IsChecked is not bool @checked) return;

        _settings.CustomTargetInjection = @checked;

        if (@checked)
        {
            CustomTargetTextBox.IsEnabled = true;
            Animations.ToggleButtonTransitions.CheckedAnimation(CustomTargetGrid);
        }
        else
        {
            CustomTargetTextBox.IsEnabled = false;
            Animations.ToggleButtonTransitions.UnCheckedAnimation(CustomTargetGrid);
        }

        _settings.TrySave(SaveFailureMessage);
    }

    void CustomTargetTextBox_OnTextChanged(object sender, TextChangedEventArgs args)
    {
        _settings.CustomTargetProcessName = string.IsNullOrWhiteSpace(CustomTargetTextBox.Text)
            ? "Minecraft.Windows.exe"
            : CustomTargetTextBox.Text;
        _settings.TrySave(SaveFailureMessage);
    }
}
