namespace Bemo

module TabButtonPolicy =
    let activeOnly = "activeOnly"
    let all = "all"
    let none = "none"

    let normalize mode =
        match mode with
        | "all" | "none" -> mode
        | _ -> activeOnly

    type Decision = {
        showClose: bool
        showPin: bool
        pinClickable: bool
    }

    // Active means the group's active tab (the strip's frontmost tab when
    // the top edge is the only one), independently of keyboard focus.
    // Hit testing supplies hover=true for the tab under the current pointer.
    let decide closeMode pinMode isActive hover pinned iconOnly closeRoom pinRoom =
        let allowed mode =
            match normalize mode with
            | "all" -> true
            | "none" -> false
            | _ -> isActive
        let pinVisible = pinned && not iconOnly && pinRoom
        { showClose = not pinned && not iconOnly && closeRoom && hover && allowed closeMode
          showPin = pinVisible
          pinClickable = pinVisible && allowed pinMode }
