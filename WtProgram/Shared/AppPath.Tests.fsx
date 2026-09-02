// Checks for AppPath. Run with:
//     fsi.exe --exec WtProgram/Shared/AppPath.Tests.fsx
//
// The module is pure, so this needs neither a build nor a running WindowTabs.

#load "AppPath.fs"

open System
open Bemo

let mutable passed = 0
let mutable failed = 0

let check name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        printfn "FAIL  %s" name

let equal name (expected: 'a) (actual: 'a) =
    if expected = actual then passed <- passed + 1
    else
        failed <- failed + 1
        printfn "FAIL  %s\n        expected %A\n        actual   %A" name expected actual

let wa v = sprintf @"C:\Program Files\WindowsApps\Claude_%s_x64__pzs8sxrjxfjjc\app\claude.exe" v
let lmp v = sprintf @"C:\Users\me\AppData\Local\LINE\Data\plugin\LineMediaPlayer\%s\LineMediaPlayer.exe" v
let chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe"

// The version-directory rule applies only to executables named in
// Settings\VersionFolder.json. The shipped file lists this one; everything
// else keeps its full path.
AppPath.setVersionDirectoryApps [ "LineMediaPlayer.exe" ]

// Run a check with a different list, then put the shipped one back.
let withNames names f =
    let saved = AppPath.versionDirectoryAppNames () |> Set.toList
    AppPath.setVersionDirectoryApps names
    try f () finally AppPath.setVersionDirectoryApps saved

// ----- the rule is opt-in, per executable -----

check "an application that is not listed keeps its version directories"
    (not (AppPath.sameApp @"C:\Program Files\Microsoft Visual Studio\18\Common7\IDE\devenv.exe"
                          @"C:\Program Files\Microsoft Visual Studio\19\Common7\IDE\devenv.exe"))

check "listing it makes the rule apply"
    (withNames [ "devenv.exe" ] (fun () ->
        AppPath.sameApp @"C:\Program Files\Microsoft Visual Studio\18\Common7\IDE\devenv.exe"
                        @"C:\Program Files\Microsoft Visual Studio\19\Common7\IDE\devenv.exe"))

check "the name is matched without regard to case"
    (withNames [ "LINEMEDIAPLAYER.EXE" ] (fun () ->
        AppPath.sameApp @"C:\a\LineMediaPlayer\1.0\LineMediaPlayer.exe"
                        @"C:\a\LineMediaPlayer\2.0\LineMediaPlayer.exe"))

check "an empty list turns the rule off"
    (withNames [] (fun () ->
        not (AppPath.sameApp @"C:\a\LineMediaPlayer\1.0\LineMediaPlayer.exe"
                             @"C:\a\LineMediaPlayer\2.0\LineMediaPlayer.exe")))

check "but a Store application never needs listing"
    (withNames [] (fun () ->
        AppPath.sameApp
            @"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pub\app\claude.exe"
            @"C:\Program Files\WindowsApps\Claude_2.0.0.0_x64__pub\app\claude.exe"))

// ----- the reported defect -----

check "a Store app is the same app across an update"
    (AppPath.sameApp (wa "1.40609.0.0") (wa "1.40609.1.0"))

check "and across many updates"
    (AppPath.sameApp (wa "1.1.4328.0") (wa "1.40609.1.0"))

check "the architecture is ignored too"
    (AppPath.sameApp
        @"C:\Program Files\WindowsApps\63996TranKyNam.aText_1.2.3.0_neutral__wfd09jcz50d5g\aText.exe"
        @"C:\Program Files\WindowsApps\63996TranKyNam.aText_1.3.0.0_x64__wfd09jcz50d5g\aText.exe")

check "a plugin with the version in a directory of its own survives an update"
    (AppPath.sameApp (lmp "1.2.0.635") (lmp "1.2.0.650"))

// ----- what gets stored -----

equal "a Store path becomes a pattern that still names the publisher"
    @"c:\program files\windowsapps\claude_*_*__pzs8sxrjxfjjc\app\claude.exe"
    (AppPath.normalize (wa "1.40609.1.0"))

equal "a version directory becomes a wildcard"
    @"c:\users\me\appdata\local\line\data\plugin\linemediaplayer\*\linemediaplayer.exe"
    (AppPath.normalize (lmp "1.2.0.635"))

