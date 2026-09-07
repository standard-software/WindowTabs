namespace Bemo
open System

// Opening another window of a tab's program - the tab menu's "New tab"
// items and the "add a tab to the right of the active tab" hot key.
//
// One function for both, so a Store application, Windows Terminal and a
// launch that fails are handled the same way whichever way the user asked:
// LaunchPath decides what to start, and what cannot be started is reported
// in a dialog rather than dropped. Moved here from the tab menu code so the
// hot key could share it instead of copying it.
module NewWindowLaunch =

    /// Start `processPath`'s program again through `launch`, which gets the
    /// resolved path (see LaunchPath.resolve) and does the actual start.
    /// Every way that can fail ends in a dialog and a Debug line.
    let start (processPath: string) (launch: string -> unit) =
        let showUwpError () =
            let appName = IO.Path.GetFileNameWithoutExtension(processPath)
            let message = String.Format(Localization.getString("NewLaunchErrorUWP"), appName)
            AppDialog.info "WindowTabs" message
        try
            match LaunchPath.resolve processPath with
            | Some(path) -> launch path
            | None -> showUwpError ()
        with
        | :? System.ComponentModel.Win32Exception as ex when LaunchPath.isStoreApp processPath ->
            showUwpError ()
            Diagnostics.Debug.WriteLine(sprintf "UWP app cannot be launched: %s - %s" processPath ex.Message)
        | :? System.ComponentModel.Win32Exception as ex ->
            let message = String.Format(Localization.getString("NewLaunchErrorProcess"), processPath, ex.Message)
            AppDialog.info "WindowTabs Error" message
            Diagnostics.Debug.WriteLine(sprintf "Error starting process: %s - %s" processPath ex.Message)
        | ex ->
            let message = String.Format(Localization.getString("NewLaunchErrorUnexpected"), ex.Message)
            AppDialog.info "WindowTabs Error" message
            Diagnostics.Debug.WriteLine(sprintf "Unexpected error starting process: %s - %s" processPath ex.Message)
