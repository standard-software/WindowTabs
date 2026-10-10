namespace Bemo

// Numeric Win32 flags keep the policy independent of native calls and assemblies.
module FramelessWindow =
    let isOrdinary style styleEx =
        styleEx &&& 0x00000080L = 0L && // WS_EX_TOOLWINDOW
        style &&& 0x00030000L = 0x00030000L // WS_MINIMIZEBOX | WS_MAXIMIZEBOX

    let isCandidate style styleEx hasTitle =
        styleEx &&& (0x00000080L ||| 0x08000000L) = 0L && // TOOLWINDOW | NOACTIVATE
        style &&& 0x40000000L = 0L && // WS_CHILD
        hasTitle

    let accepts style styleEx hasTitle listed =
        isOrdinary style styleEx || (listed && isCandidate style styleEx hasTitle)

    // Keep expensive identity queries behind the ordinary and cheap fallback checks.
    let acceptsWindow style styleEx hasEntries hasTitle isListed =
        isOrdinary style styleEx ||
        (hasEntries && isCandidate style styleEx (hasTitle ()) && isListed ())

    type Insets = { left: int; top: int; right: int; bottom: int }
    let zeroInsets = { left=0; top=0; right=0; bottom=0 }

    // Assumptions: the toolkit scales shadows in integer steps, and removes
    // them when maximized. Keep both assumptions here for later adjustment.
    let scaledInsets dpi maximized (value: Insets) =
        let factor = if maximized then 0 else max 1 (dpi / 96)
        value.top * factor, value.left * factor, value.right * factor, value.bottom * factor

    // Preserve the window's own DPI when its monitor scale stays the same.
    // Before a cross-DPI move completes, only the destination scale is known.
    let placementDpi windowDpi currentMonitorDpi destinationDpi =
        if currentMonitorDpi = destinationDpi then windowDpi else destinationDpi

    // Invalid settings must not publish a negative-sized visible body. A
    // normal write expands first, so this fallback never changes a round trip.
    let usableInsets width height (top, left, right, bottom) =
        if int64 left + int64 right >= int64 width || int64 top + int64 bottom >= int64 height then
            0,0,0,0
        else top,left,right,bottom

    // Existing frame geometry uses positive OUTSIDE margins. Transparent
    // pixels INSIDE the native rectangle have the opposite sign. Never put
    // the combined value into the detector's cache of actual frame windows.
    let combineMargins (top, left, right, bottom) (innerTop, innerLeft, innerRight, innerBottom) =
        top-innerTop, left-innerLeft, right-innerRight, bottom-innerBottom

    let externalMargins (top, left, right, bottom) (innerTop, innerLeft, innerRight, innerBottom) =
        top+innerTop, left+innerLeft, right+innerRight, bottom+innerBottom
