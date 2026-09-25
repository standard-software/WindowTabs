namespace Bemo

// Where hideOffScreen parks a window, and how to recognise one that has been
// left there.
//
// A group keeps every tab but the selected one just past the bottom right
// corner of the desktop, and a tab drag parks the windows it is not showing
// in the same place. While the window is in a group that is harmless, and the
// periodic scan puts back a group member left parked by mistake (Program,
// "stranded"). A window that is parked and in NO group has nobody to put it
// back: off-screen fails isTabbableWindow, so it is never grouped again, and
// the stranded pass only walks group members. It stays out there, visible to
// Windows, listed in the taskbar and Alt+Tab, and unreachable. Two ways to get
// there have been seen:
//  - a tab drag that ended with the parked window outside every group
//    (Outlook's Calendar, dropped onto its Mail tab);
//  - WindowTabs killed or crashed while a group had background tabs. The next
//    WindowTabs starts with no groups at all, and every tab the old one parked
//    is an orphan.
//
// The test is geometry only: off every monitor, AND at or beyond both the
// right edge and the bottom edge of the work area union - the corner that
// hideOffScreen aims at. Applications that hide a window of their own put it
// at negative coordinates (-32000 for a minimized window, often -10000 or so
// for a deliberately hidden one), never down and to the right, so the region
// is ours. Plain values, so ParkedWindow.Tests.fsx can check it without
// starting WindowTabs.
module ParkedWindow =

    /// A rectangle as left, top, right, bottom.
    type Box = { left: int; top: int; right: int; bottom: int }

    let private box l t r b = { left = l; top = t; right = r; bottom = b }
    let ofXYWH x y w h = box x y (x + w) (y + h)

    let private maxRight (areas: Box list) = areas |> List.fold (fun m a -> max m a.right) 0
    let private maxBottom (areas: Box list) = areas |> List.fold (fun m a -> max m a.bottom) 0

    /// The top-left corner hideOffScreen moves a window to: 100 pixels past the
    /// rightmost and the bottommost work area edge. Taken as two separate
    /// maxima, so with monitors side by side it is past the corner of neither.
    let parkPoint (workAreas: Box list) =
        (maxRight workAreas + 100, maxBottom workAreas + 100)

    let private overlaps (a: Box) (b: Box) =
        min a.right b.right > max a.left b.left &&
        min a.bottom b.bottom > max a.top b.top

    /// True for a window that sits where only hideOffScreen puts windows.
    let isParked (displays: Box list) (workAreas: Box list) (window: Box) =
        not (List.isEmpty displays) &&
        window.right > window.left && window.bottom > window.top &&
        not (displays |> List.exists (overlaps window)) &&
        window.left >= maxRight workAreas &&
        window.top >= maxBottom workAreas

    /// Where to put a parked window back: centred in the given work area,
    /// shrunk to fit it if it is larger. Returns left, top, width, height.
    let homeFor (workArea: Box) (width: int) (height: int) =
        let areaW = workArea.right - workArea.left
        let areaH = workArea.bottom - workArea.top
        let w = min width areaW
        let h = min height areaH
        (workArea.left + (areaW - w) / 2, workArea.top + (areaH - h) / 2, w, h)
