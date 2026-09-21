namespace Bemo

open System

/// Debug-only trace of what each group sees of the virtual desktops. Truncated
/// at each start, written only while WindowTabs runs from a Debug build, and
/// only when a group's reading changes - a line per group per second would
/// bury the one line that matters.
module VirtualDesktopTrace =
#if DEBUG
    let private path =
        IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs", "vdesktop_trace.log")
    let mutable private started = false
    let private gate = obj()
#endif
    // A thunk, not a string, so the sprintf at the call site does not run in
    // Release (see RestoreTrace, which this follows).
    let log (f: unit -> string) =
#if DEBUG
        try
            lock gate (fun () ->
                if not started then
                    started <- true
                    try IO.File.WriteAllText(path, "") with _ -> ()
                IO.File.AppendAllText(path,
                    sprintf "%s %s\r\n" (DateTime.Now.ToString("HH:mm:ss.fff")) (f())))
        with _ -> ()
#else
        ignore f
#endif

/// A tab group is one window as far as the person using it is concerned, so it
/// belongs on one virtual desktop. Windows offers no way to keep it that way:
/// a tabbed window can be sent to another desktop from task view or the
/// taskbar, and nothing tells us it happened. So each group looks at where its
/// own windows are, once a second, and this module says what that picture
/// means.
///
/// Nothing here calls Windows. The caller reads the desktop ids and carries
/// out the answer, so the rule itself is arithmetic over made-up numbers and
/// can be run without opening a window (VirtualDesktopIntegrity.Tests.fsx).
module VirtualDesktopIntegrity =

    /// Whether a confirmed straddle is acted on, or only written to the trace.
    /// Turning it off leaves a build that watches and writes the trace
    /// without touching a group - which is how the reading itself was put on
    /// trial after it took a session apart once.
    let mutable actOnStraddle = true

    /// One window of a group, as the rule sees it. `desktop` is None when
    /// Windows would not say which desktop the window is on: GetWindowDesktopId
    /// answers TYPE_E_ELEMENTNOTFOUND for a window of another process often
    /// enough that it has to be an ordinary case rather than an error. A window
    /// we cannot read is never treated as having moved - that would take a
    /// group apart because an answer was missing for a second.
    type WindowDesktop =
        {
            hwnd: IntPtr
            desktop: Guid option
        }

    type Decision =
        /// Every window we can read is on one desktop, or too little is known
        /// to say otherwise.
        | Settled
        /// The group is spread over more than one desktop. `baseDesktop` is
        /// where the group is taken to be, and `strays` are the windows that
        /// are somewhere else.
        | Straddling of baseDesktop: Guid * strays: IntPtr list

    let private readable windows =
        windows |> List.choose (fun w -> w.desktop |> Option.map (fun d -> w.hwnd, d))

    /// What a group looked like a moment ago, so that the window which moved
    /// can be told from the windows which did not.
    let reading (windows: WindowDesktop list) = readable windows |> Map.ofList

    /// `before` is the previous reading, `active` the group's front window.
    ///
    /// The window that moved is the one that left. That sounds too obvious to
    /// write down, and it is the whole rule: the first version of this took the
    /// front window's desktop for the group's, and since sending a window to
    /// another desktop also brings it to the front, a group would decide that
    /// the one window which had stayed put was in the right place and the other
    /// fifteen had wandered off - and then detach them, one a second. So a
    /// window's desktop is compared with the desktop that window was on, not
    /// with whichever window happens to be in front.
    ///
    /// Only when there is nothing to compare against - the first reading after
    /// WindowTabs starts, where a restored group may already be spread out -
    /// does the picture alone have to answer. Then the desktop most of the
    /// group is on is taken to be the group's, and the front window breaks a
    /// tie.
    let decide (before: Map<IntPtr, Guid>) (active: IntPtr) (windows: WindowDesktop list) : Decision =
        let known = readable windows
        match known with
        | [] | [_] -> Settled
        | _ ->
            let desktops = known |> List.map snd |> List.distinct
            if desktops.Length <= 1 then Settled
            else
                let moved =
                    known |> List.filter (fun (hwnd, desktop) ->
                        match before.TryFind(hwnd) with
                        | Some(was) -> was <> desktop
                        | None -> false)
                let stayedDesktops =
                    known
                    |> List.filter (fun (hwnd, _) -> moved |> List.forall (fun (m, _) -> m <> hwnd))
                    |> List.map snd
                    |> List.distinct
                match moved, stayedDesktops with
                // Some windows moved and the rest are still together: the ones
                // that moved are the ones that left, wherever the front window
                // may be.
                | (_ :: _), [whereTheGroupIs] -> Straddling(whereTheGroupIs, moved |> List.map fst)
                | _ ->
                    let count desktop =
                        known |> List.filter (fun (_, other) -> other = desktop) |> List.length
                    let most = desktops |> List.map count |> List.max
                    let contenders = desktops |> List.filter (fun d -> count d = most)
                    let baseDesktop =
                        match contenders with
                        | [only] -> only
                        | _ ->
                            match known |> List.tryFind (fun (hwnd, _) -> hwnd = active) with
                            | Some(_, desktop) when List.contains desktop contenders -> desktop
                            | _ -> List.head contenders
                    let strays =
                        known
                        |> List.filter (fun (_, desktop) -> desktop <> baseDesktop)
                        |> List.map fst
                    Straddling(baseDesktop, strays)

    /// Whether a decision is worth acting on at all.
    ///
    /// Windows whose id changed since the last reading were moved while we were
    /// watching: there is no doubt about those, however many of them there are,
    /// and a whole group can be dragged to another desktop a window at a time.
    /// A group that merely looks spread out, with nothing to compare against,
    /// is another matter - if it says that most of itself has wandered off it
    /// has read something wrong, and nothing it says should take windows out.
    let plausible (before: Map<IntPtr, Guid>) (windows: WindowDesktop list) (decision: Decision) =
        match decision with
        | Settled -> false
        | Straddling(_, strays) ->
            let known = readable windows
            let movedSinceLastReading hwnd =
                match before.TryFind(hwnd), known |> List.tryFind (fun (h, _) -> h = hwnd) with
                | Some(was), Some(_, now) -> was <> now
                | _ -> false
            not strays.IsEmpty
            && (strays |> List.forall movedSinceLastReading
                || strays.Length * 2 <= known.Length)

    /// The same straddle, seen twice running. Switching desktops, or a window
    /// answering oddly for a moment while it is being cloaked, shows up on one
    /// tick and is gone on the next; a window that was really moved keeps
    /// answering the same way. Only a picture that survives a second is acted
    /// on.
    let confirmed (previous: Decision) (current: Decision) =
        match previous, current with
        | Straddling(before, strayedBefore), Straddling(now, strayedNow)
            when before = now && List.sort strayedBefore = List.sort strayedNow ->
            Some(now, strayedNow)
        | _ -> None
