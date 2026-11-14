#load "LinkGroupTabs.fs"
open Bemo

let check name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
let tabs = [1, "a"; 2, "b"; 3, "c"; 4, "d"]
check "current desktop first, both partitions stable"
    ([2, "b"; 4, "d"; 1, "a"; 3, "c"], 4)
    (LinkGroupTabs.ordered [2, "b"; 4, "d"] tabs)
check "parent desktop first, all tabs counted"
    ([3, "c"; 1, "a"; 2, "b"; 4, "d"], 4)
    (LinkGroupTabs.ordered [(3, "c")] tabs)
check "no preferred tabs" (tabs, 4) (LinkGroupTabs.ordered [] tabs)
check "all preferred tabs" (tabs, 4) (LinkGroupTabs.ordered tabs tabs)
check "one tab" ([(1, "a")], 1) (LinkGroupTabs.ordered [(1, "a")] [(1, "a")])
check "empty group" ([], 0) (LinkGroupTabs.ordered [] ([]: (int * string) list))
check "identity distinguishes equal names"
    ([2, "a"; 1, "a"], 2)
    (LinkGroupTabs.ordered [(2, "a")] [1, "a"; 2, "a"])
check "first listed tab supplies icon" 2
    (LinkGroupTabs.ordered [(2, "b")] tabs |> fst |> List.head |> fst)
printfn "LinkGroupTabs: 8 checks passed"
