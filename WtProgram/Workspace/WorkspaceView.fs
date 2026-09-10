namespace Bemo
open System
open System.Collections.Generic
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32.Forms
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Aga.Controls
open Aga.Controls.Tree

[<AllowNullLiteral>]
type WorkspaceNode(model:Dynamic) as this =
    inherit Node()
    do
        let children = model?children : List2<Dynamic>
        children.iter <| fun(child) -> 
            this.Nodes.Add(WorkspaceNode(child))
        model.cast<IWorkspaceNode>().removed.Add this.onRemoved

    member this.model = model

    member this.showSettings = this.model?showSettings
    member this.icon = this.model?icon
    member this.name = this.model?name
    member this.title = 
        if this.showSettings then this.model?title else null
    member this.matchType =
        if this.showSettings then
            let t : obj = this.model?matchType
            box(WorkspaceMatchType.caption (unbox<WorkspaceWindowTitleMatchType> t))
        else null

    member this.onRemoved() =
        this.Parent.Nodes.Remove(this).ignore


type WorkspaceView() as this =
    let Cell = CellScope()
    let mutable loadingInitialWorkspaces = true
    let signature() = Services.settings.root.getValueCI("workspaces") |> Option.map(fun value -> value.ToString())
    let mutable lastSignature = None
    

    member this.wm = Cell.cacheProp this <| fun() ->
        let wm = WorkspaceModel()
        wm.workspaceAdded.Add this.onWorkspaceAdded
        try
            wm.init()
        finally
            loadingInitialWorkspaces <- false
            lastSignature <- signature()
        wm

    member this.nameColumn = Cell.cacheProp this <| fun() ->
        TreeColumn(Localization.getString("Name"), 150)

    member this.matchTypeColumn = Cell.cacheProp this <| fun() ->
        TreeColumn(Localization.getString("MatchType"), 75)

    member this.titleColumn = Cell.cacheProp this <| fun() ->
        TreeColumn(Localization.getString("Title"), 350)
        
    member this.model = Cell.cacheProp this <| fun() -> 
        let model = TreeModel()
        model

    member this.panel = Cell.cacheProp this <| fun() -> 
        let panel = Panel()
        panel.Controls.Add(this.tree)
        panel.Controls.Add(this.toolbar)
        panel

    member this.iconNodeControl = Cell.cacheProp this <| fun() ->
        let control = NodeControls.NodeStateIcon()
        control.ParentColumn <- this.nameColumn
        control.DataPropertyName <- "icon"
        control.LeftMargin <- 3
        control

    member this.textNodeControl = Cell.cacheProp this <| fun() ->
        let control = SmoothNodeTextBox()
        control.Trimming <- StringTrimming.EllipsisCharacter
        control.DisplayHiddenContentInToolTip <- true
        control.ParentColumn <- this.nameColumn
        control.DataPropertyName <- "name"
        control.LeftMargin <- 3
        control

    member this.titleNodeControl = Cell.cacheProp this <| fun() ->
        let control = SmoothNodeTextBox()
        control.Trimming <- StringTrimming.EllipsisCharacter
        control.DisplayHiddenContentInToolTip <- true
        control.ParentColumn <- this.titleColumn
        control.DataPropertyName <- "title"
        control.LeftMargin <- 3
        control

    member this.matchTypeNodeControl = Cell.cacheProp this <| fun() ->
        let control = SmoothNodeTextBox()
        control.Trimming <- StringTrimming.EllipsisCharacter
        control.DisplayHiddenContentInToolTip <- true
        control.ParentColumn <- this.matchTypeColumn
        control.DataPropertyName <- "matchType"
        control.LeftMargin <- 3
        control

    member this.tree = Cell.cacheProp this <| fun() ->
        let tree = TreeViewAdv()
        tree.FullRowSelect <- true
        tree.UseColumns <- true
        tree.RowHeight <- 24
        tree.Columns.Add(this.nameColumn)
        tree.Columns.Add(this.matchTypeColumn)
        tree.Columns.Add(this.titleColumn)
        tree.NodeControls.Add(this.iconNodeControl)
        tree.NodeControls.Add(this.textNodeControl)
        tree.NodeControls.Add(this.matchTypeNodeControl)
        tree.NodeControls.Add(this.titleNodeControl)
        tree.Model <- this.model
        tree.Dock <- DockStyle.Fill
        tree.SelectionChanged.Add <| this.onTreeSelectionChanged
        tree

    member this.newButton : ToolStripButton = Cell.cacheProp this <| fun() ->
        let btn = ToolStripButton(Localization.getString("New"))
        btn.Image <- Services.openImage("add.png")
        btn.Click.Add <| fun _ -> this.onNewButton()
        btn

    member this.restoreButton : ToolStripButton = Cell.cacheProp this <| fun() ->
        let btn = ToolStripButton(Localization.getString("Restore"))
        btn.Image <- Services.openImage("restore.png")
        btn.Click.Add <| fun _ -> this.onRestoreButton()
        this.wm.canRestoreChanged.Add <| fun(canRestore) -> 
            btn.Enabled <- canRestore
        btn

    member this.removeButton : ToolStripButton = Cell.cacheProp this <| fun() ->
        let btn = ToolStripButton(Localization.getString("Remove"))
        btn.Image <- Services.openImage("delete.png")
        btn.Click.Add <| fun _ -> this.onRemoveButton()
        btn

    member this.editButton : ToolStripButton = Cell.cacheProp this <| fun() ->
        let btn = ToolStripButton(Localization.getString("Edit"))
        btn.Image <- Services.openImage("edit.png")
        btn.Click.Add <| fun _ -> this.onEditButton()
        // Off for a node that has nothing to edit (a group), like Restore
        // is off for anything but a workspace.
        this.wm.canEditChanged.Add <| fun(canEdit) ->
            btn.Enabled <- canEdit
        btn

    member this.toolbar = Cell.cacheProp this <| fun() ->
        let ts = ToolStripEx(
            ClickThrough=true
        )
        ts.GripStyle  <- ToolStripGripStyle.Hidden
        ts.Dock <- DockStyle.Top
        ts.Items.Add(this.newButton).ignore
        ts.Items.Add(this.restoreButton).ignore
        ts.Items.Add(this.editButton).ignore
        ts.Items.Add(this.removeButton).ignore
        ts

    member this.findNode(node:TreeNodeAdv) =
        this.model.FindNode(this.tree.GetPath(node)) :?> WorkspaceNode

    member this.onTreeSelectionChanged(e) =
        let node = this.tree.SelectedNode
        let model =
            if node <> null then
                this.findNode(node).model
            else
                null
        this.wm.selected <- model

    member this.onNewButton() =
        this.wm.create()

    member this.onRestoreButton() =
        this.wm.restore()

    member this.onRemoveButton() =
        this.wm.remove()
        
    member this.onEditButton() =
        if this.wm.edit(this.panel) then
            this.tree.FullUpdate()

    member this.onWorkspaceAdded(ws:Workspace) =
        let wsNode = WorkspaceNode(ws)
        this.model.Nodes.Insert(0, wsNode)
        if not loadingInitialWorkspaces then
            this.tree.Root.CollapseAll()
            let path = this.model.GetPath(wsNode)
            let treeNode = this.tree.FindNode(path)
            treeNode.ExpandAll()
            InvokerService.invoker.asyncInvoke <| fun() ->
                this.tree.SelectedNode <- treeNode

    member this.refresh() =
        let current = signature()
        if current <> lastSignature then
            loadingInitialWorkspaces <- true
            try
                this.model.Nodes.Clear()
                this.wm.reload()
                lastSignature <- current
            finally
                loadingInitialWorkspaces <- false

    interface ISettingsView with
        member x.key = SettingsViewType.LayoutSettings
        member x.title = Localization.getString("Workspace")
        member x.control = this.panel :> Control
