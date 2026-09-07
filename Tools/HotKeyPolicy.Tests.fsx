// Checks HotKeyPolicy - the decisions behind the Shortcut Keys tab - without
// starting WindowTabs. The module depends on FSharp.Core only, so this script
// loads the very file that ships rather than a copy of the rules.
//
//   dotnet fsi Tools\HotKeyPolicy.Tests.fsx
//
// Exit code 0 when every check passes, 1 otherwise. Run it after touching
// HotKeyPolicy.fs, the hot key defaults in Program.fs, or the number-key
// handling in ShortcutKeysView.fs.

#load "../WtProgram/Shared/HotKeyPolicy.fs"

open Bemo
open Bemo.HotKeyPolicy

let mutable failed = 0
let mutable passed = 0
let check name ok =
    if ok then
        passed <- passed + 1
        printfn "  PASS  %s" name
    else
        failed <- failed + 1
        printfn "  FAIL  %s" name

// ------------------------------------------------------------------ codes --

printfn "codes"
// The values Program.fs ships for nextTab / prevTab, as the hot key control
// packs them: Ctrl+Alt+Right and Ctrl+Alt+Left (0x0E = CONTROL|ALT|EXT).
check "nextTab default decodes to Ctrl+Alt, VK_RIGHT" (toRegisterHotKey 3623 = (MOD_CONTROL ||| MOD_ALT, 0x27))
check "prevTab default decodes to Ctrl+Alt, VK_LEFT" (toRegisterHotKey 3621 = (MOD_CONTROL ||| MOD_ALT, 0x25))
check "Ctrl+1 packs to 0x0231" (ctrlDigit 1 = 0x0231)
check "Alt+9 packs to 0x0439" (altDigit 9 = 0x0439)
check "Ctrl digit -> MOD_CONTROL, VK_1" (toRegisterHotKey (ctrlDigit 1) = (MOD_CONTROL, 0x31))
check "Alt digit -> MOD_ALT (bit swapped from HOTKEYF_ALT)" (toRegisterHotKey (altDigit 1) = (MOD_ALT, 0x31))
check "Shift flag -> MOD_SHIFT" (toRegisterHotKey (pack HOTKEYF_SHIFT 0x70) = (MOD_SHIFT, 0x70))
check "a bare key (no modifier) is allowed" (isSet (pack 0 0x70) && toRegisterHotKey (pack 0 0x70) = (0, 0x70))
check "None is not set" (not (isSet noKey))
check "modifiers alone are not set" (not (isSet (pack HOTKEYF_CONTROL 0)))
check "pack/unpack round trip"
    ([ for f in [0; 1; 2; 4; 7] do for vk in [1; 0x31; 0x7F; 0xFF] -> (f, vk) ]
     |> List.forall (fun (f, vk) -> let c = pack f vk in flagsOf c = f && virtualKeyOf c = vk))

// ------------------------------------------------------------------- mode --

printfn "mode"
check "both off -> custom" (numberKeyMode false false = CustomNumbers)
check "ctrl on -> Ctrl" (numberKeyMode true false = CtrlNumbers)
check "alt on -> Alt" (numberKeyMode false true = AltNumbers)
check "both on (hand-edited file) -> Ctrl wins" (numberKeyMode true true = CtrlNumbers)
check "custom mode is editable, the others are locked"
    (numberFieldsEditable CustomNumbers && not (numberFieldsEditable CtrlNumbers) && not (numberFieldsEditable AltNumbers))

// The user's own keys: 1 -> F1, the rest unset.
let custom n = if n = 1 then pack 0 0x70 else noKey
check "Ctrl mode shows Ctrl+N whatever is stored" (numbers |> List.forall (fun n -> numberKeyCode CtrlNumbers custom n = ctrlDigit n))
check "Alt mode shows Alt+N whatever is stored" (numbers |> List.forall (fun n -> numberKeyCode AltNumbers custom n = altDigit n))
check "custom mode shows the stored key" (numberKeyCode CustomNumbers custom 1 = pack 0 0x70 && numberKeyCode CustomNumbers custom 2 = noKey)

// --------------------------------------------------------------- bindings --

printfn "bindings"
// A settings reader: the two shipped defaults, one custom number, nothing else.
let stored key =
    if key = nextTabKey then 3623
    elif key = prevTabKey then 3621
    elif key = activateTabKey 3 then pack HOTKEYF_SHIFT 0x33
    else noKey

let ctrlBindings = bindings CtrlNumbers stored
check "Ctrl mode registers 9 numbers + next + prev (newTabRight unset)" (ctrlBindings.Length = 11)
check "Ctrl mode number N is Ctrl+N -> ActivateTab N"
    (numbers |> List.forall (fun n ->
        ctrlBindings |> List.exists (fun b -> b.name = activateTabKey n && b.code = ctrlDigit n && b.action = ActivateTab n)))
check "next / prev carry their stored codes"
    (ctrlBindings |> List.exists (fun b -> b.action = NextTab && b.code = 3623) &&
     ctrlBindings |> List.exists (fun b -> b.action = PrevTab && b.code = 3621))
check "unset newTabRight is not registered" (ctrlBindings |> List.forall (fun b -> b.action <> NewTabRight))
check "dialog order: numbers, next, prev"
    (ctrlBindings |> List.map (fun b -> b.action) = ([ for n in numbers -> ActivateTab n ] @ [ NextTab; PrevTab ]))

let customBindings = bindings CustomNumbers stored
check "custom mode registers only the number with a key"
    (customBindings |> List.filter (fun b -> match b.action with ActivateTab _ -> true | _ -> false)
     |> List.map (fun b -> b.action, b.code) = [ (ActivateTab 3, pack HOTKEYF_SHIFT 0x33) ])
