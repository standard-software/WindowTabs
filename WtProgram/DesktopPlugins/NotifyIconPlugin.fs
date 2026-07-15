namespace Bemo
open System
open System.Windows.Forms
open System.Reflection
open Newtonsoft.Json.Linq
open System.Diagnostics
open System.IO
open System.Net
open System.Text.RegularExpressions
open System.Threading
open Microsoft.Win32

// Watchdog module to detect UI thread freeze and auto-restart
module Watchdog =
    let mutable private watchdogThread: Thread option = None
    let mutable private stopRequested = false
    let mutable private uiThreadInvoker: Invoker option = None  // Store UI thread's invoker
    let private freezeTimeout = 10000  // 10 seconds timeout for freeze detection
    let private checkInterval = 5000   // Check every 5 seconds
    let private requiredConsecutiveFailures = 1  // Restart after 1 timeout (10 seconds unresponsive)

    // Use AutoResetEvent for more reliable signaling
    let private pingResponse = new AutoResetEvent(false)

    let respondToPing() =
        pingResponse.Set() |> ignore

    let private trySaveAndRestart() =
        try
            // Try to save tab groups before restart
            let saveComplete = new ManualResetEvent(false)
            try
                match uiThreadInvoker with
                | Some invoker ->
                    invoker.asyncInvoke(fun () ->
                        try
                            Services.program.saveTabGroupsBeforeExit()
                        with _ -> ()
                        saveComplete.Set() |> ignore
                    )
                | None -> ()
                // Wait max 2 seconds for save
                saveComplete.WaitOne(2000) |> ignore
            with _ -> ()

            // Start new process and exit
            let exePath = Assembly.GetExecutingAssembly().Location
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- "cmd.exe"
            startInfo.Arguments <- sprintf "/c timeout /t 2 /nobreak >nul && start \"\" \"%s\"" exePath
            startInfo.WindowStyle <- ProcessWindowStyle.Hidden
            startInfo.CreateNoWindow <- true
            Process.Start(startInfo) |> ignore
            ForceExitState.isForceExiting <- true
            Environment.Exit(0)
        with _ ->
            Environment.Exit(1)

    let private watchdogLoop() =
        // Wait before starting monitoring to allow app to initialize
        Thread.Sleep(10000)

        let mutable consecutiveFailures = 0

        while not stopRequested do
            try
                // Send ping to UI thread using the stored UI thread invoker
                match uiThreadInvoker with
                | Some invoker ->
                    try
                        invoker.asyncInvoke(fun () -> respondToPing())
                    with _ -> ()
                | None -> ()

                // Wait for response with timeout
                let responded = pingResponse.WaitOne(freezeTimeout)

                if responded then
                    // UI thread responded, reset failure count
                    consecutiveFailures <- 0
                else
                    // UI thread did not respond
                    consecutiveFailures <- consecutiveFailures + 1

                    if consecutiveFailures >= requiredConsecutiveFailures && not stopRequested && not ForceExitState.isForceExiting then
                        // UI thread is frozen (confirmed by multiple consecutive failures), force restart
                        trySaveAndRestart()

                // Wait before next check
                Thread.Sleep(checkInterval)
            with _ ->
                Thread.Sleep(checkInterval)

    let start() =
        // Don't start watchdog when debugger is attached (prevents false positives during debugging)
        if Debugger.IsAttached then
            ()
        elif watchdogThread.IsNone then
            // Capture UI thread's invoker (must be called from UI thread)
            uiThreadInvoker <- Some(InvokerService.invoker)
            stopRequested <- false
            let thread = new Thread(ThreadStart(watchdogLoop))
            thread.IsBackground <- true
            thread.Name <- "WindowTabs Watchdog"
            thread.Start()
            watchdogThread <- Some(thread)

    let stop() =
        stopRequested <- true
        pingResponse.Set() |> ignore  // Unblock any waiting

