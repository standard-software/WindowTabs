// Copyright 2008 Bemo Software, Inc. All Rights Reserved.

using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

namespace Bemo
{
    #region Structures
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES 
    {
        public Int32 dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public Int32 fVisible;
        public Int32 fSourceClientAreaOnly;
    };
    #endregion

    #region Constants
    public sealed class DWMWINDOWATTRIBUTE
    {
        public const int DWMWA_NCRENDERING_ENABLED              = 1;
        public const int DWMWA_NCRENDERING_POLICY               = 2;
        public const int DWMWA_TRANSITIONS_FORCEDISABLED        = 3;
        public const int DWMWA_ALLOW_NCPAINT                    = 4;
        public const int DWMWA_CAPTION_BUTTON_BOUNDS            = 5;
        public const int DWMWA_NONCLIENT_RTL_LAYOUT             = 6;
        public const int DWMWA_FORCE_ICONIC_REPRESENTATION      = 7;
        public const int DWMWA_FLIP3D_POLICY                    = 8;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS            = 9;
        public const int DWMWA_HAS_ICONIC_BITMAP                = 10;
        public const int DWMWA_DISALLOW_PEEK                    = 11;
        public const int DWMWA_EXCLUDED_FROM_PEEK               = 12;
        public const int DWMWA_CLOAK                            = 13;
        public const int DWMWA_CLOAKED                          = 14;
        public const int DWMWA_LAST                             = 15;
    }

    // DWMWA_CLOAKED values
    public sealed class DWM_CLOAKED
    {
        public const int DWM_CLOAKED_APP       = 0x00000001;  // Cloaked by its owner app
        public const int DWM_CLOAKED_SHELL     = 0x00000002;  // Cloaked by the shell
        public const int DWM_CLOAKED_INHERITED = 0x00000004;  // Inherited from owner window
    }
    public sealed class DWMNCRENDERINGPOLICY
    {
        public const int DWMNCRP_USEWINDOWSTYLE = 0;
        public const int DWMNCRP_DISABLED       = 1;
        public const int DWMNCRP_ENABLED        = 2;
        public const int DWMNCRP_LAST           = 3;
    }
    public sealed class DWM_THUMBNAIL_PROPERTY_FLAGS
    {
        public const int DWM_TNP_RECTDESTINATION = 0x00000001;
        public const int DWM_TNP_RECTSOURCE = 0x00000002;
        public const int DWM_TNP_OPACITY = 0x00000004;
        public const int DWM_TNP_VISIBLE = 0x00000008;
        public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;
    }
    #endregion

    public sealed class DwmApi
    {
#if DEBUG
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int TraceNative_DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
        public static int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute)
        {
            var call = Bemo.Win32.GroupCallTrace.Begin("DwmGetWindowAttribute", hwnd);
            try { return TraceNative_DwmGetWindowAttribute(hwnd, dwAttribute, out pvAttribute, cbAttribute); }
            finally { Bemo.Win32.GroupCallTrace.End(call); }
        }
#else
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
#endif
#if DEBUG
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int TraceNative_DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        public static int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute)
        {
            var call = Bemo.Win32.GroupCallTrace.Begin("DwmGetWindowAttribute", hwnd);
            try { return TraceNative_DwmGetWindowAttribute(hwnd, dwAttribute, out pvAttribute, cbAttribute); }
            finally { Bemo.Win32.GroupCallTrace.End(call); }
        }
#else
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
#endif
#if DEBUG
        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        private static extern int TraceNative_DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
        public static int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute)
        {
            var call = Bemo.Win32.GroupCallTrace.Begin("DwmSetWindowAttribute", hwnd);
            try { return TraceNative_DwmSetWindowAttribute(hwnd, dwAttribute, ref pvAttribute, cbAttribute); }
            finally { Bemo.Win32.GroupCallTrace.End(call); }
        }
#else
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
#endif
        [DllImport("dwmapi.dll")]
        public static extern int DwmIsCompositionEnabled(out bool enabled);
#if DEBUG
        [DllImport("dwmapi.dll", EntryPoint = "DwmSetIconicThumbnail")]
        private static extern int TraceNative_DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hBmp, int dwSITFlags);
        public static int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hBmp, int dwSITFlags)
        {
            var call = Bemo.Win32.GroupCallTrace.Begin("DwmSetIconicThumbnail", hwnd);
            try { return TraceNative_DwmSetIconicThumbnail(hwnd, hBmp, dwSITFlags); }
            finally { Bemo.Win32.GroupCallTrace.End(call); }
        }
#else
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hBmp, int dwSITFlags);
#endif
#if DEBUG
        [DllImport("dwmapi.dll", EntryPoint = "DwmSetIconicLivePreviewBitmap")]
        private static extern int TraceNative_DwmSetIconicLivePreviewBitmap(IntPtr hwnd, IntPtr hBmp, ref POINT pptClient, int dwSITFlags);
        public static int DwmSetIconicLivePreviewBitmap(IntPtr hwnd, IntPtr hBmp, ref POINT pptClient, int dwSITFlags)
        {
            var call = Bemo.Win32.GroupCallTrace.Begin("DwmSetIconicLivePreviewBitmap", hwnd);
            try { return TraceNative_DwmSetIconicLivePreviewBitmap(hwnd, hBmp, ref pptClient, dwSITFlags); }
            finally { Bemo.Win32.GroupCallTrace.End(call); }
        }
#else
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetIconicLivePreviewBitmap(IntPtr hwnd, IntPtr hBmp, ref POINT pptClient, int dwSITFlags);
#endif
#if DEBUG
        [DllImport("dwmapi.dll", EntryPoint = "DwmInvalidateIconicBitmaps")]
        private static extern int TraceNative_DwmInvalidateIconicBitmaps(IntPtr hwnd);
        public static int DwmInvalidateIconicBitmaps(IntPtr hwnd)
        {
            var call = Bemo.Win32.GroupCallTrace.Begin("DwmInvalidateIconicBitmaps", hwnd);
            try { return TraceNative_DwmInvalidateIconicBitmaps(hwnd); }
            finally { Bemo.Win32.GroupCallTrace.End(call); }
        }
#else
        [DllImport("dwmapi.dll")]
        public static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);
#endif
        [DllImport("dwmapi.dll")]
        public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")]
        public static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnail, ref DWM_THUMBNAIL_PROPERTIES props);
#if DEBUG
        [DllImport("dwmapi.dll", EntryPoint = "#113", SetLastError = true)]
        private static extern uint TraceNative_DwmpActivateLivePreview(bool doPeek, IntPtr hWnd, IntPtr hwndTop, bool unknown);
        public static uint DwmpActivateLivePreview(bool doPeek, IntPtr hWnd, IntPtr hwndTop, bool unknown)
        {
            var call = Bemo.Win32.GroupCallTrace.Begin("DwmpActivateLivePreview", hWnd);
            try { return TraceNative_DwmpActivateLivePreview(doPeek, hWnd, hwndTop, unknown); }
            finally { Bemo.Win32.GroupCallTrace.End(call); }
        }
#else
        [DllImport("dwmapi.dll", EntryPoint = "#113", SetLastError = true)]
        public static extern uint DwmpActivateLivePreview(bool doPeek, IntPtr hWnd, IntPtr hwndTop, bool unknown);
#endif

    }
}
