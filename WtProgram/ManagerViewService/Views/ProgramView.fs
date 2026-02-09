namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32.Forms
open Aga.Controls
open Aga.Controls.Tree

module ImgHelper =
    let imgFromIcon (icon:Icon) =
        let img =
            try
                icon.ToBitmap().img
            with _ ->
                SystemIcons.Application.ToBitmap().img
        img.resize(Sz(16,16)).bitmap :> Image


type ExeNode(procPath) =
    inherit Node(Path.GetFileName(procPath))
    let icon =
        let procIcon = Win32Helper.GetFileIcon(procPath)
        ImgHelper.imgFromIcon (Ico.fromHandle(procIcon).def(System.Drawing.SystemIcons.Application))
    let mutable _enableTabs = Services.filter.getIsTabbingEnabledForProcess(procPath)
    let mutable _enableAutoGrouping = Services.program.getAutoGroupingEnabled(procPath)
    let mutable _category1 = Services.program.getCategoryEnabled(procPath, 1)
    let mutable _category2 = Services.program.getCategoryEnabled(procPath, 2)
    let mutable _category3 = Services.program.getCategoryEnabled(procPath, 3)
    let mutable _category4 = Services.program.getCategoryEnabled(procPath, 4)
    let mutable _category5 = Services.program.getCategoryEnabled(procPath, 5)
    member this.Icon with get() = icon
    member this.enableTabs
        with get() = _enableTabs
        and set(newValue) =
            _enableTabs <- newValue
            Services.filter.setIsTabbingEnabledForProcess procPath _enableTabs
            // When Tabs is disabled, also disable Auto Grouping
            if not _enableTabs then
                _enableAutoGrouping <- false
                Services.program.setAutoGroupingEnabled procPath false
    member this.enableAutoGrouping
        with get() = _enableAutoGrouping
        and set(newValue) =
            _enableAutoGrouping <- newValue
            Services.program.setAutoGroupingEnabled procPath _enableAutoGrouping
    member this.category1
        with get() = _category1
        and set(newValue) =
            _category1 <- newValue
            Services.program.setCategoryEnabled procPath 1 _category1
    member this.category2
        with get() = _category2
        and set(newValue) =
            _category2 <- newValue
            Services.program.setCategoryEnabled procPath 2 _category2
    member this.category3
        with get() = _category3
        and set(newValue) =
            _category3 <- newValue
            Services.program.setCategoryEnabled procPath 3 _category3
    member this.category4
        with get() = _category4
        and set(newValue) =
            _category4 <- newValue
            Services.program.setCategoryEnabled procPath 4 _category4
    member this.category5
        with get() = _category5
        and set(newValue) =
            _category5 <- newValue
            Services.program.setCategoryEnabled procPath 5 _category5

    interface INode with
        member x.showSettings = true

type WindowNode(window:Window) =
    inherit Node(window.text)
    let icon = ImgHelper.imgFromIcon window.iconSmall
    member this.Icon with get() = icon 
    interface INode with
        member x.showSettings = false

