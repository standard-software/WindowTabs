namespace Bemo
open System

type IDragDropParent =
    abstract member dragBegin : unit -> unit
    abstract member dragDrop : (Pt * obj) -> unit
    abstract member dragEnd : unit -> unit

type IDragDropNotification =
    abstract member dragBegin : unit -> unit
    abstract member dragEnd : unit -> unit

type IDragDropTarget =
    inherit IDragDropNotification
    abstract member dragEnter : obj -> Pt -> bool
    abstract member dragMove : Pt -> unit
    abstract member dragExit : unit -> unit

// A strip of a group that can have strips on several edges (four-edge tabs).
// `group` is the same object for every strip of one group: a drag that goes
// from one of them to another stays in the group and only changes strip.
// `runsAcross`: the strip runs left to right (top, bottom), so the drag
// leaves it upwards or downwards; a left or right strip is left sideways.
type IGroupDragTarget =
    abstract member group : obj
    abstract member runsAcross : bool

// Empty-edge receivers use their exact bounds while highlighted.
type IReceiverDragTarget =
    abstract member isReceiver : bool

// A source-owned session keeps body hit testing and rollback on the group thread.
type IOwnWindowDragSession =
    inherit IDisposable
    abstract member targetAt : Pt * IntPtr * IntPtr option -> IntPtr option
    abstract member leaving : unit -> unit
    abstract member refreshHit : unit -> unit
    abstract member prepareReturn : unit -> unit
    abstract member cancel : unit -> unit
    abstract member complete : unit -> unit

// Cross-group hover requests are posted, never synchronously called back.
type IDragHoverGroup =
    abstract member ownsHoverWindow : IntPtr -> bool
    abstract member bodyDragTarget : Pt * IntPtr option -> IntPtr option
    abstract member hoverFront : obj option * (unit -> bool) -> unit
    // Transfer each receiver visit rollback to the drag owner. Rollbacks run
    // synchronously in reverse visit order, even after leaving all receivers.
    abstract member takeHoverRestore : unit -> (unit -> unit) option
    abstract member endHover : bool -> unit
    abstract member completeDrop : obj -> unit

type IOwnWindowDragSource =
    abstract member beginOwnWindowDrag : obj * (IntPtr -> bool) -> IOwnWindowDragSession option

type IDragDrop =
    abstract member registerNotification: IDragDropNotification -> unit
    abstract member unregisterNotification: IDragDropNotification -> unit
    abstract member registerTarget : (IntPtr * IDragDropTarget) -> unit
    abstract member unregisterTarget : IntPtr -> unit
    abstract member beginDrag : (IntPtr * (unit -> Img) * Pt * Pt * obj) -> unit