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
