using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Flarial.Launcher.Managers;

namespace Flarial.Launcher.Styles;

public partial class BackupItem : UserControl
{
    public string Time { get; set; }
    public string Path { get; set; }

    public BackupItem()
    {
        InitializeComponent();
        DataContext = this;
    }

    async void LoadBackup(object sender, RoutedEventArgs e)
    {
        IsHitTestVisible = false;
        IDisposable launcherBusy = null;

        MainWindow.CreateMessageBox("Loading the backup. This may take some time!");
        MainWindow.CreateMessageBox("Don't launch Minecraft in the mean time.");

        try
        {
            launcherBusy = (Application.Current.MainWindow as MainWindow)?.BeginBlockingLauncherOperation(
                "Restoring...",
                "The launcher cannot be closed while a backup restore is in progress.",
                "Restoring backup...");

            var result = await BackupManager.LoadBackup(Path);
            MainWindow.CreateMessageBox(result.Message);
        }
        finally
        {
            launcherBusy?.Dispose();
            IsHitTestVisible = true;
        }
    }

    async void DeleteBackup(object sender, RoutedEventArgs e)
    {
        IsHitTestVisible = false;

        try
        {
            var result = await BackupManager.DeleteBackup(Path);
            if (!result.Success)
            {
                MainWindow.CreateMessageBox(result.Message);
                return;
            }

            var animationX = new DoubleAnimation
            {
                To = 0,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                Duration = TimeSpan.FromMilliseconds(250)
            };
            var animationY = animationX.Clone();

            var storyboard = new Storyboard();

            Storyboard.SetTarget(animationX, this);
            Storyboard.SetTargetProperty(animationX, new PropertyPath("RenderTransform.ScaleX"));
            Storyboard.SetTarget(animationY, this);
            Storyboard.SetTargetProperty(animationY, new PropertyPath("RenderTransform.ScaleY"));

            storyboard.Children.Add(animationX);
            storyboard.Children.Add(animationY);

            storyboard.Begin(this);

            await Task.Delay(animationX.Duration.TimeSpan);

            (this.VisualParent as VirtualizingStackPanel)?.Children.Remove(this);
            MainWindow.CreateMessageBox(result.Message);
        }
        finally
        {
            IsHitTestVisible = true;
        }

    }
}
