using System.Windows;
using System.Windows.Media.Imaging;

namespace screenshot;

/// <summary>
/// The tangerine tile every window carries, so Alt-Tab and the taskbar show the
/// app icon on windows that would otherwise inherit the generic WPF default.
/// Decoded once and shared: the bitmap is frozen after loading, so one instance
/// is safe across windows and threads.
/// </summary>
public static class WindowIcon
{
    private static readonly BitmapFrame? _icon = Load();

    /// <summary>Points <paramref name="window"/> at the shared app icon (no-op if it failed to load).</summary>
    public static void Apply(Window window)
    {
        if (_icon != null) window.Icon = _icon;
    }

    private static BitmapFrame? Load()
    {
        try
        {
            // Relative pack URI: resolves against the calling assembly, so it
            // keeps working whatever the exe is renamed to.
            var frame = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon.png"));
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;   // an icon is decoration — never let it keep a window from opening
        }
    }
}
