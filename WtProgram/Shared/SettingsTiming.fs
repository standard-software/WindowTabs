namespace Bemo

open System
open System.Diagnostics
open System.IO
open System.Globalization

module SettingsTiming =
    let enabled() =
#if DEBUG
        not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINDOWTABS_BENCHMARK_SETTINGS"))) &&
        not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINDOWTABS_BENCHMARK_LOG")))
#else
        false
#endif
    let mutable scenario = "manual"
    type Run(version: string) =
#if DEBUG
        let clock = Stopwatch.StartNew()
        let id = Guid.NewGuid().ToString("N")
        let rows = Collections.Generic.List<string>()
        let mutable programsReady = false
        let mutable shown = false
#endif
        member this.ProgramsReady =
#if DEBUG
            lock rows (fun () -> programsReady && shown)
#else
            false
#endif
        member this.Mark(stage: string) =
#if DEBUG
            if enabled() then
                let elapsed = clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
                lock rows (fun () ->
                    if stage.StartsWith("programs-ready:", StringComparison.Ordinal) then programsReady <- true
                    if stage = "shown" then shown <- true
                    rows.Add(sprintf "%s\t%d\t%s\t%s\t%s" id (Process.GetCurrentProcess().Id) version stage elapsed))
#endif
            ()
        member this.Flush() =
#if DEBUG
            if enabled() then
                lock rows (fun () ->
                    if rows.Count > 0 then
                        File.AppendAllLines(Environment.GetEnvironmentVariable("WINDOWTABS_BENCHMARK_LOG"), rows)
                        rows.Clear())
#endif
            ()
    let mutable current: Run option = None
    let beginRun version =
        let run = Run(version)
#if DEBUG
        if enabled() then
            current <- Some run
            run.Mark("request")
            run.Mark("scenario:" + scenario)
#endif
        run
    let mark stage = current |> Option.iter (fun run -> run.Mark(stage))
    let measure stage action =
        mark (stage + ":begin")
        let result = action()
        mark (stage + ":end")
        result
#if DEBUG
    let startupClock = Stopwatch()
    let startStartup() = if enabled() then startupClock.Restart()
    let recordStartup version =
        if enabled() then
            use process = Process.GetCurrentProcess()
            let row = sprintf "startup\t%d\t%s\tstartup-ready:privateBytes=%d:handles=%d\t%s"
                        process.Id version process.PrivateMemorySize64 process.HandleCount
                        (startupClock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
            File.AppendAllLines(Environment.GetEnvironmentVariable("WINDOWTABS_BENCHMARK_LOG"), [|row|])
#endif
