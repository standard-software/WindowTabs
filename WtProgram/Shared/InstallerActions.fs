namespace Bemo

open System
open System.IO
open System.Security.Cryptography
open Microsoft.Win32
open Newtonsoft.Json
open Newtonsoft.Json.Linq

// The installer's file-maintenance steps, invoked by WtSetup.msi as
//
//     WindowTabs.exe --installer-backup-modified
//     WindowTabs.exe --installer-store-hashes
//
// Both used to be PowerShell one-liners carried inside the MSI as
// "powershell.exe -NoProfile -EncodedCommand <base64>" custom actions.
// Base64-obfuscated PowerShell that reads the registry, walks a directory
// tree, hashes files, copies and deletes them is, to a malware classifier,
// indistinguishable from a dropper: users started reporting the installer as
// Trojan:Script/Wacatac.H!ml, a machine-learning verdict rather than a
// signature match. The work is unchanged - doing it here means the MSI
// carries no script for a classifier to judge.
//
// The stored format is deliberately identical to what the PowerShell wrote,
// so an installation upgraded from an older version still matches its own
// recorded hashes and does not back up every file as if it had been edited.
module InstallerActions =

    let private registryKey = @"Software\WindowTabs"

    // The two folders holding files a user may legitimately edit in place.
    let private stateFolders = [ "Language"; "Settings" ]

    // Where the running copy was installed. Absent when WindowTabs has never
    // been installed by the MSI, which is also the case during the very first
    // install - there is nothing to preserve then, and every step is a no-op.
    let private installPath () =
        use key = Registry.CurrentUser.OpenSubKey(registryKey)
        if isNull key then None else
        match key.GetValue("InstallPath") with
        | :? string as p when p <> "" && Directory.Exists(p) -> Some(p)
        | _ -> None

    // "Language\English.json" - the path relative to the install folder, which
    // is the name a hash is recorded under. [INSTALLFOLDER] carries a trailing
    // separator and a hand-written registry value may not, so both are trimmed.
    let private relativeName (root: string) (full: string) =
        full.Substring(root.Length).TrimStart('\\')

    let private hashOf (sha: SHA256) (path: string) =
        BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "")

    let private stateFiles (root: string) =
        stateFolders
        |> List.collect (fun folder ->
            let dir = Path.Combine(root, folder)
            if Directory.Exists(dir)
            then Directory.GetFiles(dir, "*", SearchOption.AllDirectories) |> List.ofArray
            else [])

    let private recordedHashes () =
        use key = Registry.CurrentUser.OpenSubKey(registryKey)
        if isNull key then None else
        match key.GetValue("FileHashes") with
        | :? string as json when json <> "" ->
            try Some(JObject.Parse(json)) with _ -> None
        | _ -> None

    // Copy aside every Language/Settings file the user has edited, so that the
    // install can lay down fresh defaults over the top without losing the
    // edits. A file counts as edited when its hash differs from the one
    // recorded at the end of the previous install - and a file with no
    // recorded hash counts as edited too, because nothing proves it was ours.
    //
    // Runs from the OUTGOING executable, before the install starts. Nothing is
    // deleted here; the backup is the only side effect.
    let backupModifiedFiles () =
        match installPath () with
        | None -> ()
        | Some(root) ->
            let recorded = recordedHashes ()
            let backupDir =
                Path.Combine(root, "Backup_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"))
            use sha = SHA256.Create()
            for file in stateFiles root do
                let name = relativeName root file
                let unchanged =
                    match recorded with
                    | Some(o) ->
                        match o.[name] with
                        | null -> false
                        | token -> string token = hashOf sha file
                    | None -> false
                if not unchanged then
                    let target = Path.Combine(backupDir, name)
                    Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
                    File.Copy(file, target, true)

    // Record what the install just wrote, so the next upgrade can tell an
    // untouched default from a file the user has edited. Runs from the
    // INCOMING executable, after the install has finished.
    let storeFileHashes () =
        match installPath () with
        | None -> ()
        | Some(root) ->
            let hashes = JObject()
            use sha = SHA256.Create()
            for file in stateFiles root do
                hashes.[relativeName root file] <- JValue(hashOf sha file)
            use key = Registry.CurrentUser.CreateSubKey(registryKey)
            key.SetValue("FileHashes", hashes.ToString(Formatting.None), RegistryValueKind.String)

    // Returns true when the arguments were an installer step, in which case the
    // process has done its work and must exit without starting the
    // application. Every failure is swallowed: these steps are conveniences,
    // and an installer must not stop because one of them could not run.
    let tryRun (argv: string[]) =
        let step =
            argv
            |> Array.tryPick (fun a ->
                match a.ToLowerInvariant() with
                | "--installer-backup-modified" -> Some(backupModifiedFiles)
                | "--installer-store-hashes" -> Some(storeFileHashes)
                | _ -> None)
        match step with
        | None -> false
        | Some(run) ->
            (try run () with _ -> ())
            true
