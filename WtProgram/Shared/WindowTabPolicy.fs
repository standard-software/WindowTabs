namespace Bemo

module WindowTabPolicy =
    // Only exceptions are stored. Application transitions reset its windows.
    let isExcluded hwnd pid exclusions =
        Map.tryFind hwnd exclusions |> Option.exists (fun (owner, _) -> owner = pid)

    let setEnabled hwnd pid app appEnabled enabled exclusions =
        if not appEnabled then exclusions
        elif enabled then Map.remove hwnd exclusions
        else Map.add hwnd (pid, app) exclusions

    let resetApp app wasEnabled enabled exclusions =
        if wasEnabled = enabled then exclusions
        else Map.filter (fun _ (_, ownerApp) -> ownerApp <> app) exclusions

    let forget hwnd exclusions = Map.remove hwnd exclusions
