using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using screenshot.Services;

namespace screenshot;

/// <summary>
/// Full-screen region selection overlay. Hovering snaps to the window under
/// the cursor — a plain click captures it; dragging draws a free rect; a click
/// on the bare desktop captures that monitor. Space captures the monitor,
/// Esc cancels, and a tiny accidental drag is a no-op. A pixel loupe follows
/// the cursor through the whole gesture for precise edges. Reports the result
/// (virtual-screen DIPs) via <see cref="Completed"/>.
/// </summary>
public partial class OverlayWindow : Window
{
    public event Action<Rect?>? Completed;

    private const int LoupeSpan = 13;       // physical pixels across the loupe
    private const double LoupeSize = 132;   // DIPs incl. border

    private static readonly uint OwnPid = NativeMethods.GetCurrentProcessId();
    private static readonly int RectSize = Marshal.SizeOf<NativeMethods.RECT>();

    private Point _start;
    private Point _current;
    private bool _dragging;
    private bool _finished;
    private BitmapSource? _frozen;
    private Rect? _chipRect;
    private IntPtr _snapHwnd;
    private Rect? _snapRect;   // snapped window, overlay-local DIPs
    private readonly DispatcherTimer _hintTimer;
    private bool _hintFading;

    // Scratch state for the EnumWindows callback (one hit-test at a time).
    private readonly System.Text.StringBuilder _className = new(64);
    private readonly NativeMethods.EnumWindowsProc _enumCallback;
    private Point _hitPoint;
    private IntPtr _hitWnd;
    private Rect? _hitFrame;

