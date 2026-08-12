using System;
using System.Runtime.InteropServices;

namespace Bemo
{
    /// <summary>
    /// Process-, thread- and monitor-level DPI helpers, plus the icon
    /// re-extraction used to get an application icon at a physical size that
    /// is not 16 or 32 pixels.
    ///
    /// WindowTabs declares Per-Monitor-V2 awareness in its application
    /// manifest (WtProgram/app.manifest). <see cref="EnablePerMonitorV2"/> is
    /// a runtime fallback for the case where that declaration never reaches
    /// the executable (a packaging step that strips the win32 manifest). When
    /// the manifest already applied, the call fails with ERROR_ACCESS_DENIED
    /// and nothing changes.
    ///
    /// Every entry point here is Windows-8.1/10 era, so each import is guarded:
    /// on an older OS the P/Invoke throws EntryPointNotFoundException (or
    /// DllNotFoundException for shcore.dll) on first use, which is caught and
    /// remembered so the process degrades to the old, DPI-unaware behaviour
    /// instead of crashing.
    /// </summary>
    public static class DpiApi
    {
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new IntPtr(-1);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = new IntPtr(-2);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new IntPtr(-3);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        // PROCESS_DPI_AWARENESS (shcore.dll, Windows 8.1+)
        private const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;

        // CopyImage: re-read the image from the resource it was loaded from
        // instead of stretching the handle's current bitmap.
        private const int LR_COPYFROMRESOURCE = 0x4000;
        private const int IMAGE_ICON = 1;

        public const int DefaultDpi = 96;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref RECT rect, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CopyImage(IntPtr hImage, int uType, int cxDesired, int cyDesired, int fuFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static bool threadContextApiMissing;
        private static bool monitorDpiApiMissing;

        /// <summary>
        /// Opt the process into Per-Monitor-V2 DPI awareness. Returns true when
        /// this call changed the awareness; false when it was already set (the
        /// normal case: the manifest wins) or when no API is available.
        /// </summary>
        public static bool EnablePerMonitorV2()
        {
            try
            {
                return SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                // Windows 8.1: per-monitor V1.
                return SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE) == 0;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                // Vista+: system DPI awareness, the best that is available.
                return SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            return false;
        }

        /// <summary>
        /// Switch the calling thread to DPI-unaware. Windows created by the
        /// thread while this context is active behave exactly as they did
        /// before WindowTabs became DPI-aware (the OS bitmap-stretches them),
        /// which is what the legacy WinForms settings UI expects.
        /// Returns the previous context, to be passed to
        /// <see cref="RestoreThreadContext"/>.
        /// </summary>
        public static IntPtr SetThreadUnaware()
        {
            if (threadContextApiMissing) return IntPtr.Zero;
            try
            {
                return SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_UNAWARE);
            }
            catch (EntryPointNotFoundException)
            {
                threadContextApiMissing = true;
                return IntPtr.Zero;
            }
        }

        public static void RestoreThreadContext(IntPtr previous)
        {
            if (previous == IntPtr.Zero || threadContextApiMissing) return;
            try
            {
                SetThreadDpiAwarenessContext(previous);
            }
            catch (EntryPointNotFoundException)
            {
                threadContextApiMissing = true;
            }
        }

        /// <summary>
        /// Effective DPI of the monitor that contains (or is nearest to) the
        /// given rectangle. Falls back to 96 when the monitor cannot be
        /// queried, so callers always get a usable value.
        /// </summary>
        public static int GetDpiForRect(int left, int top, int width, int height)
        {
            RECT rect = new RECT();
            rect.Left = left;
            rect.Top = top;
            rect.Right = left + width;
            rect.Bottom = top + height;
            try
            {
                return GetDpiForMonitorHandle(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
            }
            catch (EntryPointNotFoundException)
            {
                return DefaultDpi;
            }
        }

        public static int GetDpiForPoint(int x, int y)
        {
            try
            {
                return GetDpiForMonitorHandle(MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST));
            }
            catch (EntryPointNotFoundException)
            {
                return DefaultDpi;
            }
        }

        public static int GetDpiForMonitorHandle(IntPtr monitor)
        {
            if (monitor == IntPtr.Zero || monitorDpiApiMissing) return DefaultDpi;
            try
            {
                uint dpiX, dpiY;
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0 && dpiX > 0)
                {
                    return (int)dpiX;
                }
                return DefaultDpi;
            }
            catch (EntryPointNotFoundException)
            {
                monitorDpiApiMissing = true;
                return DefaultDpi;
            }
            catch (DllNotFoundException)
            {
                monitorDpiApiMissing = true;
                return DefaultDpi;
            }
        }

        /// <summary>
        /// A copy of <paramref name="hIcon"/> rendered at exactly
        /// <paramref name="size"/> x <paramref name="size"/> device pixels, or
        /// IntPtr.Zero when Windows refuses. LR_COPYFROMRESOURCE makes Windows
        /// go back to the icon's original resource and pick the published image
        /// closest to the requested size (an icon usually carries 16/24/32/48/256),
        /// which is what keeps a tab icon sharp at 125%, 150% or 200% instead of
        /// stretching the 16 px image. Without the flag the current image is
        /// simply stretched, so the retry below is still an improvement over
        /// letting GDI+ scale at draw time.
        ///
        /// The returned handle is owned by the caller and must be released with
        /// <see cref="DestroyScaledIcon"/>.
        /// </summary>
        public static IntPtr CopyIconAtSize(IntPtr hIcon, int size)
        {
            if (hIcon == IntPtr.Zero || size <= 0) return IntPtr.Zero;
            try
            {
                IntPtr copy = CopyImage(hIcon, IMAGE_ICON, size, size, LR_COPYFROMRESOURCE);
                if (copy != IntPtr.Zero) return copy;
                return CopyImage(hIcon, IMAGE_ICON, size, size, 0);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        public static void DestroyScaledIcon(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return;
            try { DestroyIcon(hIcon); }
            catch (Exception) { }
        }
    }
}
