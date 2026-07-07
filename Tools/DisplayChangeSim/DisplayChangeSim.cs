// DisplayChangeSim - reproduce display-settings-change freezes in WindowTabs.
//
// Modes:
//   list                 Enumerate display devices and their current modes.
//   storm [count]        Broadcast WM_SETTINGCHANGE + WM_DISPLAYCHANGE to all
//                        top-level windows [count] times (default 30). This is
//                        the message storm a real display change produces,
//                        without actually changing any display. Safe and fast;
//                        this alone used to deadlock WindowTabs.
//   res <w> <h> [sec]    Change the primary display resolution to w x h,
//                        restore after [sec] seconds (default 5). Produces a
//                        genuine WM_DISPLAYCHANGE storm.
//   off <n>              Detach display n (1-based, \\.\DISPLAYn). The real
//                        "monitor count changed" event.
//   on <n>               Re-attach display n using its registry settings.
//
// Build: run build_tool.bat (uses the .NET Framework csc, no SDK required).

using System;
using System.Runtime.InteropServices;
using System.Threading;

static class DisplayChangeSim
{
    const int WM_SETTINGCHANGE = 0x001A;
    const int WM_DISPLAYCHANGE = 0x007E;
    static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
    const int SMTO_ABORTIFHUNG = 0x0002;

    const int ENUM_CURRENT_SETTINGS = -1;
    const int ENUM_REGISTRY_SETTINGS = -2;

    const int CDS_UPDATEREGISTRY = 0x01;
    const int CDS_NORESET = 0x10000000;

    const int DM_POSITION = 0x20;
    const int DM_PELSWIDTH = 0x80000;
    const int DM_PELSHEIGHT = 0x100000;

