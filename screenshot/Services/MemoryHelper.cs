using System.Diagnostics;
using System.Runtime.InteropServices;

namespace screenshot.Services;

/// <summary>
/// Working-set trimming for a tray-resident utility: collect managed memory and
/// page out the idle working set when the app retreats to the tray.
/// </summary>
internal static class MemoryHelper
{
    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
        catch { }
    }
}
