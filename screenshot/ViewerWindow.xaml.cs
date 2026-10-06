using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace screenshot;

/// <summary>
/// Full-screen image viewer. Click anywhere or press Esc to close. The hint
/// pill teaches once per view (2s), then gets out of the way; any mouse
/// movement brings it back briefly.
/// </summary>
public partial class ViewerWindow : Window
{
    private readonly DispatcherTimer _hintTimer;

    public ViewerWindow(ImageSource image)
    {
        InitializeComponent();
        WindowIcon.Apply(this);
        Img.Source = image;
        Activated += (_, _) => Focus();

        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            HintPill.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
        };
        Loaded += (_, _) => _hintTimer.Start();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        HintPill.BeginAnimation(OpacityProperty, null);
        HintPill.Opacity = 1;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void OnClose(object sender, MouseButtonEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // With an IME active, WPF reports Alt-routed keys as Key.System;
        // SystemKey carries the real key. Esc must close either way.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) Close();
    }
}
