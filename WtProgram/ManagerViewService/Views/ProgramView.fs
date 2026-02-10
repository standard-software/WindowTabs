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
    let mutable _category6 = Services.program.getCategoryEnabled(procPath, 6)
    let mutable _category7 = Services.program.getCategoryEnabled(procPath, 7)
    let mutable _category8 = Services.program.getCategoryEnabled(procPath, 8)
    let mutable _category9 = Services.program.getCategoryEnabled(procPath, 9)
    let mutable _category10 = Services.program.getCategoryEnabled(procPath, 10)
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
    member this.category6
        with get() = _category6
        and set(newValue) =
            _category6 <- newValue
            Services.program.setCategoryEnabled procPath 6 _category6
    member this.category7
        with get() = _category7
        and set(newValue) =
            _category7 <- newValue
            Services.program.setCategoryEnabled procPath 7 _category7
    member this.category8
        with get() = _category8
        and set(newValue) =
            _category8 <- newValue
            Services.program.setCategoryEnabled procPath 8 _category8
    member this.category9
        with get() = _category9
        and set(newValue) =
            _category9 <- newValue
            Services.program.setCategoryEnabled procPath 9 _category9
    member this.category10
        with get() = _category10
        and set(newValue) =
            _category10 <- newValue
            Services.program.setCategoryEnabled procPath 10 _category10

    // Get the category number (0 = unset, 1-10 = category number)
    member this.categoryNumber
        with get() =
            if _category1 then 1
            elif _category2 then 2
            elif _category3 then 3
            elif _category4 then 4
            elif _category5 then 5
            elif _category6 then 6
            elif _category7 then 7
            elif _category8 then 8
            elif _category9 then 9
            elif _category10 then 10
            else 0

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
        let addCheckBoxColumn displayName propName colWidth visibilityCheck =
            let content =
                match displayName with
                | Some name -> name
                | None -> Localization.getString(propName)
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
            exeNode.category1 || exeNode.category2 || exeNode.category3 || exeNode.category4 || exeNode.category5 ||
            exeNode.category6 || exeNode.category7 || exeNode.category8 || exeNode.category9 || exeNode.category10
        // Category visibility: show only when autoGrouping is ON and (this category is checked OR no category is checked)
        let categoryVisibility categoryNum (exeNode:ExeNode) =
            exeNode.enableAutoGrouping &&
            (let thisCategory =
                match categoryNum with
                | 1 -> exeNode.category1 | 2 -> exeNode.category2 | 3 -> exeNode.category3
                | 4 -> exeNode.category4 | 5 -> exeNode.category5 | 6 -> exeNode.category6
                | 7 -> exeNode.category7 | 8 -> exeNode.category8 | 9 -> exeNode.category9
                | 10 -> exeNode.category10 | _ -> false
             thisCategory || not (hasAnyCategory exeNode))
        addCheckBoxColumn None "enableTabs" 50 None |> ignore
        addCheckBoxColumn None "enableAutoGrouping" 100 (Some(fun (exeNode:ExeNode) -> exeNode.enableTabs)) |> ignore
        for i in 1..10 do
            let header = sprintf "%s%d" (Localization.getString("category")) i
            addCheckBoxColumn (Some header) (sprintf "category%d" i) 70 (Some(categoryVisibility i)) |> ignore
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
                // Sort by category number first (0 = unset first, then 1-5), then by name
                procNodes.sortBy(fun n -> (n.categoryNumber, n.Text)).iter <| fun node -> model.Nodes.Add(node)
                statusBar.Text <- "Ready"

    interface ISettingsView with
        member x.key = SettingsViewType.ProgramSettings
        member x.title = Localization.getString "Programs"
        member x.control = panel :> Control
