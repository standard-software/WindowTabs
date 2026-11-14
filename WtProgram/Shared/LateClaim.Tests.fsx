// dotnet fsi WtProgram/Shared/LateClaim.Tests.fsx
#load "TabOrder.fs"
#load "AppPath.fs"
#load "RestorePosition.fs"
#load "ClosedTabClaim.fs"
open System
open Bemo

let mutable checks = 0
let mutable failures = 0
let check name expected actual =
    checks <- checks + 1
    if expected <> actual then
        failures <- failures + 1
        printfn "FAIL %s: expected %A, got %A" name expected actual
let hs (s: string) = s.Split(',') |> Array.map (fun h -> IntPtr(Convert.ToInt64(h, 16))) |> Array.toList
let h s = hs s |> List.head
let self = h "100A"
let existing = hs "1001,1002,1003,1004,100C,1005,1006,1007,1008,1009"
let place snapshot old fallback current =
    TabOrder.placeRestoredTab snapshot (fun _ -> None) self old fallback
        (current |> List.map (fun x -> TabOrder.placed x 3))

// Three reopenings of the same window, each with the rank it was recorded
// at and the order the old placement produced. The snapshots below are
// witnesses constructed from those orders.
let events = [
    9, "1001,1002,1003,1004,100C,1005,1006,1007,1008,100A,1009"
    9, "1001,1002,1003,1004,100C,100A,1009,1008,1005,1006,1007"
    16, "1001,1002,1003,1004,100C,1009,100A,1008,1005,1006,1007"
]
let mutable current = existing @ [self]
let mutable legacy = current
for i, (rank, observed) in List.indexed events do
    let label = sprintf "event %d" (i + 1)
    let old = IntPtr(0x700000 + i)
    let desired = hs observed
    let before, after = desired |> List.splitAt (desired |> List.findIndex ((=) self))
    let absent = [1 .. rank - before.Length] |> List.map (fun n -> IntPtr(0x800000 + i * 100 + n))
    let snapshot = before @ absent @ [old] @ List.tail after
    let mapping x = if x = self then Some old else None
    legacy <- TabOrder.restoreOrder snapshot mapping (legacy |> List.map (fun x -> TabOrder.placed x 3))
    check (label + " legacy reproduces the old order") desired legacy
    current <- place snapshot old rank current
    check (label + " preserves every existing peer") existing (current |> List.filter ((<>) self))
    check (label + " retains all windows exactly once") (Set.ofList (existing @ [self])) (Set.ofList current)
    check (label + " is idempotent") current (place snapshot old rank current)
check "legacy placement really changed peer order" false (existing = (legacy |> List.filter ((<>) self)))

let a, b, c, x = h "A", h "B", h "C", h "D"
check "reopening restores original middle position" [a; self; c]
    (place [a; b; c] b 1 [a; c; self])
check "user reordered peers remain in their current order" [c; a; self]
    (place [a; b; c] b 1 [c; a; self])
check "unknown peer keeps its place" [a; x; self; c]
    (place [a; b; c] b 1 [a; x; c; self])
check "missing snapshot uses recorded index" [a; self; c] (place [] b 1 [a; c; self])
check "missing arrival is a no-op" [a; c] (place [a; b; c] b 1 [a; c])
check "fallback cannot cross visual bands" [a; c; self]
    (TabOrder.placeRestoredTab [] (fun _ -> None) self b 0
        [TabOrder.placed a 0; TabOrder.placed c 1; TabOrder.placed self 3])
check "claimed identity wins over a reused handle" [a; c; self]
    (place [self; a; c; b] b 3 [self; a; c])

let rec permutations = function
    | [] -> [[]]
    | xs -> xs |> List.collect (fun x -> permutations (List.filter ((<>) x) xs) |> List.map (fun p -> x :: p))
let saved = hs "A,B,C,D,E"
for arrival in permutations saved do
    let mutable tabs = []
    for old in arrival do
        let live = IntPtr(old.ToInt64() + 256L)
        let before = tabs
        let placed = (tabs @ [live]) |> List.map (fun t -> TabOrder.placed t 3)
        tabs <- TabOrder.placeRestoredTab saved (fun t -> Some(IntPtr(t.ToInt64() - 256L))) live old 0 placed
        check "each arrival preserves peers" before (tabs |> List.filter ((<>) live))
    check "all 120 arrival sequences restore saved order" (saved |> List.map (fun t -> IntPtr(t.ToInt64() + 256L))) tabs

