using System;
using System.Runtime.InteropServices;

namespace screenshot.Services;

/// <summary>
/// Drops Escape presses that no human made.
///
/// Other screenshot tools cancel a competitor's overlay before showing their own by sending
/// Escape. Feishu does this, and WeChat and QQ do the same thing. The Escape arrives while our
/// overlay owns the screen, the overlay reads it as "the user pressed Esc", and the capture is
/// abandoned before a single pixel is taken. The symptom is the overlay vanishing the instant
/// it appears, which looks like a hotkey conflict and is not one.
///
/// There are two ways such a key can arrive and they need separate checks:
///
/// <list type="bullet">
/// <item><b>Injected</b> (<c>SendInput</c> / <c>keybd_event</c>). A <c>WH_KEYBOARD_LL</c> hook
/// sees every keystroke and sets <c>LLKHF_INJECTED</c> on the ones that did not come from the
/// keyboard. Injected Escape is swallowed in the hook, so it never reaches the overlay.</item>
/// <item><b>Posted</b> (<c>PostMessage</c> of <c>WM_KEYDOWN</c>). A posted message never passes
/// through a hook, so <see cref="IsPhysicalKeyDown"/> is the backstop: while a real key's
/// <c>WM_KEYDOWN</c> is being handled the key is still held down, whereas an injected one has
/// already been released and a posted one never set that state at all.</item>
/// </list>
///
/// Install it as the cancellable surface goes on screen and dispose it when that surface
/// closes. The hook is global for as long as it is installed, so the window is kept short.
/// </summary>
internal sealed class ForeignInputGuard : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private bool _disposed;

    public ForeignInputGuard()
    {
        // Held in a field on purpose: the OS keeps a raw pointer to this delegate, so a
        // collected one would leave the hook calling into freed memory.
        _proc = Hook;
    }

    /// <summary>Foreign Escape presses dropped since this guard was installed.</summary>
    public int Swallowed { get; private set; }

    /// <summary>Installs the hook. Safe to call twice; a failure degrades to the key-state check.</summary>
    public void Install()
    {
        if (_disposed || _hook != IntPtr.Zero) return;

        // Over Remote Desktop every keystroke is injected, including the user's own, so the
        // hook would swallow the Escape they pressed to cancel. Leave it off there and let the
        // key-state check handle a posted message instead.
        if (NativeMethods.IsRemoteSession())
        {
            DiagnosticLog.Write("input: remote session, not installing the keyboard hook (every key looks injected here)");
            return;
        }

        // hMod only matters for hooks injected into another process; a low-level hook runs on
        // this thread's message loop, which the WPF dispatcher already pumps.
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);

        DiagnosticLog.Write(_hook != IntPtr.Zero
            ? "input: keyboard hook installed, injected Escape will be dropped"
            : $"input: keyboard hook FAILED (winerr={Marshal.GetLastWin32Error()}), relying on the key-state check");
    }

    private IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsInjectedEscape(wParam, lParam))
        {
            Swallowed++;
            DiagnosticLog.Write(
                $"input: dropped an injected Escape ({Swallowed} so far) — another app tried to cancel the overlay");
            return 1;   // swallow it: it must not reach the overlay
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsInjectedEscape(IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        if (msg != NativeMethods.WM_KEYDOWN && msg != NativeMethods.WM_SYSKEYDOWN) return false;

        var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        return info.vkCode == NativeMethods.VK_ESCAPE
               && (info.flags & NativeMethods.LLKHF_INJECTED) != 0;
    }

    /// <summary>
    /// True when the key is down on the keyboard right now. This is what separates a real press
    /// from a posted message, which never touches this state.
    /// </summary>
    public static bool IsPhysicalKeyDown(uint vk) =>
        (NativeMethods.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
