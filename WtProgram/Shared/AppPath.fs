namespace Bemo

open System
open System.Collections.Generic

// Naming an application in a way that survives its updates.
//
// Per-application settings - tabbing on/off, auto grouping, categories - are
// stored against the executable's path. That works for an ordinary
// installation, where the path outlives every update. It does not work when
// the path carries the version, because every update then files the
// application under a new name: the settings, still filed under yesterday's
// path, stop applying. To the user the settings have simply vanished, and
// setting them again files a second entry that the next update orphans in
// turn.
//
// Two shapes do this, and both were found in one user's settings, where half
// of all stored paths were dead. They are treated differently, because one is
// a rule of the platform and the other is a habit of particular installers.
//
//   Store (MSIX) applications ALWAYS live in
//       C:\Program Files\WindowsApps\<Name>_<Version>_<Arch>__<PublisherId>\...
//   Windows decides that layout, not the application, so the version and the
//   architecture are always dropped and the name and publisher always kept.
//   Sixteen generations of one application had accumulated under it.
//
//   Other installers put the version in a directory of its own, as LINE does
//   for its media player plugin:
//       ...\LINE\Data\plugin\LineMediaPlayer\1.2.0.635\LineMediaPlayer.exe
//   Five generations of that had accumulated. Nothing marks such a directory
//   as a version except that it is made of digits and dots, and that guess is
//   wrong often enough to matter: "C:\Program Files\Microsoft Visual
//   Studio\18\..." is the same shape, and somebody who keeps two versions of
//   Visual Studio installed wants them told apart. So this rule is applied
//   only to executables named in Settings\Version_Folder.json.
//
// The part that moves is replaced by "*", and the pattern is what gets
// stored:
//
//     C:\Program Files\WindowsApps\Claude_*_*__pzs8sxrjxfjjc\app\claude.exe
//     ...\LINE\Data\plugin\LineMediaPlayer\*\LineMediaPlayer.exe
//
// Storing the pattern rather than one of the real paths means there is never
// a question of which generation is the current one, and so no rule for
// choosing between them. It also reads for itself in the settings file. The
// rest of the path is kept, so two executables in one package are still two
// applications, and Path.GetFileName still gives the name the Programs tab
// shows. ("*" is not in Path.GetInvalidPathChars, and the only path function
// that rejects it is GetFullPath, which is not used on these strings.)
module AppPath =

    [<Literal>]
    let private windowsApps = @"\windowsapps\"

    [<Literal>]
    let private wildcard = "*"

    // Executables whose installer keeps each version in a directory of its
    // own. Empty until the settings file is read, so an unconfigured build
    // touches nobody's paths but the Store ones. Replaced wholesale rather
    // than mutated: normalize is called from more than one thread.
    let mutable private versionDirectoryApps : Set<string> = Set.empty

    let setVersionDirectoryApps (exeNames: string seq) =
        versionDirectoryApps <-
            exeNames
            |> Seq.filter (String.IsNullOrWhiteSpace >> not)
            |> Seq.map (fun n -> n.Trim().ToLowerInvariant())
            |> Set.ofSeq

    let versionDirectoryAppNames () = versionDirectoryApps

    // Rewrite the package folder of a Store path, leaving anything else as it
    // came in. Split from the right: a package name may contain dots and
    // digits, but not an underscore, so the last two underscore-separated
    // fields before "__" are always version and architecture.
    //
    // Resource packages ("...neutral_split.scale-100_8wekyb3d8bbwe") carry no
    // executable and have no "__" - they fall out here and are treated as
    // ordinary paths, which costs nothing.
    let private canonicalPackageFolder (lowered: string) =
        let rewriteFolder (folder: string) =
            match folder.LastIndexOf("__") with
            | -1 -> None
            | sep ->
                let publisher = folder.Substring(sep + 2)
                let fields = folder.Substring(0, sep).Split('_')
                if publisher = "" || fields.Length < 3 then None else
                let name = String.Join("_", fields.[.. fields.Length - 3])
                let version = fields.[fields.Length - 2]
                let arch = fields.[fields.Length - 1]
                // Already a pattern, or a real version. Anything else is a
                // folder that only looks like a package and is left alone.
                let versionOk =
                    version = wildcard
                    || (version <> "" && version |> Seq.forall (fun c -> Char.IsDigit(c) || c = '.'))
                if name = "" || arch = "" || not versionOk then None
                else Some(sprintf "%s_%s_%s__%s" name wildcard wildcard publisher)
        match lowered.IndexOf(windowsApps) with
        | -1 -> lowered
        | marker ->
            let folderStart = marker + windowsApps.Length
            let rest = lowered.Substring(folderStart)
            match rest.IndexOf('\\') with
            | -1 -> lowered
            | slash ->
                match rewriteFolder (rest.Substring(0, slash)) with
                | None -> lowered
                | Some(folder) -> lowered.Substring(0, folderStart) + folder + rest.Substring(slash)

    // A directory named only with digits and dots is a version. "." and ".."
    // are not: a segment has to hold a digit to count.
    let private isVersionSegment (segment: string) =
        segment <> ""
        && segment |> Seq.exists Char.IsDigit
        && segment |> Seq.forall (fun c -> Char.IsDigit(c) || c = '.')

    // The last segment is the file name itself and is never touched, so an
    // executable actually called "1.2.exe" keeps its identity - and it is
    // also what decides whether this rule applies at all.
    let private stripVersionSegments (path: string) =
        let segments = path.Split('\\')
        if segments.Length < 2 then path else
        let exeName = segments.[segments.Length - 1]
        if not (versionDirectoryApps.Contains(exeName)) then path else
        String.Join("\\",
            segments |> Array.mapi (fun i s ->
                if i < segments.Length - 1 && isVersionSegment s then wildcard else s))

    // The name to file an application under, and to compare two paths by.
    // Applying it to a pattern gives the same pattern back, so a stored
    // setting and a live process path can be compared with one another
    // whichever they are.
    //
    // Lower-cased throughout: Windows paths are case-insensitive, and the
    // stored settings were written in whatever case the reporting process
    // happened to use.
    let normalize (path: string) =
        if String.IsNullOrEmpty(path) then ""
        else path.ToLowerInvariant() |> canonicalPackageFolder |> stripVersionSegments

    let sameApp (a: string) (b: string) = normalize a = normalize b

    // True when the path carries a version that its next update will move.
    let isVersioned (path: string) =
        if String.IsNullOrEmpty(path) then false else normalize path <> path.ToLowerInvariant()

    // ----- collections of paths -----
    //
    // The settings hold lists of paths. Everything below asks "is this
    // application in the list", not "is this string in the list", and stores
    // the pattern rather than the path it was given.

    let containsApp (paths: string seq) (path: string) =
        let wanted = normalize path
        paths |> Seq.exists (fun p -> normalize p = wanted)

    let removeApp (paths: string seq) (path: string) =
        let wanted = normalize path
        paths |> Seq.filter (fun p -> normalize p <> wanted) |> List.ofSeq

    let addApp (paths: string seq) (path: string) =
        removeApp paths path @ [ normalize path ]

    // Rewrite a stored list into patterns, dropping the entries that then say
    // the same thing. Settings written before this module existed hold one
    // entry per generation; this is what collapses them. Order is preserved,
    // so a settings file is not shuffled on every load.
    let canonicalise (paths: string seq) =
        let seen = HashSet<string>()
        [ for p in paths do
            let pattern = normalize p
            if seen.Add(pattern) then yield pattern ]
