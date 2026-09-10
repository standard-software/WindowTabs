namespace Bemo
open System
open System.Collections.Generic
open System.Drawing
open System.IO
open System.Text.RegularExpressions
open System.Windows.Forms
open Bemo.Win32.Forms
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Aga.Controls
open Aga.Controls.Tree

type IEditInfo =
    abstract member title : string
    abstract member fields : List2<string * Control>
    abstract member height : int
    abstract member ok : unit -> unit

[<AllowNullLiteral>]
type IWorkspaceNode =
    inherit INode
    abstract member beginEdit : unit -> IEditInfo
    // Whether Edit applies to this node at all. A group has nothing worth
    // editing: its name labels the tree row and nothing else.
    abstract member canEdit : bool
    abstract member remove : unit -> unit
    abstract member removed: IEvent<unit>

type WorkspaceWindowTitleMatchType =
    | ExactMatch = 0
    | StartsWith = 1
    | EndsWith = 2
    | Contains = 3
    | RegEx = 4

// The match types as the user sees them: in a fixed order, with captions
// from the language file (the enum names were shown before, in English).
module WorkspaceMatchType =
    let all = [
        WorkspaceWindowTitleMatchType.ExactMatch
        WorkspaceWindowTitleMatchType.StartsWith
        WorkspaceWindowTitleMatchType.EndsWith
        WorkspaceWindowTitleMatchType.Contains
        WorkspaceWindowTitleMatchType.RegEx
    ]
    let caption (t: WorkspaceWindowTitleMatchType) = Localization.getString("MatchType" + t.ToString())

// A drop-down of the match types, showing their captions.
type MatchTypeEditor() as this =
    let control =
        let c = ComboBox()
        c.DropDownStyle <- ComboBoxStyle.DropDownList
        for t in WorkspaceMatchType.all do c.Items.Add(WorkspaceMatchType.caption t) |> ignore
        c
    member this.value
        with get() : WorkspaceWindowTitleMatchType =
            match control.SelectedIndex with
            | i when i >= 0 && i < WorkspaceMatchType.all.Length -> WorkspaceMatchType.all.[i]
            | _ -> WorkspaceWindowTitleMatchType.ExactMatch
        and set(v: WorkspaceWindowTitleMatchType) =
            control.SelectedIndex <- (WorkspaceMatchType.all |> List.tryFindIndex ((=) v) |> Option.defaultValue 0)
    interface IPropEditor with
        member x.value
            with get() = box this.value
            and set(v) = this.value <- unbox<WorkspaceWindowTitleMatchType> v
        member x.control = control :> Control
        member x.changed = control.SelectedIndexChanged |> Event.map ignore

// What a tab looked like when the workspace was saved, so the restore can
// put it back: the same items the restart restore keeps. Colours travel as
// RRGGBBAA text and the side as "TopLeft" / "TopRight", as in the settings
// file. A workspace saved by an earlier version has none of this, and its
// windows restore exactly as they always did.
type WorkspaceTabState = {
    fillColor: string option
    underlineColor: string option
    borderColor: string option
    pinned: bool
    name: string option
    align: string option
    // Position in the tab strip, left to right
    order: int
}

type private DpiImage(source: Image) =
    // Do not dispose an image when the scale changes: TreeViewAdv may still
    // be completing a paint that obtained it. Reuse one bitmap per distinct
    // monitor scale instead; a desktop normally has only a few such scales.
    let images = Dictionary<float, Image>()

    member this.image =
        let scale = SettingsDpi.current()
        match images.TryGetValue(scale) with
        | true, image -> image
        | _ ->
            let image = SettingsDpi.scaledImage(source)
            images.[scale] <- image
            image

