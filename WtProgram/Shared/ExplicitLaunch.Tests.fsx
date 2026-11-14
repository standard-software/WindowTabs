// dotnet fsi WtProgram/Shared/ExplicitLaunch.Tests.fsx
#load "ExplicitLaunch.fs"
#load "TabOrder.fs"
#load "AppPath.fs"
#load "RestorePosition.fs"
#load "ClosedTabClaim.fs"
open System
open Bemo

let mutable checks, failures = 0, 0
let check name expected actual =
    checks <- checks + 1
    if expected <> actual then
        failures <- failures + 1
        printfn "FAIL %s: expected %A, got %A" name expected actual
let h s = IntPtr(Convert.ToInt64(s, 16))
let hs (s: string) = s.Split(',') |> Array.map h |> Array.toList
// Times are seconds from an arbitrary origin; only their order and spacing
// matter (a request stays fresh for 30 s, a record older than the request is
// the one it suppresses).
let at (seconds: float) = DateTime(2000, 1, 1).AddSeconds seconds
let exe = @"C:\Apps\app.exe"
let title = "title-1"
let peers = hs "1001,1002,1003,1004,1005,1006,1007,1008,1009,100A,100B"
let request time destination : ExplicitLaunch.Request<string> = {
    id = Guid.NewGuid(); exePath = exe; startedAt = at time
    existing = Set.ofList peers; destination = destination
}
type Entry = {
    path: string; title: string; closedAt: DateTime; hwnd: IntPtr; rank: int; group: IntPtr
    rename: string option; fill: string option; underline: string option; border: string option; pinned: bool; leftAligned: bool
}
let entry time hwnd rank group = {
    path = exe; title = title; closedAt = at time; hwnd = h hwnd; rank = rank; group = h group
    rename = None; fill = None; underline = None; border = None; pinned = false; leftAligned = true
}
let identity e = e.path, e.title, e.closedAt
let suppress now r title cache = ExplicitLaunch.suppress (at now) r title identity cache

// First arrival, close, arrival, close, arrival. The recorded placements are
// old seats, not the destination the user asked for: test each destination
// separately; the old rank must never win for any of them.
let events = [
    80.0,  85.0,  90.0, "2001", "1001", 17
    100.0, 102.0, 108.0, "2002", "3001", 5
    114.0, 117.0, 122.0, "2003", "3001", 5
]
for destination in ["right"; "fresh"; "other"; "shortcut"] do
    let mutable cache = [entry 50.0 "999" 17 "1001"]
    for opened, matched, closed, handle, token, rank in events do
        let hwnd = h handle
        let r = request opened destination
        let active, pending = ExplicitLaunch.take (at opened) exe hwnd [r]
        check "request consumed" [] pending
        let early, unchanged = suppress opened active "title-0" cache
        check "early blocked before title match" true early
        check "unmatched records retained" cache unchanged
        let late, remaining = suppress matched active title cache
        check (string matched + " late blocked") true late
        check "matching records deleted" [] remaining
        let chosen =
            match destination with
            | "fresh" -> [hwnd]
            | "other" -> [h "111"; hwnd]
            | _ -> peers.Head :: hwnd :: peers.Tail
        let actual =
            if late then chosen
            else TabOrder.placeRestoredTab (peers @ [hwnd]) (fun _ -> None) hwnd hwnd rank
                    (chosen |> List.map (fun x -> TabOrder.placed x 1))
        check "operation keeps its position" chosen actual
        check "existing order stays fixed" (chosen |> List.filter ((<>) hwnd)) (actual |> List.filter ((<>) hwnd))
        // Closing an explicit window still records it for ordinary reopening.
        cache <- [entry closed handle 5 "3001"]
        let allowed, retained = suppress closed None title cache
        check "ordinary reopen allowed" false allowed
        check "ordinary reopen keeps entry" cache retained
        let restored = TabOrder.placeRestoredTab [] (fun _ -> None) hwnd hwnd 5
                            ((peers @ [hwnd]) |> List.map (fun x -> TabOrder.placed x 1))
        check "ordinary reopen restores seat" 5 (restored |> List.findIndex ((=) hwnd))
        check "ordinary reopen preserves peers" peers (restored |> List.filter ((<>) hwnd))