    public OverlayWindow()
    {
        InitializeComponent();
        _enumCallback = TrySnapCandidate;
        // Cover the whole virtual screen (all monitors).
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        LoupeHost.SizeChanged += (_, _) =>
            LoupeHost.Clip = new EllipseGeometry(
                new Rect(0, 0, LoupeHost.ActualWidth, LoupeHost.ActualHeight));

        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            FadeHint();
        };
    }

    public void Open()
    {
        // Freeze the frame once — the loupe reads pixels from this snapshot.
        _frozen = ScreenCapture.Capture(new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));

        Show();
        Activate();
        Focus();
        LayoutMask(null);
        if (NativeMethods.GetCursorPos(out var cursor))
            UpdateWindowHover(new Point(
                cursor.X / ScreenCapture.PrimaryScale - Left,
                cursor.Y / ScreenCapture.PrimaryScale - Top));

        if (Settings.HasSeenOverlayHint)
        {
            HintPill.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Teach once, then never again.
            Settings.HasSeenOverlayHint = true;
            CenterHint();
            _hintTimer.Start();
        }
    }

    private void CenterHint()
    {
        HintPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintPill, (ActualWidth - HintPill.DesiredSize.Width) / 2);
        Canvas.SetTop(HintPill, (ActualHeight - HintPill.DesiredSize.Height) / 2);
    }

    private void FadeHint()
    {
        if (_hintFading || HintPill.Visibility != Visibility.Visible) return;
        _hintFading = true;
        _hintTimer.Stop();
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
        anim.Completed += (_, _) => HintPill.Visibility = Visibility.Collapsed;
        HintPill.BeginAnimation(OpacityProperty, anim);
    }

    private Rect? Selection
    {
        get
        {
            if (!_dragging) return null;
            double x = Math.Min(_start.X, _current.X);
            double y = Math.Min(_start.Y, _current.Y);
            double w = Math.Abs(_current.X - _start.X);
            double h = Math.Abs(_current.Y - _start.Y);
            return new Rect(x, y, w, h);
        }
    }

    private void LayoutMask(Rect? sel)
    {
        double W = ActualWidth, H = ActualHeight;
        if (sel is not { } r || r.Width <= 0 || r.Height <= 0)
        {
            SetRect(MaskLeft, 0, 0, W, H);
            SetRect(MaskRight, 0, 0, 0, 0);
            SetRect(MaskTop, 0, 0, 0, 0);
            SetRect(MaskBottom, 0, 0, 0, 0);
            SelectionBox.Visibility = Visibility.Collapsed;
            SizeChip.Visibility = Visibility.Collapsed;
            _chipRect = null;
            return;
        }

        SetRect(MaskLeft, 0, 0, r.Left, H);
        SetRect(MaskRight, r.Right, 0, Math.Max(0, W - r.Right), H);
        SetRect(MaskTop, r.Left, 0, r.Width, r.Top);
        SetRect(MaskBottom, r.Left, r.Bottom, r.Width, Math.Max(0, H - r.Bottom));

        if (r.Width > 5 && r.Height > 5)
        {
            SelectionBox.Visibility = Visibility.Visible;
            SetRect(SelectionBox, r.Left, r.Top, r.Width, r.Height);

            // Physical pixel size is what the resulting image will be.
            double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            SizeText.Text = $"{Math.Round(r.Width * scale)} × {Math.Round(r.Height * scale)}";

            SizeChip.Visibility = Visibility.Visible;
            SizeChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double cw = SizeChip.DesiredSize.Width, ch = SizeChip.DesiredSize.Height;

            // Outside the selection, hugging its bottom-right corner — never
            // covering the pixels being captured. Flip above at the bottom edge.
            double cx = r.Right - cw;
            double cy = r.Bottom + 8;
            if (cy + ch > H - 4) cy = r.Top - ch - 8;
            if (cy < 4) cy = r.Bottom - ch - 6;
            cx = Math.Clamp(cx, 8, Math.Max(8, W - cw - 8));
            cy = Math.Clamp(cy, 4, Math.Max(4, H - ch - 4));
            Canvas.SetLeft(SizeChip, cx);
            Canvas.SetTop(SizeChip, cy);
            _chipRect = new Rect(cx, cy, cw, ch);
        }
        else
        {
            SelectionBox.Visibility = Visibility.Collapsed;
            SizeChip.Visibility = Visibility.Collapsed;
            _chipRect = null;
        }
    }

    private static void SetRect(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
    }

    // ── Pixel loupe ──────────────────────────────────────────

    private void UpdateLoupe(Point p)
    {
        if (_frozen is not { } bmp || bmp.PixelWidth < LoupeSpan || bmp.PixelHeight < LoupeSpan)
        {
            Loupe.Visibility = Visibility.Collapsed;
            return;
        }

        double scale = ScreenCapture.PrimaryScale;
        int px = (int)Math.Round(p.X * scale);
        int py = (int)Math.Round(p.Y * scale);
        int sx = Math.Clamp(px - LoupeSpan / 2, 0, bmp.PixelWidth - LoupeSpan);
        int sy = Math.Clamp(py - LoupeSpan / 2, 0, bmp.PixelHeight - LoupeSpan);

        var crop = new CroppedBitmap(bmp, new Int32Rect(sx, sy, LoupeSpan, LoupeSpan));
        crop.Freeze();
        LoupeImg.Source = crop;

        // Trail the cursor down-right; flip at screen edges. While dragging,
        // never cover the size chip — both live at the moving corner.
        const double gap = 26;
        Point[] candidates =
        {
            new(p.X + gap, p.Y + gap),
            new(p.X - gap - LoupeSize, p.Y + gap),
            new(p.X + gap, p.Y - gap - LoupeSize),
            new(p.X - gap - LoupeSize, p.Y - gap - LoupeSize),
        };
        foreach (var c in candidates)
        {
            var rect = new Rect(c.X, c.Y, LoupeSize, LoupeSize);
            if (rect.Left < 4 || rect.Top < 4 ||
                rect.Right > ActualWidth - 4 || rect.Bottom > ActualHeight - 4) continue;
            if (_chipRect is { } chip && rect.IntersectsWith(chip)) continue;
            Canvas.SetLeft(Loupe, c.X);
            Canvas.SetTop(Loupe, c.Y);
            Loupe.Visibility = Visibility.Visible;
            return;
        }

        // Nowhere clean to go (tiny screen) — clamp and accept the overlap.
        double fx = Math.Clamp(p.X + gap, 4, Math.Max(4, ActualWidth - LoupeSize - 4));
        double fy = Math.Clamp(p.Y + gap, 4, Math.Max(4, ActualHeight - LoupeSize - 4));
        Canvas.SetLeft(Loupe, fx);
        Canvas.SetTop(Loupe, fy);
        Loupe.Visibility = Visibility.Visible;
    }

    // ── Window snapping ──────────────────────────────────────

    /// <summary>Highlight the topmost foreign window under the cursor; a plain
    /// click later captures it. The dim mask opens a hole at its visible frame.
    /// Re-hit-tests every move: a smaller window stacked inside the old snap's
    /// rect (e.g. the taskbar) must win too.</summary>
    private void UpdateWindowHover(Point p)
    {
        var hit = FindTopLevelWindowAt(p, out Rect? frame);
        if (hit == _snapHwnd || (hit == null && _snapHwnd == IntPtr.Zero)) return;
        if (hit == null)
        {
            ClearSnap();
            LayoutMask(null);
            return;
        }
        _snapHwnd = hit.Value;
        _snapRect = frame;
        LayoutMask(_snapRect);
    }

    private void ClearSnap()
    {
        _snapHwnd = IntPtr.Zero;
        _snapRect = null;
    }

    /// <summary>EnumWindows walks top-level windows in z-order, top first —
    /// the first one that contains the cursor is the visible winner. Hit state
    /// travels through fields: this runs on every mouse move, so no per-call
    /// closures or buffers.</summary>
    private IntPtr? FindTopLevelWindowAt(Point local, out Rect? frame)
    {
        _hitPoint = local;
        _hitWnd = IntPtr.Zero;
        _hitFrame = null;
        NativeMethods.EnumWindows(_enumCallback, IntPtr.Zero);
        frame = _hitFrame;
        return _hitWnd != IntPtr.Zero ? _hitWnd : null;
    }

    private bool TrySnapCandidate(IntPtr h, IntPtr _)
    {
        NativeMethods.GetWindowThreadProcessId(h, out uint pid);
        if (pid == OwnPid) return true;                      // our overlay / preview cards
        if (!NativeMethods.IsWindowVisible(h)) return true;
        // Click-through overlays and never-activatable windows (NVIDIA's
        // overlay is WS_EX_NOACTIVATE) can't be the user's real target.
        // NOT WS_EX_TOOLWINDOW — the Windows 11 taskbar itself is one.
        int ex = NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE);
        if ((ex & (NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE)) != 0)
            return true;
        if (NativeMethods.DwmGetWindowAttribute(h,
                NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return true;                                     // UWP virtualised away

        if (VisibleFrameLocal(h) is not { } r) return true;
        if (r.Width < 20 || r.Height < 20) return true;      // tooltips, IME slivers
        if (!r.Contains(_hitPoint)) return true;

        _className.Clear();
        NativeMethods.GetClassName(h, _className, _className.Capacity);
        if (_className.ToString() is "Progman" or "WorkerW") return true; // wallpaper layer

        _hitWnd = h;
        _hitFrame = r;
        return false;
    }

    /// <summary>The window's visible frame in overlay-local DIPs. DWM bounds
    /// exclude the invisible resize border, so the highlight matches what the
    /// user actually sees. Returns null for off-screen / minimized windows.</summary>
    private Rect? VisibleFrameLocal(IntPtr h)
    {
        double scale = ScreenCapture.PrimaryScale;
        if (NativeMethods.DwmGetWindowAttribute(h,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT frame,
                RectSize) != 0 &&
            !NativeMethods.GetWindowRect(h, out frame))
            return null;

        return new Rect(
            frame.Left / scale - Left,
            frame.Top / scale - Top,
            (frame.Right - frame.Left) / scale,
            (frame.Bottom - frame.Top) / scale);
    }

    // ── Input ────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_finished) return;
        FadeHint();
        _dragging = true;
        _start = _current = e.GetPosition(this);
        CaptureMouse();
        LayoutMask(Selection);
        UpdateLoupe(_current);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_finished) return;
        var p = e.GetPosition(this);
        if (_dragging)
        {
            _current = p;
            LayoutMask(Selection);
            UpdateLoupe(p);   // magnify the moving corner all the way to mouse-up
        }
        else
        {
            FadeHint();
            UpdateWindowHover(p);
            UpdateLoupe(p);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _finished) return;
        _current = e.GetPosition(this);
        // Compute BEFORE clearing _dragging (Selection getter depends on it).
        var sel = Selection;
        _dragging = false;
        ReleaseMouseCapture();

        if (sel is { Width: > 5, Height: > 5 } r)
        {
            Finish(new Rect(r.X + Left, r.Y + Top, r.Width, r.Height));   // → virtual screen DIPs
            return;
        }

        bool moved = Math.Abs(_current.X - _start.X) > 4 || Math.Abs(_current.Y - _start.Y) > 4;
        if (moved)
        {
            // A slip of a drag is a do-over, not a request to grab the whole monitor.
            ClearSnap();
            LayoutMask(null);
            return;
        }

        // A true click: the snapped window if one is highlighted, else the monitor.
        Finish(_snapRect is { } w
            ? new Rect(w.X + Left, w.Y + Top, w.Width, w.Height)
            : ScreenUnderCursor());
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // With an IME active, WPF reports Alt-routed keys as Key.System;
        // SystemKey carries the real key. Esc must cancel either way.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            e.Handled = true;
            Finish(null);
        }
        else if (key == Key.Space)
        {
            e.Handled = true;
            Finish(ScreenUnderCursor());
        }
    }

    /// <summary>Bounds of the monitor under the cursor, in virtual-screen DIPs.</summary>
    private static Rect ScreenUnderCursor()
    {
        try
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            double scale = ScreenCapture.PrimaryScale;
            var b = screen.Bounds; // physical pixels
            return new Rect(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale);
        }
        catch
        {
            return new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        }
    }

    private void Finish(Rect? result)
    {
        if (_finished) return;
        _finished = true;
        Completed?.Invoke(result);
    }
}
