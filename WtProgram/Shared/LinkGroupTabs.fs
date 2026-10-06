namespace Bemo

module LinkGroupTabs =
    // Keep the group's order within each desktop partition, and count every tab.
    let ordered preferred tabs =
        let preferredIds = preferred |> List.map fst |> Set.ofList
        let first, rest = tabs |> List.partition (fun (id, _) -> Set.contains id preferredIds)
        first @ rest, List.length tabs
