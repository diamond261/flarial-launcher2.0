using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Flarial.Launcher.Managers;

namespace Flarial.Launcher.Styles;

internal static class NotificationVerificationRunner
{
    static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);

    internal static async Task<int> RunAsync()
    {
        Logger.Info("Notification verification started");

        try
        {
            const string success = "Notification verification passed.";
            await WithTimeoutAsync(RunCoreAsync(), s_timeout, "Notification verification timed out.");
            Logger.Info(success);
            Console.WriteLine(success);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Notification verification failed", ex);
            Console.Error.WriteLine($"Notification verification failed: {ex.Message}");
            return 1;
        }
    }

    static async Task RunCoreAsync()
    {
        Window window = new()
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            Content = new StackPanel()
        };

        var host = (StackPanel)window.Content;

        try
        {
            window.Show();
            await WaitForUiAsync();

            var first = MainWindow.ShowMessageBoxAsync(host, "first");
            var second = MainWindow.ShowMessageBoxAsync(host, "second");
            var third = MainWindow.ShowMessageBoxAsync(host, "third");
            await Task.WhenAll(first, second, third);
            await WaitForUiAsync();

            if (host.Children.Count != 1)
                throw new InvalidOperationException($"Expected exactly one active notification after rapid reuse, found {host.Children.Count}.");

            if (host.Children[0] is not MessageBox messageBox || !string.Equals(messageBox.Text, "third", StringComparison.Ordinal))
                throw new InvalidOperationException("Rapid reuse did not leave the latest notification active.");

            await messageBox.DismissAsync();
            await WaitForUiAsync();

            if (host.Children.Count != 0)
                throw new InvalidOperationException("Manual notification dismissal left orphaned visuals.");

            await MainWindow.ShowMessageBoxAsync(host, "timer");
            await WaitForUiAsync();

            if (host.Children.Count != 1)
                throw new InvalidOperationException("Timed notification did not appear.");

            await Task.Delay(MessageBox.DefaultDismissDelay + TimeSpan.FromMilliseconds(600));
            await WaitForUiAsync();

            if (host.Children.Count != 0)
                throw new InvalidOperationException("Timed notification dismissal left orphaned visuals.");
        }
        finally
        {
            window.Close();
        }
    }

    static Task WaitForUiAsync()
        => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    static async Task WithTimeoutAsync(Task task, TimeSpan timeout, string message)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            throw new TimeoutException(message);

        await task;
    }
}