let r = request 100.0 "right"
let old = entry 90.0 "2001" 5 "3001"
let otherTitle = { old with title = "title-2" }
let otherExe = { old with path = @"C:\Other\app.exe" }
let newer = { old with closedAt = at 101.0 }
let records = [old; otherTitle; otherExe; newer; { old with path = exe.ToUpperInvariant() }]
check "narrow deletion across duplicate identities" (true, [otherTitle; otherExe; newer]) (suppress 102.0 (Some r) title records)
check "empty title deletes nothing" (true, records) (suppress 102.0 (Some r) "" records)
check "non-explicit window deletes nothing" (false, records) (suppress 102.0 None title records)
let r2 = request 101.0 "fresh"
let first, pending = ExplicitLaunch.take (at 102.0) (exe.ToUpperInvariant()) (h "2002") [r;r2]
check "FIFO keeps destination" (Some "right") (first |> Option.map (fun x -> x.destination))
check "second request remains" [r2.id] (pending |> List.map (fun x -> x.id))
let second, rest = ExplicitLaunch.take (at 103.0) exe (h "2003") pending
check "second destination survives" (Some "fresh") (second |> Option.map (fun x -> x.destination))
check "both consumed" [] rest
check "existing window cannot consume intent" (None, [r]) (ExplicitLaunch.take (at 102.0) exe peers.Head [r])
check "different exe cannot consume intent" (None, [r]) (ExplicitLaunch.take (at 102.0) "other.exe" (h "123") [r])
check "expired request cannot suppress ordinary reopen" (None, []) (ExplicitLaunch.take (at 130.0) exe (h "123") [r])
// Protection is based on request time, including both startup titles.
let startupA = { old with title = "startup A - App" }
let startupB = { old with title = "startup B - App" }
let renamedEntry = { old with title = "alpha - App"; underline = Some "teal"; leftAligned = false }
let initialCache = [startupA; startupB; renamedEntry; newer]
let blockedA, afterA = suppress 101.0 (Some r) startupA.title initialCache
let blockedB, afterB = suppress 104.0 (Some r) startupB.title afterA
check "startup A blocked" true blockedA
check "startup A discarded" [startupB; renamedEntry; newer] afterA
check "startup B blocked" true blockedB
check "startup B discarded without consuming renamedEntry" [renamedEntry; newer] afterB
check "still protected just before 15 seconds" (Some r) (ExplicitLaunch.active (at 114.999) (Some r))
check "protection expires at exactly 15 seconds" None (ExplicitLaunch.active (at 115.0) (Some r))
check "renamedEntry at 10 seconds discarded without move" (true, []) (suppress 110.0 (Some r) renamedEntry.title [renamedEntry])
check "renamedEntry at 20 seconds retained for claim" (false, [renamedEntry]) (suppress 120.0 (Some r) renamedEntry.title [renamedEntry])
check "newer record never discarded while protected" (true, [newer]) (suppress 110.0 (Some r) newer.title [newer])
check "newer record retained after expiry" (false, [newer]) (suppress 120.0 (Some r) newer.title [newer])
check "discarded startup record stays gone after expiry" (false, afterB) (suppress 120.0 (Some r) startupB.title afterB)
check "launch-time cutoff is inclusive" (true, []) (suppress 110.0 (Some r) title [{ old with closedAt = r.startedAt }])