type ProgramView() as this=

    let invoker = InvokerService.invoker
    let toolBar = 
        let ts = ToolStrip()
        ts.GripStyle  <- ToolStripGripStyle.Hidden
        let refreshBtn = 
            let btn = ToolStripButton(Localization.getString("Refresh"))
            btn.Click.Add <| fun _ -> this.populateNodes()
            btn
        ts.Items.Add(refreshBtn).ignore
        ts
    let statusBar = 
        let sb = StatusBar()
        sb.Text <- "Ready"
        sb
    let tree,model =
        let tree = TreeViewAdv()
        let model = TreeModel()
        let nameColumn = TreeColumn(Localization.getString("Name"), 200)
        tree.UseColumns <- true
        tree.Columns.Add(nameColumn)
        tree.RowHeight <- 24
        let addCheckBoxColumn colText propName colWidth visibilityCheck =
            let content = Localization.getString(propName)
            let parentColumn =
                let col = TreeColumn(content, colWidth)
                col.TextAlign <- HorizontalAlignment.Center
                col
            tree.Columns.Add(parentColumn)
            let control = NodeControls.NodeCheckBox()
            control.ParentColumn <- parentColumn
            control.IsVisibleValueNeeded.Add <| fun e ->
                let path = tree.GetPath(e.Node)
                if path <> null && path.LastNode <> null then
                    let node = path.LastNode :?> INode
                    // Check basic visibility (showSettings)
                    let basicVisible = node.showSettings
                    // Check additional visibility condition if provided
                    let additionalVisible =
                        match visibilityCheck with
                        | Some checkFn ->
                            match path.LastNode with
                            | :? ExeNode as exeNode -> checkFn exeNode
                            | _ -> true
                        | None -> true
                    e.Value <- basicVisible && additionalVisible
                else
                    e.Value <- false
            // Center the checkbox horizontally in the column
            // Checkbox size is 13 pixels (NodeCheckBox.ImageSize)
            let checkboxSize = 13
            control.LeftMargin <- (colWidth - checkboxSize) / 2
            control.EditEnabled <- true
            control.DataPropertyName <- propName
            tree.NodeControls.Add(control)
            control
        // Helper function to check if any category is selected
        let hasAnyCategory (exeNode:ExeNode) =
            exeNode.category1 || exeNode.category2 || exeNode.category3 || exeNode.category4 || exeNode.category5
        // Category visibility: show only when autoGrouping is ON and (this category is checked OR no category is checked)
        let categoryVisibility categoryNum (exeNode:ExeNode) =
            exeNode.enableAutoGrouping &&
            (match categoryNum with
             | 1 -> exeNode.category1 || not (hasAnyCategory exeNode)
             | 2 -> exeNode.category2 || not (hasAnyCategory exeNode)
             | 3 -> exeNode.category3 || not (hasAnyCategory exeNode)
             | 4 -> exeNode.category4 || not (hasAnyCategory exeNode)
             | 5 -> exeNode.category5 || not (hasAnyCategory exeNode)
             | _ -> false)
        addCheckBoxColumn "Tabs" "enableTabs" 50 None |> ignore
        addCheckBoxColumn "Auto Grouping" "enableAutoGrouping" 100 (Some(fun (exeNode:ExeNode) -> exeNode.enableTabs)) |> ignore
        addCheckBoxColumn "Category 1" "category1" 70 (Some(categoryVisibility 1)) |> ignore
        addCheckBoxColumn "Category 2" "category2" 70 (Some(categoryVisibility 2)) |> ignore
        addCheckBoxColumn "Category 3" "category3" 70 (Some(categoryVisibility 3)) |> ignore
        addCheckBoxColumn "Category 4" "category4" 70 (Some(categoryVisibility 4)) |> ignore
        addCheckBoxColumn "Category 5" "category5" 70 (Some(categoryVisibility 5)) |> ignore
        tree.NodeControls.Add(
            let control = NodeControls.NodeIcon()
            control.ParentColumn <- nameColumn
            control.LeftMargin <- 3
            control.DataPropertyName <- "Icon"
            control)
        tree.NodeControls.Add(
            let control = SmoothNodeTextBox()
            control.Trimming <- StringTrimming.EllipsisCharacter
            control.DisplayHiddenContentInToolTip <- true
            control.ParentColumn <- nameColumn
            control.DataPropertyName <- "Text"
            control.LeftMargin <- 3
            control)
        tree.Model <- model
        tree,model
    let panel =
        let panel = Panel()
        toolBar.Dock <- DockStyle.Top
        tree.Dock <- DockStyle.Fill
        statusBar.Dock <- DockStyle.Bottom
        panel.Controls.Add(tree)
        panel.Controls.Add(toolBar)
        panel.Controls.Add(statusBar)
        panel

    do  
        this.populateNodes()
        Services.settings.notifyValue "enableTabbingByDefault" <| fun(_) ->
            this.populateNodes()

    member private this.populateNodes() =
        model.Nodes.Clear()
        ThreadHelper.queueBackground <| fun() ->
            let os = OS()
            let procs = Services.program.appWindows.fold (Map2()) <| fun procs hwnd ->
                invoker.asyncInvoke <| fun() ->
                    statusBar.Text <- sprintf "Scanning window 0x%x" hwnd
                let window = os.windowFromHwnd(hwnd)
                let procPath = window.pid.processPath
                procs.add procPath (procs.tryFind(procPath).def(List2()).append(window))
            let procNodes = procs.items.map <| fun (procPath, windows) ->
                let procNode = ExeNode(procPath)
                windows.iter <| fun window ->
                    let windowNode = WindowNode(window)
                    procNode.Nodes.Add(windowNode)
                procNode
            
            invoker.asyncInvoke <| fun() ->
                model.Nodes.Clear()
                procNodes.sortBy(fun n -> n.Text).iter <| fun node -> model.Nodes.Add(node)
                statusBar.Text <- "Ready"

    interface ISettingsView with
        member x.key = SettingsViewType.ProgramSettings
        member x.title = Localization.getString "Programs"
        member x.control = panel :> Control
