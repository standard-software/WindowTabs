namespace Bemo

// Pure decisions shared by the settings model and tab-strip placement.
// Keeping Win32 and UI code out makes the compatibility defaults testable.
module TabBehaviorPolicy =
    [<Literal>]
    let verticalAuto = "auto"

    [<Literal>]
    let verticalAlwaysDown = "down"

    let normalizeVerticalDirection value =
        if value = verticalAlwaysDown then verticalAlwaysDown else verticalAuto

    let showInside verticalDirection automaticInside =
        normalizeVerticalDirection verticalDirection = verticalAlwaysDown || automaticInside

    let hideTabs hideOnFullscreen isFullscreen hideWhileMoving isMoving =
        (hideOnFullscreen && isFullscreen) || (hideWhileMoving && isMoving)