type WorkspaceWindow() as this =
    inherit Dynamic()
    // 16-px PNG, drawn by NodeStateIcon at its natural size. The tree's rows
    // grow with the monitor scale, so the icon has to as well.
    let dpiIcon = DpiImage(Services.openImage("window.png"))
    let removedEvent = Event<_>()
    let data = ModelObject()

    member this.name 
        with get() = data.get("name").cast<string>()
        and set(value) = data.set("name", value)

    member this.title 
        with get() = data.get("title").cast<string>()
        and set(value) = data.set("title", value)

    member this.matchType 
        with get() = data.get("matchType").cast<WorkspaceWindowTitleMatchType>()
        and set(value) = data.set("matchType", value)

    member this.zorder
        with get() = data.get("zorder").cast<int>()
        and set(value) = data.set("zorder", value)

    // Not in `data`: the tree binds to that, and these are not shown there.
    member val tabState : WorkspaceTabState option = None with get, set
    // Full path of the executable, for the dialog to show; matching is by
    // title, so this is information only. Empty for a window saved before
    // it was recorded.
    member val processPath : string = "" with get, set

    member this.icon = dpiIcon.image
    member this.children = List2<Dynamic>()
    interface IWorkspaceNode with
        member x.showSettings = true
        member x.remove() =
            removedEvent.Trigger()
        member x.removed = removedEvent.Publish
        member x.canEdit = true
        member x.beginEdit() =
            // The program, for information: its full path when known (see
            // WorkspaceModel.edit for a window saved before it was recorded),
            // its executable's name otherwise.
            let pathBox = new TextBox()
            pathBox.ReadOnly <- true
            // Greyed like a disabled control, so it does not look editable;
            // still a text box, so the path can be selected and copied (the
            // colours are set in WorkspaceModel.edit, after theming).
            pathBox.BackColor <- SystemColors.Control
            pathBox.ForeColor <- SystemColors.GrayText
            pathBox.Text <- (if this.processPath <> "" then this.processPath else this.name)
            let titleEditor = TextEditor() :> IPropEditor
            titleEditor.value <- this.title
            let matchTypeEditor = MatchTypeEditor()
            matchTypeEditor.value <- this.matchType
            // The name the tab is to show once restored; blank means none,
            // and the tab shows the window's title as it always did.
            let tabNameEditor = TextEditor() :> IPropEditor
            tabNameEditor.value <- (this.tabState |> Option.bind (fun st -> st.name) |> Option.defaultValue "")
            { new IEditInfo with
                member x.title = Localization.getString("WindowSettings")
                // Keys, not captions: the form builder looks them up.
                member x.fields =
                    List2([
                        ("ProcessPath", pathBox :> Control)
                        ("Title", titleEditor.control)
                        ("MatchType", matchTypeEditor.cast<IPropEditor>().control)
                        ("TabName", tabNameEditor.control)
                    ])
                member x.height  = 290
                member x.ok() =
                    this.title <- titleEditor.value.cast<string>()
                    this.matchType <- matchTypeEditor.value
                    let tabName =
                        match tabNameEditor.value.cast<string>() with
                        | null -> None
                        | s when String.IsNullOrWhiteSpace(s) -> None
                        | s -> Some(s.Trim())
                    // A window saved before this version has no tab state.
                    // It gets one only when a name is given, so that clearing
                    // the field leaves such a window restoring as it always did.
                    match this.tabState, tabName with
                    | Some(st), _ -> this.tabState <- Some { st with name = tabName }
                    | None, Some(_) ->
                        this.tabState <- Some { fillColor = None; underlineColor = None; borderColor = None
                                                pinned = false; name = tabName; align = None; order = this.zorder }
                    | None, None -> ()
            }

    member this.serialize() =
        let obj = JObject()
        obj.setString("name", this.name)
        obj.setString("title", this.title)
        obj.setInt32("zorder", this.zorder)
        obj.setInt32("matchType", int32(this.matchType))
        if this.processPath <> "" then obj.setString("processPath", this.processPath)
        // Only when there is state; an older reader ignores these keys.
        this.tabState |> Option.iter (fun st ->
            st.fillColor |> Option.iter (fun v -> obj.setString("tabFillColor", v))
            st.underlineColor |> Option.iter (fun v -> obj.setString("tabUnderlineColor", v))
            st.borderColor |> Option.iter (fun v -> obj.setString("tabBorderColor", v))
            if st.pinned then obj.setInt32("tabPinned", 1)
            st.name |> Option.iter (fun v -> obj.setString("tabName", v))
            st.align |> Option.iter (fun v -> obj.setString("tabAlign", v))
            obj.setInt32("tabOrder", st.order))
        obj

    static member deserialize(obj:JObject) =
        let window = WorkspaceWindow()
        window.name <- obj.getString("name").Value
        window.title <-  obj.getString("title").Value
        window.zorder <- obj.getInt32("zorder").Value
        window.matchType <- enum<WorkspaceWindowTitleMatchType>(obj.getInt32("matchType").Value)
        window.processPath <- (obj.getString("processPath") |> Option.defaultValue "")
        // tabOrder is the marker: a window saved with its tab state always
        // has it, one saved before this version never does.
        window.tabState <-
            obj.getInt32("tabOrder") |> Option.map (fun order ->
                { fillColor = obj.getString("tabFillColor")
                  underlineColor = obj.getString("tabUnderlineColor")
                  borderColor = obj.getString("tabBorderColor")
                  pinned = (obj.getInt32("tabPinned") = Some(1))
                  name = obj.getString("tabName")
                  align = obj.getString("tabAlign")
                  order = order })
        window
    
    
