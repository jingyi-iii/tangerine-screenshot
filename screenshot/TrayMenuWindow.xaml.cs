using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using screenshot.Services;

namespace screenshot;

/// <summary>
/// The tray icon's right-click menu, as a themed popup window. It replaces the
/// WinForms ContextMenuStrip that used to live in <see cref="TrayService"/>: a
/// strip draws its own fixed chrome and cannot follow the app's light/dark theme,
/// which left the menu as the one surface in the app that looked like 2005.
///
/// Positioning uses the icon rect the shell reports (physical pixels) plus the
/// window's real DPI, measured after the first render — the menu has to sit
/// against the icon on whatever monitor and scale the taskbar is on.
/// </summary>
public partial class TrayMenuWindow : Window
{
    /// <summary>
    /// Transparent room the drop shadow needs, in DIPs. Mirrors the root grid's
    /// margin and is subtracted whenever the surface is aligned to the icon.
    /// </summary>
    private const double ShadowPad = 18;

    /// <summary>Gap between the icon and the menu surface, in DIPs.</summary>
    private const double IconGap = 6;

    /// <summary>
    /// A tray click can bounce activation back to the shell a few frames after we
    /// open, and closing on that would make the menu flash and vanish. Inside this
    /// window a deactivation is ignored; the mouse capture below still closes the
    /// menu the moment the user clicks anywhere else.
    /// </summary>
    private const int DeactivationGraceMs = 250;

    private readonly TrayMenuAnchor? _anchor;
    private readonly List<Button> _rows = new();
    private bool _hotkeysUnavailable;
    private bool _opened;
    private bool _closing;
    private int _openedTick;

    public event EventHandler? RegionRequested;
    public event EventHandler? FullScreenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? HotkeyRetryRequested;

    /// <summary>
    /// True while the global hotkeys are unregistered: adds the retry row, in the
    /// theme's danger red, right where a user who lost their shortcut would look.
    /// </summary>
    public bool HotkeysUnavailable
    {
        get => _hotkeysUnavailable;
        set
        {
            _hotkeysUnavailable = value;
            ApplyHotkeyState();
        }
    }

    /// <param name="anchor">
    /// Where the icon is; null only for the QA render path, which draws the surface
    /// without ever opening it.
    /// </param>
    public TrayMenuWindow(TrayMenuAnchor? anchor = null)
    {
        InitializeComponent();
        _anchor = anchor;
        _rows.AddRange(new[] { RegionRow, FullRow, WarnRow, ExitRow });
        ApplyHotkeyState();
        Loaded += (_, _) => _opened = true;
        Closed += (_, _) => Mouse.Capture(null);
    }

    // ── Open / dismiss ───────────────────────────────────────

    /// <summary>Shows the menu anchored to the tray icon.</summary>
    public void Open()
    {
        if (_anchor is not { } anchor)
        {
            DiagnosticLog.Write("tray menu: no icon geometry from the shell — not opening");
            return;
        }

        // Provisional spot: the shell's physical pixels converted with the app's
        // usual primary-monitor scale. It only has to land the window on the icon's
        // monitor, because Place() re-does the maths with the DPI WPF measures once
        // the window is up.
        double scale = ScreenCapture.PrimaryScale;
        if (scale <= 0) scale = 1;
        Left = anchor.IconBounds.Left / scale;
        Top = anchor.IconBounds.Top / scale;

        ContentRendered += OnFirstRender;
        Show();
        _openedTick = Environment.TickCount;

        // Capture is the safety net for the case where the shell refuses us the
        // foreground: a window that never activated never gets Deactivated either,
        // so without this the menu would stay on screen until something else
        // happened. With it, the first click anywhere else closes the menu — the
        // same bargain WPF's own ContextMenu makes.
        Mouse.Capture(Root, CaptureMode.SubTree);
    }

