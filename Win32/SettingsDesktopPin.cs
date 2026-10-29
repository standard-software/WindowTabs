using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Bemo
{
    // Only the common prefix through GetViewForHwnd is used. Keep every slot,
    // including unused methods. The returned view stays an opaque COM pointer:
    // passing it to PinView does not require a version-specific IApplicationView IID.
    // Sources: MScholtes/VirtualDesktop (VirtualDesktop11-24H2.cs) and
    // MScholtes/PSVirtualDesktop (VirtualDesktop.ps1).
    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISettingsViewsModern
    {
        [PreserveSig] int GetViews(out IntPtr views);
        [PreserveSig] int GetViewsByZOrder(out IntPtr views);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr views);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
    }

    // Windows 10 1607/1803 and Server 2016 use this older collection IID.
    [ComImport, Guid("2C08ADF0-A386-4B35-9250-0FE183476FCC")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISettingsViewsLegacy
    {
        [PreserveSig] int GetViews(out IntPtr views);
        [PreserveSig] int GetViewsByZOrder(out IntPtr views);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr views);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
    }

    [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISettingsPinnedApps
    {
        [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinAppId([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int UnpinAppId([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int IsViewPinned(IntPtr view, [MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinView(IntPtr view);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISettingsShellServices
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    }

    // Pins this form and its owned dialogs, never the application as a whole.
    // No desktop-switch observation, window movement, or unpinning is involved.
    public sealed class SettingsDesktopPin : IDisposable
    {
        private readonly Form form;
        private readonly Action<string> trace;
        private readonly WINEVENTPROC callback;
        private IntPtr hook;
        private GCHandle callbackRoot;
        private bool disposed;
        private bool queued;
        private bool pinning;
        private int generation;

        public SettingsDesktopPin(Form form, Action<string> trace)
        {
            this.form = form;
            this.trace = trace;
            callback = OnWindowShown;
            form.VisibleChanged += OnVisibleChanged;
            form.HandleDestroyed += OnHandleDestroyed;
            form.Disposed += OnDisposed;
        }

        private void Log(string message)
        {
#if DEBUG
            trace("settings desktop pin " + message);
#endif
        }

        private void StartHook()
        {
            if (hook != IntPtr.Zero) return;
            int pid;
            int tid = WinUserApi.GetWindowThreadProcessId(form.Handle, out pid);
            callbackRoot = GCHandle.Alloc(callback);
            // EVENT_OBJECT_SHOW, own process/thread, WINEVENT_OUTOFCONTEXT.
            // Native modal loops dispatch these events as well.
            hook = WinUserApi.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, callback, pid, tid, 0);
            if (hook == IntPtr.Zero)
            {
                callbackRoot.Free();
                Log("show hook unavailable");
            }
        }

        private void StopHook()
        {
            generation++;
            queued = false;
            if (hook == IntPtr.Zero) return;
            if (WinUserApi.UnhookWinEvent(hook))
            {
                hook = IntPtr.Zero;
                callbackRoot.Free();
            }
            else
            {
                // Keep the callback rooted if native code still owns it. A later
                // hide/dispose retries cleanup; never install a duplicate hook.
                Log("show hook cleanup failed");
            }
        }

        private void OnVisibleChanged(object sender, EventArgs args)
        {
            if (disposed) return;
            if (!form.Visible) { StopHook(); return; }
            try
            {
                StartHook();
                PinWindows();
                // The shell may not have registered a just-shown view yet.
                // One queued retry only; there is no polling or movement fallback.
                QueuePin();
            }
            catch (Exception ex) { LogFailure("show", ex); }
        }

        private void OnWindowShown(IntPtr eventHook, int number, IntPtr hwnd,
            IntPtr objectId, IntPtr childId, int threadId, int eventTime)
        {
            try
            {
                if (disposed || !form.Visible || objectId != IntPtr.Zero || childId != IntPtr.Zero) return;
                foreach (IntPtr owned in VirtualDesktopHelper.GetOwnedDialogWindows(form.Handle))
                    if (hwnd == owned) { QueuePin(); break; }
            }
            catch (Exception ex) { LogFailure("show event", ex); }
        }

        private void QueuePin()
        {
            if (queued || disposed || !form.IsHandleCreated || !form.Visible) return;
            queued = true;
            int stamp = generation;
            try
            {
                form.BeginInvoke((MethodInvoker)delegate
                {
                    if (stamp != generation) return;
                    queued = false;
                    if (!disposed && form.Visible) PinWindows();
                });
            }
            catch (Exception ex) { queued = false; LogFailure("queue", ex); }
        }

        private static object Query(ISettingsShellServices shell, Guid service, Guid iid)
        {
            object result;
            int hr = shell.QueryService(ref service, ref iid, out result);
            if (hr < 0)
            {
                Release(result);
                Marshal.ThrowExceptionForHR(hr);
            }
            if (result == null) throw new COMException("Shell service unavailable.");
            return result;
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        private void PinWindows()
        {
            if (pinning || disposed || !form.Visible || !form.IsHandleCreated) return;
            pinning = true;
            object shellObject = null;
            object views = null;
            object pinsObject = null;
            try
            {
                shellObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239")));
                ISettingsShellServices shell = (ISettingsShellServices)shellObject;
                // Modern first: Windows 10 1809+, Windows 11 including 26200.
                // QueryService negotiates support rather than trusting a build label.
                Guid collectionId = typeof(ISettingsViewsModern).GUID;
                try { views = Query(shell, collectionId, collectionId); }
                catch (COMException)
                {
                    collectionId = typeof(ISettingsViewsLegacy).GUID;
                    views = Query(shell, collectionId, collectionId);
                }
                pinsObject = Query(shell, new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD"), typeof(ISettingsPinnedApps).GUID);
                ISettingsPinnedApps pins = (ISettingsPinnedApps)pinsObject;
                Log("interfaces ready collection=" + collectionId.ToString());
                foreach (IntPtr hwnd in VirtualDesktopHelper.GetOwnedDialogWindows(form.Handle))
                {
                    if (disposed || !form.Visible) break;
                    int pid;
                    WinUserApi.GetWindowThreadProcessId(hwnd, out pid);
                    using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
                        if (pid != process.Id) continue;
                    IntPtr view = IntPtr.Zero;
                    string kind = hwnd == form.Handle ? "root" : "owned";
                    try
                    {
                        int hr = views is ISettingsViewsModern
                            ? ((ISettingsViewsModern)views).GetViewForHwnd(hwnd, out view)
                            : ((ISettingsViewsLegacy)views).GetViewForHwnd(hwnd, out view);
                        Marshal.ThrowExceptionForHR(hr);
                        if (view == IntPtr.Zero) throw new COMException("Window view unavailable.");
                        bool pinned;
                        Marshal.ThrowExceptionForHR(pins.IsViewPinned(view, out pinned));
                        if (pinned) { Log(kind + " already pinned"); continue; }
                        Marshal.ThrowExceptionForHR(pins.PinView(view));
                        Marshal.ThrowExceptionForHR(pins.IsViewPinned(view, out pinned));
                        Log(kind + (pinned ? " pinned" : " pin not confirmed"));
                    }
                    catch (Exception ex) { LogFailure(kind, ex); }
                    finally { if (view != IntPtr.Zero) Marshal.Release(view); }
                }
            }
            catch (Exception ex) { LogFailure("interfaces", ex); }
            finally
            {
                Release(pinsObject);
                Release(views);
                Release(shellObject);
                pinning = false;
            }
        }

        private void LogFailure(string stage, Exception ex)
        {
            Log(stage + " failed hr=0x" + Marshal.GetHRForException(ex).ToString("X8"));
        }

        private void OnHandleDestroyed(object sender, EventArgs args) { StopHook(); }
        private void OnDisposed(object sender, EventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            StopHook();
            form.VisibleChanged -= OnVisibleChanged;
            form.HandleDestroyed -= OnHandleDestroyed;
            form.Disposed -= OnDisposed;
            // Hiding a cached window must not unpin it. PinWindows checks it
            // again on every Show, including after Explorer has restarted.
        }
    }
}