// Mixed alignment and pinning, including peers reordered by the user.
let zones = Map.ofList [saved.[0], 0; saved.[1], 1; saved.[2], 2; saved.[3], 3; saved.[4], 1]
for arrival in permutations saved do
    let mutable tabs : TabOrder.Placed list = []
    for old in arrival do
        let before = tabs |> List.map (fun t -> t.handle)
        let added = (tabs @ [TabOrder.placed old zones.[old]]) |> List.sortBy (fun t -> t.zone)
        let result = TabOrder.placeRestoredTab saved (fun _ -> None) old old 0 added
        check "mixed-band arrival preserves peers" before (result |> List.filter ((<>) old))
        tabs <- result |> List.map (fun t -> TabOrder.placed t zones.[t])
    check "mixed-band restore reaches saved order within bands" (saved |> List.sortBy (fun t -> zones.[t]))
        (tabs |> List.map (fun t -> t.handle))
for peers in permutations existing.[0..4] do
    let result = place (List.rev peers @ [h "EE"]) (h "EE") 5 (peers @ [self])
    check "conflicting snapshots preserve arbitrary peer order" peers (result |> List.filter ((<>) self))

// Application identity must survive a package update on all claim paths.
do
    let package version publisher exe =
        sprintf @"C:\Program Files\WindowsApps\Example.Terminal_%s_x64__%s\app\%s" version publisher exe
    let oldPath = package "2.3.4.0" "examplepub" "terminal.exe"
    let newPath = package "3.4.5.0" "examplepub" "terminal.exe"
    let title = "title-alpha"
    check "closed-tab identity survives a package update" true
        (ClosedTabClaim.sameIdentity (oldPath, title) (newPath, title))
    check "title still distinguishes windows of an updated package" false
        (ClosedTabClaim.sameIdentity (oldPath, title) (newPath, "title-beta"))
    check "publisher remains part of the restore identity" false
        (ClosedTabClaim.sameIdentity (oldPath, title) (package "3.4.5.0" "otherpub" "terminal.exe", title))
    check "executable within the package remains part of the identity" false
        (ClosedTabClaim.sameIdentity (oldPath, title) (package "3.4.5.0" "examplepub" "helper.exe", title))
    check "duplicate recording recognizes an updated live peer" false
        (ClosedTabClaim.canRecord (IntPtr 1) (oldPath, title) [IntPtr 2, (newPath, title)])
    for route in ["early"; "late"] do
        let entry = oldPath, title
        let matches = if ClosedTabClaim.sameIdentity entry (newPath, title) then [0] else []
        check (route + " finds the saved package entry") (ClosedTabClaim.Claim 0)
            (ClosedTabClaim.decide (IntPtr 3) Set.empty matches)
        match ClosedTabClaim.take (IntPtr 3) ((=) entry) Set.empty [entry] with
        | Some(taken, claimed, remaining) ->
            check (route + " retains the raw saved path") oldPath (fst taken)
            check (route + " consumes the entry exactly once") [] remaining
            check (route + " cannot claim again after a user regroup") ClosedTabClaim.AlreadyClaimed
                (ClosedTabClaim.decide (IntPtr 3) claimed matches)
        | None -> check (route + " takes the matching entry") true false
    let ordinary = [@"C:\Apps\Editor\editor.exe"; @"c:\apps\editor\EDITOR.EXE";
                    @"D:\Apps\Editor\editor.exe"; @"C:\Apps\Editor\2.0\editor.exe";
                    @"C:\Apps\Editor\3.0\editor.exe"]
    for a in ordinary do
        for b in ordinary do
            check "ordinary paths retain ordinal case-insensitive comparison"
                (String.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                (ClosedTabClaim.sameIdentity (a, title) (b, title))

// Early and late claims use the same quarantine-aware candidate selection.
do
    let identity = @"C:\Apps\Example\editor.exe", "title-alpha"
    let pending = [identity, 1, true; identity, 2, true]
    let offer entries =
        let held = ClosedTabClaim.heldIndices entries
        entries |> List.mapi (fun i (key, _, _) ->
            key, held.Contains i, ({index=i;center=None;desktop=None} : RestorePosition.Candidate))
        |> ClosedTabClaim.resolve identity {center=None;scale=1.0;desktop=None}
        |> fst
    for route in ["early";"late"] do
        check (route + " takes the first held record by order") (ClosedTabClaim.Claim 0)
            (ClosedTabClaim.decide (IntPtr 801) Set.empty (offer pending))
        for survivors in [[pending.Head]; [pending.[1]]] do
            check (route + " releases either survivor when its competitor is removed") (ClosedTabClaim.Claim 0)
                (ClosedTabClaim.decide (IntPtr 801) Set.empty (offer survivors))
        check (route + " still accepts ordinary unique records") (ClosedTabClaim.Claim 0)
            (ClosedTabClaim.decide (IntPtr 801) Set.empty (offer [identity, 1, false]))
    check "a pending ordinary competitor keeps a held record quarantined" (Set.singleton 0)
        (ClosedTabClaim.heldIndices [identity, 1, true; identity, 2, false])
    check "same-group twins do not require cross-group quarantine" Set.empty
        (ClosedTabClaim.heldIndices [identity, 1, true; identity, 1, true])
    check "another identity cannot keep a survivor held" Set.empty
        (ClosedTabClaim.heldIndices [identity, 1, true; ((fst identity), "title-beta"), 2, true])

// Cross-group position claims share startup's physical-coordinate policy.
do
    let candidate i x desktop : RestorePosition.Candidate = {index=i; center=x; desktop=desktop}
    let observation x scale desktop : RestorePosition.Observation = {center=x; scale=scale; desktop=desktop}
    let a = Guid("00000000-0000-0000-0000-000000000001")
    let b = Guid("00000000-0000-0000-0000-000000000002")
    let choose live cs = RestorePosition.choose live cs
    let chosen = function RestorePosition.Selected(i,_,_,_) | RestorePosition.Ordered(i,_,_) -> Some i | _ -> None
    let ordered = function RestorePosition.Ordered _ -> true | _ -> false
    let identity = "app.exe", "title-1"
    let cs = [candidate 0 (Some(0.,0.)) (Some a); candidate 1 (Some(500.,0.)) (Some b)]
    for scale in [1.;1.25;1.5;2.] do
        for i,x in [0,0.;1,500.] do
            let live = observation (Some(x,0.)) scale None
            check "position selects the nearest saved group" (Some i) (choose live cs |> chosen)
            for phase in ["early";"late"] do
                let indexes,_ = ClosedTabClaim.resolve identity live (cs |> List.map (fun c -> identity,true,c))
                check (phase + " uses the shared position decision") [i] indexes
    let near = [candidate 0 (Some(0.,0.)) (Some a);candidate 1 (Some(1.,0.)) (Some b)]
    check "nearest wins even with a small gap and another desktop" (Some 0)
        (choose (observation (Some(0.,0.)) 2. (Some b)) near |> chosen)
    check "diagnostics use physical centre distance" (RestorePosition.Selected(0,50.,None,false))
        (choose (observation (Some(0.,0.)) 2. None) [candidate 0 (Some(30.,40.)) None])
    check "unknown geometry follows saved list order" (Some 9)
        (choose (observation None 1. (Some b)) [candidate 9 None None;candidate 2 None (Some b)] |> chosen)
    check "no candidate is held" (RestorePosition.Held "no-candidates") (choose (observation None 1. None) [])
    let batch = ClosedTabClaim.resolveMany
                    [observation (Some(9.,0.)) 1. None;observation (Some(100.,0.)) 2. (Some b)]
                    [candidate 7 (Some(0.,0.)) None;candidate 8 (Some(10.,0.)) (Some a)]
    check "late batch consumes the globally nearest pair before the other arrival" [(8,0);(7,1)]
        (batch |> List.map (fun (index,pair) -> index,pair.liveIndex))
    let single = ClosedTabClaim.resolveMany
                    [observation (Some(20.,0.)) 1. None;observation (Some(1.,0.)) 1. None]
                    [candidate 7 (Some(0.,0.)) None]
    check "a sole record takes the closest of multiple arrivals" [(7,1)]
        (single |> List.map (fun (index,pair) -> index,pair.liveIndex))
    let ordinary,_ = ClosedTabClaim.resolve identity (observation (Some(490.,0.)) 1. None)
                        (cs |> List.map (fun c -> identity,false,c))
    check "same group ambiguity also uses position" [1] ordinary
    for center in [None;Some(nan,0.);Some(infinity,0.)] do
        check "invalid live geometry still falls back" true (choose (observation center 1. None) cs |> ordered)
    for rect in [None;Some(0,0,0,100);Some(0,0,100,-1);Some(-32000,-32000,100,100)] do
        check "invalid saved rectangle has no usable center" None (RestorePosition.center rect)
    let indexes,_ = ClosedTabClaim.resolve identity (observation None 1. None) [identity,false,cs.Head]
    check "ordinary identity needs no geometry" [0] indexes
    check "another title is never position matched" ([],None)
        (ClosedTabClaim.resolve ("app.exe","title-2") (observation (Some(0.,0.)) 1. None) [identity,true,cs.Head])

printfn "%d checks, %d failures" checks failures
exit (if failures = 0 then 0 else 1)
