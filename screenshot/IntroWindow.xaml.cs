using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace screenshot;

/// <summary>
/// First-run greeting: a single pill teaching the one shortcut, shown once
/// in the app's lifetime. Fades in, lingers ~3s, fades out, gone forever.
/// </summary>
public partial class IntroWindow : Window
{
    public IntroWindow()
    {
        InitializeComponent();
        WindowIcon.Apply(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }
}
