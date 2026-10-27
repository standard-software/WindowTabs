namespace Bemo
open System
open System.Drawing

type WindowDecorator = {
    windowBounds: Rect
    monitorBounds: List2<Rect>
    decoratorHeight : int
    decoratorHeightOffset : int
    decoratorIndentFlipped : int
    decoratorIndentFlippedRight : int
    decoratorIndentNormal : int
    decoratorIndentNormalRight : int
    } with

    // (The screen region this type used to build with GDI regions is gone: the
    // questions asked of it are rectangle arithmetic, and the regions were
    // created on every placement update and freed only by the garbage
    // collector - a drag of a window's top edge produced two thousand of them
    // a second.)

    member private this.indents(isCentered) =
        if isCentered then this.decoratorIndentFlipped, this.decoratorIndentFlippedRight
        else this.decoratorIndentNormal, this.decoratorIndentNormalRight

    member private this.outsideBounds =
        let rect = this.windowBounds
        let left, right = this.indents(false)
        Rect(
            Pt(rect.x + left, rect.y - this.decoratorHeight + this.decoratorHeightOffset),
            Sz(rect.width - left - right, this.decoratorHeight)
        )

    member this.insideBounds = 
        let rect = this.windowBounds
        let left, right = this.indents(true)
        Rect(
            Pt(rect.x + left, rect.y - 1), // offset by one so it covers edge case #741
            Sz(rect.width - left - right, this.decoratorHeight)
        )

    member this.shouldShowInside =
        let box (r: Rect) = r.x, r.y, r.width, r.height
        TabEdgeLayout.showsInside TabEdges.Edge.Top
            (this.monitorBounds.list |> List.map box)
            (box this.insideBounds) (box this.outsideBounds)

    member this.placementFor(verticalDirection: string, restrictToMonitor: bool) =
        let box (r: Rect) = r.x, r.y, r.width, r.height
        let automaticInside =
            if restrictToMonitor then
                TabEdgeLayout.showsInsideHome TabEdges.Edge.Top
                    (this.monitorBounds.list |> List.map box) (box this.outsideBounds)
            else this.shouldShowInside
        let inside = TabBehaviorPolicy.showInside verticalDirection automaticInside
        inside, (if inside then this.insideBounds else this.outsideBounds)

    member this.showInside(verticalDirection: string) =
        TabBehaviorPolicy.showInside verticalDirection this.shouldShowInside

    member this.boundsFor(verticalDirection: string) : Rect =
        if this.showInside(verticalDirection) then this.insideBounds else this.outsideBounds
    