// Auto-update: checks the GitHub releases of this repository and installs a
// newer release on request. The MSI install is detected by comparing the exe
// folder with the InstallPath the installer writes to HKCU; anything else
// (the portable zip, dev builds) uses the zip overwrite flow.
module UpdateChecker =
    let private releaseApiUrl = "https://api.github.com/repos/standard-software/WindowTabs/releases/latest"

    type ReleaseInfo = {
        tag: string
        msiUrl: string option
        zipUrl: string option
    }

    // Version strings look like ss_2026.07.10, ss_jp_2026.03.25 or the dev
    // form ss_2026.07.10_next3 — compare by the embedded date only.
    let versionDate (v: string) =
        let m = Regex.Match(v, @"(\d{4})\.(\d{2})\.(\d{2})")
        if m.Success then
            try Some(DateTime(int m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value)) with _ -> None
        else None

    let isNewer (currentVersion: string) (tag: string) =
        match versionDate currentVersion, versionDate tag with
        | Some(cur), Some(latest) -> latest > cur
        | _ -> false

    let private newWebClient() =
        // GitHub requires TLS 1.2+, which .NET Framework does not enable by default
        ServicePointManager.SecurityProtocol <- ServicePointManager.SecurityProtocol ||| SecurityProtocolType.Tls12
        let wc = new WebClient()
        wc.Headers.Add("User-Agent", "WindowTabs")
        wc

    let fetchLatestRelease() =
        use wc = newWebClient()
        let json = JObject.Parse(wc.DownloadString(releaseApiUrl))
        let assetUrl (name: string) =
            match json.["assets"] with
            | :? JArray as assets ->
                assets
                |> Seq.tryPick (fun a ->
                    let o = a :?> JObject
                    if String.Equals(o.["name"].ToString(), name, StringComparison.OrdinalIgnoreCase)
                    then Some(o.["browser_download_url"].ToString())
                    else None)
            | _ -> None
        {
            tag = json.["tag_name"].ToString()
            msiUrl = assetUrl "WtSetup.msi"
            zipUrl = assetUrl "WindowTabs.zip"
        }

    let appDir() = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    let isMsiInstall() =
        try
            use key = Registry.CurrentUser.OpenSubKey(@"Software\WindowTabs")
            match key with
            | null -> false
            | key ->
                match key.GetValue("InstallPath") with
                | :? string as installPath when installPath <> "" ->
                    let norm (p: string) = p.Trim().TrimEnd('\\').ToLowerInvariant()
                    norm installPath = norm (appDir())
                | _ -> false
        with _ -> false

    let download (url: string) =
        let dest = Path.Combine(Path.GetTempPath(), "WindowTabsUpdate_" + Path.GetFileName(Uri(url).LocalPath))
        use wc = newWebClient()
        wc.DownloadFile(url, dest)
        dest

    // The installer closes the running WindowTabs itself (util:CloseApplication)
    // and offers to relaunch it when done.
    let installMsi (msiPath: string) =
        Process.Start("msiexec.exe", sprintf "/i \"%s\"" msiPath) |> ignore

    // Overwrite-in-place update for the portable zip: a detached PowerShell
    // waits for this process to exit, extracts the zip over the app folder
    // and restarts WindowTabs.
    let installZipAndExit (zipPath: string) =
        let extractDir = zipPath + ".extracted"
        let exePath = Path.Combine(appDir(), "WindowTabs.exe")
        let pid = Process.GetCurrentProcess().Id
        let command =
            sprintf "Wait-Process -Id %d -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; Expand-Archive -LiteralPath '%s' -DestinationPath '%s' -Force; Copy-Item -Path '%s\\*' -Destination '%s' -Recurse -Force; Start-Process -FilePath '%s'"
                pid zipPath extractDir extractDir (appDir()) exePath
        let psi = ProcessStartInfo()
        psi.FileName <- "powershell.exe"
        psi.Arguments <- sprintf "-NoProfile -ExecutionPolicy Bypass -Command \"%s\"" command
        psi.WindowStyle <- ProcessWindowStyle.Hidden
        psi.CreateNoWindow <- true
        Process.Start(psi) |> ignore
        Services.program.shutdown()

