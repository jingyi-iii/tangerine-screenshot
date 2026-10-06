using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace screenshot.Services;

public sealed class ScreenshotEventArgs : EventArgs
{
    /// <summary>
    /// Working copy of the shot, or null when the file could not be written. The
    /// image is always available as <see cref="Bitmap"/>, so a storage problem
    /// never costs the user their screenshot.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>The captured image itself — never null for a successful capture.</summary>
    public BitmapSource? Bitmap { get; init; }

    /// <summary>Top-left of the captured region in virtual screen DIPs, or (-1,-1) for full screen.</summary>
    public double X { get; init; } = -1;
    public double Y { get; init; } = -1;
    public bool IsRegion { get; init; }
}

/// <summary>
/// Core capture orchestration: global hotkeys, region/full-screen capture,
/// temp file management, clipboard and save-as. Mirrors the Qt original's
/// ScreenshotManager. Owns no UI — communicates through events.
/// </summary>
public sealed class ScreenshotManager
{
    public const int HotkeyRegionId = 1;
    public const int HotkeyFullScreenId = 2;

    private readonly List<string> _tempFiles = new();
    private HwndSource? _hwndSource;
    private bool _capturing;
    /// <summary>
    /// Identifies the newest capture flow. A flow that finishes after a newer one has
    /// taken over must not publish <see cref="CaptureFinished"/>: that event is what
    /// tears the selection overlay down, so a stale flow would close an overlay the
    /// user is drawing in right now.
    /// </summary>
    private int _flowId;
    private bool _regionHotkeyOk;
    private bool _fullHotkeyOk;
    private bool _hotkeyFailedOnce;

    public bool IsCapturing => _capturing;
    public bool HotkeysAvailable => _regionHotkeyOk && _fullHotkeyOk;

    /// <summary>UI should hide/dim itself before pixels are grabbed.</summary>
    public event EventHandler? CaptureStarted;
    /// <summary>Capture flow finished (success or cancel); UI restores.</summary>
    public event EventHandler? CaptureFinished;
    /// <summary>Region selection overlay should be shown.</summary>
    public event EventHandler? RegionSelectionRequested;
    /// <summary>A screenshot was taken and saved to a temp file.</summary>
    public event EventHandler<ScreenshotEventArgs>? Captured;
    /// <summary>Global hotkey registration failed (e.g. already taken).</summary>
    public event EventHandler? HotkeyRegistrationFailed;
    /// <summary>A later retry succeeded — hotkeys are live again.</summary>
    public event EventHandler? HotkeysRestored;

    // ── Global hotkeys (Win32) ───────────────────────────────

