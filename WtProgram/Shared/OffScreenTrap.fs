namespace Bemo
open System

// TEMPORARY: catches the rare case of WindowTabs placing a window where no
// monitor is, which has taken a whole group of windows out of sight more than
// once. Every placement call checks its target; one that lands outside every
// screen writes the rectangle and the call stack that produced it to
// %APPDATA%\WindowTabs\offscreen_trap.log. Nothing is prevented - the point is
// to learn which code path computes it.
module OffScreenTrap =
    let private path =
        IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "WindowTabs", "offscreen_trap.log")

    let mutable private written = 0

    // The union of the monitors, read live: a rectangle with no overlap at all
    // is off screen. Fully outside, not partly - a window dragged half past an
    // edge is normal.
    let private isOffScreen (bounds: Rect) =
        try
            let screens = System.Windows.Forms.Screen.AllScreens
            screens
            |> Array.forall (fun s ->
                let r = s.Bounds
                bounds.x >= r.Right || bounds.x + bounds.width <= r.Left ||
                bounds.y >= r.Bottom || bounds.y + bounds.height <= r.Top)
        with _ -> false

    // Debug builds only: a release build must not write a file, or spend
    // anything, on every window placement.
    let check (caller: string) (hwnd: IntPtr) (bounds: Rect) =
#if DEBUG
        try
            if bounds.width > 0 && bounds.height > 0 && written < 200 && isOffScreen bounds then
                written <- written + 1
                let title =
                    try Win32Helper.GetWindowText(hwnd) with _ -> ""
                let stack =
                    try
                        Diagnostics.StackTrace(1, false).GetFrames()
                        |> Array.truncate 24
                        |> Array.map (fun f ->
                            let m = f.GetMethod()
                            if isNull m then "?"
                            else (if isNull m.DeclaringType then "" else m.DeclaringType.Name + ".") + m.Name)
                        |> Array.filter (fun s -> not (s.StartsWith "OffScreenTrap"))
                        |> String.concat " < "
                    with _ -> "(no stack)"
                IO.File.AppendAllText(path,
                    sprintf "%s %s hwnd=%X to (%d,%d %dx%d) title=%s\r\n    %s\r\n"
                        (DateTime.Now.ToString("HH:mm:ss.fff")) caller (int64 hwnd)
                        bounds.x bounds.y bounds.width bounds.height
                        (if title.Length > 60 then title.Substring(0, 60) else title) stack)
        with _ -> ()
#else
        ignore caller
        ignore hwnd
        ignore bounds
#endif
