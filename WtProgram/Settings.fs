namespace Bemo
open System
open System.Drawing
open System.Collections.Generic
open System.IO
open Microsoft.FSharp.Reflection
open Newtonsoft.Json
open Newtonsoft.Json.Linq

// Compute the per-channel midpoint of two RGB colors. Used to derive a
// "Selected" color from the existing Inactive / MouseOver pair when a
// preset or saved theme does not specify one explicitly.
module ColorMix =
    let midpoint (c1: Color) (c2: Color) =
        Color.FromArgb(
            (int c1.R + int c2.R) / 2,
            (int c1.G + int c2.G) / 2,
            (int c1.B + int c2.B) / 2)

type Settings(isStandAlone) as this =
    let mutable cachedSettingsString = None
    let mutable cachedSettingsRec = None
    let mutable hasExistingSettings = false
    // One safety copy of the settings file per process, taken the first time
    // this process overwrites it. On 2026-08-24 a freshly started build wrote
    // pure defaults over the user's settings; whatever causes such a write,
    // the pre-launch content must stay recoverable. Auto backups follow the
    // pattern <file>.bak.<stamp> and only the 10 newest are kept.
    let mutable backedUpThisProcess = false
    // Latched when a read fails or a parse falls back to empty settings.
    // While it is set every value the app holds is a default that came from
    // nowhere, and saving from that state is exactly how the settings file
    // gets wiped - so writes are refused until a read parses cleanly again.
    let mutable settingsUntrusted = false
    // The backup scan below runs at most once per process: a settings file
    // that no backup can replace would otherwise rescan the whole directory
    // on every settings access.
    let mutable recoveryAttempted = false
    // One log line per kind of failure. Without the record cache to absorb
    // them (see the settings getter) a fallback is reached on every settings
    // access, and a log that repeats itself thousands of times buries the
    // first occurrence - the only one that still explains the incident.
    let loggedFallbacks = HashSet<string>()
    let settingChangedEvent = Event<string* obj>()
    let valueCache = Dictionary<string, obj>()

    do
        hasExistingSettings <- this.fileExists
        Services.register(this :> ISettings)

    member this.clearCaches() =
        cachedSettingsString <- None
        cachedSettingsRec <- None
        valueCache.Clear()

    member this.path =
        let path = 
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs")
        Path.Combine(path, "WindowTabsSettings.txt")
    // // debug: use local settings file
    // member this.path =
    //     let path = 
    //         if isStandAlone then "."
    //         else Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs")
    //     Path.Combine(path, "WindowTabsSettings.txt")

    member this.fileExists : bool = File.Exists(this.path) 

    member this.settingsString
        with get() = 
            if cachedSettingsString.IsNone then
                cachedSettingsString <-
                    try
                        // Short retry on sharing violations: during a tray
                        // restart the old process can still be inside its
                        // final File.Replace when the new one reads, and one
                        // failed read here is all it takes for the new
                        // process to come up with defaults.
                        let rec readRetry attempts =
                            try File.ReadAllText(this.path)
                            with :? IOException when attempts > 0 ->
                                Threading.Thread.Sleep(100)
                                readRetry (attempts - 1)
                        if this.fileExists then Some(readRetry 5) else None
                    with
                    | ex ->
                        // A read failure here is how a later save comes to
                        // write DEFAULTS over the user's real settings. What
                        // prevents that is the latch below, not the record:
                        // no write is allowed until a read succeeds. The
                        // record is for the machine where the fault is being
                        // looked into, which runs a debug build.
                        settingsUntrusted <- true
#if DEBUG
                        (try
                            File.AppendAllText(this.path + ".read_error.log",
                                sprintf "%s read failed: %O" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) ex + Environment.NewLine)
                         with _ -> ())
