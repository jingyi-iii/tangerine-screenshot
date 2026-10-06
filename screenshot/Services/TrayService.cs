using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace screenshot.Services;

/// <summary>
/// System tray icon (WinForms NotifyIcon hosted in the WPF app) — the app's
/// only permanent presence. Left-click takes a region screenshot; right-click
/// asks <see cref="App"/> to open the themed menu. WinForms is confined to this
/// class: it owns the icon, the shell registration and the icon's geometry, and
/// nothing else. The menu itself is a WPF window, because a ContextMenuStrip can
/// only draw its own fixed chrome.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    /// <summary>
    /// Keeps the decoded tray icon alive for the service's lifetime. NotifyIcon
    /// clones the handle internally since .NET 5, but the icon is ours to own and
    /// collect, and a collected icon is a tray entry that shows nothing at all.
    /// </summary>
    private readonly Icon _icon;
    private bool _disposed;

    public event EventHandler? RegionRequested;

    /// <summary>
    /// Right-clicked the icon. Carries the icon's geometry so the menu window can
    /// open against it; raised only when the shell can tell us where the icon is,
    /// since a menu with nothing to anchor to has nowhere to go.
    /// </summary>
    public event EventHandler<TrayMenuAnchor>? MenuRequested;

    /// <summary>Size of the icon frame the shell actually handed us, for diagnostics.</summary>
    public string IconDescription { get; private set; } = "unknown";

    public TrayService()
    {
        // Held for the service's lifetime: this is our GDI+ handle, and letting it
        // be collected can leave the NotifyIcon displaying nothing at all.
        _icon = LoadTrayIcon();
        IconDescription = $"{_icon.Width}x{_icon.Height}" +
            (ReferenceEquals(_icon, SystemIcons.Application) ? " (stock fallback)" : " (tangerine)");
        DiagnosticLog.Write($"TrayService: icon={IconDescription}, SmallIconSize={SystemInformation.SmallIconSize}");

        _notifyIcon = new NotifyIcon
        {
            Text = "Screenshot — Ctrl+Shift+S",
            Icon = _icon,
            Visible = true
        };
        DiagnosticLog.Write($"TrayService: NotifyIcon.Visible={_notifyIcon.Visible}, window=0x{NotifyWindowHandle().ToString("X")}");

        // WinForms throws away Shell_NotifyIcon's result, so "Visible=True" can mean
        // nothing at all. Ask the shell where the icon ended up and, if it is not
        // there, retry — a rejected icon is completely invisible to the user.
        VerifyRegisteredWithShell();

        // Left-click is the product; right-click asks App for the themed menu.
        // The menu is opened on mouse-up because the right button's up message is
        // the one WinForms always reports, whether or not a context menu exists.
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                RegionRequested?.Invoke(this, EventArgs.Empty);
        };
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var anchor = TryGetAnchor();
            if (anchor.HasValue)
                MenuRequested?.Invoke(this, anchor.Value);
            else
                DiagnosticLog.Write("tray menu: the shell has no icon rect for us — right-click ignored");
        };
    }

    // ── Shell registration diagnostics ───────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public int CbSize;
        public IntPtr HWnd;
        public int Uid;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Registers the icon by calling Shell_NotifyIcon directly, so the API's own
    /// return value is visible instead of being swallowed by WinForms.
    /// </summary>
    private void TryDirectRegistration()
    {
        const uint NIM_ADD = 0, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
        var hwnd = NotifyWindowHandle();
        var hicon = _icon.Handle;

        var data = new NotifyIconData
        {
            CbSize = Marshal.SizeOf(typeof(NotifyIconData)),
            HWnd = hwnd,
            Uid = 1,
            Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            CallbackMessage = 0x0400 + 1024,
            HIcon = hicon,
            Tip = "Screenshot — Ctrl+Shift+S",
        };

        uint dpi = GetThreadDpiAwarenessContext_Value();
        DiagnosticLog.Write($"tray diagnostics: hwnd=0x{hwnd.ToInt64():X} hIcon=0x{hicon.ToInt64():X} threadDpiCtx=0x{dpi:X}");
        var added = Shell_NotifyIcon(NIM_ADD, ref data);
        DiagnosticLog.Write($"tray diagnostics: Shell_NotifyIcon(NIM_ADD) -> {added}, winerr={Marshal.GetLastWin32Error()}");
        DiagnosticLog.Write($"tray diagnostics: after direct add, {DescribeShellLookup()}");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    private static uint GetThreadDpiAwarenessContext_Value()
    {
        try { return (uint)GetThreadDpiAwarenessContext().ToInt64(); }
        catch { return 0; }
    }

    // ── Shell registration check ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int CbSize;
        public IntPtr HWnd;
        public int Uid;
        public Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out WinRect rect);

    /// <summary>
    /// Asks the shell where our icon is, in physical screen pixels. Everything that
    /// needs the icon's position goes through here: the registration check below,
    /// and the menu's anchor.
    /// </summary>
    private bool TryQueryIconRect(out WinRect rect)
    {
        rect = default;
        var hwnd = NotifyWindowHandle();
        if (hwnd == IntPtr.Zero) return false;

        var id = new NotifyIconIdentifier
        {
            CbSize = Marshal.SizeOf(typeof(NotifyIconIdentifier)),
            HWnd = hwnd,
            Uid = 1,
            GuidItem = Guid.Empty,
        };

        try
        {
            return Shell_NotifyIconGetRect(ref id, out rect) == 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"tray icon: shell query failed — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Where the menu should open: the icon's rect plus the work area of the monitor
    /// it sits on, which is what tells us where the taskbar is. Physical pixels,
    /// because that is what the shell speaks; the menu window converts to DIPs once
    /// it knows which monitor it landed on.
    /// </summary>
    public TrayMenuAnchor? TryGetAnchor()
    {
        if (!TryQueryIconRect(out var rect)) return null;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return null;

        var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var work = Screen.FromPoint(
            new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)).WorkingArea;
        return new TrayMenuAnchor(bounds, work);
    }

    /// <summary>
    /// Asks the shell where our tray icon is. Only the shell knows whether
    /// Shell_NotifyIcon actually took the icon; WinForms discards that answer.
    /// </summary>
    private bool IsRegisteredWithShell()
    {
        if (NotifyWindowHandle() == IntPtr.Zero)
        {
            DiagnosticLog.Write("tray icon: the NotifyIcon has no message window yet");
            return false;
        }

        if (!TryQueryIconRect(out var rect))
        {
            DiagnosticLog.Write("tray icon: shell has no icon for us");
            return false;
        }

        DiagnosticLog.Write($"tray icon: shell holds it at ({rect.Left},{rect.Top}) {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
        return true;
    }

    /// <summary>
    /// Confirms the icon reached the shell, retrying once if it did not. Only the
    /// shell knows whether Shell_NotifyIcon took the icon — WinForms discards that
    /// answer, so "Visible=True" on its own proves nothing.
    /// </summary>
    private void VerifyRegisteredWithShell()
    {
        if (IsRegisteredWithShell()) return;

        Thread.Sleep(300);
        _notifyIcon.Visible = false;
        _notifyIcon.Visible = true;
        if (IsRegisteredWithShell()) return;

        DiagnosticLog.Write("tray icon: the shell did not accept the WinForms icon — trying a direct Shell_NotifyIcon call");
        TryDirectRegistration();
    }

    /// <summary>One-line summary of what the shell currently holds for us.</summary>
    private string DescribeShellLookup()
    {
        if (NotifyWindowHandle() == IntPtr.Zero) return "no message window yet";
        return TryQueryIconRect(out var rect)
            ? $"FOUND at ({rect.Left},{rect.Top})"
            : "NOT FOUND (the shell has no icon for us)";
    }

    /// <summary>The private WinForms window that carries the icon's shell messages.</summary>
    private IntPtr NotifyWindowHandle()
    {
        var field = typeof(NotifyIcon).GetField("_window",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (field?.GetValue(_notifyIcon) as System.Windows.Forms.NativeWindow)?.Handle ?? IntPtr.Zero;
    }

    /// <summary>
    /// The tray ICO, decoded once at the exact size the shell asks for. Never
    /// throws: this runs first thing in OnStartup, and a decorative asset must
    /// not be able to keep the app from starting.
    /// </summary>
    public static Icon LoadTrayIcon()
    {
        DiagnosticLog.Write($"tray icon: {DescribeIconResource()}");
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo != null)
            {
                // Request the tray-size frame explicitly, so the hand-tuned small
                // glyphs are used at native resolution instead of Windows
                // downscaling the 32px frame.
                return new Icon(streamInfo.Stream, SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"tray icon: resource load FAILED — {ex.GetType().Name}: {ex.Message}");
        }

        // Last resort: the app still works, the tray just isn't branded.
        DiagnosticLog.Write("tray icon: falling back to the stock system icon");
        return SystemIcons.Application;
    }

    /// <summary>One line describing where the icon resource actually resolves from.</summary>
    private static string DescribeIconResource()
    {
        try
        {
            var name = System.Windows.Application.ResourceAssembly?.GetName().Name ?? "null";
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            return info == null
                ? $"resource MISSING (resolveAs={name})"
                : $"resource ok, {info.Stream.Length} bytes (resolveAs={name})";
        }
        catch (Exception ex)
        {
            return $"resource THREW {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// The one balloon the app still raises: the hotkeys — the whole product — could
    /// not be registered, so the user has to be told now rather than when they next
    /// open the menu and notice the warning row.
    /// </summary>
    public void NotifyHotkeyFailed()
    {
        DiagnosticLog.Write("hotkeys: registration FAILED (another app likely owns the shortcut)");
        try
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "Screenshot",
                "Could not register global hotkeys (they may be used by another app). Open the tray menu to retry.",
                ToolTipIcon.Warning);
        }
        catch { }
    }

    /// <summary>Confirms the retry the user just asked for from the menu, so the click has an answer.</summary>
    public void NotifyHotkeysActive()
    {
        try
        {
            _notifyIcon.ShowBalloonTip(
                2500, "Screenshot",
                "Hotkeys active: Ctrl+Shift+S region · Ctrl+Shift+F full screen.",
                ToolTipIcon.Info);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        // NotifyIcon clones the handle, so our own icon is safe to release — but
        // SystemIcons.* are shared static handles and must never be disposed.
        if (!ReferenceEquals(_icon, SystemIcons.Application)) _icon.Dispose();
    }
}

/// <summary>
/// Where the tray icon is: its rect, and the work area of the monitor it sits on,
/// both in physical screen pixels because that is the space the shell reports in.
/// The work area is what reveals the taskbar's edge, which decides whether the menu
/// opens above, below or beside the icon.
/// </summary>
public readonly record struct TrayMenuAnchor(Rectangle IconBounds, Rectangle WorkArea);
