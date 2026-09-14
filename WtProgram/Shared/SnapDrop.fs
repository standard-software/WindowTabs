namespace Bemo

// A position-first drop may deliver WM_DPICHANGED asynchronously. Scheduling
// and the clock are supplied by Desktop so no UI thread sleeps in this policy.
module SnapDrop =
    let finishAfterDpiChange (currentDpi: uint32) (targetDpi: uint32) perMonitorAware
                            (isWindow: unit -> bool) (readDpi: unit -> uint32)
                            (elapsedMs: unit -> int64)
                            (schedule: int -> (unit -> unit) -> unit) (finish: unit -> unit) =
        if perMonitorAware && currentDpi <> targetDpi then
            let rec checkDpi() =
                if not (isWindow()) then finish()
                elif readDpi() = targetDpi then schedule 20 finish
                elif elapsedMs() >= 200L then finish()
                else schedule 10 checkDpi
            schedule 10 checkDpi
        else finish()
