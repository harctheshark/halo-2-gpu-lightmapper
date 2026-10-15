// Review fix (finding 7): while the window writes lightmap tags outside a bake (restore, undo, recovery restore, repair),
// Windows is told why a shutdown should wait (ShutdownBlockReasonCreate) and the session-ending query is cancelled
// (MainWindow.OnSessionEnding). A forced shutdown (the PC's 8 h auto-shutdown may be one) still ends the process -- which is
// why TagRestore also leaves a recovery marker before its first replace.
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace H2GpuLightmapper.Ui
{
    public static class ShutdownBlock
    {
        public static void Create(Window w, string reason)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero) ShutdownBlockReasonCreate(h, reason);
            }
            catch (Exception) { }
        }

        public static void Destroy(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero) ShutdownBlockReasonDestroy(h);
            }
            catch (Exception) { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
    }
}