and 
    [<AllowNullLiteral>]
    WorkspaceGroup() as this =
    inherit Dynamic()
    [<DefaultValue>] val mutable name : string
    [<DefaultValue>] val mutable placement : OSWindowPlacement
    [<DefaultValue>] val mutable workspace : Workspace
    let mutable _windows  = System.Collections.Generic.List<Dynamic>()
    let removedEvent = Event<_>()

    member this.addWindow(window) =
        _windows.Add(window)
        window.cast<IWorkspaceNode>().removed.Add <| fun()-> this.removeWindow(window)

    member this.removeWindow(window) =
        _windows.Remove(window).ignore

    member this.windows = List2(_windows)
    member this.children = this.windows
    
    interface IWorkspaceNode with
        member x.showSettings = false
        member x.remove() =
            removedEvent.Trigger()
        member x.removed = removedEvent.Publish
        // Nothing to edit: the name only labels the tree row.
        member x.canEdit = false
        member x.beginEdit() = Unchecked.defaultof<IEditInfo>
    
    member this.serialize() =
        let placementObj = 
            let obj = JObject()
            obj.setInt32("showCmd", this.placement.showCmd)
            obj.setPt("ptMaxPosition", this.placement.ptMaxPosition)
            obj.setPt("ptMinPosition", this.placement.ptMinPosition)
            obj.setRect("rcNormalPosition", this.placement.rcNormalPosition)
            obj
        let windowObjects = this.children.map <| fun child -> child?serialize()
        let groupObj = JObject()
        groupObj.setString("name", this.name)
        groupObj.setObject("placement", placementObj)
        groupObj.setObjectArray("windows", windowObjects)
        groupObj

    static member deserialize(obj:JObject) =
        let group = WorkspaceGroup(
            name = obj.getString("name").Value,
            placement =(
                let obj = obj.getObject("placement").Value
                {
                    flags = 0
                    showCmd = obj.getInt32("showCmd").Value
                    ptMaxPosition = obj.getPt("ptMaxPosition")
                    ptMinPosition = obj.getPt("ptMinPosition")
                    rcNormalPosition = obj.getRect("rcNormalPosition")
                })
        )
        obj.getObjectArray("windows").Value.map(WorkspaceWindow.deserialize).iter(group.addWindow)
        group


