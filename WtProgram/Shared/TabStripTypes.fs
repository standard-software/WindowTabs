namespace Bemo
open System
open System.Drawing

type Tab = Tab of IntPtr

and TabInfo = {
    text: string
    iconSmall: Icon
    iconBig: Icon
    preview: unit -> Img
    isRenamed: bool
}

and TabDragInfo = {
    tab: Tab
    tabOffset: Pt
    imageOffset: Pt
    tabInfo: TabInfo
    // Multi-select drag continuation: source group hwnd + selection snapshot
    // (excluding the dragged tab) so the target group's dragEnter can move
    // the selected tabs across along with the dragged tab.
    sourceGroupHwnd: IntPtr
    selectedHwnds: List<IntPtr>
    }

and TabStripPlacment = {
    showInside: bool
    bounds: Rect
    // DPI scale of the monitor this placement was computed for (1.0 = 100%).
    // It travels WITH the placement so the strip's own drawing constants use
    // the exact factor that produced `bounds`; deriving it a second time from
    // a different rectangle could disagree at a monitor boundary and leave the
    // box and its contents at different scales.
    scale: float
    }

and TabPart =
    | TabBackground
    | TabIcon
    | TabClose
    | TabPin

and TabDirection =
    | TabUp
    | TabDown

and TabAlign =
    | TopLeft
    | TopRight

and TabDock =
    | TabDockTop
    | TabDockBottom
    | TabDockLeft
    | TabDockRight

and TabAppearanceInfo = {
    tabHeight: int
    tabMaxWidth: int
    tabPinnedTabWidth: int
    tabPinnedTabWidthIcon: bool
    tabOverlap: int
    tabHeightOffset : int
    tabIndentFlipped : int
    tabIndentNormal : int
    tabInactiveTextColor : Color
    tabSelectedTextColor : Color
    tabMouseOverTextColor : Color
    tabActiveTextColor : Color
    tabFlashTextColor : Color
    tabInactiveTabColor: Color
    tabSelectedTabColor: Color
    tabMouseOverTabColor: Color
    tabActiveTabColor: Color
    tabFlashTabColor: Color
    tabInactiveBorderColor: Color
    tabSelectedBorderColor: Color
    tabMouseOverBorderColor: Color
    tabActiveBorderColor: Color
    tabFlashBorderColor: Color
    }
