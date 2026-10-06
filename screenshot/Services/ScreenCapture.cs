using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace screenshot.Services;

/// <summary>
/// GDI BitBlt based screen capture. Coordinates come in as WPF device-independent
/// pixels (1/96"); they are converted with the primary monitor scale, which matches
/// the GDI virtual-screen coordinate space for single / uniform-DPI multi-monitor
/// setups. CAPTUREBLT is not requested, so our own layered windows are excluded.
/// </summary>
internal static class ScreenCapture
{
    public static double PrimaryScale => NativeMethods.GetDpiForSystem() / 96.0;

    public static BitmapSource? Capture(Rect dipRect)
    {
        double scale = PrimaryScale;
        int x = (int)Math.Round(dipRect.X * scale);
        int y = (int)Math.Round(dipRect.Y * scale);
        int w = Math.Max(1, (int)Math.Round(dipRect.Width * scale));
        int h = Math.Max(1, (int)Math.Round(dipRect.Height * scale));

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, w, h);
            if (memDc == IntPtr.Zero || hBitmap == IntPtr.Zero) return null;

            oldObj = NativeMethods.SelectObject(memDc, hBitmap);
            bool ok = NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(memDc, oldObj);
            oldObj = IntPtr.Zero;
            if (!ok) return null;

            // CreateBitmapSourceFromHBitmap copies the pixel data.
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldObj);
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
