namespace Bemo
open System
open System.Drawing

type WindowDecorator = {
    windowBounds: Rect
    monitorBounds: List2<Rect>
    decoratorHeight : int
    decoratorHeightOffset : int
    decoratorIndentFlipped : int
    decoratorIndentNormal : int
    } with

    // (The screen region this type used to build with GDI regions is gone: the
    // questions asked of it are rectangle arithmetic, and the regions were
    // created on every placement update and freed only by the garbage
    // collector - a drag of a window's top edge produced two thousand of them
    // a second.)

    member private this.indent(isCentered) = if isCentered then this.decoratorIndentFlipped else this.decoratorIndentNormal

    member private this.outsideBounds =
        let rect = this.windowBounds
        let indent = this.indent(false)
        Rect(
            Pt(rect.x + indent, rect.y - this.decoratorHeight + this.decoratorHeightOffset),
            Sz(rect.width - 2 * indent, this.decoratorHeight)
        )

    member this.insideBounds = 
        let rect = this.windowBounds
        let indent = this.indent(true)
        Rect(
            Pt(rect.x + indent, rect.y - 1), // offset by one so it covers edge case #741
            Sz(rect.width - 2 * indent, this.decoratorHeight)
        )

    member this.shouldShowInside =
        // How much of the strip a monitor would show, inside the window versus
        // above it. Plain rectangle intersection: the same answer the GDI
        // regions gave, without a handle per call.
        let overlapHeight (monitor: Rect) (strip: Rect) =
            let top = max monitor.top strip.top
            let bottom = min monitor.bottom strip.bottom
            max 0 (bottom - top)
        let inside = this.insideBounds
        let outside = this.outsideBounds
        this.monitorBounds.any <| fun monitorBounds ->
            let horizontal (strip: Rect) =
                min monitorBounds.right strip.right > max monitorBounds.left strip.left
            let heightOf strip = if horizontal strip then overlapHeight monitorBounds strip else 0
            heightOf inside > heightOf outside

    member this.showInside(verticalDirection: string) =
        TabBehaviorPolicy.showInside verticalDirection this.shouldShowInside

    member this.boundsFor(verticalDirection: string) : Rect =
        if this.showInside(verticalDirection) then this.insideBounds else this.outsideBounds
    

