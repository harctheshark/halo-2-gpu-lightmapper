using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace H2GpuLightmapper.Ui
{
    /// <summary>
    /// Immersive dark title bar via DwmSetWindowAttribute. Attribute 20 (Win10 20H1+/Win11), falling back to 19 (Win10 1809-1909).
    /// Apply it in SourceInitialized (before the first show) so the caption never paints light first.
    /// </summary>
    public static class DarkTitleBar
    {
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>Returns the HRESULT of the call that was accepted (0 = S_OK); sets attrUsed to 20 or 19.</summary>
        public static int Apply(IntPtr hwnd, bool dark, out int attrUsed)
        {
            int v = dark ? 1 : 0;
            attrUsed = DWMWA_USE_IMMERSIVE_DARK_MODE;
            int hr = DwmSetWindowAttribute(hwnd, attrUsed, ref v, 4);
            if (hr != 0)
            {
                attrUsed = DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
                hr = DwmSetWindowAttribute(hwnd, attrUsed, ref v, 4);
            }
            return hr;
        }

        /// <summary>Hook a WPF window so the dark caption is applied on SourceInitialized. Returns nothing; failures are ignored (cosmetic).</summary>
        public static void Attach(Window w, bool dark = true)
        {
            w.SourceInitialized += (s, e) =>
            {
                int used;
                Apply(new WindowInteropHelper(w).Handle, dark, out used);
            };
        }

        /// <summary>Read-back of attribute 20 (Win11 answers it; older builds may return E_INVALIDARG).</summary>
        public static int Query(IntPtr hwnd, out int value)
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, out value, 4);
        }

        /// <summary>The visible frame in physical screen pixels (excludes the invisible resize borders).</summary>
        public static RECT FrameBounds(IntPtr hwnd)
        {
            RECT r;
            DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT)));
            return r;
        }
    }
}