    /// <summary>Creates the message-only window and registers the global hotkeys on it (no visible UI).</summary>
    public void RegisterHotkeys()
    {
        if (_hwndSource == null)
        {
            var parameters = new HwndSourceParameters("screenshot-hotkeys")
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                ParentWindow = new IntPtr(-3)             // HWND_MESSAGE
            };
            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(WndProc);
        }
        RefreshHotkeys();
    }

    /// <summary>(Re)registers any hotkey that failed before. Returns true when all are live.</summary>
    public bool RefreshHotkeys()
    {
        if (_hwndSource == null) return false;
        var hwnd = _hwndSource.Handle;
        if (!_regionHotkeyOk)
            _regionHotkeyOk = NativeMethods.RegisterHotKey(hwnd, HotkeyRegionId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, (uint)'S');
        if (!_fullHotkeyOk)
            _fullHotkeyOk = NativeMethods.RegisterHotKey(hwnd, HotkeyFullScreenId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, (uint)'F');

        if (HotkeysAvailable)
        {
            if (_hotkeyFailedOnce)
            {
                _hotkeyFailedOnce = false;
                HotkeysRestored?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }
        _hotkeyFailedOnce = true;
        HotkeyRegistrationFailed?.Invoke(this, EventArgs.Empty);
        return false;
    }

    public void UnregisterHotkeys()
    {
        if (_hwndSource == null) return;
        _hwndSource.RemoveHook(WndProc);
        NativeMethods.UnregisterHotKey(_hwndSource.Handle, HotkeyRegionId);
        NativeMethods.UnregisterHotKey(_hwndSource.Handle, HotkeyFullScreenId);
        _hwndSource.Dispose();
        _hwndSource = null;
        _regionHotkeyOk = _fullHotkeyOk = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            // Defer to the dispatcher — never run UI logic inside the hook.
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                if (id == HotkeyRegionId) BeginRegionCapture();
                else if (id == HotkeyFullScreenId) _ = CaptureFullScreenAsync();
            });
        }
        return IntPtr.Zero;
    }

    // ── Capture flow ─────────────────────────────────────────

    public void BeginRegionCapture()
    {
        if (_capturing) CancelRegionCapture();
        _flowId++;
        _capturing = true;
        CaptureStarted?.Invoke(this, EventArgs.Empty);
        RegionSelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    public void CancelRegionCapture()
    {
        if (!_capturing) return;
        _flowId++;              // whatever was running is no longer the owner
        _capturing = false;
        CaptureFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ends a finished flow. Only the newest one may clear the flag and announce the
    /// end — an older flow's tail must leave a newer capture's state alone.
    /// </summary>
    private void FinishFlow(int flow)
    {
        if (flow != _flowId)
        {
            DiagnosticLog.Write($"capture: flow {flow} finished after flow {_flowId} took over — leaving it alone");
            return;
        }
        _capturing = false;
        CaptureFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the overlay with the selected rect (virtual screen DIPs).</summary>
    public void CompleteRegionCapture(Rect dipRect)
    {
        _capturing = false;
        int flow = _flowId;
        _ = CompleteRegionCaptureAsync(dipRect, flow);
    }

    private async Task CompleteRegionCaptureAsync(Rect dipRect, int flow)
    {
        try
        {
            DiagnosticLog.Write($"capture: region {dipRect.Width:F0}x{dipRect.Height:F0} at ({dipRect.X:F0},{dipRect.Y:F0})");
            if (dipRect.Width > 5 && dipRect.Height > 5)
            {
                // Let the compositor settle after the overlay hides.
                await Task.Delay(120);
                var bitmap = ScreenCapture.Capture(dipRect);
                DiagnosticLog.Write($"capture: ScreenCapture.Capture -> {(bitmap == null ? "NULL" : $"{bitmap.PixelWidth}x{bitmap.PixelHeight}")}");
                if (bitmap != null)
                {
                    // The card is shown from the in-memory image either way: persisting
                    // a temp copy is a convenience, not a precondition.
                    var path = SaveTemp(bitmap);
                    DiagnosticLog.Write($"capture: SaveTemp -> {path ?? "NULL (in-memory preview)"}");
                    Captured?.Invoke(this, new ScreenshotEventArgs
                    {
                        Path = path, Bitmap = bitmap, X = dipRect.X, Y = dipRect.Y, IsRegion = true
                    });
                }
            }
            else
            {
                DiagnosticLog.Write("capture: region too small, skipped");
            }
        }
        catch (Exception ex)
        {
            // A capture/IO failure must not escape the discarded Task — but it must
            // leave a trace, or the UI just silently never shows anything.
            DiagnosticLog.Write($"capture: FAILED {ex}");
        }
        finally
        {
            FinishFlow(flow);
        }
    }

    public async Task CaptureFullScreenAsync()
    {
        if (_capturing) return;
        int flow = ++_flowId;
        _capturing = true;
        try
        {
            CaptureStarted?.Invoke(this, EventArgs.Empty);
            await Task.Delay(150);
            var rect = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            DiagnosticLog.Write($"capture: full screen {rect.Width:F0}x{rect.Height:F0}");
            var bitmap = ScreenCapture.Capture(rect);
            DiagnosticLog.Write($"capture: full ScreenCapture.Capture -> {(bitmap == null ? "NULL" : $"{bitmap.PixelWidth}x{bitmap.PixelHeight}")}");
            if (bitmap != null)
            {
                var path = SaveTemp(bitmap);
                DiagnosticLog.Write($"capture: full SaveTemp -> {path ?? "NULL (in-memory preview)"}");
                Captured?.Invoke(this, new ScreenshotEventArgs { Path = path, Bitmap = bitmap, IsRegion = false });
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"capture: full FAILED {ex}");
        }
        finally
        {
            FinishFlow(flow);
        }
    }

    // ── Files / clipboard ────────────────────────────────────

    /// <summary>
    /// Writes the shot to the first working temp location. %TEMP% can be denied
    /// (policy, ACLs, virtualisation), and when it is, returning null silently
    /// kills the whole flow — no file, no Captured event, no preview card.
    /// </summary>
    private string? SaveTemp(BitmapSource bitmap)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        foreach (var dir in TempLocations())
        {
            var path = Path.Combine(dir, $"screenshot_{stamp}.png");
            try
            {
                Directory.CreateDirectory(dir);
                SavePng(bitmap, path);
                lock (_tempFiles) _tempFiles.Add(path);
                // Both sides need the same treatment: GetFullPath keeps the trailing
                // separator of Path.GetTempPath(), so trimming only one side made this
                // claim "%TEMP% unusable" for every shot that went to %TEMP%.
                if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)).Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                        StringComparison.OrdinalIgnoreCase))
                {
                    DiagnosticLog.Write($"capture: %TEMP% unusable, saved to fallback {dir}");
                }
                return path;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"capture: cannot save to {dir} — {ex.GetType().Name}: {ex.Message}");
            }
        }

        DiagnosticLog.Write("capture: no writable location for the screenshot");
        return null;
    }

    /// <summary>Candidate folders for the working copy, best first.</summary>
    private static IEnumerable<string> TempLocations()
    {
        yield return Path.GetTempPath();

        // Per-user app data: normally writable for the logged-in user.
        string? appData = null;
        try
        {
            appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "screenshot", "temp");
        }
        catch { }
        if (!string.IsNullOrEmpty(appData)) yield return appData;

        yield return ArchiveDirectory;

        // Last resort: beside the exe. Not pretty, but a shot kept somewhere odd
        // always beats a shot silently thrown away.
        yield return Path.Combine(AppContext.BaseDirectory, "captures");
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
    }

    public static BitmapSource? LoadBitmap(string path)
    {
        return LoadCore(path, 0);
    }

    /// <summary>Load a downscaled decode (aspect preserved) — keeps preview cards cheap.</summary>
    public static BitmapSource? LoadThumbnail(string path, int decodePixelWidth)
    {
        return LoadCore(path, decodePixelWidth);
    }

    private static BitmapSource? LoadCore(string path, int decodePixelWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad; // do not lock the file
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a PNG on the clipboard as both an image and PNG bytes. Returns false when
    /// there was nothing to copy or the clipboard refused it — the clipboard is a
    /// shared, lockable resource, and callers must not report success blindly.
    /// </summary>
    public bool CopyToClipboard(string? path, BitmapSource? inMemory = null)
    {
        // Prefer the file (full resolution); fall back to the in-memory capture.
        var bitmap = (path != null ? LoadBitmap(path) : null) ?? inMemory;
        if (bitmap == null) return false;

        try
        {
            var data = new DataObject();
            data.SetImage(bitmap);

            // Also expose PNG so apps that prefer it (browsers, IMs) paste well.
            using var ms = new MemoryStream();
            SavePngToStream(bitmap, ms);
            ms.Position = 0;
            data.SetData("PNG", ms);

            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"clipboard: copy failed — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void SavePngToStream(BitmapSource bitmap, Stream stream)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    /// <summary>Where auto-dismissed previews archive to — also the Save-As default.</summary>
    public static string ArchiveDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>Save-as dialog. Returns the destination path, or null if cancelled/failed.</summary>
    public string? SaveImageToFile(string? srcPath, Window? owner, BitmapSource? inMemory = null)
    {
        try { Directory.CreateDirectory(ArchiveDirectory); } catch { /* dialog still works elsewhere */ }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Screenshot",
            FileName = srcPath != null ? Path.GetFileName(srcPath) : $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            DefaultExt = ".png",
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp",
            InitialDirectory = ArchiveDirectory
        };
        if (dlg.ShowDialog(owner) != true) return null;

        try
        {
            string dst = dlg.FileName;
            string ext = Path.GetExtension(dst).ToLowerInvariant();
            if (ext == ".png" && srcPath != null && File.Exists(srcPath))
            {
                File.Copy(srcPath, dst, overwrite: true);
            }
            else
            {
                // No working-copy file: encode the in-memory capture instead.
                var bitmap = (srcPath != null ? LoadBitmap(srcPath) : null) ?? inMemory;
                if (bitmap == null) return null;
                BitmapEncoder encoder = ext switch
                {
                    ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
                    ".bmp" => new BmpBitmapEncoder(),
                    _ => new PngBitmapEncoder()
                };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var fs = new FileStream(dst, FileMode.Create, FileAccess.Write);
                encoder.Save(fs);
            }
            return dst;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"save: FAILED {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes a capture to the archive folder. Returns the destination, or null if
    /// no location would accept it — the caller must not lose the image silently.
    /// </summary>
    public static string? TryArchive(BitmapSource bitmap, string? preferredName = null)
    {
        var name = preferredName ?? $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        foreach (var dir in new[]
                 {
                     // Not %TEMP%: SweepStaleTempFiles deletes screenshot_*.png there on
                     // the next launch, so "archived" into %TEMP% would mean "gone
                     // tomorrow" — the one thing an archive must never mean.
                     ArchiveDirectory,
                     Path.Combine(AppContext.BaseDirectory, "captures"),
                 })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    encoder.Save(fs);
                }
                return path;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"archive: cannot write to {dir} — {ex.GetType().Name}: {ex.Message}");
            }
        }
        return null;
    }

    public void DeletePreview(string? path)
    {
        if (path == null) return;
        try
        {
            // The session's own list is the authority: a working copy that had to fall
            // back outside %TEMP% still has to be discardable. The path pattern stays as
            // a second gate so a file this app never wrote is never deleted.
            bool ours;
            lock (_tempFiles) ours = _tempFiles.Contains(path);
            if (!ours && !IsOwnTempFile(path)) return;

            File.Delete(path);
            lock (_tempFiles) _tempFiles.Remove(path);
        }
        catch { /* best effort */ }
    }

    private static bool IsOwnTempFile(string path)
    {
        string name = Path.GetFileName(path);
        string? dir = Path.GetDirectoryName(path);
        return name.StartsWith("screenshot_", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(dir ?? ""),
                Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Delete all temp files created this session (on exit).</summary>
    public void CleanupTempFiles()
    {
        lock (_tempFiles)
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); } catch { }
            }
            _tempFiles.Clear();
        }
    }

    /// <summary>Sweep leftovers from crashed sessions (older than a day).</summary>
    public static void SweepStaleTempFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "screenshot_*.png"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1))
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }
}
