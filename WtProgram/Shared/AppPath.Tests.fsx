// Checks for AppPath. Run with:
//     dotnet fsi WtProgram/Shared/AppPath.Tests.fsx
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

let wa v = sprintf @"C:\Program Files\WindowsApps\Example.App_%s_x64__examplepub\app\example-app.exe" v
let lmp v = sprintf @"C:\Fixtures\Plugins\PluginApp\%s\PluginApp.exe" v
let chrome = @"C:\Program Files\Example\Browser\app.exe"

// The version-directory rule applies only to executables named in
// Settings\VersionFolder.json. Install a symbolic name for this test;
// everything else keeps its full path.
AppPath.setVersionDirectoryApps [ "PluginApp.exe" ]

// Run a check with a different list, then put the shipped one back.
let withNames names f =
    let saved = AppPath.versionDirectoryAppNames () |> Set.toList
    AppPath.setVersionDirectoryApps names
    try f () finally AppPath.setVersionDirectoryApps saved

// ----- the rule is opt-in, per executable -----

check "an application that is not listed keeps its version directories"
    (not (AppPath.sameApp @"C:\Program Files\Example IDE\18\Common7\IDE\ide.exe"
                          @"C:\Program Files\Example IDE\19\Common7\IDE\ide.exe"))

check "listing it makes the rule apply"
    (withNames [ "ide.exe" ] (fun () ->
        AppPath.sameApp @"C:\Program Files\Example IDE\18\Common7\IDE\ide.exe"
                        @"C:\Program Files\Example IDE\19\Common7\IDE\ide.exe"))

check "the name is matched without regard to case"
    (withNames [ "PLUGINAPP.EXE" ] (fun () ->
        AppPath.sameApp @"C:\a\PluginApp\1.0\PluginApp.exe"
                        @"C:\a\PluginApp\2.0\PluginApp.exe"))

check "an empty list turns the rule off"
    (withNames [] (fun () ->
        not (AppPath.sameApp @"C:\a\PluginApp\1.0\PluginApp.exe"
                             @"C:\a\PluginApp\2.0\PluginApp.exe")))

check "but a Store application never needs listing"
    (withNames [] (fun () ->
        AppPath.sameApp
            @"C:\Program Files\WindowsApps\Example.App_1.0.0.0_x64__pub\app\example-app.exe"
            @"C:\Program Files\WindowsApps\Example.App_2.0.0.0_x64__pub\app\example-app.exe"))

// ----- the reported defect -----

check "a Store app is the same app across an update"
    (AppPath.sameApp (wa "1.2.0.0") (wa "1.3.0.0"))

check "and across many updates"
    (AppPath.sameApp (wa "1.1.0.0") (wa "1.3.0.0"))

check "the architecture is ignored too"
    (AppPath.sameApp
        @"C:\Program Files\WindowsApps\Example.Editor_1.2.3.0_neutral__editorpub\editor.exe"
        @"C:\Program Files\WindowsApps\Example.Editor_1.3.0.0_x64__editorpub\editor.exe")

check "a plugin with the version in a directory of its own survives an update"
    (AppPath.sameApp (lmp "1.2.0.0") (lmp "1.3.0.0"))

// ----- what gets stored -----

equal "a Store path becomes a pattern that still names the publisher"
    @"c:\program files\windowsapps\example.app_*_*__examplepub\app\example-app.exe"
    (AppPath.normalize (wa "1.3.0.0"))

equal "a version directory becomes a wildcard"
    @"c:\fixtures\plugins\pluginapp\*\pluginapp.exe"
    (AppPath.normalize (lmp "1.2.0.0"))

equal "normalising a pattern gives the same pattern back"
    (AppPath.normalize (wa "1.3.0.0"))
    (AppPath.normalize (AppPath.normalize (wa "1.3.0.0")))

check "so a stored pattern matches a live path"
    (AppPath.sameApp (AppPath.normalize (wa "1.1.0.0")) (wa "1.3.0.0"))

equal "Path.GetFileName still reads a pattern, which is what the Programs tab shows"
    "example-app.exe" (IO.Path.GetFileName(AppPath.normalize (wa "1.3.0.0")))

check "File.Exists tolerates a pattern rather than throwing"
    (not (IO.File.Exists(AppPath.normalize (wa "1.3.0.0"))))

// ----- what must stay apart -----

check "a different publisher is a different app"
    (not (AppPath.sameApp (wa "1.3.0.0")
            @"C:\Program Files\WindowsApps\Example.App_1.3.0.0_x64__aaaaaaaaaaaaa\app\example-app.exe"))

check "two executables in one package stay apart"
    (not (AppPath.sameApp
            @"C:\Program Files\WindowsApps\Example.Terminal_2.0.0.0_x64__terminalpub\WindowsTerminal.exe"
            @"C:\Program Files\WindowsApps\Example.Terminal_2.0.0.0_x64__terminalpub\OpenConsole.exe"))

check "the Store example-app.exe is not the CLI example-app.exe"
    (not (AppPath.sameApp (wa "1.3.0.0")
            @"C:\Fixtures\Cli\example-app.exe"))

check "two plugins under the same parent stay apart"
    (not (AppPath.sameApp (lmp "1.2.0.0")
            @"C:\Fixtures\Plugins\Other\1.2.0.0\Other.exe"))

check "an ordinary path is left alone"
    (not (AppPath.sameApp chrome @"C:\Program Files\Example\OtherBrowser\other.exe"))