#endif
                        None
            cachedSettingsString

        and set(newSettings : string option) =
            try
                let newContent = newSettings.Value
                // Dirty check: skip when the on-disk content (mirrored in cache) is identical.
                // Periodic save fires every 10s; this avoids the ~7 ms disk write when nothing changed.
                match cachedSettingsString with
                | Some(prev) when prev = newContent -> ()
                | _ ->
                    let settingsDir = Path.GetDirectoryName(this.path)
                    if Directory.Exists(settingsDir).not then
                        Directory.CreateDirectory(settingsDir).ignore
                    // Atomic write: write the new content to a sibling temp file,
                    // then move it over the target. Survives force-quit mid-write
                    // without corrupting the existing settings file.
                    // Before overwriting an existing file: keep a timestamped copy the
                    // first time this process writes, and EVERY time the new
                    // content is under a third of what is on disk - a shrink
                    // of that size is how both settings wipes looked
                    // (2026-08-24 / 08-25), and the per-process-only backup
                    // missed the second one because the long-running process
                    // had already used up its single backup.
                    let existingLen =
                        if File.Exists(this.path) then (try (FileInfo(this.path)).Length with _ -> 0L) else 0L
                    if existingLen > 0L && (not backedUpThisProcess || int64 newContent.Length * 3L < existingLen) then
                        backedUpThisProcess <- true
                        try
                            File.Copy(this.path, this.path + ".bak." + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true)
                            let old =
                                Directory.GetFiles(settingsDir, Path.GetFileName(this.path) + ".bak.*")
                                |> Array.sortDescending
                            if old.Length > 10 then
                                old.[10..] |> Array.iter (fun f -> try File.Delete(f) with _ -> ())
                        with _ -> ()
                    // Two ways a write is refused, both meaning "this content
                    // was not derived from the user's real settings":
                    //  - the last read failed or fell back to empty settings.
                    //    On 2026-08-26 a saved value holding a URL broke the
                    //    JSONC comment strip, and the empty state that came
                    //    back was saved over 39 KB of settings.
                    //  - the content shrank to a third of the file on disk
                    //    AND carries no Version. Every legitimate save stamps
                    //    the running build into Version, so an empty one
                    //    (08-24 / 08-25) or a missing key (08-26) marks a
                    //    state that never saw the user's file.
                    // A refused write leaves the file its last good content;
                    // in a debug build it is also recorded. The in-memory caches are left alone too,
                    // so a healthy state formed later can still save normally.
                    // The marker follows how JObject.ToString() formats the
                    // settings - two-space indent, ": " between key and value.
                    // Should that ever change, the key stops being found and
                    // every sharp shrink is refused instead of allowed: an
                    // inconvenience, but on the side that keeps the file.
                    let carriesVersion =
                        let marker = "\"Version\": \""
                        let i = newContent.IndexOf(marker)
                        i >= 0 && i + marker.Length < newContent.Length &&
                        newContent.[i + marker.Length] <> '"'
                    let refusedReason =
                        if settingsUntrusted then
                            Some("settings had been read as empty")
                        elif existingLen > 0L &&
                             int64 newContent.Length * 3L < existingLen &&
                             carriesVersion.not then
                            Some("shrank to a third with no Version")
                        else None
                    let looksLikeWipe = refusedReason.IsSome
                    // DEBUG builds only: trace every write with the caller
                    // stack, so the next wipe-like incident names the code
                    // path that produced it. Not compiled into Release - it
                    // grows with every settings change, and a release build
                    // writes no log at all.
                    // At the cap the previous file is kept as .1, the way the
                    // other traces are: this is the only record of how a wipe
                    // came about, it fills in a few days of ordinary use, and
                    // deleting it outright threw away the whole history
                    // rather than the oldest of it.