// Exercise the suppression, pristine and ordinary claim decisions together.
// Expiry removes the claim cutoff, but keeps provenance to permit overwriting user changes.
let arrival = h "2004"
let emptyState = false, None, None, None
let inherited = true, Some "amber", None, None
let claim now request current renamed claimed title cache =
    let blocked, remaining = suppress now request title cache
    let matches =
        if blocked || not (ExplicitLaunch.canClaimState request (not renamed && current = emptyState)) then []
        else remaining |> List.indexed |> List.choose (fun (i, e) ->
            if ClosedTabClaim.sameIdentity (exe, title) (e.path, e.title) then Some i else None)
    let decision = ClosedTabClaim.decide arrival claimed matches
    let taken =
        match decision with
        | ClosedTabClaim.Claim i -> ClosedTabClaim.take arrival ((=) remaining.[i]) claimed remaining
        | _ -> None
    decision, taken
let decision20, taken20 = claim 120.0 (Some r) inherited false Set.empty renamedEntry.title afterB
check "20-second renamedEntry claims despite inherited color and pin" (ClosedTabClaim.Claim 0) decision20
let restored, claimed, retained = Option.get taken20
check "claim retains recorded group" renamedEntry.group restored.group
// The applied record is a full replacement, including absent inherited fields.
let appliedState = restored.pinned, restored.fill, restored.underline, restored.border
check "saved colors replace inherited fill" (false, None, Some "teal", None) appliedState
check "inherited pin is replaced" false restored.pinned
check "saved alignment is retained" false restored.leftAligned
let restoredZone = TabOrder.zoneOf restored.leftAligned restored.pinned
check "saved pin and alignment determine band" 3 restoredZone
let restoredOrder = TabOrder.placeRestoredTab [] (fun _ -> None) arrival restored.hwnd restored.rank
                        ((peers @ [arrival]) |> List.map (fun x -> TabOrder.placed x restoredZone))
check "claim restores recorded position" renamedEntry.rank (restoredOrder |> List.findIndex ((=) arrival))
check "claim consumes record once" [newer] retained
check "second claim blocked" ClosedTabClaim.AlreadyClaimed
    (fst (claim 121.0 (Some r) inherited false claimed renamedEntry.title [renamedEntry]))
check "renamedEntry at 10 seconds cannot claim" (ClosedTabClaim.NoMatch, None)
    (claim 110.0 (Some r) inherited false Set.empty renamedEntry.title [renamedEntry])
check "newer record cannot claim during protection" (ClosedTabClaim.NoMatch, None)
    (claim 110.0 (Some r) inherited false Set.empty newer.title [newer])
check "expiry uses ordinary rules even for newer records" (ClosedTabClaim.Claim 0)
    (fst (claim 120.0 (Some r) inherited false Set.empty newer.title [newer]))
let userChanged = true, Some "violet", Some "green", Some "silver"
let overwriteDecision, overwriteTaken = claim 120.0 (Some r) userChanged true Set.empty renamedEntry.title [renamedEntry]
check "explicit recolor and rename allow claim after expiry" (ClosedTabClaim.Claim 0) overwriteDecision
let overwritten, _, _ = Option.get overwriteTaken
check "saved colors and pin replace user values" (false, None, Some "teal", None)
    (overwritten.pinned, overwritten.fill, overwritten.underline, overwritten.border)
check "absent saved rename clears user rename" None overwritten.rename
check "saved alignment replaces user alignment" false overwritten.leftAligned
check "user changes cannot bypass active protection" (ClosedTabClaim.NoMatch, None)
    (claim 110.0 (Some r) userChanged true Set.empty renamedEntry.title [renamedEntry])
check "ordinary recolored window still cannot claim" (ClosedTabClaim.NoMatch, None)
    (claim 120.0 None userChanged false Set.empty renamedEntry.title [renamedEntry])
check "ordinary renamed window still cannot claim" (ClosedTabClaim.NoMatch, None)
    (claim 120.0 None emptyState true Set.empty renamedEntry.title [renamedEntry])
check "ordinary pristine window can claim" (ClosedTabClaim.Claim 0)
    (fst (claim 120.0 None emptyState false Set.empty renamedEntry.title [renamedEntry]))

printfn "ExplicitLaunch: %d checks, %d failures" checks failures
if failures <> 0 then exit 1
