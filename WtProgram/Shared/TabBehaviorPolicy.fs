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

    // TabPositionByDefault as the settings file holds it -> the value the
    // program works with. Older files hold "left" / "right"; "center" and
    // "TopCenter" are retired and fall back to "TopLeft". Anything else is
    // kept as it is (WindowGroup reads every value but "TopLeft" as right).
    let loadTabPosition (stored: string option) =
        match stored with
        | Some("left") -> "TopLeft"
        | Some("center") -> "TopLeft"
        | Some("TopCenter") -> "TopLeft"
        | Some("right") -> "TopRight"
        | Some(v) -> v
        | None -> "TopRight"

    let showInside verticalDirection automaticInside =
        normalizeVerticalDirection verticalDirection = verticalAlwaysDown || automaticInside

    // Automatic inside placement still reserves space for an outside strip.
    let reserveTopMargin enabled verticalDirection =
        enabled && normalizeVerticalDirection verticalDirection <> verticalAlwaysDown

    let hideTabs hideOnFullscreen isFullscreen hideWhileMoving isMoving =
        (hideOnFullscreen && isFullscreen) || (hideWhileMoving && isMoving)

    let hideFromIconMouseDown enabled activeAtMouseDown pressedOnIcon =
        enabled && activeAtMouseDown && pressedOnIcon

    // A pending timer belongs to one edge; unrelated hover changes keep it.
    let restartHideDelay shouldHide alreadyHidden delay pendingDelay =
        shouldHide && not alreadyHidden && pendingDelay <> Some delay

    // WinForms timers require a positive interval even when the UI accepts zero.
    let hideTimerInterval delay = max 1 delay
