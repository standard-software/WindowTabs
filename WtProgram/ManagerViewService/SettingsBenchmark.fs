namespace Bemo

open System
open System.Windows.Forms

module SettingsBenchmark =
    let install() =
#if DEBUG
        if SettingsTiming.enabled() then
            SettingsTiming.recordStartup Services.program.version
            let timer = new Timer()
            let mutable opened = 0
            let mutable showing = false
            let visibleWait = Diagnostics.Stopwatch()
            timer.Interval <- 750
            timer.Tick.Add(fun _ ->
                timer.Stop()
                try
                    let ready = SettingsTiming.current |> Option.exists (fun run -> run.ProgramsReady)
                    if showing && not ready && visibleWait.Elapsed.TotalSeconds < 30.0 then
                        timer.Start()
                    elif showing then
                        if not ready then SettingsTiming.mark "programs-incomplete:timeout"
                        // Exercise a persisted change while visible, on the disposable
                        // settings copy only. Reopening must reflect the new value.
                        if opened = 4 then
                            let key = "snapTabHeightMargin"
                            let previous = unbox<bool>(Services.settings.getValue key)
                            Services.settings.setValue(key, box(not previous))
                            SettingsTiming.mark "settings-changed:snapTabHeightMargin"
                        match DesktopManagerFormState.currentForm with
                        | Some form ->
                            form.Update()
                            SettingsTiming.mark "ready-before-close"
                            form.Close()
                        | None -> SettingsTiming.mark "missing-form"
                        SettingsTiming.current |> Option.iter (fun run -> run.Flush())
                        showing <- false
                        if opened = 6 then
                            timer.Dispose()
                            Services.program.shutdown()
                        else timer.Start()
                    else
                        SettingsTiming.scenario <-
                            if opened = 0 then "first"
                            elif opened < 4 then "warm"
                            elif opened = 4 then "changed"
                            else "changed-warm"
                        Services.managerView.show()
                        visibleWait.Restart()
                        opened <- opened + 1
                        showing <- true
                        timer.Start()
                with ex ->
                    SettingsTiming.mark ("benchmark-error:" + ex.Message)
                    SettingsTiming.current |> Option.iter (fun run -> run.Flush())
                    timer.Dispose()
                    Services.program.shutdown())
            // Give each process the same settling interval after startup.
            timer.Interval <- 2000
            timer.Tick.Add(fun _ -> timer.Interval <- 750)
            timer.Start()
#endif
        ()
