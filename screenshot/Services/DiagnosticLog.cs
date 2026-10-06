using System.IO;

namespace screenshot.Services;

/// <summary>
/// Best-effort runtime breadcrumb trail. A tray app that misbehaves has no
/// console and no window to read, so every lifecycle step writes one timestamped
/// line that can be inspected afterwards. Never throws, never blocks startup.
///
/// Location: the first writable of
///   1. &lt;Pictures&gt;\Screenshots\screenshot-trace.log
///   2. %TEMP%\screenshot-trace.log
///   3. &lt;exe folder&gt;\screenshot-trace.log
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _resolved;
    private static bool _disabled;

    /// <summary>Where the trail is (or would be) written; null if nowhere is writable.</summary>
    public static string? Path
    {
        get { lock (Gate) { Resolve(); return _path; } }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_disabled) return;
            Resolve();
            if (_path == null) { _disabled = true; return; }
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch
            {
                _disabled = true;   // logging must never become the failure
            }
        }
    }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;

        var candidates = new List<string>();
        try
        {
            candidates.Add(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Screenshots", "screenshot-trace.log"));
        }
        catch { }
        try { candidates.Add(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "screenshot-trace.log")); } catch { }
        try { candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "screenshot-trace.log")); } catch { }

        foreach (var candidate in candidates)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(candidate, $"{Environment.NewLine}=== session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                _path = candidate;
                return;
            }
            catch { /* try the next location */ }
        }
    }
}