and 
    [<AllowNullLiteral>]
    Workspace() as this =
    inherit Dynamic()
    let removedEvent = Event<_>()
    [<DefaultValue>] val mutable name : string
    let mutable _groups  = System.Collections.Generic.List<Dynamic>()
    let dpiIcon = DpiImage(Services.openImage("workspace.png"))
    
    member this.addGroup(group) =
        group.cast<IWorkspaceNode>().removed.Add <| fun()-> this.removeGroup(group)
        _groups.Add(group)

    member this.removeGroup(group) =
        _groups.Remove(group).ignore

    member this.groups = List2(_groups)
    member this.children = this.groups
    member this.icon = dpiIcon.image

    interface IWorkspaceNode with
        member x.showSettings = false
        member x.remove() =
            removedEvent.Trigger()
        member x.removed = removedEvent.Publish
        member x.canEdit = true
        member x.beginEdit() =
            let nameEditor = TextEditor() :> IPropEditor
            nameEditor.value <- this?name
            { new IEditInfo with
                member x.title = Localization.getString("WorkspaceNameSettings")
                member x.fields = List2([("Name", nameEditor.control)])
                member x.height  = 200
                member x.ok() = this?name <- nameEditor.value.cast<string>()
            }

    member this.serialize() =
        let layoutObj = JObject()
        layoutObj.setString("name", this.name)
        layoutObj.setObjectArray("groups", this.children.map <| fun child-> child?serialize())
        layoutObj

    static member deserialize(obj:JObject) =
        let groups = obj.getObjectArray("groups").Value.map(WorkspaceGroup.deserialize)
        let ws = Workspace(
            name = obj.getString("name").Value
        )
        groups.iter(ws.addGroup)
        ws

type WindowResolver() as this =
    let os = OS()
    let mutable hwnds = Services.program.appWindows
    let hwndToTitle = Map2(hwnds.map(fun hwnd -> (hwnd, os.windowFromHwnd(hwnd).text)))

    member this.title(hwnd) = hwndToTitle.find(hwnd)
    member this.removeHwnd(hwnd) =
        hwnds <- hwnds.where((<>) hwnd)

    member this.resolve(windowInfo:Dynamic) =
        let target : string = windowInfo?title

        let isMatch =
            match windowInfo?matchType with
            | WorkspaceWindowTitleMatchType.ExactMatch ->
                fun(title) -> title = target
            | WorkspaceWindowTitleMatchType.Contains ->
                fun(title) -> title.Contains(target)
            | WorkspaceWindowTitleMatchType.StartsWith ->
                fun(title) -> title.StartsWith(target)
            | WorkspaceWindowTitleMatchType.EndsWith ->
                fun(title) -> title.EndsWith(target)
            | WorkspaceWindowTitleMatchType.RegEx ->
                let re = Regex(target)
                fun(title) -> re.IsMatch(title)
            | _ -> fun(title) -> false

        hwnds.tryFind(this.title >> isMatch)
  

type IWorkspaceModel =
    abstract member list : List2<Workspace>
    abstract member create : string -> unit
    abstract member restore : int
    abstract member update : int * Workspace -> unit
    abstract member delete : int -> unit
      