    const int DISP_CHANGE_SUCCESSFUL = 0;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        int flags, int timeoutMs, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool EnumDisplayDevices(string device, int devNum, ref DISPLAY_DEVICE dd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int ChangeDisplaySettingsEx(string deviceName, IntPtr devMode, IntPtr hwnd, int flags, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(int numPathArrayElements, IntPtr pathArray, int numModeInfoArrayElements, IntPtr modeInfoArray, int flags);

    const int SDC_TOPOLOGY_EXTEND = 0x00000004;
    const int SDC_APPLY = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    static DEVMODE NewDevMode()
    {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        return dm;
    }

    static void Broadcast(int msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr result;
        // SMTO_ABORTIFHUNG + timeout so the tool itself never hangs on a
        // frozen window (that is exactly what we may be creating).
        SendMessageTimeout(HWND_BROADCAST, msg, wParam, lParam, SMTO_ABORTIFHUNG, 1000, out result);
    }

    static int Storm(int count)
    {
        Console.WriteLine("Broadcasting WM_SETTINGCHANGE + WM_DISPLAYCHANGE x {0} ...", count);
        var w = 2560; var h = 1440; // reported size is irrelevant for the repro
        for (int i = 0; i < count; i++)
        {
            Broadcast(WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
            Broadcast(WM_DISPLAYCHANGE, new IntPtr(32), new IntPtr((h << 16) | (w & 0xFFFF)));
            Console.Write(".");
            Thread.Sleep(50);
        }
        Console.WriteLine();
        Console.WriteLine("Done. If WindowTabs is frozen its tab strips no longer respond to hover.");
        return 0;
    }

    static int List()
    {
        var dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        for (int i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            string mode = "";
            if (attached)
            {
                var dm = NewDevMode();
                if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                    mode = string.Format("{0}x{1} @({2},{3})", dm.dmPelsWidth, dm.dmPelsHeight, dm.dmPositionX, dm.dmPositionY);
            }
            Console.WriteLine("{0}  {1}  attached={2}{3}  {4}",
                dd.DeviceName, mode, attached, primary ? " primary" : "", dd.DeviceString);
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        }
        return 0;
    }

    static int Res(int w, int h, int seconds)
    {
        var original = NewDevMode();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref original))
        {
            Console.WriteLine("Failed to read current display mode.");
            return 1;
        }
        Console.WriteLine("Current: {0}x{1}. Switching to {2}x{3} ...", original.dmPelsWidth, original.dmPelsHeight, w, h);

        var dm = original;
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        var r = ChangeDisplaySettings(ref dm, 0);
        if (r != DISP_CHANGE_SUCCESSFUL)
        {
            Console.WriteLine("ChangeDisplaySettings failed: {0} (mode not supported?)", r);
            return 1;
        }
        Console.WriteLine("Switched. Restoring in {0} s ...", seconds);
        Thread.Sleep(seconds * 1000);
        original.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        ChangeDisplaySettings(ref original, 0);
        Console.WriteLine("Restored.");
        return 0;
    }

    static int Off(int n)
    {
        string name = @"\\.\DISPLAY" + n;
        Console.WriteLine("Detaching {0} ...", name);
        var dm = NewDevMode();
        // Zero size + position with these fields marked = detach from desktop
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        dm.dmPelsWidth = 0;
        dm.dmPelsHeight = 0;
        dm.dmPositionX = 0;
        dm.dmPositionY = 0;
        var r = ChangeDisplaySettingsEx(name, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r != DISP_CHANGE_SUCCESSFUL)
        {
            Console.WriteLine("ChangeDisplaySettingsEx failed: {0}", r);
            return 1;
        }
        // Apply the accumulated registry changes (this is when the actual
        // display-change storm happens)
        ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Console.WriteLine("Detached.");
        return 0;
    }

    static int On(int n)
    {
        string name = @"\\.\DISPLAY" + n;
        // "off" writes a zero-size mode into the registry (that is what
        // detaches the display), so the registry mode is usually unusable for
        // re-attaching. Try it anyway when it looks valid, otherwise ask the
        // OS to re-extend the desktop onto all connected displays.
        var dm = NewDevMode();
        bool haveRegistryMode =
            EnumDisplaySettings(name, ENUM_REGISTRY_SETTINGS, ref dm)
            && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0;
        if (haveRegistryMode)
        {
            Console.WriteLine("Attaching {0} from registry settings ({1}x{2}) ...", name, dm.dmPelsWidth, dm.dmPelsHeight);
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
            var r = ChangeDisplaySettingsEx(name, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (r == DISP_CHANGE_SUCCESSFUL)
            {
                ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                Console.WriteLine("Attached.");
                return 0;
            }
            Console.WriteLine("Registry attach failed ({0}), falling back to SetDisplayConfig.", r);
        }
        Console.WriteLine("Extending desktop onto all connected displays (SetDisplayConfig) ...");
        var rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND | SDC_APPLY);
        if (rc != 0)
        {
            Console.WriteLine("SetDisplayConfig failed: {0}", rc);
            return 1;
        }
        Console.WriteLine("Attached (extend topology). Note: this re-attaches ALL detached displays.");
        return 0;
    }

    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 1)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "list": return List();
                    case "storm": return Storm(args.Length >= 2 ? int.Parse(args[1]) : 30);
                    case "res":
                        if (args.Length >= 3)
                            return Res(int.Parse(args[1]), int.Parse(args[2]), args.Length >= 4 ? int.Parse(args[3]) : 5);
                        break;
                    case "off": if (args.Length >= 2) return Off(int.Parse(args[1])); break;
                    case "on": if (args.Length >= 2) return On(int.Parse(args[1])); break;
                }
            }
            Console.WriteLine("Usage:");
            Console.WriteLine("  DisplayChangeSim list");
            Console.WriteLine("  DisplayChangeSim storm [count]");
            Console.WriteLine("  DisplayChangeSim res <width> <height> [restoreAfterSec]");
            Console.WriteLine("  DisplayChangeSim off <displayNumber>");
            Console.WriteLine("  DisplayChangeSim on <displayNumber>");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }
}
