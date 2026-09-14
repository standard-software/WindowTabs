// Loads the assembly as a library; never invokes Program.main or input hooks.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "../Newtonsoft.Json.dll"
#r "../WtProgram/bin/Debug/FSharp.PowerPack.dll"
#r "../WtProgram/bin/Debug/Aga.Controls.dll"
#r "../WtProgram/bin/Debug/WindowTabs.exe"
#r "../WtProgram/bin/Debug/Win32.dll"
open System
open System.IO
open Bemo
open Newtonsoft.Json.Linq

let check name condition =
    if not condition then failwith name
    printfn "PASS %s" name
let directory = Path.Combine(__SOURCE_DIRECTORY__, "TestResults", "CaptionSettings-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory(directory) |> ignore
let path = Path.Combine(directory, "settings.json")
Environment.SetEnvironmentVariable("WINDOWTABS_BENCHMARK_SETTINGS", path)
File.WriteAllText(path, "{\"Version\":\"test\",\"RunAtStartup\":false}")
let settings = Settings(false)
// Abort before any read/write if the Debug-only override is unavailable.
check "Settings path is isolated inside the worktree" (settings.path = path)
let service = settings :> ISettings
check "Missing key defaults to OFF" (not (unbox<bool>(service.getValue "lockWindowPosition")))
let mutable notifications = []
service.notifyValue "lockWindowPosition" (fun value -> notifications <- unbox<bool> value :: notifications)
service.setValue("lockWindowPosition", box true)
check "ON is saved as a JSON boolean" (JObject.Parse(File.ReadAllText(path)).["LockWindowPosition"].Value<bool>())
settings.clearCaches()
check "ON survives clearing all caches and rereading disk" (unbox<bool>(service.getValue "lockWindowPosition"))
service.setValue("lockWindowPosition", box false)
settings.clearCaches()
check "OFF survives rereading disk" (not (unbox<bool>(service.getValue "lockWindowPosition")))
check "Live setting notifications carry ON and OFF" (notifications = [false; true])
check "Unrelated persisted setting remains" (not (JObject.Parse(File.ReadAllText(path)).["RunAtStartup"].Value<bool>()))
printfn "All 7 checks passed; isolated fixture: %s" path
