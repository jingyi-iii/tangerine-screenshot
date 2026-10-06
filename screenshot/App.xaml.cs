using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using screenshot.Services;

namespace screenshot;

public partial class App : Application
{
    /// <summary>Global capture orchestrator (hotkeys, screenshots, clipboard, files).</summary>
    public static ScreenshotManager Capture { get; } = new();

    private TrayService? _tray;
    private OverlayWindow? _overlay;
    private readonly List<PreviewWindow> _previews = new();
    /// <summary>The tray menu currently open, if any; a second right-click replaces it.</summary>
    private TrayMenuWindow? _menu;
    /// <summary>True while the global hotkeys are unregistered — the menu then offers a retry.</summary>
    private bool _hotkeysUnavailable;
    /// <summary>Path of the most recent shot, for --verify-flow's persistence check.</summary>
    private string? _lastShotPath;

    /// <summary>
    /// Runs before anything else in this class — before App.xaml is loaded and
    /// before OnStartup. Failures in either place would otherwise kill the app
    /// silently, with no tray icon and nothing on disk to look at.
    /// </summary>
    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
    }

    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;   // keep the tray app alive; log what happened
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            StartupCore(e);
        }
        catch (Exception ex)
        {
            // Without a crash log this failure mode is invisible: the app dies
            // before the tray icon exists, so there is nothing to click and no
            // window to read. Always leave evidence behind.
            LogCrash(ex);
            Shutdown(1);
        }
    }

    private void StartupCore(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagnosticLog.Write($"startup: exe={Environment.ProcessPath}");
        DiagnosticLog.Write($"startup: trace file = {DiagnosticLog.Path ?? "NONE WRITABLE"}");
        ThemeManager.Initialize();
        DiagnosticLog.Write($"theme: initialized, IsDark={ThemeManager.IsDark}");
        ScreenshotManager.SweepStaleTempFiles();

        // Headless smoke test: screenshot --selftest
        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            return;
        }

        // Headless menu render: screenshot --menu-shot[=<dir>] writes the tray menu
        // in both themes as PNGs. The menu is the one surface a script cannot reach
        // (it takes a right-click on a tray icon), so this is how a change to it gets
        // looked at on a machine nobody is sitting at.
        if (TryGetArgValue(e.Args, "--menu-shot", out var shotDir))
        {
            RunMenuShots(shotDir);
            return;
        }

        _tray = new TrayService();
        DiagnosticLog.Write($"tray: created, icon={_tray.IconDescription}");
        _tray.RegionRequested += (_, _) => Capture.BeginRegionCapture();
        _tray.MenuRequested += (_, anchor) => ShowTrayMenu(anchor);

        Capture.CaptureFinished += (_, _) => CloseOverlay();
        Capture.RegionSelectionRequested += (_, _) =>
        {
            // A hotkey can fire while the menu is still open, and the menu holds the
            // mouse capture; the overlay has to get those clicks, not us.
            CloseTrayMenu();
            ShowOverlay();
        };
        Capture.Captured += (_, args) =>
        {
            CloseTrayMenu();
            ShutterSound.Play();
            CreatePreview(args);
        };
        Capture.HotkeyRegistrationFailed += (_, _) =>
        {
            _hotkeysUnavailable = true;
            _tray?.NotifyHotkeyFailed();
        };
        Capture.HotkeysRestored += (_, _) => _hotkeysUnavailable = false;

        Capture.RegisterHotkeys();
        DiagnosticLog.Write($"hotkeys: region={Capture.HotkeysAvailable}");

        // The product is the hotkey. Teach it once, then get out of the way.
        if (!Settings.HasSeenIntro)
        {
            Settings.HasSeenIntro = true;
            new IntroWindow().Show();
        }

        // State goes to the trace, not to the screen. The tray icon appearing is the
        // sign of life; a balloon announcing it on every launch is noise, and the one
        // balloon worth interrupting for — hotkeys that failed to register — still
        // fires, because that is the case where the app cannot do its job.
        LogStartupState();

        // Headless end-to-end check of the capture → preview flow.
        if (e.Args.Contains("--verify-flow"))
        {
            RunFlowVerification();
            return;
        }

        // Take one real capture and leave the card on screen (visual inspection).
        if (e.Args.Contains("--demo-capture"))
        {
            DiagnosticLog.Write("demo: capturing full screen and leaving the card up");
            _ = Capture.CaptureFullScreenAsync();
        }

        // Open the menu where the tray icon is and leave it up, so the styling can
        // be looked at without hunting for the icon.
        if (e.Args.Contains("--demo-menu"))
        {
            var anchor = _tray?.TryGetAnchor() ?? SyntheticAnchor();
            DiagnosticLog.Write($"demo: opening the tray menu, icon={(anchor.IconBounds.IsEmpty ? "synthetic" : "from shell")}");
            ShowTrayMenu(anchor);
        }

        // One-shot idle trim shortly after startup so the tray app doesn't
        // sit on startup peak memory.
        var idleTrim = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        idleTrim.Tick += (_, _) =>
        {
            idleTrim.Stop();
            MemoryHelper.Trim();
        };
        idleTrim.Start();
    }

    // Debounced working-set trim: batches rapid open/close activity into one trim
    // fired ~0.9s after things settle (e.g. preview cards being closed).
    private System.Windows.Threading.DispatcherTimer? _trimTimer;

    internal void ScheduleMemoryTrim()
    {
        if (_trimTimer == null)
        {
            _trimTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900)
            };
            _trimTimer.Tick += (_, _) =>
            {
                _trimTimer.Stop();
                MemoryHelper.Trim();
            };
        }
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    // ── Tray menu ────────────────────────────────────────────

    /// <summary>
    /// Opens the menu against the tray icon. A fresh window each time on purpose:
    /// the menu is short-lived, and rebuilding it is how it picks up the current
    /// theme and the current hotkey state without any invalidation plumbing.
    /// </summary>
    private void ShowTrayMenu(TrayMenuAnchor anchor)
    {
        CloseTrayMenu();

        var menu = new TrayMenuWindow(anchor) { HotkeysUnavailable = _hotkeysUnavailable };
        menu.RegionRequested += (_, _) => Capture.BeginRegionCapture();
        menu.FullScreenRequested += (_, _) => _ = Capture.CaptureFullScreenAsync();
        menu.ExitRequested += (_, _) => Shutdown();
        menu.HotkeyRetryRequested += (_, _) =>
        {
            if (Capture.RefreshHotkeys()) _tray?.NotifyHotkeysActive();
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
        };

        _menu = menu;
        menu.Open();
    }

    private void CloseTrayMenu()
    {
        var menu = _menu;
        _menu = null;
        if (menu == null) return;
        try { menu.Close(); } catch { }
    }

    /// <summary>
    /// A stand-in for the tray icon's rect, bottom-right of the primary screen: used
    /// by --demo-menu when the shell cannot tell us where the icon is.
    /// </summary>
    private static TrayMenuAnchor SyntheticAnchor()
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                   ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var icon = new System.Drawing.Rectangle(work.Right - 120, work.Bottom + 4, 24, 24);
        return new TrayMenuAnchor(icon, work);
    }

    // ── Tray menu QA render (--menu-shot) ────────────────────

    /// <summary>
    /// Writes the menu, in both themes and both hotkey states, as PNGs. The theme is
    /// pinned per shot and the process exits straight afterwards, so nothing stays
    /// pinned. Exits non-zero if any render failed.
    /// </summary>
    private void RunMenuShots(string? targetDir)
    {
        var dir = string.IsNullOrWhiteSpace(targetDir) ? Environment.CurrentDirectory : targetDir;
        var failures = 0;

        foreach (var dark in new[] { false, true })
        {
            ThemeManager.Force(dark);
            foreach (var warn in new[] { false, true })
            {
                var name = $"tray-menu-{(dark ? "dark" : "light")}{(warn ? "-hotkey-warning" : "")}.png";
                var path = Path.Combine(dir, name);
                try
                {
                    // Never shown: RenderSurface draws the visual tree offscreen.
                    var surface = new TrayMenuWindow { HotkeysUnavailable = warn }.RenderSurface();
                    WritePng(OnBackdrop(surface), path);
                    DiagnosticLog.Write($"menu shot: {name} {surface.PixelWidth}x{surface.PixelHeight} -> {path}");
                }
                catch (Exception ex)
                {
                    failures++;
                    DiagnosticLog.Write($"menu shot: {name} FAILED — {ex}");
                }
            }
        }

        Environment.ExitCode = failures == 0 ? 0 : 1;
        Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// Composites a rendered menu over the theme's control background. The render
    /// keeps the menu's transparent shadow margin, and a mostly-transparent PNG is
    /// hard to review.
    /// </summary>
    private static BitmapSource OnBackdrop(BitmapSource surface)
    {
        const double pad = 16;
        var backdrop = Application.Current?.TryFindResource("ControlBgBrush") as Brush ?? Brushes.White;
        double width = surface.Width + pad * 2, height = surface.Height + pad * 2;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
            dc.DrawImage(surface, new Rect(pad, pad, surface.Width, surface.Height));
        }

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void WritePng(BitmapSource bitmap, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Reads a "--name" or "--name=value" switch. The switch being present is the
    /// answer; an absent value comes back null so callers can pick a default.
    /// </summary>
    private static bool TryGetArgValue(string[] args, string name, out string? value)
    {
        value = null;
        var match = args.FirstOrDefault(a =>
            a.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;
        int separator = match.IndexOf('=');
        if (separator >= 0) value = match[(separator + 1)..].Trim('"');
        return true;
    }

    // ── Region selection overlay ─────────────────────────────

    private void ShowOverlay()
    {
        CloseOverlay();
        _overlay = new OverlayWindow();
        _overlay.Completed += OnOverlayCompleted;
        _overlay.Open();
    }

    private void OnOverlayCompleted(Rect? result)
    {
        var overlay = _overlay;
        _overlay = null;
        if (overlay != null)
        {
            overlay.Completed -= OnOverlayCompleted;
            overlay.Hide();
            overlay.Close();
        }

        if (result.HasValue) Capture.CompleteRegionCapture(result.Value);
        else Capture.CancelRegionCapture();
    }

    private void CloseOverlay()
    {
        var overlay = _overlay;
        _overlay = null;
        if (overlay != null)
        {
            overlay.Completed -= OnOverlayCompleted;
            try { overlay.Close(); } catch { }
        }
    }

    // ── Preview windows: anchored bottom-right of the captured screen ──

    private void CreatePreview(ScreenshotEventArgs args)
    {
        _lastShotPath = args.Path;
        DiagnosticLog.Write($"preview: creating (file={args.Path ?? "none"}, bitmap={args.Bitmap?.PixelWidth ?? 0}x{args.Bitmap?.PixelHeight ?? 0})");
        try
        {
            var preview = new PreviewWindow(args.Path, WorkAreaOfCapture(args), args.Bitmap);
            preview.ContentRendered += (_, _) => ReflowPreviews();
            preview.Interacted += (_, _) => ReflowPreviews();
            preview.Closed += (_, _) =>
            {
                _previews.Remove(preview);
                ReflowPreviews();
                ScheduleMemoryTrim();
            };
            _previews.Add(preview);
            preview.Show();
            DiagnosticLog.Write($"preview: shown, IsVisible={preview.IsVisible}, count={_previews.Count}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"preview: FAILED {ex}");
            LogCrash(ex);
        }
    }

    // ── Flow verification (--verify-flow) ────────────────────

    /// <summary>
    /// Drives the real capture → preview path without a human clicking, and writes
    /// each step to the trace. Exits non-zero if any step fails, so the flow can be
    /// checked on a machine where nobody is watching the screen.
    /// </summary>
    private void RunFlowVerification()
    {
        DiagnosticLog.Write("verify: starting capture → preview flow check");

        // Kick off the real capture path (this is what a hotkey press does), then
        // check the result from the message loop: the preview is created
        // asynchronously through Capture.Captured, so it does not exist yet here.
        try
        {
            Capture.CompleteRegionCapture(new Rect(0, 0, 320, 200));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"verify: FAIL capture threw {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
            Shutdown(1);
            return;
        }

        var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            VerifyPreview();
        };
        check.Start();
    }

    /// <summary>Assertions on the preview card the capture flow produced.</summary>
    private void VerifyPreview()
    {
        var failures = 0;
        void Step(string name, bool ok, string detail)
        {
            DiagnosticLog.Write($"verify: {(ok ? "PASS" : "FAIL")} {name} — {detail}");
            if (!ok) failures++;
        }

        Step("preview was created", _previews.Count > 0, $"_previews.Count={_previews.Count}");
        if (_previews.Count > 0)
        {
            var p = _previews[^1];
            Step("preview is visible", p.IsVisible, $"IsVisible={p.IsVisible}");
            Step("preview rendered with size", p.ActualWidth > 0 && p.ActualHeight > 0,
                $"{p.ActualWidth:F0}x{p.ActualHeight:F0} at ({p.Left:F0},{p.Top:F0})");
            Step("preview icon set", p.Icon != null, p.Icon == null ? "null" : "set");
        }

        // Persistence is checked too: a card with no file behind it means
        // copy/save/drag all degrade, so it must not pass silently.
        Step("a shot file exists on disk", _lastShotPath != null && File.Exists(_lastShotPath),
            _lastShotPath ?? "no file was written");

        DiagnosticLog.Write($"verify: DONE, failures={failures}");
        Environment.ExitCode = failures == 0 ? 0 : 1;
        Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// Writes the live startup state — tray icon frame and hotkey registration — to
    /// the trace, where "the icon never showed up" and "the hotkeys never took" can
    /// be told apart afterwards without a debugger.
    /// </summary>
    private void LogStartupState()
    {
        DiagnosticLog.Write(
            $"startup state: tray icon: {_tray?.IconDescription ?? "not created"}  ·  " +
            $"hotkeys: {(Capture.HotkeysAvailable ? "registered" : "UNAVAILABLE (tray menu can retry)")}");
    }

    /// <summary>Work area (virtual-screen DIPs) of the monitor the shot came from.</summary>
    private static Rect WorkAreaOfCapture(ScreenshotEventArgs args)
    {
        double scale = ScreenCapture.PrimaryScale;
        System.Drawing.Point phys;
        if (args.IsRegion)
        {
            phys = new System.Drawing.Point(
                (int)Math.Round(args.X * scale), (int)Math.Round(args.Y * scale));
        }
        else
        {
            NativeMethods.GetCursorPos(out var cursor);
            phys = new System.Drawing.Point(cursor.X, cursor.Y);
        }
        var wa = System.Windows.Forms.Screen.FromPoint(phys).WorkingArea;
        return new Rect(wa.X / scale, wa.Y / scale, wa.Width / scale, wa.Height / scale);
    }

    /// <summary>Re-anchor every open preview: newest at its screen's corner, older ones above.</summary>
    private void ReflowPreviews()
    {
        foreach (var screen in _previews.GroupBy(p => p.AnchorWorkArea))
        {
            double bottom = screen.Key.Bottom - 20;
            var list = screen.ToList();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var p = list[i];
                double h = p.ActualHeight > 0 ? p.ActualHeight : 300;
                double w = p.ActualWidth > 0 ? p.ActualWidth : 372;
                p.MoveTo(screen.Key.Right - w - 20, bottom - h);
                bottom -= h + 12;
            }
        }
    }

    // ── Self test ────────────────────────────────────────────

    private void RunSelfTest()
    {
        var lines = new List<string>();
        int code = 0;
        try
        {
            var region = ScreenCapture.Capture(new Rect(0, 0, 320, 200));
            lines.Add($"region: {(region != null ? $"OK {region.PixelWidth}x{region.PixelHeight}" : "FAIL")}");
            if (region == null) code = 1;

            var full = ScreenCapture.Capture(new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
            lines.Add($"fullscreen: {(full != null ? $"OK {full.PixelWidth}x{full.PixelHeight}" : "FAIL")}");
            if (full == null) code = 1;

            lines.Add($"primaryScale: {ScreenCapture.PrimaryScale}");

            // The tray icon is loaded before anything else at startup, so check it
            // here rather than letting a bad asset go unnoticed. LoadTrayIcon never
            // throws — it falls back to the stock system icon — so the fallback itself
            // is the failure to report, and the shared stock icon must not be disposed.
            try
            {
                var trayIcon = TrayService.LoadTrayIcon();
                bool branded = !ReferenceEquals(trayIcon, System.Drawing.SystemIcons.Application);
                lines.Add(branded
                    ? $"trayIcon: OK {trayIcon.Width}x{trayIcon.Height}"
                    : "trayIcon: FAIL fell back to the stock system icon (Assets/app.ico missing or unreadable)");
                if (!branded) code = 1;
                if (branded) trayIcon.Dispose();   // never dispose SystemIcons.*
            }
            catch (Exception ex)
            {
                lines.Add($"trayIcon: FAIL {ex.GetType().Name}: {ex.Message}");
                code = 1;
            }
        }
        catch (Exception ex)
        {
            lines.Add($"exception: {ex.Message}");
            code = 2;
        }

        try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "screenshot_selftest.txt"), lines); }
        catch { }

        Environment.ExitCode = code;
        Shutdown();
    }

    // ── Crash logging ────────────────────────────────────────

    /// <summary>Reports written so far. Guarded because unhandled exceptions can
    /// surface on any thread, at any time.</summary>
    private static int _crashReports;

    /// <summary>
    /// How many crashes are recorded before the log gives up. A single latch meant the
    /// first exception — often a harmless one — consumed the only record, leaving every
    /// later, more informative failure invisible; a hard cap keeps a hot-path crash from
    /// filling the disk instead.
    /// </summary>
    private const int MaxCrashReports = 5;

    /// <summary>
    /// Writes one crash report next to the exe and/or in %TEMP%. Never throws —
    /// it runs inside exception handlers — and tries every location, because in a
    /// sandboxed or locked-down environment %TEMP% itself may be unwritable.
    /// </summary>
    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        int n = Interlocked.Increment(ref _crashReports);
        if (n > MaxCrashReports) return;

        var report = n == MaxCrashReports
            ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] further crash reports suppressed after {MaxCrashReports}{Environment.NewLine}{Environment.NewLine}"
            : $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] (crash {n}) {ex}{Environment.NewLine}{Environment.NewLine}";

        var locations = new List<string>();
        try { locations.Add(Path.Combine(AppContext.BaseDirectory, "screenshot_crash.log")); } catch { }
        try { locations.Add(Path.Combine(Path.GetTempPath(), "screenshot_crash.log")); } catch { }

        foreach (var path in locations)
        {
            try
            {
                File.AppendAllText(path, report);
                return;
            }
            catch { /* try the next location */ }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CloseTrayMenu();
        Capture.UnregisterHotkeys();
        _tray?.Dispose();
        Capture.CleanupTempFiles();
        base.OnExit(e);
    }
}
