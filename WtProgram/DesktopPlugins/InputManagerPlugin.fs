namespace Bemo
open System
open System.Runtime.InteropServices

type InputManagerPlugin(msgSet:Set2<Int32>) as this =
    let hookProcDelegate = HOOKPROC(this.llHook)
    let OS = OS()

    member this.desktop = Services.desktop

    member this.foregroundGroup = Services.desktop.foregroundGroup

    member this.llHook nCode (wParam:IntPtr) lParam = 
        let msg = wParam.ToInt32()
        if msgSet.contains(msg) then
            this.foregroundGroup.iter <| fun group ->
                let hookStruct = unbox<MSLLHOOKSTRUCT>(Marshal.PtrToStructure(lParam, typeof<MSLLHOOKSTRUCT>))
                let pt = hookStruct.pt.Pt
                let data = hookStruct.mouseData.IntPtr
                let groupInfo = group.cast<GroupInfo>()
                groupInfo.invokeGroup <| fun() ->
                    groupInfo.group.postMouseLL(msg, pt, data)

        WinUserApi.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam)

    member this.registerMouseLLHook() =
        WinUserApi.SetWindowsHookEx(WindowHookTypes.WH_MOUSE_LL, hookProcDelegate, IntPtr.Zero, 0).ignore

    // No keyboard hook. There used to be a WH_KEYBOARD_LL hook here that fed
    // every keystroke on the machine to the foreground group, for the
    // Ctrl+number plugin alone; that plugin is gone (the number keys are
    // RegisterHotKey hot keys now, see Program.syncHotKeys), and a hook with
    // no listener would still route all typing through this process.

    interface IPlugin with
        member x.init() =
            this.registerMouseLLHook()