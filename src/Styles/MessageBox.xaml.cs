using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Flarial.Launcher.Styles
{
    /// <summary>
    /// Interaction logic for MessageBox.xaml
    /// </summary>
    public partial class MessageBox : UserControl
    {
        internal static readonly TimeSpan DefaultDismissDelay = TimeSpan.FromSeconds(3);
        static readonly TimeSpan s_closeAnimationDuration = TimeSpan.FromMilliseconds(200);

        public string Text { get; set; }
        public bool ShowFlarialLogo { get; set; }
        public event EventHandler Closed;

        bool _closing;
        bool _closed;
        readonly DispatcherTimer _closeTimer;
        readonly TaskCompletionSource<bool> _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MessageBox()
        {
            InitializeComponent();
            this.DataContext = this;
            Text = "temp";

            _closeTimer = new DispatcherTimer
            {
                Interval = DefaultDismissDelay
            };
            _closeTimer.Tick += async (_, _) =>
            {
                _closeTimer.Stop();
                await CloseAsync();
            };

            Loaded += MessageBox_OnLoaded;
            Unloaded += (_, _) => _closeTimer.Stop();
        }

        private async void ButtonBase_OnClick(object sender, RoutedEventArgs e)
            => await CloseAsync();

        public Task DismissAsync() => CloseAsync();

        private void MessageBox_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_closing || _closed)
                return;

            _closeTimer.Stop();
            _closeTimer.Start();
        }

        async Task CloseAsync()
        {
            if (_closing)
            {
                await _closeCompletion.Task;
                return;
            }

            _closing = true;
            _closeTimer.Stop();

            if (!IsLoaded)
            {
                CompleteClose();
                return;
            }

            var sb = new Storyboard();

            var an1 = new DoubleAnimation
            {
                Duration = s_closeAnimationDuration,
                To = 0,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var an2 = new DoubleAnimation
            {
                Duration = s_closeAnimationDuration,
                To = 0,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var an3 = new ThicknessAnimation
            {
                Duration = s_closeAnimationDuration,
                To = new Thickness(0, 25, 0, -25),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            Storyboard.SetTarget(an1, this);
            Storyboard.SetTargetProperty(an1, new PropertyPath("RenderTransform.ScaleX"));
            Storyboard.SetTarget(an2, this);
            Storyboard.SetTargetProperty(an2, new PropertyPath("RenderTransform.ScaleY"));
            Storyboard.SetTarget(an3, this);
            Storyboard.SetTargetProperty(an3, new PropertyPath(MarginProperty));

            sb.Children.Add(an1);
            sb.Children.Add(an2);
            sb.Children.Add(an3);

            TaskCompletionSource<bool> animationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            sb.Completed += (_, _) => animationCompletion.TrySetResult(true);
            sb.Begin(this);

            _ = Task.Delay(s_closeAnimationDuration + TimeSpan.FromMilliseconds(100))
                .ContinueWith(_ => Dispatcher.InvokeAsync(() => animationCompletion.TrySetResult(true), DispatcherPriority.Send));

            await animationCompletion.Task;
            CompleteClose();
        }

        void CompleteClose()
        {
            if (_closed)
                return;

            _closed = true;
            RemoveFromVisualTree();
            Closed?.Invoke(this, EventArgs.Empty);
            _closeCompletion.TrySetResult(true);
        }

        void RemoveFromVisualTree()
        {
            if (Parent is Panel directPanel)
            {
                directPanel.Children.Remove(this);
                return;
            }

            DependencyObject current = this;
            while (current is not null)
            {
                if (current is Panel panel)
                {
                    panel.Children.Remove(this);
                    return;
                }

                if (current is ContentControl contentControl && ReferenceEquals(contentControl.Content, this))
                {
                    contentControl.Content = null;
                    return;
                }

                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) as DependencyObject;
            }
        }
    }
}
