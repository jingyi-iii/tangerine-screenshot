using System.IO;

namespace screenshot.Services;

/// <summary>
/// Tiny persisted flags (first-run hints). key=value lines in
/// %LocalAppData%\screenshot\settings.txt. Best-effort: never throws.
/// </summary>
public static class Settings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "screenshot");
    private static readonly string FilePath = Path.Combine(Dir, "settings.txt");
    private static readonly Dictionary<string, string> _values = Load();

    /// <summary>The one-time "press Ctrl+Shift+S" intro pill has been shown.</summary>
    public static bool HasSeenIntro
    {
        get => Get(nameof(HasSeenIntro));
        set => Set(nameof(HasSeenIntro), value);
    }

    /// <summary>The region-overlay hint pill has been shown once.</summary>
    public static bool HasSeenOverlayHint
    {
        get => Get(nameof(HasSeenOverlayHint));
        set => Set(nameof(HasSeenOverlayHint), value);
    }

    private static bool Get(string key) =>
        _values.TryGetValue(key, out var v) && v == "1";

    private static void Set(string key, bool value)
    {
        _values[key] = value ? "1" : "0";
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, _values.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch { /* best effort */ }
    }

    private static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line[..i]] = line[(i + 1)..];
            }
        }
        catch { }
        return d;
    }
}
