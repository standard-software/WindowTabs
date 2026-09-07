namespace Bemo

// Everything the Shortcut Keys tab DECIDES, with no Win32 in it.
//
// Program.fs owns the RegisterHotKey calls, the foreground hook and the tab
// switching; ShortcutKeysView.fs owns the dialog. Both ask this module the
// same questions - which key each number field stands for under the check
// boxes, which keys are worth registering, whether the window that just came
// to the front wants them held or released, and what has to change to get
// from the keys held now to the keys wanted - so the fields and the actual
// registrations cannot disagree. Plain functions over ints, strings and
// booleans, which is what lets Tools\HotKeyPolicy.Tests.fsx load this very
// file and check the rules without WindowTabs running.
//
// Depends on nothing but FSharp.Core.
module HotKeyPolicy =

    // ---------------------------------------------------------------- codes --

    // A hot key is stored as the packed int the hot key control uses:
    // low byte = virtual key, high byte = HOTKEYF_* flags. The same values
    // live in Win32\CommCtrl.cs; they are repeated here so the module stays
    // loadable on its own.
    [<Literal>]
    let HOTKEYF_SHIFT = 0x01
    [<Literal>]
    let HOTKEYF_CONTROL = 0x02
    [<Literal>]
    let HOTKEYF_ALT = 0x04

    // What RegisterHotKey wants instead. The Alt and Shift bits are swapped
    // between the two encodings.
    [<Literal>]
    let MOD_ALT = 0x0001
    [<Literal>]
    let MOD_CONTROL = 0x0002
    [<Literal>]
    let MOD_SHIFT = 0x0004

    /// The "None" of a hot key field.
    let noKey = 0

    let pack (hotKeyFlags: int) (virtualKey: int) =
        (virtualKey &&& 0xFF) ||| ((hotKeyFlags &&& 0xFF) <<< 8)

    let virtualKeyOf (code: int) = code &&& 0xFF

    let flagsOf (code: int) = (code >>> 8) &&& 0xFF

    /// A field with no key in it registers nothing. Modifiers alone are not
    /// a key either: the hot key control never produces them, but a
    /// hand-edited file could.
    let isSet (code: int) = virtualKeyOf code <> 0

    /// (fsModifiers, vk) for RegisterHotKey.
    let toRegisterHotKey (code: int) =
        let flags = flagsOf code
        let modifiers =
            (if flags &&& HOTKEYF_CONTROL <> 0 then MOD_CONTROL else 0) |||
            (if flags &&& HOTKEYF_ALT <> 0 then MOD_ALT else 0) |||
            (if flags &&& HOTKEYF_SHIFT <> 0 then MOD_SHIFT else 0)
        modifiers, virtualKeyOf code

    // VK_1 .. VK_9 are 0x31 .. 0x39: the digit plus 0x30.
    let digitVirtualKey (n: int) = 0x30 + n

    let ctrlDigit (n: int) = pack HOTKEYF_CONTROL (digitVirtualKey n)
    let altDigit (n: int) = pack HOTKEYF_ALT (digitVirtualKey n)

    // ------------------------------------------------------------- settings --

    // Keys under the "HotKeys" object of the settings file. nextTab and
    // prevTab predate this tab and keep their names, so nobody's setting is
    // lost; the rest are new.
    let nextTabKey = "nextTab"
    let prevTabKey = "prevTab"
    let newTabRightKey = "newTabRight"
    let activateTabKey (n: int) = sprintf "activateTab%d" n

    /// The tab numbers the grid offers.
    let numbers = [ 1 .. 9 ]

    // Root-level booleans, by their SettingsRec field names.
    // EnableCtrlNumberHotKey predates this tab too.
    let enableCtrlNumberSetting = "enableCtrlNumberHotKey"
    let enableAltNumberSetting = "enableAltNumberHotKey"

    // ----------------------------------------------------------------- mode --

    /// What the nine number fields mean.
    type NumberKeyMode =
        /// Every field is Ctrl + its digit; the fields are locked.
        | CtrlNumbers
        /// Every field is Alt + its digit; the fields are locked.
        | AltNumbers
        /// Each field holds whatever the user put in it; an empty one is off.
        | CustomNumbers

    /// The two check boxes are exclusive in the dialog, so both flags being
    /// on can only come from a hand-edited file. Ctrl wins then: it is the
    /// older setting, the one an existing user actually chose. Nothing is
    /// written back until the user touches a check box.
    let numberKeyMode (ctrlEnabled: bool) (altEnabled: bool) =
        if ctrlEnabled then CtrlNumbers
        elif altEnabled then AltNumbers
        else CustomNumbers

    /// Whether the number fields take input under a mode.
    let numberFieldsEditable (mode: NumberKeyMode) =
        match mode with
        | CustomNumbers -> true
        | CtrlNumbers | AltNumbers -> false

    /// The code field N shows - and registers - under a mode. `stored` is the
    /// user's own key for that number, consulted only in the custom mode.
    let numberKeyCode (mode: NumberKeyMode) (stored: int -> int) (n: int) =
        match mode with
        | CtrlNumbers -> ctrlDigit n
        | AltNumbers -> altDigit n
        | CustomNumbers -> stored n

    // ------------------------------------------------------------- bindings --

    /// What a registered key does when it fires.
    type HotKeyAction =
        /// Activate the N-th tab (1-based, left to right) of the foreground
        /// window's group.
        | ActivateTab of int
        | NextTab
        | PrevTab
        /// Start the foreground window's program again and put the new
        /// window right of the active tab.
        | NewTabRight

    /// One key to hold: its settings name, its packed code, its action.
    type Binding = {
        name: string
        code: int
        action: HotKeyAction
    }

    /// For log lines.
    let describe (b: Binding) = sprintf "%s=0x%04X" b.name b.code

    /// Every key the settings ask for, in the order of the dialog: numbers
    /// 1-9, Next Tab, Previous Tab, add a tab to the right. `stored` reads a
    /// hot key by settings name. Fields left at None are not in the list: an
    /// unset key must not be registered, or it would be VK 0 with some
    /// modifiers and RegisterHotKey would fail or, worse, succeed.
    let bindings (mode: NumberKeyMode) (stored: string -> int) : Binding list =
        let numberBindings =
            numbers |> List.map (fun n ->
                { name = activateTabKey n
                  code = numberKeyCode mode (fun n -> stored (activateTabKey n)) n
                  action = ActivateTab n })
        let others =
            [ { name = nextTabKey; code = stored nextTabKey; action = NextTab }
              { name = prevTabKey; code = stored prevTabKey; action = PrevTab }
              { name = newTabRightKey; code = stored newTabRightKey; action = NewTabRight } ]
        numberBindings @ others |> List.filter (fun b -> isSet b.code)

    /// Two fields holding one key. RegisterHotKey would refuse the second
    /// anyway, so it is dropped here, where the drop can be seen: the first
    /// field in dialog order keeps the key. Returns (kept, dropped).
    let dedupe (bindings: Binding list) : Binding list * Binding list =
        let kept, dropped, _ =
            bindings |> List.fold (fun (kept, dropped, seen) b ->
                if Set.contains b.code seen then kept, b :: dropped, seen
                else b :: kept, dropped, Set.add b.code seen) ([], [], Set.empty)
        List.rev kept, List.rev dropped

    // ----------------------------------------------------------- foreground --

    /// Where the window that just came to the front stands.
    type Foreground =
        /// A window that is a tab in one of the groups.
        | TabbedWindow
        /// A window OWNED by a tabbed window: its Save As, its Options, its
        /// message box.
        | DialogOfTabbedWindow
        /// A window of WindowTabs itself: the settings dialog, a tab strip.
        | OwnWindow
        /// Anything else - another program, the desktop, nothing at all.
        | Other

    /// The keys are held for a tabbed window and for nothing else.
    ///
    /// DialogOfTabbedWindow is a design decision, not a requirement. The
    /// dialog is no tab; the keys act on the group of the window in front,
    /// and switching tabs under a modal dialog would leave the dialog modal
    /// to a window that just went behind another. And a bare key - the
    /// dialog allows one, "1" say - would eat the digit the user types into
    /// a file name. So the keys are released for the dialog and come back
    /// when it closes and its owner is in front again.
    ///
    /// OwnWindow likewise. The settings dialog is the main case: its hot key
    /// fields have to SEE Ctrl+1, Alt+2 and so on to record them, and a key
    /// that RegisterHotKey holds never reaches the control - so while the
    /// dialog is in front the keys must be released or the user could not
    /// set them. The tab strip is the other case, and it is in front only
    /// for an instant (a click on it activates the tab's window, which brings
    /// the keys straight back). Neither is in any group, so holding the keys
    /// there would do nothing anyway.
    let wantsHotKeys (foreground: Foreground) =
        match foreground with
        | TabbedWindow -> true
        | DialogOfTabbedWindow | OwnWindow | Other -> false

    // ----------------------------------------------------------------- plan --

    /// What to register and what to release to get from `held` to `wanted`.
    /// Release comes first: a name whose code changed is in both lists.
    type Plan = {
        release: Binding list
        acquire: Binding list
    }

    /// Only the difference is touched. Held and wanted the same - a switch
    /// from one tabbed window to another, or a window pass that changed
    /// nothing - is an empty plan, which is what makes the sync safe to run
    /// from every signal that might have changed the answer. A key that was
    /// wanted and refused is not in `held`, so it is asked for again.
    let plan (held: Binding list) (wanted: Binding list) : Plan =
        { release = held |> List.filter (fun h -> not (List.contains h wanted))
          acquire = wanted |> List.filter (fun w -> not (List.contains w held)) }