    private void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;
        Place();
        PlayAppear();
        AssertOnTop();
    }

    /// <summary>Aligns the surface with the icon, staying inside that monitor's work area.</summary>
    private void Place()
    {
        if (_anchor is not { } anchor) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;

        // The shell reports physical pixels; WPF positions in DIPs.
        var icon = anchor.IconBounds;
        var work = anchor.WorkArea;
        double il = icon.Left / sx, it = icon.Top / sy, ir = icon.Right / sx, ib = icon.Bottom / sy;
        double wl = work.Left / sx, wt = work.Top / sy, wr = work.Right / sx, wb = work.Bottom / sy;

        // The icon hangs off the work area on the taskbar's edge; that is how we
        // know which way the menu should open. A menu that opens under the taskbar
        // is a menu nobody can read.
        bool taskbarBottom = icon.Top >= work.Bottom - 1;
        bool taskbarTop = icon.Bottom <= work.Top + 1;
        bool taskbarRight = icon.Left >= work.Right - 1;
        bool taskbarLeft = icon.Right <= work.Left + 1;

        double w = ActualWidth, h = ActualHeight;
        double x, y;
        if (taskbarLeft || taskbarRight)
        {
            // Side taskbar: open beside the icon, vertically centred on it.
            x = taskbarRight ? il - IconGap + ShadowPad - w : ir + IconGap - ShadowPad;
            y = (it + ib) / 2 - h / 2;
        }
        else
        {
            // Bottom/top taskbar (or an auto-hidden one, where the icon sits inside
            // the work area): right-align with the icon, overhanging it by a couple
            // of pixels so the surface lines up with the icon instead of stopping
            // just short of it.
            x = ir + 2 + ShadowPad - w;
            bool openUp = taskbarBottom || (!taskbarTop && (it + ib) / 2 > (wt + wb) / 2);
            y = openUp ? it - IconGap + ShadowPad - h : ib + IconGap - ShadowPad;
        }

        double minX = wl - ShadowPad, minY = wt - ShadowPad;
        x = Math.Clamp(x, minX, Math.Max(minX, wr + ShadowPad - w));
        y = Math.Clamp(y, minY, Math.Max(minY, wb + ShadowPad - h));

        Left = x;
        Top = y;
        DiagnosticLog.Write(
            $"tray menu: placed ({x:F0},{y:F0}) {w:F0}x{h:F0} dpi={sx:F2}x{sy:F2} " +
            $"icon=({il:F0},{it:F0},{ir:F0},{ib:F0}) work=({wl:F0},{wt:F0},{wr:F0},{wb:F0})");
    }

    private void PlayAppear()
    {
        if (!SystemParameters.MenuAnimation)
        {
            Root.Opacity = 1;
            AppearSlide.Y = 0;
            return;
        }
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
        AppearSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>
    /// Re-asserts topmost once the menu is up. A window shown while something else
    /// owns the foreground can end up behind it, which for a menu opened from the
    /// tray looks exactly like "right-click did nothing".
    /// </summary>
    private void AssertOnTop()
    {
        try
        {
            Topmost = false;
            Topmost = true;
            Activate();
            DiagnosticLog.Write($"tray menu: open, activated={IsActive}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"tray menu: could not assert topmost — {ex.Message}");
        }
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (!_opened || _closing) return;
        if (Environment.TickCount - _openedTick < DeactivationGraceMs) return;
        DiagnosticLog.Write("tray menu: closing (window deactivated)");
        CloseMenu();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_opened || _closing) return;

        // Losing the capture is not by itself a click away: pressing a row hands the
        // capture to that Button, and treating that as a dismissal closed the menu on
        // mouse-down, before the row's Click could ever fire. Only a loss that leaves
        // the cursor off the surface means the user clicked somewhere else — which is
        // the whole point of holding the capture.
        if (IsCursorOverSurface()) return;

        DiagnosticLog.Write("tray menu: closing (click outside)");
        CloseMenu();
    }

    /// <summary>
    /// Whether the mouse is over the menu surface right now, in physical pixels.
    /// Deliberately not <c>IsMouseOver</c>: that state is refreshed as part of the
    /// same input pass that raises the capture loss, so it can still describe where
    /// the cursor was a moment ago.
    /// </summary>
    private bool IsCursorOverSurface()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var cursor)) return false;
            var topLeft = Shell.PointToScreen(new Point(0, 0));
            var bottomRight = Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight));
            return cursor.X >= topLeft.X && cursor.X < bottomRight.X
                && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y;
        }
        catch
        {
            return false;
        }
    }

    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); }
        catch (Exception ex) { DiagnosticLog.Write($"tray menu: close failed — {ex.Message}"); }
    }

    // ── Keyboard ─────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseMenu();
                break;
            case Key.Down:
                e.Handled = true;
                MoveFocus(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveFocus(-1);
                break;
        }
    }

    /// <summary>
    /// Nothing is focused when the menu opens, so the first arrow key picks an end
    /// of the list — otherwise the menu would open with its first row already lit
    /// up, which reads as a hover the user never made.
    /// </summary>
    private void MoveFocus(int delta)
    {
        var visible = _rows.Where(r => r.IsVisible).ToList();
        if (visible.Count == 0) return;
        int current = Keyboard.FocusedElement is Button focused ? visible.IndexOf(focused) : -1;
        int next = current < 0
            ? (delta > 0 ? 0 : visible.Count - 1)
            : Math.Clamp(current + delta, 0, visible.Count - 1);
        visible[next].Focus();
    }

    // ── Row clicks ───────────────────────────────────────────

    // Every row closes the menu before it acts: the capture overlay and the preview
    // cards are shown by these handlers, and a topmost menu hanging over them would
    // be the first thing the user sees of the result.
    private void OnRegionClick(object sender, RoutedEventArgs e) => Raise(RegionRequested);
    private void OnFullScreenClick(object sender, RoutedEventArgs e) => Raise(FullScreenRequested);
    private void OnExitClick(object sender, RoutedEventArgs e) => Raise(ExitRequested);
    private void OnRetryClick(object sender, RoutedEventArgs e) => Raise(HotkeyRetryRequested);

    private void Raise(EventHandler? handler)
    {
        CloseMenu();
        handler?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyHotkeyState()
    {
        var visibility = _hotkeysUnavailable ? Visibility.Visible : Visibility.Collapsed;
        // The separator above the warning only exists to group it, so it travels
        // with it: a rule with nothing under it is what the old menu showed.
        WarnRow.Visibility = visibility;
        SepWarning.Visibility = visibility;
    }

    // ── QA render ────────────────────────────────────────────

    /// <summary>
    /// Draws the menu surface to a bitmap without opening a window, so
    /// <c>--menu-shot</c> can produce light and dark pictures of the menu on a
    /// machine nobody is sitting at. Returns the surface including its shadow margin.
    /// </summary>
    public BitmapSource RenderSurface()
    {
        // The surface starts invisible and slid down for the open animation; the
        // render has to see it at rest.
        Root.Opacity = 1;
        AppearSlide.Y = 0;

        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Root.Arrange(new Rect(new Point(0, 0), Root.DesiredSize));
        Root.UpdateLayout();

        // Sized from the laid-out content plus one shadow margin on each side. The
        // content bounds start at the origin and the margin is applied as a paint
        // offset, so both margins have to be added on top of them — sizing from the
        // element's own width instead clips the shadow off the right and bottom.
        var content = VisualTreeHelper.GetDescendantBounds(Root);
        double width = content.Right + ShadowPad * 2, height = content.Bottom + ShadowPad * 2;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Root);
        bitmap.Freeze();
        return bitmap;
    }
}