equal "normalising a pattern gives the same pattern back"
    (AppPath.normalize (wa "1.40609.1.0"))
    (AppPath.normalize (AppPath.normalize (wa "1.40609.1.0")))

check "so a stored pattern matches a live path"
    (AppPath.sameApp (AppPath.normalize (wa "1.1.4328.0")) (wa "1.40609.1.0"))

equal "Path.GetFileName still reads a pattern, which is what the Programs tab shows"
    "claude.exe" (IO.Path.GetFileName(AppPath.normalize (wa "1.40609.1.0")))

check "File.Exists tolerates a pattern rather than throwing"
    (not (IO.File.Exists(AppPath.normalize (wa "1.40609.1.0"))))

// ----- what must stay apart -----

check "a different publisher is a different app"
    (not (AppPath.sameApp (wa "1.40609.1.0")
            @"C:\Program Files\WindowsApps\Claude_1.40609.1.0_x64__aaaaaaaaaaaaa\app\claude.exe"))

check "two executables in one package stay apart"
    (not (AppPath.sameApp
            @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.11911.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe"
            @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.11911.0_x64__8wekyb3d8bbwe\OpenConsole.exe"))

check "the Store claude.exe is not the CLI claude.exe"
    (not (AppPath.sameApp (wa "1.40609.1.0")
            @"C:\nvm4w\nodejs\node_modules\@anthropic-ai\claude-code\bin\claude.exe"))

check "two plugins under the same parent stay apart"
    (not (AppPath.sameApp (lmp "1.2.0.635")
            @"C:\Users\me\AppData\Local\LINE\Data\plugin\Other\1.2.0.635\Other.exe"))

check "an ordinary path is left alone"
    (not (AppPath.sameApp chrome @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"))

check "a versioned path and an unversioned one stay apart"
    (not (AppPath.sameApp (lmp "1.2.0.635")
            @"C:\Users\me\AppData\Local\LINE\Data\plugin\LineMediaPlayer\LineMediaPlayer.exe"))

// ----- ordinary paths change only in case -----

equal "a non-Store path normalises to itself, lower-cased"
    @"c:\program files\google\chrome\application\chrome.exe" (AppPath.normalize chrome)

check "case alone makes no difference"
    (AppPath.sameApp chrome @"c:\PROGRAM FILES\google\CHROME\application\Chrome.EXE")

check "a plain directory name is not a version"
    (not (AppPath.sameApp @"C:\app\alpha\LineMediaPlayer.exe" @"C:\app\beta\LineMediaPlayer.exe"))

check "digits mixed into letters are not a version"
    (not (AppPath.sameApp @"C:\app\v1.2\LineMediaPlayer.exe" @"C:\app\v1.3\LineMediaPlayer.exe"))

check "a single number is a version"
    (AppPath.sameApp @"C:\app\2024\LineMediaPlayer.exe" @"C:\app\2025\LineMediaPlayer.exe")

check "the file name itself is never treated as a version"
    (withNames [ "1.2.exe"; "1.3.exe" ] (fun () ->
        not (AppPath.sameApp @"C:\app\1.2.exe" @"C:\app\1.3.exe")))

check "an ordinary path is not reported as versioned"
    (not (AppPath.isVersioned chrome))

check "a Store path is"
    (AppPath.isVersioned (wa "1.40609.1.0"))

check "and so is a version directory"
    (AppPath.isVersioned (lmp "1.2.0.635"))

// ----- shapes that must not be mangled -----

let unchanged name p =
    equal name ((p: string).ToLowerInvariant()) (AppPath.normalize p)

unchanged "a resource package has no publisher separator and is left alone"
    @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24_neutral_split.scale-100_8wekyb3d8bbwe\x.exe"

unchanged "a folder with too few fields is left alone"
    @"C:\Program Files\WindowsApps\Something__pub\x.exe"

unchanged "a non-numeric version field is left alone"
    @"C:\Program Files\WindowsApps\Name_notaversion_x64__pub\x.exe"

unchanged "the marker without a following folder is left alone"
    @"C:\Program Files\WindowsApps\Claude_1.0_x64__pub"

unchanged "dots alone are not a version, so a relative path survives"
    @"C:\app\..\LineMediaPlayer.exe"

equal "an empty path is empty" "" (AppPath.normalize null)
equal "an empty string is empty" "" (AppPath.normalize "")

// ----- list operations -----

let stale = [ wa "1.7196.0.0"; chrome; wa "1.40609.0.0" ]

check "containsApp finds the app under a new version"
    (AppPath.containsApp stale (wa "1.40609.1.0"))

equal "removeApp takes out every generation"
    [ chrome ] (AppPath.removeApp stale (wa "1.40609.1.0"))

equal "addApp leaves one entry, and it is the pattern"
    [ chrome; AppPath.normalize (wa "1.40609.1.0") ]
    (AppPath.addApp stale (wa "1.40609.1.0"))

equal "adding an ordinary path stores that path"
    [ AppPath.normalize chrome ] (AppPath.addApp [] chrome)

equal "adding the same app under two versions still leaves one entry"
    1 (AppPath.addApp (AppPath.addApp [] (wa "1.1.0.0")) (wa "2.0.0.0")).Length

equal "canonicalise collapses every generation to one pattern"
    [ AppPath.normalize (wa "1.0.0.0"); AppPath.normalize chrome ]
    (AppPath.canonicalise [ wa "1.7196.0.0"; chrome; wa "1.40609.0.0" ])

equal "canonicalise keeps the original order"
    [ AppPath.normalize chrome; AppPath.normalize (wa "1.0.0.0") ]
    (AppPath.canonicalise [ chrome; wa "1.7196.0.0"; wa "1.40609.0.0" ])

equal "canonicalise on a clean list only lower-cases it"
    [ @"c:\a\x.exe"; @"c:\b\y.exe" ]
    (AppPath.canonicalise [ @"C:\a\x.exe"; @"C:\b\y.exe" ])

equal "canonicalise of an empty list is empty" [] (AppPath.canonicalise [])

// ----- one category per application -----
//
// The rule the settings load applies: walk the ten category lists in order and
// let the lowest number keep each application.

let claimLowest (lists: string list list) =
    let claimed = Collections.Generic.HashSet<string>()
    lists |> List.map (fun paths -> paths |> AppPath.canonicalise |> List.filter claimed.Add)

equal "an application in three categories is left in the lowest"
    [ []; [ AppPath.normalize (wa "1.0.0.0") ]; []; [] ]
    (claimLowest [ []; [ wa "1.1.4328.0" ]; [ wa "1.26832.0.0" ]; [ wa "1.34493.1.0" ] ])

equal "other applications in those categories are untouched"
    [ [ AppPath.normalize chrome ]; [ AppPath.normalize (wa "1.0.0.0") ]; [ @"c:\other.exe" ] ]
    (claimLowest [ [ chrome ]; [ wa "1.1.4328.0" ]; [ @"C:\other.exe" ] ])

// ----- the real settings file, when it is there -----

let settingsPath =
    IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WindowTabs", "WindowTabsSettings.txt")

if IO.File.Exists(settingsPath) then
    let text = IO.File.ReadAllText(settingsPath)
    let paths =
        Text.RegularExpressions.Regex.Matches(text, "\"([A-Za-z]:\\\\\\\\[^\"]+?\\.exe)\"")
        |> Seq.cast<Text.RegularExpressions.Match>
        |> Seq.map (fun m -> m.Groups.[1].Value.Replace(@"\\", @"\"))
        |> Seq.distinct
        |> List.ofSeq
    let versioned = paths |> List.filter AppPath.isVersioned
    printfn "  (real settings file: %d paths, %d carrying a version)" paths.Length versioned.Length
    let collapsed = AppPath.canonicalise paths
    printfn "  (they collapse to %d applications)" collapsed.Length
    check "collapsing the real file keeps every application that was distinct"
        (collapsed.Length = (paths |> List.map AppPath.normalize |> List.distinct |> List.length))
    check "collapsing the real file is idempotent"
        (AppPath.canonicalise collapsed = collapsed)
else
    printfn "  (no settings file on this machine; skipped the real-data checks)"

printfn ""
printfn "AppPath: %d passed, %d failed" passed failed
if failed > 0 then exit 1