type WorkspaceModel() as this =
    inherit Dynamic()
    let os = OS()
    let workspaceAddedEvt = Event<_>()
    let selectedChangedEvt = Event<_>()
    let canRestoreChangedEvt = Event<_>()
    let canEditChangedEvt = Event<_>()
    let _workspaces = System.Collections.Generic.List<Workspace>()
    let mutable _selected = null : obj

    do
        Observable.init(this)

    member this.workspaces =  _workspaces.list
    member this.workspaceAdded = workspaceAddedEvt.Publish

    member this.selected
        with get() = _selected
        and set(value) =
            _selected <- value
            selectedChangedEvt.Trigger(value)
            canRestoreChangedEvt.Trigger(this.canRestore)
            canEditChangedEvt.Trigger(this.canEdit)

    member this.selectedChanged = selectedChangedEvt.Publish

    member private this.newWorkspaceName() =
        let nextNumber = this.workspaces.choose(fun(w) -> w.name.Replace("Workspace ", "").tryToInt()).maxBy 0 id + 1
        sprintf "Workspace %A" nextNumber

    member private this.createWorkspace() =
        let zorder = os.windowZorders
        let groups = Services.desktop.groups.enumerate.map <| fun (i, group) ->
            let windowsInZorder = group.windows.sortBy(zorder.find)
            let innerZorder = Map2(windowsInZorder.enumerate.map(fun(innerZorder, hwnd) -> hwnd, innerZorder))
            let wsGroup = WorkspaceGroup(
                name = sprintf "Group %d" (i + 1),
                // Read in the DPI-unaware coordinate space - see the comment
                // on restoreWorkspace below. GetWindowPlacement answers in the
                // caller's space, and this rectangle goes straight into
                // settings.json, so it has to keep meaning what it always did.
                placement = (
                    let hwnd = windowsInZorder.head
                    Dpi.withUnawareContext <| fun() -> os.windowFromHwnd(hwnd).placement)
            )
            // The strip's order, for putting the tabs back in it.
            let stripOrder = (try group.visualOrderThreadSafe.list with _ -> [])
            group.windows.enumerate.iter <| fun (j, hwnd) ->
                let window = os.windowFromHwnd(hwnd)
                let ww = WorkspaceWindow()
                ww.name <- window.pid.exeName
                // As the settings file keeps application paths: a Store app's
                // package folder and a per-version folder become patterns, so
                // the entry does not go stale at the application's next update.
                ww.processPath <- (try AppPath.normalize window.pid.processPath with _ -> "")
                ww.title <- window.text
                ww.zorder <- innerZorder.find(hwnd)
                ww.matchType <- WorkspaceWindowTitleMatchType.ExactMatch
                // The tab's state, read the way the context menu reads it.
                // Anything failing here leaves the window as it was saved
                // before this version: identity and placement only.
                ww.tabState <-
                    try
                        let rgba (c: Color) = SavedTabState.Rgba.format (c.R, c.G, c.B, c.A)
                        Some {
                            fillColor = Services.program.getWindowFillColor(hwnd) |> Option.map rgba
                            underlineColor = Services.program.getWindowUnderlineColor(hwnd) |> Option.map rgba
                            borderColor = Services.program.getWindowBorderColor(hwnd) |> Option.map rgba
                            pinned = Services.program.isWindowPinned(hwnd)
                            name = Services.program.getWindowNameOverride(hwnd)
                            align =
                                Services.program.getWindowAlignment(hwnd)
                                |> Option.map (function TopLeft -> "TopLeft" | TopRight -> "TopRight")
                            order = stripOrder |> List.tryFindIndex ((=) hwnd) |> Option.defaultValue j
                        }
                    with _ -> None
                wsGroup.addWindow(ww)
            wsGroup
            
        let ws = Workspace(
            name = this.newWorkspaceName()
        )

        groups.iter(ws.addGroup)
        ws

    // Workspace rectangles are the one part of the settings dialog that must
    // NOT follow it into the DPI-aware coordinate space.
    //
    // A workspace stores the GetWindowPlacement rectangle of each group's top
    // window in settings.json. Both GetWindowPlacement and SetWindowPlacement
    // answer in the coordinate space of the CALLING THREAD, and every one of
    // those calls used to run on the settings dialog's thread, which was
    // DPI-unaware for its whole lifetime. So every workspace on every user's
    // machine is stored in virtualized coordinates.
    //
    // Making the dialog aware would silently change what those numbers mean: a
    // workspace saved by an older version would restore its windows to a
    // different place and size, and a workspace saved by this version would not
    // restore correctly on an older one. The file format is unversioned, so
    // there is nothing to tell the two apart by either.
    //
    // Keeping just these two API calls on an unaware thread context keeps the
    // stored numbers in exactly the space they have always been in, in both
    // directions, with no migration and no schema version - the calls are made
    // under the same thread context as before, so Windows performs the same
    // conversion it performed before. Nothing else needs to move: the window
    // matching, the group construction and the tab strips all work in real
    // device pixels, which is what the rest of the process already does.
    member private this.restoreWorkspace(workspace:Workspace) =
        let windowResolver = WindowResolver()
      
        Services.program.suspendTabMonitoring()

        let hwndToGroup = Map2(Services.desktop.groups.collect <| fun group ->
            group.windows.map <| fun hwnd -> (hwnd, group)
        )
        let removeWindow hwnd = hwndToGroup.find(hwnd).removeWindow(hwnd)

        workspace.children.iter <| fun (groupInfo) ->
            let windows : List2<Dynamic> = groupInfo?windows
            let windows = windows.reverse
            // Each saved window with the live one it resolved to, so the
            // saved tab state can be put on the right window below.
            let resolved =
                windows.sortBy(fun w -> w?zorder).choose (fun w ->
                    windowResolver.resolve w |> Option.map (fun hwnd -> (w, hwnd)))
            let windows = resolved.map snd

            windows.iter removeWindow
            windows.iter <| fun hwnd -> WinUserApi.ShowWindow(hwnd, ShowWindowCommands.SW_RESTORE).ignore
            Dpi.withUnawareContext <| fun() ->
                windows.iter <| fun hwnd -> os.windowFromHwnd(hwnd).setPlacement(groupInfo?placement)
            os.setZorder(windows)

            let group = Services.desktop.createGroup(false)
            windows.iter <| fun hwnd -> group.addWindow(hwnd, false)

            // Tab state saved with the windows, put back through the same
            // calls the context menu uses. Only windows saved with it have
            // any; a workspace from an earlier version stops here, as it
            // always did. Every call is queued on the group's own thread
            // behind the addWindow calls above, so the tabs exist by then.
            let withState =
                resolved.list |> List.choose (fun (w, hwnd) ->
                    match w with
                    | :? WorkspaceWindow as ww -> ww.tabState |> Option.map (fun st -> (st, hwnd))
                    | _ -> None)
            withState |> List.iter (fun (st, hwnd) ->
                try
                    let color (s: string option) =
                        s |> Option.bind SavedTabState.Rgba.parse
                          |> Option.map (fun (r, g, b, a) -> Color.FromArgb(int a, int r, int g, int b))
                    color st.fillColor |> Option.iter (fun c -> group.setTabFillColor(hwnd, Some c))
                    color st.underlineColor |> Option.iter (fun c -> group.setTabUnderlineColor(hwnd, Some c))
                    color st.borderColor |> Option.iter (fun c -> group.setTabBorderColor(hwnd, Some c))
                    st.name |> Option.iter (fun n -> group.setTabName(hwnd, Some n))
                    st.align |> Option.iter (fun a -> group.setTabAlign(hwnd, (if a = "TopRight" then TopRight else TopLeft)))
                    if st.pinned then group.pinTab(hwnd)
                with _ -> ())
            // Then the order, once every tab is on its side: the strip
            // moves within a side, so the side has to be settled first.
            withState
            |> List.sortBy (fun (st, _) -> st.order)
            |> List.iteri (fun i (_, hwnd) -> try group.moveTab(hwnd, i) with _ -> ())

        Services.program.resumeTabMonitoring()

    member this.addWorkspace(ws:Workspace) =
        ws.cast<IWorkspaceNode>().removed.Add <| fun() -> this.onWorkspaceRemoved(ws)
        _workspaces.Add(ws)
        this.saveSettings() 
        workspaceAddedEvt.Trigger(ws) 
         
    member this.create() =
        let ws = this.createWorkspace()
        this.addWorkspace(ws)
    
    member this.remove() =
        if this.selected <> null then
            this.selected?remove()
            // Removing a group or a window only took it out of its parent in
            // memory; the file was written again at the next workspace-level
            // change, so the settings dialog opened on the old contents.
            this.saveSettings()

    member this.canRestore =
        this.selected <> null && this.selected.GetType() = typeof<Workspace>

    member this.canRestoreChanged = canRestoreChangedEvt.Publish

    member this.canEdit =
        this.selected <> null && this.selected.cast<IWorkspaceNode>().canEdit

    member this.canEditChanged = canEditChangedEvt.Publish

    member this.restore() =
        if this.selected <> null then
            let ws = this.selected :?> Workspace
            this.restoreWorkspace(ws)

    member this.edit(parent) =
        let selected = this.selected
        // A window saved before its path was recorded gets it from the live
        // window that matches it now, if there is one, and keeps it from
        // then on; failing that the dialog shows the executable's name.
        (match selected with
         | :? WorkspaceWindow as ww when ww.processPath = "" ->
            try
                match WindowResolver().resolve(ww) with
                | Some(hwnd) -> ww.processPath <- AppPath.normalize (os.windowFromHwnd(hwnd).pid.processPath)
                | None -> ()
            with _ -> ()
         | _ -> ())
        if this.canEdit then
            let editInfo = selected?beginEdit()
            let table = UIHelper.formCompact(editInfo?fields)
            let form = UIHelper.okCancelForm table
            // 380px gives the input column ~250px after the 100px label
            // column + form padding — enough for a comfortable Match Type
            // dropdown without horizontal scrollbar.
            form.Width <- 380
            form.Height <- editInfo?height
            form.StartPosition <- FormStartPosition.CenterParent
            form.Text <- editInfo?title
            // Scale the 96-dpi design size just assigned above, before the
            // dark theme so the theming pass sees final control sizes.
            // CenterParent puts this on the settings dialog's monitor, which
            // is the scale applyToChildDialog uses.
            SettingsDpi.applyToChildDialog form
            // Apply dark mode if the user enabled "Settings Dialog Dark Mode"
            // on the View tab. Same pattern as the Save / Edit theme dialogs.
            let darkOn =
                try
                    match Services.settings.root.getBool("EnableDarkMode") with
                    | Some(v) -> v
                    | None -> false
                with _ -> false
            if darkOn then
                Bemo.DarkMode.applyDarkColorsBeforeShow form
                form.HandleCreated.Add(fun _ ->
                    try Bemo.DarkMode.applyDarkThemeBranch15ToForm form true
                    with _ -> ())
            // A read-only box reads as disabled, after the theme pass has
            // coloured everything: grey text on the form's grey in light
            // mode; in dark mode the mid-grey that disabled buttons get their
            // text in (DarkMode), on the same panel colour as the editable
            // boxes.
            form.Shown.Add(fun _ ->
                let rec shade (c: Control) =
                    (match c with
                     | :? TextBox as tb when tb.ReadOnly ->
                        if darkOn then
                            tb.BackColor <- Bemo.DarkMode.darkPanel
                            tb.ForeColor <- Color.FromArgb(160, 160, 160)
                        else
                            tb.BackColor <- SystemColors.Control
                            tb.ForeColor <- SystemColors.GrayText
                     | _ -> ())
                    for child in c.Controls do shade child
                try shade form with _ -> ()
                // Start in the first box that can be typed in - the title, not
                // the read-only path the form would otherwise focus and
                // select whole - with the caret at its start and nothing
                // selected.
                let rec firstEditable (c: Control) : TextBox option =
                    match c with
                    | :? TextBox as tb when not tb.ReadOnly -> Some(tb)
                    | _ -> c.Controls |> Seq.cast<Control> |> Seq.tryPick firstEditable
                try
                    firstEditable form |> Option.iter (fun tb ->
                        tb.Select()
                        tb.SelectionStart <- 0
                        tb.SelectionLength <- 0)
                with _ -> ())
            let ok = form.ShowDialog(parent) = DialogResult.OK
            if ok then
                editInfo?ok()
                this.saveSettings()
            ok
        else
            false

    member this.init() =
        this.loadSettings()

    member this.loadSettings() =
        let settingsObj = Services.settings.root
        let workspaces = settingsObj.getObjectArray("workspaces").def(List2()).map(Workspace.deserialize)
        workspaces.iter this.addWorkspace

    member this.saveSettings() =
        let settingsObj = Services.settings.root
        let workspaceObjs = this.workspaces.map <| fun ws -> ws.serialize()
        settingsObj.setObjectArray("workspaces", workspaceObjs)
        Services.settings.root <- settingsObj

    member this.reload() =
        this.selected <- null
        _workspaces.Clear()
        this.loadSettings()


    member this.onWorkspaceRemoved(ws) =
        _workspaces.Remove(ws).ignore
        this.saveSettings()
