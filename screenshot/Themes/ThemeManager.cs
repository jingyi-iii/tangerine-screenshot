using System.Windows;
using Microsoft.Win32;

namespace screenshot;

/// <summary>
/// Loads the light/dark theme dictionary to match Windows and swaps it live
/// when the system preference changes. All theme brushes must be consumed via
/// DynamicResource so open windows follow the swap.
/// </summary>
public static class ThemeManager
{
    private const string LightSource = "Themes/LightTheme.xaml";
    private const string DarkSource = "Themes/DarkTheme.xaml";

    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        Apply(DetectDark());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>
    /// Pins a theme regardless of the system preference. Only the QA render
    /// (--menu-shot) needs this: it writes both themes' menus in one run, from a
    /// process that exits immediately, so nothing is left pinned afterwards.
    /// </summary>
    public static void Force(bool dark) => Apply(dark);

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            var dispatcher = Application.Current?.Dispatcher;
            dispatcher?.BeginInvoke(() => Apply(DetectDark()));
        }
    }

    private static bool DetectDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = light, 0 = dark, missing = light.
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Apply(bool dark)
    {
        if (Application.Current == null) return;
        // Other General preference changes (fonts, colors that don't flip the
        // theme) also raise the event — don't rebuild dictionaries for nothing.
        if (_current != null && dark == IsDark) return;
        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(dark ? DarkSource : LightSource, UriKind.Relative)
            };
            var merged = Application.Current.Resources.MergedDictionaries;
            if (_current != null) merged.Remove(_current);
            merged.Add(dict);
            _current = dict;
            IsDark = dark;
        }
        catch (Exception)
        {
            // A bad dictionary must not be fatal: without it WPF falls back to its
            // built-in chrome and the capture flow still works.
        }
    }
}