check "custom mode still registers next / prev" (customBindings.Length = 3)

let withNewTab key = if key = newTabRightKey then pack (HOTKEYF_CONTROL ||| HOTKEYF_SHIFT) 0x54 else stored key
check "newTabRight registers once it has a key"
    (bindings CustomNumbers withNewTab |> List.exists (fun b -> b.action = NewTabRight && b.name = newTabRightKey))
check "an all-None file registers nothing" ((bindings CustomNumbers (fun _ -> noKey)).IsEmpty)
check "no binding ever has VK 0" (bindings AltNumbers withNewTab |> List.forall (fun b -> isSet b.code))

// ------------------------------------------------------------- duplicates --

printfn "duplicates"
// The user put Ctrl+Alt+Right - the Next Tab default - into number 2 as
// well, and F1 into both 1 and newTabRight.
let clashing key =
    if key = activateTabKey 2 then 3623
    elif key = activateTabKey 1 then pack 0 0x70
    elif key = newTabRightKey then pack 0 0x70
    else stored key
let kept, dropped = dedupe (bindings CustomNumbers clashing)
check "the first field in dialog order keeps a repeated key"
    (kept |> List.exists (fun b -> b.action = ActivateTab 2 && b.code = 3623) &&
     kept |> List.exists (fun b -> b.action = ActivateTab 1 && b.code = pack 0 0x70))
check "the later fields are dropped" (dropped |> List.map (fun b -> b.action) = [ NextTab; NewTabRight ])
check "kept + dropped = all, order preserved" (kept @ dropped |> List.length = (bindings CustomNumbers clashing).Length)
check "kept codes are unique" (kept |> List.map (fun b -> b.code) |> List.distinct |> List.length = kept.Length)
check "no duplicates -> nothing dropped" (snd (dedupe ctrlBindings) = [])
check "describe names the field and the code" (describe { name = "x"; code = 0x0231; action = ActivateTab 1 } = "x=0x0231")

// ------------------------------------------------------------- foreground --

printfn "foreground"
check "tabbed window wants the keys" (wantsHotKeys TabbedWindow)
check "a dialog owned by a tabbed window does not (design decision)" (not (wantsHotKeys DialogOfTabbedWindow))
check "own window (settings dialog) does not" (not (wantsHotKeys OwnWindow))
check "other program / desktop does not" (not (wantsHotKeys Other))

// ------------------------------------------------------------------- plan --

printfn "plan"
let noPlan = { release = []; acquire = [] }
check "nothing held, nothing wanted -> nothing" (plan [] [] = noPlan)
check "nothing held, keys wanted -> acquire them all" (plan [] ctrlBindings = { release = []; acquire = ctrlBindings })
check "held = wanted -> nothing (tabbed -> tabbed, or a quiet window pass)" (plan ctrlBindings ctrlBindings = noPlan)
check "held, nothing wanted -> release them all" (plan ctrlBindings [] = { release = ctrlBindings; acquire = [] })

// Ctrl mode -> Alt mode: the numbers change code, next / prev stay.
let altBindings = bindings AltNumbers stored
let switchMode = plan ctrlBindings altBindings
check "mode switch releases the 9 Ctrl numbers only"
    (switchMode.release |> List.map (fun b -> b.action) = [ for n in numbers -> ActivateTab n ] &&
     switchMode.release |> List.forall (fun b -> b.code = ctrlDigit (match b.action with ActivateTab n -> n | _ -> 0)))
check "mode switch acquires the 9 Alt numbers only"
    (switchMode.acquire |> List.map (fun b -> b.action) = [ for n in numbers -> ActivateTab n ] &&
     switchMode.acquire |> List.forall (fun b -> b.code = altDigit (match b.action with ActivateTab n -> n | _ -> 0)))
check "a name whose code changed is in both lists"
    (switchMode.release |> List.exists (fun b -> b.name = activateTabKey 1) &&
     switchMode.acquire |> List.exists (fun b -> b.name = activateTabKey 1))

// A refused key is not held, so it is asked for again.
let refused = ctrlBindings |> List.find (fun b -> b.action = NextTab)
let heldWithoutRefused = ctrlBindings |> List.filter (fun b -> b <> refused)
check "a refused key is asked for again, and nothing else is touched"
    (plan heldWithoutRefused ctrlBindings = { release = []; acquire = [ refused ] })

// A day in the life. Apply each plan to a `held` list and watch what the
// OS would see: Notepad, a tabbed window, another tab of the same group, its
// Save As dialog, the tabbed window again, the settings dialog, Notepad.
let apply held (p: Plan) =
    (held |> List.filter (fun h -> not (List.contains h p.release))) @ p.acquire
let walk =
    [ Other; TabbedWindow; TabbedWindow; DialogOfTabbedWindow; TabbedWindow; OwnWindow; Other ]
    |> List.scan (fun (held, _) fg ->
        let p = plan held (if wantsHotKeys fg then ctrlBindings else [])
        apply held p, (p.release.Length, p.acquire.Length)) ([], (0, 0))
    |> List.tail
check "walk: (release, acquire) = 0/0, 0/11, 0/0, 11/0, 0/11, 11/0, 0/0"
    (walk |> List.map snd = [ (0, 0); (0, 11); (0, 0); (11, 0); (0, 11); (11, 0); (0, 0) ])
check "walk ends with nothing held" ((walk |> List.last |> fst) = [])

// ------------------------------------------------------------------- done --

printfn ""
if failed = 0 then
    printfn "all %d checks passed" passed
    exit 0
else
    printfn "%d of %d check(s) FAILED" failed (passed + failed)
    exit 1
