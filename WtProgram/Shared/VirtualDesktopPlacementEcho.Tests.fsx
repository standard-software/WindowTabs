// Run with dotnet fsi; no native windows are created.
#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "Regroup.fs"
#load "SavedTabState.fs"
#load "AppPath.fs"
#load "RestorePosition.fs"
#load "SavedSession.fs"
#load "VirtualDesktopIntegrity.fs"
#load "VirtualDesktopGroups.fs"

open System
open System.Collections.Generic
open Newtonsoft.Json.Linq
open Bemo
open Bemo.VirtualDesktopGroups

let mutable failures = 0
let mutable checks = 0
let is name expected actual =
    checks <- checks + 1
    if expected <> actual then
        failures <- failures + 1
        printfn "FAIL %s" name

// Own writes cannot replace a newer group base; external moves still can.
do
    let p = ((1, 2, 3, 4), 1)
    let q = ((5, 6, 7, 8), 1)
    let mutable expected = Some p
    let mutable groupBase = q
    let observe actual =
        expected <- placementEcho expected actual
        if expected.IsNone then groupBase <- actual
    observe p
    is "own placement echo preserves newer base" q groupBase
    observe p
    is "repeated placement echo preserves newer base" q groupBase
    let externalMove = ((9, 10, 3, 4), 1)
    observe externalMove
    is "external shared move is adopted" externalMove groupBase
    observe p
    is "return to an invalidated target is external" p groupBase
    is "cleared write cannot suppress a later shared move" None
        (placementEcho None p)
    is "a new write replaces the previous echo target" None
        (placementEcho (Some q) p)
    is "show state change is not a placement echo" None
        (placementEcho (Some p) (fst p, 2))
    is "size change is not a placement echo" None
        (placementEcho (Some p) ((1, 2, 7, 8), 1))

printfn "%d checks, %d failures" checks failures
if failures > 0 then exit 1