check "a versioned path and an unversioned one stay apart"
    (not (AppPath.sameApp (lmp "1.2.0.0")
            @"C:\Fixtures\Plugins\PluginApp\PluginApp.exe"))

// ----- ordinary paths change only in case -----

equal "a non-Store path normalises to itself, lower-cased"
    @"c:\program files\example\browser\app.exe" (AppPath.normalize chrome)

check "case alone makes no difference"
    (AppPath.sameApp chrome @"c:\PROGRAM FILES\example\BROWSER\App.EXE")

check "a plain directory name is not a version"
    (not (AppPath.sameApp @"C:\app\alpha\PluginApp.exe" @"C:\app\beta\PluginApp.exe"))

check "digits mixed into letters are not a version"
    (not (AppPath.sameApp @"C:\app\v1.2\PluginApp.exe" @"C:\app\v1.3\PluginApp.exe"))

check "a single number is a version"
    (AppPath.sameApp @"C:\app\1\PluginApp.exe" @"C:\app\2\PluginApp.exe")

check "the file name itself is never treated as a version"
    (withNames [ "1.2.exe"; "1.3.exe" ] (fun () ->
        not (AppPath.sameApp @"C:\app\1.2.exe" @"C:\app\1.3.exe")))

check "an ordinary path is not reported as versioned"
    (not (AppPath.isVersioned chrome))

check "a Store path is"
    (AppPath.isVersioned (wa "1.3.0.0"))

check "and so is a version directory"
    (AppPath.isVersioned (lmp "1.2.0.0"))

// ----- shapes that must not be mangled -----

let unchanged name p =
    equal name ((p: string).ToLowerInvariant()) (AppPath.normalize p)

unchanged "a resource package has no publisher separator and is left alone"
    @"C:\Program Files\WindowsApps\Example.Terminal_1.24_neutral_split.scale-100_terminalpub\x.exe"

unchanged "a folder with too few fields is left alone"
    @"C:\Program Files\WindowsApps\Something__pub\x.exe"

unchanged "a non-numeric version field is left alone"
    @"C:\Program Files\WindowsApps\Name_notaversion_x64__pub\x.exe"

unchanged "the marker without a following folder is left alone"
    @"C:\Program Files\WindowsApps\Example.App_1.0_x64__pub"

unchanged "dots alone are not a version, so a relative path survives"
    @"C:\app\..\PluginApp.exe"

equal "an empty path is empty" "" (AppPath.normalize null)
equal "an empty string is empty" "" (AppPath.normalize "")

// ----- list operations -----

let stale = [ wa "1.4.0.0"; chrome; wa "1.2.0.0" ]

check "containsApp finds the app under a new version"
    (AppPath.containsApp stale (wa "1.3.0.0"))

equal "removeApp takes out every generation"
    [ chrome ] (AppPath.removeApp stale (wa "1.3.0.0"))

equal "addApp leaves one entry, and it is the pattern"
    [ chrome; AppPath.normalize (wa "1.3.0.0") ]
    (AppPath.addApp stale (wa "1.3.0.0"))

equal "adding an ordinary path stores that path"
    [ AppPath.normalize chrome ] (AppPath.addApp [] chrome)

equal "adding the same app under two versions still leaves one entry"
    1 (AppPath.addApp (AppPath.addApp [] (wa "1.1.0.0")) (wa "2.0.0.0")).Length

equal "canonicalise collapses every generation to one pattern"
    [ AppPath.normalize (wa "1.0.0.0"); AppPath.normalize chrome ]
    (AppPath.canonicalise [ wa "1.4.0.0"; chrome; wa "1.2.0.0" ])

equal "canonicalise keeps the original order"
    [ AppPath.normalize chrome; AppPath.normalize (wa "1.0.0.0") ]
    (AppPath.canonicalise [ chrome; wa "1.4.0.0"; wa "1.2.0.0" ])

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
    (claimLowest [ []; [ wa "1.1.0.0" ]; [ wa "1.5.0.0" ]; [ wa "1.6.0.0" ] ])

equal "other applications in those categories are untouched"
    [ [ AppPath.normalize chrome ]; [ AppPath.normalize (wa "1.0.0.0") ]; [ @"c:\other.exe" ] ]
    (claimLowest [ [ chrome ]; [ wa "1.1.0.0" ]; [ @"C:\other.exe" ] ])

// ----- synthetic snapshot only; never read the user's settings in this suite -----
let fixturePaths = [
    @"C:\Program Files\WindowsApps\Example.Terminal_2.3.4.0_x64__examplepub\app\terminal.exe"
    @"C:\Program Files\WindowsApps\Example.Terminal_3.4.5.0_x64__examplepub\app\terminal.exe"
    @"C:\Apps\Editor\editor.exe"
]
let collapsed = AppPath.canonicalise fixturePaths
equal "snapshot collapses only package generations" 2 collapsed.Length
check "snapshot keeps every distinct normalized application"
    (collapsed.Length = (fixturePaths |> List.map AppPath.normalize |> List.distinct |> List.length))
equal "snapshot canonicalization is idempotent" collapsed (AppPath.canonicalise collapsed)
check "another package with the same exe is distinct"
    (not (AppPath.sameApp fixturePaths.Head
        @"C:\Program Files\WindowsApps\Example.Other_3.4.5.0_x64__examplepub\app\terminal.exe"))

printfn ""
printfn "AppPath: %d passed, %d failed" passed failed
if failed > 0 then exit 1