#if DEBUG
                    try
                        let tracePath = this.path + ".write_trace.log"
                        if File.Exists(tracePath) && (FileInfo(tracePath)).Length > 5_000_000L then
                            let previous = tracePath + ".1"
                            (try File.Delete(previous) with _ -> ())
                            (try File.Move(tracePath, previous) with _ -> ())
                        File.AppendAllText(tracePath,
                            sprintf "%s %s %d bytes (disk had %d)%s%s%s"
                                (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                                (match refusedReason with
                                 | Some(reason) -> sprintf "REFUSED (%s) write of" reason
                                 | None -> "write")
                                newContent.Length existingLen
                                Environment.NewLine Environment.StackTrace (Environment.NewLine + Environment.NewLine))
                    with _ -> ()
#endif
#if DEBUG
                    if looksLikeWipe then
                        (try
                            File.AppendAllText(this.path + ".read_error.log",
                                sprintf "%s REFUSED settings write (%s: %d bytes over %d on disk)" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")) (refusedReason.def "") newContent.Length existingLen + Environment.NewLine)
                         with _ -> ())
#endif
                    // The refusal itself is what protects the file, and it
                    // happens in either build; only the note of it is written
                    // where someone is going to read it.
                    if looksLikeWipe.not then
                        let tempPath = this.path + ".tmp"
                        File.WriteAllText(tempPath, newContent)
                        if File.Exists(this.path) then
                            File.Replace(tempPath, this.path, null)
                        else
                            File.Move(tempPath, this.path)
                        // Refresh in-memory caches with the freshly written content
                        cachedSettingsString <- Some(newContent)
                        cachedSettingsRec <- None
                        valueCache.Clear()
            with
            | ex ->
                // Log error but don't crash
                System.Diagnostics.Debug.WriteLine(sprintf "Failed to save settings: %s" ex.Message)
            
    // Falling back to an empty JObject is what turns a single swallowed
    // exception into "the settings are the defaults" further up - the wipe
    // signature seen on 08-24 / 08-25. The fallbacks stay (crashing here
    // would be worse); what stops them reaching the file is the latch, which
    // is set in either build.
    member private this.logEmptyFallback (where: string) (ex: exn) =
        settingsUntrusted <- true
#if DEBUG
        if loggedFallbacks.Add(where) then
            try
                File.AppendAllText(this.path + ".read_error.log",
                    sprintf "%s %s fell back to empty settings - no setting is saved until a backup is adopted or WindowTabs is restarted: %O" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")) where ex + Environment.NewLine)
            with _ -> ()
#else
        ignore where
        ignore ex
#endif

    // A settings file that no longer parses does not have to end as "the
    // settings are the defaults". The backups taken before every sharp shrink
    // hold the same settings from minutes earlier, so the newest one that
    // parses AND carries a non-empty Version is adopted - the Version test is
    // what skips a backup written from an already-decayed state, the way
    // .bak.20260826_181345 was. The file that could not be read is kept as
    // <file>.corrupt.<stamp> instead of being overwritten.
    member private this.tryRecoverFromBackup() : JObject option =
        if recoveryAttempted then None
        else
            recoveryAttempted <- true
            try
                let dir = Path.GetDirectoryName(this.path)
                let name = Path.GetFileName(this.path)
                Directory.GetFiles(dir, name + ".bak.*")
                |> Array.sortDescending
                |> Array.tryPick (fun backup ->
                    try
                        let text = File.ReadAllText(backup)
                        let parsed = parseJsoncObject(text)
                        match parsed.getString("Version") with
                        | Some(version) when version <> "" ->
                            let stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")
                            let kept = this.path + ".corrupt." + stamp
                            if File.Exists(this.path) then File.Move(this.path, kept)
                            File.Copy(backup, this.path, true)
                            // File and derived state are replaced together, so
                            // nothing built while the settings were unreadable
                            // survives into the recovered process.
                            cachedSettingsString <- Some(text)
                            cachedSettingsRec <- None
                            valueCache.Clear()
                            settingsUntrusted <- false
#if DEBUG
                            (try
                                File.AppendAllText(this.path + ".read_error.log",
                                    sprintf "%s RECOVERED the settings from %s (%d bytes, Version %s); the unreadable file is kept as %s"
                                        (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                                        (Path.GetFileName(backup)) text.Length version (Path.GetFileName(kept))
                                    + Environment.NewLine)
                             with _ -> ())
#endif
                            Some(parsed)
                        | _ -> None
                    with _ -> None)
            with _ -> None

    member this.settingsJson
        with get() =
            try
                match this.settingsString with
                | Some(s) ->
                    try
                        parseJsoncObject(s)
                    with
                    | ex ->
                        // A later parse succeeding is NOT enough to trust this
                        // process again: by then a record made of pure defaults
                        // may already have been built and handed out, and
                        // saving it would write those defaults over a file that
                        // reads perfectly well. Only a recovery, which swaps
                        // the file and every cache together, lifts the latch.
                        this.logEmptyFallback "settingsJson parse" ex
                        this.tryRecoverFromBackup().def(JObject())
                | None ->
                    // Nothing to parse: either there is no settings file yet -
                    // a first run, with nothing to protect - or the read failed
                    // and latched, and a backup may still hold what the file
                    // will not give up.
                    if this.fileExists then this.tryRecoverFromBackup().def(JObject())
                    else JObject()
            with
            | ex ->
                this.logEmptyFallback "settingsJson outer" ex
                JObject()  // Return empty JObject if any error occurs
        and set(settingsJson:JObject) = this.settingsString <- Some(settingsJson.ToString())

    member this.defaultTabAppearance =
        let inactiveTab = Color.FromRGB(0x9FC4F0)
        let mouseOverTab = Color.FromRGB(0xBDD5F4)
        {
            tabHeight = 25
            tabMaxWidth = 200
            tabPinnedTabWidth = 90
            tabPinnedTabWidthIcon = true
            tabOverlap = 20
            tabHeightOffset = 1
            tabIndentFlipped = 150
            tabIndentNormal = 4
            tabInactiveTextColor = Color.FromRGB(0x000000)
            tabSelectedTextColor = Color.FromRGB(0x000000)
            tabMouseOverTextColor = Color.FromRGB(0x000000)
            tabActiveTextColor = Color.FromRGB(0x000000)
            tabFlashTextColor = Color.FromRGB(0x000000)
            tabInactiveTabColor = inactiveTab
            tabSelectedTabColor = ColorMix.midpoint inactiveTab mouseOverTab
            tabMouseOverTabColor = mouseOverTab
            tabActiveTabColor = Color.FromRGB(0xFAFCFE)
            tabFlashTabColor = Color.FromRGB(0xFFBBBB)
            tabInactiveBorderColor = Color.FromRGB(0x3A70B1)
            tabSelectedBorderColor = Color.FromRGB(0x3A70B1)
            tabMouseOverBorderColor = Color.FromRGB(0x3A70B1)
            tabActiveBorderColor = Color.FromRGB(0x3A70B1)
            tabFlashBorderColor = Color.FromRGB(0x3A70B1)
        }

    member this.darkModeTabAppearance =
        let inactiveTab = Color.FromRGB(0x0D0D0D)
        let mouseOverTab = Color.FromRGB(0x1E1E1E)
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabPinnedTabWidthIcon = false
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xFFFFFF)
            tabSelectedTextColor = Color.FromRGB(0xFFFFFF)
            tabMouseOverTextColor = Color.FromRGB(0xFFFFFF)
            tabActiveTextColor = Color.FromRGB(0xFFFFFF)
            tabFlashTextColor = Color.FromRGB(0xFFFFFF)
            tabInactiveTabColor = inactiveTab
            tabSelectedTabColor = ColorMix.midpoint inactiveTab mouseOverTab
            tabMouseOverTabColor = mouseOverTab
            tabActiveTabColor = Color.FromRGB(0x2D2D2D)
            tabFlashTabColor = Color.FromRGB(0x772222)
            tabInactiveBorderColor = Color.FromRGB(0x333333)
            tabSelectedBorderColor = Color.FromRGB(0x333333)
            tabMouseOverBorderColor = Color.FromRGB(0x333333)
            tabActiveBorderColor = Color.FromRGB(0x333333)
            tabFlashBorderColor = Color.FromRGB(0x333333)
        }

    member this.darkModeBlueTabAppearance =
        let inactiveTab = Color.FromRGB(0x111827)
        let mouseOverTab = Color.FromRGB(0x4B5970)
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabPinnedTabWidthIcon = false
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xE0E0E0)
            tabSelectedTextColor = Color.FromRGB(0xE0E0E0)
            tabMouseOverTextColor = Color.FromRGB(0xE0E0E0)
            tabActiveTextColor = Color.FromRGB(0xE0E0E0)
            tabFlashTextColor = Color.FromRGB(0xE0E0E0)
            tabInactiveTabColor = inactiveTab
            tabSelectedTabColor = ColorMix.midpoint inactiveTab mouseOverTab
            tabMouseOverTabColor = mouseOverTab
            tabActiveTabColor = Color.FromRGB(0x273548)
            tabFlashTabColor = Color.FromRGB(0x991B1B)
            tabInactiveBorderColor = Color.FromRGB(0x374151)
            tabSelectedBorderColor = Color.FromRGB(0x374151)
            tabMouseOverBorderColor = Color.FromRGB(0x374151)
            tabActiveBorderColor = Color.FromRGB(0x374151)
            tabFlashBorderColor = Color.FromRGB(0x374151)
        }

    member this.lightMonoTabAppearance =
        let inactiveTab = Color.FromRGB(0xA0A0A0)
        let mouseOverTab = Color.FromRGB(0xD0D0D0)
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabPinnedTabWidthIcon = false
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0x000000)
            tabSelectedTextColor = Color.FromRGB(0x000000)
            tabMouseOverTextColor = Color.FromRGB(0x000000)
            tabActiveTextColor = Color.FromRGB(0x000000)
            tabFlashTextColor = Color.FromRGB(0x000000)
            tabInactiveTabColor = inactiveTab
            tabSelectedTabColor = ColorMix.midpoint inactiveTab mouseOverTab
            tabMouseOverTabColor = mouseOverTab
            tabActiveTabColor = Color.FromRGB(0xFFFFFF)
            tabFlashTabColor = Color.FromRGB(0xD4D4D4)
            tabInactiveBorderColor = Color.FromRGB(0x252525)
            tabSelectedBorderColor = Color.FromRGB(0x252525)
            tabMouseOverBorderColor = Color.FromRGB(0x252525)
            tabActiveBorderColor = Color.FromRGB(0x252525)
            tabFlashBorderColor = Color.FromRGB(0x252525)
        }

    member this.darkMonoTabAppearance =
        let inactiveTab = Color.FromRGB(0x0D0D0D)
        let mouseOverTab = Color.FromRGB(0xDDDDDD)
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabPinnedTabWidthIcon = false
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xFFFFFF)
            tabSelectedTextColor = Color.FromRGB(0xFFFFFF)
            tabMouseOverTextColor = Color.FromRGB(0x111111)
            tabActiveTextColor = Color.FromRGB(0xFFFFFF)
            tabFlashTextColor = Color.FromRGB(0xFFFFFF)
            tabInactiveTabColor = inactiveTab
            tabSelectedTabColor = ColorMix.midpoint inactiveTab mouseOverTab
            tabMouseOverTabColor = mouseOverTab
            tabActiveTabColor = Color.FromRGB(0x616161)
            tabFlashTabColor = Color.FromRGB(0x808080)
            tabInactiveBorderColor = Color.FromRGB(0x787878)
            tabSelectedBorderColor = Color.FromRGB(0x787878)
            tabMouseOverBorderColor = Color.FromRGB(0xF2F2F2)
            tabActiveBorderColor = Color.FromRGB(0x6B6B6B)
            tabFlashBorderColor = Color.FromRGB(0x787878)
        }

    member this.darkMono2TabAppearance =
        let inactiveTab = Color.FromRGB(0x0D0D0D)
        let mouseOverTab = Color.FromRGB(0x919191)
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabPinnedTabWidthIcon = false
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xFFFFFF)
            tabSelectedTextColor = Color.FromRGB(0xFFFFFF)
            tabMouseOverTextColor = Color.FromRGB(0xFFFFFF)
            tabActiveTextColor = Color.FromRGB(0x111111)
            tabFlashTextColor = Color.FromRGB(0xFFFFFF)
            tabInactiveTabColor = inactiveTab
            tabSelectedTabColor = ColorMix.midpoint inactiveTab mouseOverTab
            tabMouseOverTabColor = mouseOverTab
            tabActiveTabColor = Color.FromRGB(0xDDDDDD)
            tabFlashTabColor = Color.FromRGB(0x2C1818)
            tabInactiveBorderColor = Color.FromRGB(0x787878)
            tabSelectedBorderColor = Color.FromRGB(0x787878)
            tabMouseOverBorderColor = Color.FromRGB(0x6B6B6B)
            tabActiveBorderColor = Color.FromRGB(0xF2F2F2)
            tabFlashBorderColor = Color.FromRGB(0x787878)
        }

    member this.darkRedFrameTabAppearance =
        let inactiveTab = Color.FromRGB(0x0D0D0D)
        let mouseOverTab = Color.FromRGB(0xB13A3A)
        {
            tabHeight = -1
            tabMaxWidth = -1
            tabPinnedTabWidth = -1
            tabPinnedTabWidthIcon = false
            tabOverlap = -1
            tabHeightOffset = -1
            tabIndentFlipped = -1
            tabIndentNormal = -1
            tabInactiveTextColor = Color.FromRGB(0xFFFFFF)
            tabSelectedTextColor = Color.FromRGB(0xFFFFFF)
            tabMouseOverTextColor = Color.FromRGB(0x111111)
            tabActiveTextColor = Color.FromRGB(0xB13A3A)
            tabFlashTextColor = Color.FromRGB(0xFFFFFF)
            tabInactiveTabColor = inactiveTab
            tabSelectedTabColor = ColorMix.midpoint inactiveTab mouseOverTab
            tabMouseOverTabColor = mouseOverTab
            tabActiveTabColor = Color.FromRGB(0x250A0B)
            tabFlashTabColor = Color.FromRGB(0x808080)
            tabInactiveBorderColor = Color.FromRGB(0xB13A3A)
            tabSelectedBorderColor = Color.FromRGB(0xB13A3A)
            tabMouseOverBorderColor = Color.FromRGB(0xFF6666)
            tabActiveBorderColor = Color.FromRGB(0xCC4444)
            tabFlashBorderColor = Color.FromRGB(0xB13A3A)
        }

    member this.update f = this.settings <- f(this.settings)

    member x.settings
        with get() =
            if cachedSettingsRec.IsNone then 
                try
                    let settingsJson = this.settingsJson
                    let settings = {
                        includedPaths = Set2(settingsJson.getStringArray("IncludedPaths").def(List2()))
                        excludedPaths = Set2(settingsJson.getStringArray("ExcludedPaths").def(List2()))
                        autoGroupingPaths = Set2(settingsJson.getStringArray("AutoGroupingPaths").def(List2()))
                        licenseKey = settingsJson.getString("LicenseKey").def("")
                        ticket = settingsJson.getString("Ticket")
                        runAtStartup = settingsJson.getBool("RunAtStartup").def(hasExistingSettings.not)
                        hideInactiveTabs = settingsJson.getBool("HideInactiveTabs").def(false)
                        enableTabbingByDefault = settingsJson.getBool("EnableTabbingByDefault").def(hasExistingSettings.not)
                        enableCtrlNumberHotKey = settingsJson.getBool("EnableCtrlNumberHotKey").def(false)
                        enableHoverActivate = settingsJson.getBool("EnableHoverActivate").def(false)
                        tabPositionByDefault =
                            // Handle backward compatibility: convert old format to new TopXxx format
                            // Note: "center"/"TopCenter" is deprecated and falls back to "TopLeft"
                            match settingsJson.getString("TabPositionByDefault") with
                            | Some("left") -> "TopLeft"
                            | Some("center") -> "TopLeft"
                            | Some("TopCenter") -> "TopLeft"
                            | Some("right") -> "TopRight"
                            | Some(v) -> v  // Already in TopXxx format or other valid value
                            | None -> "TopRight"
                        hideTabsWhenDownByDefault =
                            // Handle backward compatibility: convert old bool values to new string format
                            // First try to get as string (new format)
                            match settingsJson.getString("HideTabsWhenDownByDefault") with
                            | Some(stringValue) -> stringValue
                            | None ->
                                // If not a string, try as bool (old format)
                                try
                                    match settingsJson.getBool("HideTabsWhenDownByDefault") with
                                    | Some(boolValue) -> if boolValue then "down" else "never"
                                    | None -> "never"
                                with
                                | _ -> "never"
                        hideTabsDelayMilliseconds = settingsJson.getInt32("HideTabsDelayMilliseconds").def(3000)
                        hideTabsOnFullscreen = settingsJson.getBool("HideTabsOnFullscreen").def(true)
                        snapTabHeightMargin = settingsJson.getBool("SnapTabHeightMargin").def(false)
                        changeTabPositionOnSnap = settingsJson.getString("ChangeTabPositionOnSnap").def("change")
                        version = settingsJson.getString("Version").def(String.Empty)
                        tabAppearance =
                            try
                                let appearanceObject = settingsJson.getObject("TabAppearance").def(JObject())
                                appearanceObject.items.fold this.defaultTabAppearance <| fun appearance (key,value) ->
                                    try
                                        let value = 
                                            let value = (value :?> JValue).Value
                                            let fieldType = Serialize.getFieldType (appearance.GetType()) key
                                            if fieldType = typeof<Int32> then box(unbox<Int64>(value).Int32)
                                            elif fieldType = typeof<Boolean> then box(unbox<bool>(value))
                                            elif fieldType = typeof<Color> then box(Color.FromRGB(Int32.Parse(unbox<string>(value), Globalization.NumberStyles.HexNumber)))
                                            else failwith "UNKNOWN TYPE"
                                        Serialize.writeField appearance key value :?> TabAppearanceInfo
                                    with
                                    | _ -> appearance  // Skip invalid field and keep current value
                            with
                            | _ -> this.defaultTabAppearance  // Use default appearance if parsing fails
                    }
                    cachedSettingsRec <- Some(settings)
                with
                | ex ->
                    // If settings loading completely fails, use all defaults
                    let defaultSettings = {
                        includedPaths = Set2(List2())
                        excludedPaths = Set2(List2())
                        autoGroupingPaths = Set2(List2())
                        licenseKey = ""
                        ticket = None
                        runAtStartup = false
                        hideInactiveTabs = false
                        enableTabbingByDefault = true
                        enableCtrlNumberHotKey = false
                        enableHoverActivate = false
                        tabPositionByDefault = "TopRight"
                        hideTabsWhenDownByDefault = "never"
                        hideTabsDelayMilliseconds = 3000
                        hideTabsOnFullscreen = true
                        snapTabHeightMargin = false
                        changeTabPositionOnSnap = "change"
                        version = String.Empty
                        tabAppearance = this.defaultTabAppearance
                    }
                    this.logEmptyFallback "settings record rebuild" ex
                    cachedSettingsRec <- Some(defaultSettings)
                    // Optionally log the error for debugging
                    System.Diagnostics.Debug.WriteLine(sprintf "Settings loading failed: %s" ex.Message)
            let loaded = cachedSettingsRec.Value
            // A record built while the settings are untrusted is made of
            // defaults. It is handed back - the app has to run on something -
            // but never kept, so it cannot outlive the state that produced it
            // and be written back once the file reads cleanly again.
            if settingsUntrusted then cachedSettingsRec <- None
            loaded

        and set(settings) =
            let settingsJson = this.settingsJson
            settingsJson.setString("Version", settings.version)
            settingsJson.setString("LicenseKey", settings.licenseKey)
            settings.ticket.iter <| fun ticket -> settingsJson.setString("Ticket", ticket)
            settingsJson.setBool("RunAtStartup", settings.runAtStartup)
            settingsJson.setBool("HideInactiveTabs", settings.hideInactiveTabs)
            settingsJson.setBool("EnableTabbingByDefault", settings.enableTabbingByDefault)
            settingsJson.setBool("EnableCtrlNumberHotKey", settings.enableCtrlNumberHotKey)
            settingsJson.setBool("EnableHoverActivate", settings.enableHoverActivate)
            settingsJson.setString("TabPositionByDefault", settings.tabPositionByDefault)
            settingsJson.setString("HideTabsWhenDownByDefault", settings.hideTabsWhenDownByDefault)
            settingsJson.setInt32("HideTabsDelayMilliseconds", settings.hideTabsDelayMilliseconds)
            settingsJson.setBool("HideTabsOnFullscreen", settings.hideTabsOnFullscreen)
            settingsJson.setBool("SnapTabHeightMargin", settings.snapTabHeightMargin)
            settingsJson.setString("ChangeTabPositionOnSnap", settings.changeTabPositionOnSnap)
            settingsJson.setStringArray("IncludedPaths", settings.includedPaths.items)
            settingsJson.setStringArray("ExcludedPaths", settings.excludedPaths.items)
            settingsJson.setStringArray("AutoGroupingPaths", settings.autoGroupingPaths.items)
            let appearanceObject =
                let appearance = settings.tabAppearance
                let obj = JObject()
                // Use FSharpType.GetRecordFields to get properties in definition order
                // This matches the order of FSharpValue.GetRecordFields values
                let props = FSharpType.GetRecordFields(appearance.GetType())
                let values = FSharpValue.GetRecordFields(appearance)
                List2(Seq.zip props values).iter <| fun (prop, value) ->
                    let key = prop.Name
                    match value with
                    | :? Color as value -> obj.setString(key, sprintf "%X" (value.ToRGB()))
                    | :? int as value -> obj.setInt64(key, int64(value))
                    | :? bool as value -> obj.setBool(key, value)
                    | :? string as value -> obj.setString(key, value)
                    | _ -> ()
                obj
            settingsJson.setObject("TabAppearance", appearanceObject)
            this.settingsJson <- settingsJson

    interface ISettings with

        member x.setValue((key,value)) =
            valueCache.Remove(key).ignore
            let settings = x.settings
            let settings = Serialize.writeField settings key value
            x.settings <- unbox<SettingsRec>(settings)
            settingChangedEvent.Trigger(key, value)

        member x.getValue(key) = 
            match valueCache.GetValue(key) with
            | None ->
                let settings = x.settings
                let value = Serialize.readField settings key
                // Same reason as the record cache: a value read out of the
                // defaults must not outlive them.
                if settingsUntrusted.not then valueCache.Add(key, value)
                value
            | Some(value) -> value

        member x.notifyValue key f =
            settingChangedEvent.Publish.Add <| fun(changedKey, value) ->
                if changedKey = key then f(value)

        member x.root
            with get() = this.settingsJson
            and set(value) = this.settingsJson <- value 
