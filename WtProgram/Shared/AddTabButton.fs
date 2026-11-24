namespace Bemo

// The add-tab button ([+]) of a tab strip. It is not a tab: it is in no
// order, cannot be selected and cannot be moved. A strip that shows one lays
// its tabs out in a strip one button shorter (the place kept at the far end),
// so the layout and the drop position of a dragged tab are calculated exactly
// as without it, only in a shorter strip. The buttons are drawn and hit
// outside that layout. Nothing here depends on a window or a tab's identity.
module AddTabButton =

    /// The group of tabs a button belongs to: the one against the start of
    /// the strip (left, or top on a left or right strip) or against its end.
    type Slot =
        | AtStart
        | AtEnd

    /// How long a button is along its strip: `wanted` (a tab's height), or
    /// nothing when the strip is too short to keep a button's place.
    let length (wanted: int) (stripLength: int) =
        if wanted > 0 && stripLength >= 2 * wanted then wanted else 0

    /// Show a button only for an alignment group containing real tabs.
    let slots (hasStart: bool) (hasEnd: bool) =
        [ if hasStart then yield AtStart
          if hasEnd then yield AtEnd ]

    /// Where each button starts along the strip. `area` is the length the
    /// tabs are laid out in (the strip less one button), `startEnd` where the
    /// start group ends and `endStart` where the end group starts.
    ///
    /// The end group's button stands in the place kept after the tabs. The
    /// start group's stands right after its last tab: in the free space
    /// between the groups, and left out while that space is too narrow for
    /// it; with no end group it can always fall back on the kept place.
    let positions (area: int) (button: int) (hasStart: bool) (hasEnd: bool) (startEnd: float) (endStart: float) : (Slot * int) list =
        if button <= 0 || area < 0 then []
        else
            slots hasStart hasEnd |> List.choose (fun slot ->
                match slot with
                | AtEnd -> Some(AtEnd, area)
                | AtStart ->
                    let at = max 0 (int (ceil startEnd))
                    if not hasEnd then Some(AtStart, min at area)
                    elif float (at + button) <= floor endStart then Some(AtStart, at)
                    else None)

    /// A press on a button and the release that makes it a click: the state
    /// after a mouse button went down or up, and the pressed button when the
    /// release completes a click on it. `hit` is the button under the pointer.
    let press (down: bool) (up: bool) (hit: 'button option) (current: 'button option) : 'button option * 'button option =
        if down then hit, None
        elif up then None, (match current, hit with
                            | Some pressed, Some released when pressed = released -> Some pressed
                            | _ -> None)
        else current, None