type NotifyIconPlugin() as this =
    let Cell = CellScope()

    let closeSettingsDialog() =
        // Close the settings dialog if one is open. The form's FormClosed
        // handler (registered in DesktopManagerForm) is responsible for
        // releasing the named "WindowTabsSettingsDialog" mutex and clearing
        // DesktopManagerFormState.currentForm — so we don't touch either
        // directly here.
        //
        // (An earlier version opened a second handle to the named mutex with
        //  initialOwner=true and immediately ReleaseMutex'd / Dispose'd it
        //  before calling form.Close(). That extra release on the UI thread
        //  decremented the lock count of the dialog's M1 ownership before
        //  FormClosed could run, leaving the named-mutex object in a state
        //  where the dialog could not be reopened after a language change.
        //  Removing the dance keeps ownership tracking simple: M1 is acquired
        //  in show(), released in FormClosed, period.)
        match DesktopManagerFormState.currentForm with
        | Some form ->
            try form.Close() with _ -> ()
        | None -> ()

    member this.icon = Cell.cacheProp this <| fun() ->
        let notifyIcon = new NotifyIcon()
        notifyIcon.Visible <- true
        notifyIcon.Text <- "WindowTabs version " + Services.program.version
        notifyIcon.Icon <- Services.openIcon("Bemo.ico")
        let contextMenu = new ContextMenu()

        // Apply dark mode setting and update menu texts when menu is about to be shown
        contextMenu.Popup.Add <| fun _ ->
            let darkModeEnabled =
                try
                    let json = Services.settings.root
                    match json.getBool("EnableDarkMode") with
                    | Some(value) -> value
                    | None -> false
                with | _ -> false
            DarkMode.setDarkModeForMenus(darkModeEnabled)

            // Update all menu item texts by checking their Tags
            for i in 0 .. contextMenu.MenuItems.Count - 1 do
                let menuItem = contextMenu.MenuItems.[i]
                match menuItem.Tag with
                | :? string as tag ->
                    match tag with
                    | "Settings" -> menuItem.Text <- Localization.getString("Settings")
                    | "CheckForUpdates" -> menuItem.Text <- Localization.getString("CheckForUpdates")
                    | "Language" ->
                        menuItem.Text <- Localization.getString("Language")
                        // Update language menu checkmarks using current language from Localization module
                        let currentLanguage = Localization.currentLanguage

                        for j in 0 .. menuItem.MenuItems.Count - 1 do
                            let langItem = menuItem.MenuItems.[j]
                            // Get language name from Tag (stored without .json extension)
                            match langItem.Tag with
                            | :? string as langName ->
                                langItem.Checked <- (currentLanguage = langName)
                                langItem.Enabled <- not (currentLanguage = langName)
                            | _ -> ()
                    | "Disable" ->
                        menuItem.Text <- Localization.getString("Disable")
                        // Update checkbox state
                        menuItem.Checked <- Services.program.isDisabled
                    | "RestartWindowTabs" -> menuItem.Text <- Localization.getString("RestartWindowTabs")
                    | "CloseWindowTabs" -> menuItem.Text <- Localization.getString("CloseWindowTabs")
                    | _ -> ()
                | _ -> ()

            // Update Settings menu item enabled state based on disabled status
            for i in 0 .. contextMenu.MenuItems.Count - 1 do
                let menuItem = contextMenu.MenuItems.[i]
                match menuItem.Tag with
                | :? string as tag when tag = "Settings" ->
                    menuItem.Enabled <- not Services.program.isDisabled
                | _ -> ()

        notifyIcon.ContextMenu <- contextMenu
        notifyIcon.DoubleClick.Add <| fun _ -> Services.managerView.show()
        notifyIcon

    member this.contextMenuItems = this.icon.ContextMenu.MenuItems

    member this.addItem(text, handler) =
        this.contextMenuItems.Add(text, EventHandler(fun obj (e:EventArgs) -> handler())) |> ignore

    member this.onNewVersion() =
        this.icon.ShowBalloonTip(
            1000,
            "A new version is available.",
            "Please visit windowtabs.com to download the latest version.",
            ToolTipIcon.Info
        )

    // Check the latest GitHub release and report the result. Only invoked
    // from the tray-menu item — WindowTabs never checks on its own.
    member this.checkForUpdates() =
        let invoker = InvokerService.invoker
        let currentVersion = Services.program.version
        ThreadHelper.queueBackground <| fun() ->
            let release = try Some(UpdateChecker.fetchLatestRelease()) with _ -> None
            invoker.asyncInvoke <| fun() ->
                match release with
                | None ->
                    MessageBox.Show(Localization.getString("UpdateCheckFailed"), "WindowTabs", MessageBoxButtons.OK, MessageBoxIcon.Warning) |> ignore
                | Some(release) ->
                    if UpdateChecker.isNewer currentVersion release.tag then
                        let message = String.Format(Localization.getString("UpdateAvailableFormat"), release.tag)
                        // Default to Cancel so an accidental Enter does not start the update
                        let result = MessageBox.Show(message, "WindowTabs", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                        if result = DialogResult.OK then
                            this.startUpdate(release)
                    else
                        MessageBox.Show(String.Format(Localization.getString("UpdateUpToDateFormat"), currentVersion), "WindowTabs", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore

    member this.startUpdate(release: UpdateChecker.ReleaseInfo) =
        let invoker = InvokerService.invoker
        let useMsi = UpdateChecker.isMsiInstall()
        match (if useMsi then release.msiUrl else release.zipUrl) with
        | None ->
            MessageBox.Show(Localization.getString("UpdateDownloadFailed"), "WindowTabs", MessageBoxButtons.OK, MessageBoxIcon.Warning) |> ignore
        | Some(url) ->
            this.icon.ShowBalloonTip(1000, "WindowTabs", Localization.getString("UpdateDownloading"), ToolTipIcon.Info)
            ThreadHelper.queueBackground <| fun() ->
                let downloaded = try Some(UpdateChecker.download url) with _ -> None
                invoker.asyncInvoke <| fun() ->
                    match downloaded with
                    | None ->
                        MessageBox.Show(Localization.getString("UpdateDownloadFailed"), "WindowTabs", MessageBoxButtons.OK, MessageBoxIcon.Warning) |> ignore
                    | Some(path) ->
                        if useMsi then UpdateChecker.installMsi path
                        else UpdateChecker.installZipAndExit path

    // Restart application using normal shutdown
    member this.restartApplication() =
        let exePath = Assembly.GetExecutingAssembly().Location
        // Start new process with a delay using cmd
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "cmd.exe"
        startInfo.Arguments <- sprintf "/c timeout /t 3 /nobreak >nul && start \"\" \"%s\"" exePath
        startInfo.WindowStyle <- ProcessWindowStyle.Hidden
        startInfo.CreateNoWindow <- true
        try
            Process.Start(startInfo) |> ignore
            Services.program.shutdown()
        with
        | ex -> MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore

    member this.getLanguageFolder() =
        let exePath = Assembly.GetExecutingAssembly().Location
        let exeDir = Path.GetDirectoryName(exePath)
        Path.Combine(exeDir, "Language")

    // Returns list of (displayName, fileName) tuples (supports JSONC format with comments)
    member this.getLanguageListFromFileList() : (string * string) list =
        try
            let fileListPath = Path.Combine(this.getLanguageFolder(), "FileList.json")
            if File.Exists(fileListPath) then
                let rawJson = File.ReadAllText(fileListPath)
                let arr = parseJsoncArray(rawJson)
                arr
                |> Seq.map (fun t ->
                    let obj = t :?> JObject
                    let name = obj.["name"].ToString()
                    let fileName = obj.["fileName"].ToString().Replace(".json", "")
                    (name, fileName))
                |> Seq.toList
            else
                // FileList.json not found - return empty list
                []
        with
        | _ -> []

    member this.createLanguageMenu() =
        // Load language list from FileList.json
        let languages = this.getLanguageListFromFileList()

        // If language list is empty, return None (hide Language menu)
        if languages.IsEmpty then
            None
        else
            let languageMenu = new MenuItem(Localization.getString("Language"))
            let currentLanguage = Localization.currentLanguage

            for (displayName, fileName) in languages do
                let langItem = new MenuItem(displayName)
                langItem.Checked <- (currentLanguage = fileName)
                langItem.Enabled <- not (currentLanguage = fileName)
                langItem.Tag <- box(fileName)  // Store fileName (without .json) in Tag for language switching
                langItem.Click.Add <| fun _ ->
                    try
                        let json = Services.settings.root
                        json.["language"] <- JToken.FromObject(fileName)
                        Services.settings.root <- json
                        Localization.setLanguage(fileName)
                        closeSettingsDialog()
                        // Theme-aware confirmation dialog (replaces the
                        // system MessageBox so it follows the dark-mode
                        // toggle). Title / message / OK button are
                        // intentionally kept in English even when the app is
                        // localized — if the user accidentally switches to
                        // a language they can't read, this dialog still
                        // tells them in English what just happened so they
                        // can navigate back and revert the language.
                        use form = new Form()
                        form.Text <- "Language Change"
                        form.FormBorderStyle <- FormBorderStyle.FixedDialog
                        form.MaximizeBox <- false
                        form.MinimizeBox <- false
                        form.StartPosition <- FormStartPosition.CenterScreen
                        form.TopMost <- true
                        form.ShowInTaskbar <- false
                        let label = new Label()
                        label.Text <- sprintf "Language has been changed to %s." displayName
                        label.Location <- System.Drawing.Point(30, 30)
                        label.AutoSize <- true
                        let okBtn = new Button()
                        okBtn.Text <- "OK"
                        okBtn.DialogResult <- DialogResult.OK
                        okBtn.Size <- System.Drawing.Size(80, 30)
                        form.Controls.Add(label)
                        form.Controls.Add(okBtn)
                        form.AcceptButton <- okBtn
                        form.CancelButton <- okBtn
                        form.Load.Add(fun _ ->
                            // Size the form around the label so multi-byte
                            // strings (Japanese / Chinese) fit comfortably.
                            let cw = max (label.Right + 30) 360
                            let ch = label.Bottom + 30 + okBtn.Height + 30
                            form.ClientSize <- System.Drawing.Size(cw, ch)
                            okBtn.Location <- System.Drawing.Point((cw - okBtn.Width) / 2, label.Bottom + 30))
                        let darkOn =
                            try
                                match Services.settings.root.getBool("EnableDarkMode") with
                                | Some(v) -> v
                                | None -> false
                            with _ -> false
                        if darkOn then
                            DarkMode.applyDarkColorsBeforeShow form
                            form.HandleCreated.Add(fun _ ->
                                try DarkMode.applyDarkThemeBranch15ToForm form true
                                with _ -> ())
                        form.ShowDialog() |> ignore
                    with
                    | ex -> MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                languageMenu.MenuItems.Add(langItem) |> ignore

            Some(languageMenu)

    interface IPlugin with
        member this.init() =
            let notifyIcon = this.icon
            let contextMenu = notifyIcon.ContextMenu

            // Create menu items
            // Non-clickable caption showing the running version
            let versionMenuItem = new MenuItem("version " + Services.program.version)
            versionMenuItem.Enabled <- false
            this.contextMenuItems.Add(versionMenuItem) |> ignore

            this.contextMenuItems.Add("-") |> ignore

            let settingsMenuItem = new MenuItem(Localization.getString("Settings"))
            settingsMenuItem.Click.Add <| fun _ -> Services.managerView.show()
            settingsMenuItem.Tag <- box("Settings")
            // Bold: matches the tray icon double-click default action
            settingsMenuItem.DefaultItem <- true
            this.contextMenuItems.Add(settingsMenuItem) |> ignore

            // Only add Language menu if FileList.json exists and is not empty
            match this.createLanguageMenu() with
            | Some(languageMenu) ->
                languageMenu.Tag <- box("Language")
                this.contextMenuItems.Add(languageMenu) |> ignore
            | None -> ()

            //this.addItem(Localization.getString("Feedback"), Forms.openFeedback) // 404 Not Found.
            this.contextMenuItems.Add("-") |> ignore

            let disableMenuItem = new MenuItem(Localization.getString("Disable"))
            disableMenuItem.Click.Add <| fun _ ->
                let newState = not Services.program.isDisabled
                Services.program.setDisabled(newState)
            disableMenuItem.Tag <- box("Disable")
            this.contextMenuItems.Add(disableMenuItem) |> ignore

            this.contextMenuItems.Add("-") |> ignore

            let updateMenuItem = new MenuItem(Localization.getString("CheckForUpdates"))
            updateMenuItem.Click.Add <| fun _ -> this.checkForUpdates()
            updateMenuItem.Tag <- box("CheckForUpdates")
            this.contextMenuItems.Add(updateMenuItem) |> ignore

            let restartMenuItem = new MenuItem(Localization.getString("RestartWindowTabs"))
            restartMenuItem.Click.Add <| fun _ -> this.restartApplication()
            restartMenuItem.Tag <- box("RestartWindowTabs")
            this.contextMenuItems.Add(restartMenuItem) |> ignore

            let closeMenuItem = new MenuItem(Localization.getString("CloseWindowTabs"))
            closeMenuItem.Click.Add <| fun _ -> Services.program.shutdown()
            closeMenuItem.Tag <- box("CloseWindowTabs")
            this.contextMenuItems.Add(closeMenuItem) |> ignore

            Services.program.newVersion.Add this.onNewVersion

            // Start watchdog to detect UI freeze and auto-restart
            Watchdog.start()

    interface IDisposable with
        member this.Dispose() =
            Watchdog.stop()
            this.icon.Dispose()
