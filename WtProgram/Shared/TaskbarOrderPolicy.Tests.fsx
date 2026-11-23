#load "TaskbarOrderPolicy.fs"
open Bemo.TaskbarOrderPolicy

let check name expected actual = if expected <> actual then failwith name
let members = Set.ofList [1; 2; 3]
check "initial wait" (Wait 3000) (decide 3000 0L 0L 1 members None)
check "before deadline" (Wait 1) (decide 3000 0L 2999L 1 members None)
check "at deadline" Execute (decide 3000 0L 3000L 1 members None)
check "late callback" Execute (decide 3000 0L 5000L 1 members None)
check "change postpones" (Wait 2500) (decide 3000 2500L 3000L 2 members None)
for changed in [1000L; 2000L; 3000L; 4000L] do
    check "busy changes wait" (Wait 2999) (decide 3000 changed (changed + 1L) 2 members None)
check "quiet after burst" Execute (decide 3000 4000L 7000L 2 members None)
check "same front and members" Ignore (decide 3000 0L 3000L 1 members (Some(1, members)))
check "new front" Execute (decide 3000 0L 3000L 2 members (Some(1, members)))
check "new member" Execute (decide 3000 0L 3000L 1 (members.Add 4) (Some(1, members)))
check "removed member" Execute (decide 3000 0L 3000L 1 (members.Remove 3) (Some(1, members)))
check "empty group" Ignore (decide 3000 0L 3000L 1 Set.empty None)
check "single member" Ignore (decide 3000 0L 3000L 1 (Set.singleton 1) None)
check "front removed" Ignore (decide 3000 0L 3000L 4 members None)
check "fixed registration interval" 300 paceMs
check "bounded additions" 3 addAttempts
check "first registration is immediate" 0 (paceRemaining 0 None)
check "registration waits" 300 (paceRemaining 10 (Some 10))
check "registration almost due" 1 (paceRemaining 309 (Some 10))
check "registration due" 0 (paceRemaining 310 (Some 10))
check "wrapped tick count" 290 (paceRemaining (System.Int32.MinValue + 4) (Some (System.Int32.MaxValue - 5)))

let exercise failCount canceled throwDelete throwAdd safe =
    let mutable deletes = 0
    let mutable adds = 0
    use source = new System.Threading.CancellationTokenSource()
    let token = source.Token
    let result = reRegister addAttempts (fun () -> safe)
                    (fun () ->
                        deletes <- deletes + 1
                        if canceled then source.Cancel(); source.Dispose()
                        if throwDelete then failwith "delete"
                        true)
                    (fun () ->
                        adds <- adds + 1
                        if throwAdd && adds <= failCount then failwith "add"
                        adds > failCount)
    result, deletes, adds, token.IsCancellationRequested
check "success" (true, 1, 1, false) (exercise 0 false false false true)
check "retry addition only" (true, 1, 3, false) (exercise 2 false false false true)
check "bounded failure" (false, 1, 3, false) (exercise 5 false false false true)
check "cancellation still restores" (true, 1, 3, true) (exercise 2 true false false true)
check "add exceptions retry" (true, 1, 3, false) (exercise 2 false false true true)
check "delete exception still restores" (false, 1, 1, false) (exercise 0 false true false true)
check "changed identity stops retries" (false, 1, 1, false) (exercise 2 false false false false)
printfn "Taskbar order policy tests passed."
