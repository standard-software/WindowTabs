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
    }

and TabStripPlacment = {
    showInside: bool
    bounds: Rect
    }

and TabPart =
    | TabBackground
    | TabIcon
    | TabClose

and TabDirection =
    | TabUp
    | TabDown

and TabAlignment =
    | TabLeft
    | TabCenter
    | TabRight

and TabDock =
    | TabDockTop
    | TabDockBottom
    | TabDockLeft
    | TabDockRight

and TabAppearanceInfo = {
    tabHeight: int
    tabMaxWidth: int
    tabOverlap: int
    tabHeightOffset : int
    tabIndentFlipped : int
    tabIndentNormal : int
    tabInactiveTextColor : Color
    tabMouseOverTextColor : Color
    tabActiveTextColor : Color
    tabFlashTextColor : Color
    tabInactiveTabColor: Color
    tabMouseOverTabColor: Color
    tabActiveTabColor: Color
    tabFlashTabColor: Color
    tabBorderColor: Color
    }
