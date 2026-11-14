#load "TabGroupHeader.fs"
open Bemo

let check name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
check "singular" "TabGroupSizeSingularFormat" (TabGroupHeader.formatKey 1)
check "plural" "TabGroupSizePluralFormat" (TabGroupHeader.formatKey 3)
check "zero" "TabGroupSizePluralFormat" (TabGroupHeader.formatKey 0)
check "caption and size only" "a : b" (TabGroupHeader.append "a" "b")
printfn "TabGroupHeader: 4 checks passed"
