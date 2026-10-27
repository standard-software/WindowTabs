namespace Bemo

open System
open Newtonsoft.Json.Linq

// The settings of the four edges as the settings file holds them, read
// without starting WindowTabs: TabEdgeSettings.Tests.fsx loads this file.
// The settings dialog (Appearance tab, EdgeSettingsPanel) writes them:
//
//   "TabEdges": ["Top", "Left"]
//       the edges tabs may use. Absent, empty or unreadable: the top edge
//       alone, which is the default and today's behaviour.
//   "TabEdgeLeft": { "tabHeight": 26, "tabMaxWidth": 200,
//                    "tabPinnedTabWidth": 40, "tabPinnedTabWidthIcon": true,
//                    "tabOverlap": 20, "tabShape": "Rounded vertical line",
//                    "tabAlignment": "TopLeft", "tabDirection": "outside",
//                    "tabText": "horizontal",
//                    "tabOutsideDistanceStart": 10, "tabOutsideDistanceEnd": 10,
//                    "tabInsideDistanceStart": 150, "tabInsideDistanceEnd": 150 }
//       one edge's own values, every one of them optional. The distances
//       are the panel's [top][bottom] on a left or right edge (8).
//
// The top edge keeps today's keys (TabAppearance, TabPositionByDefault,
// TabVerticalDirection), and the dialog goes on writing its values there,
// so that an older build reading the file finds the top edge as it left it:
// moving today's settings to the top edge (9) is reading them where they
// are. TabAppearance holds tabIndentNormal / tabIndentFlipped on the left,
// and tabIndentNormalRight / tabIndentFlippedRight on the right. An absent
// right key falls back to the corresponding left value when read.
module TabEdgeSettings =
    open TabEdges

    /// Raised on the dialog's thread after it has written any value kept
    /// here. The groups read the values again (TabStripDecorator); the top
    /// edge's appearance, alignment and direction go on arriving through
    /// today's notifications of those settings.
    let changed = Event<unit>()

    [<Literal>]
    let allowedKey = "TabEdges"

    /// The edges this build draws a strip for: all four. An edge the file
    /// lists that a build does not draw is passed over, so that no tab is
    /// ever put where it cannot be seen, and a tab saved on such an edge
    /// comes back on the first allowed one (1-5).
    let drawnEdges = [ Edge.Top; Edge.Left; Edge.Right; Edge.Bottom ]

    /// "Top", "Left", "Right", "Bottom": the names a saved tab's edge is
    /// written with.
    let edgeName (edge: Edge) = SavedTabState.formatEdge (savedOfEdge edge)

    /// Read like a saved tab's edge, without regard to case.
    let parseEdgeName (s: string) = SavedTabState.parseEdge s |> Option.map edgeOfSaved

    /// The key of one edge's own values: "TabEdgeLeft" and so on.
    let edgeKey (edge: Edge) = "TabEdge" + edgeName edge

    // Keys are read without regard to case, as the saved tabs' keys are.
    let private propertyOf (o: JObject) (key: string) =
        o.Properties()
        |> Seq.tryFind (fun p -> String.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun p -> p.Value)

    let private stringIn (o: JObject) (key: string) =
        match propertyOf o key with
        | Some(:? JValue as v) when v.Type = JTokenType.String -> Some(string v.Value)
        | _ -> None

    let private intIn (o: JObject) (key: string) =
        match propertyOf o key with
        | Some(:? JValue as v) when v.Type = JTokenType.Integer ->
            (try Some(Convert.ToInt32(v.Value)) with _ -> None)
        | _ -> None

    let private boolIn (o: JObject) (key: string) =
        match propertyOf o key with
        | Some(:? JValue as v) when v.Type = JTokenType.Boolean -> Some(unbox<bool> v.Value)
        | _ -> None

    let private namesIn (root: JObject) =
        match propertyOf root allowedKey with
        | Some(:? JArray as a) ->
            a
            |> Seq.choose (fun v ->
                match v with
                | :? JValue as j when j.Type = JTokenType.String -> parseEdgeName (string j.Value)
                | _ -> None)
            |> List.ofSeq
        | _ -> []

    /// The edges checked under "Tab position" as the file lists them, in
    /// priority order - the ones this build does not draw yet included, for
    /// the dialog shows what was checked. Nothing listed is the top edge.
    let listedOf (root: JObject) : Edge list =
        namesIn root |> TabEdges.allowedOf |> Option.map edgesOf |> Option.defaultValue [ Edge.Top ]

    /// The allowed edges. A name that is not an edge, a value that is not a
    /// string and an edge this build does not draw are passed over; nothing
    /// left, no key, or a key that is not a list, is the top edge alone.
    let allowedOf (root: JObject) : Allowed =
        namesIn root
        |> List.filter (fun e -> List.contains e drawnEdges)
        |> TabEdges.allowedOf
        |> Option.defaultValue topOnly

    /// One edge's values as the file has them; None where it has none (or
    /// one that cannot be read), and the edge's default is used.
    type EdgeValues = {
        hideMode: string option
        hideDelay: int option
        tabHeight: int option
        tabMaxWidth: int option
        tabPinnedTabWidth: int option
        tabPinnedTabWidthIcon: bool option
        tabOverlap: int option
        tabShape: string option
        align: Align option
        direction: Direction option
        text: SideText option
        textBottomInside: bool option
        outsideStart: int option
        outsideEnd: int option
        insideStart: int option
        insideEnd: int option
    }

    let noValues = {
        hideMode = None
        hideDelay = None
        tabHeight = None
        tabMaxWidth = None
        tabPinnedTabWidth = None
        tabPinnedTabWidthIcon = None
        tabOverlap = None
        tabShape = None
        align = None
        direction = None
        text = None
        textBottomInside = None
        outsideStart = None
        outsideEnd = None
        insideStart = None
        insideEnd = None
    }

    // "TopLeft" / "TopRight" as a saved tab writes them - the start and the
    // end of the strip, so top and bottom on a left strip - and the plain
    // words as well.
    let private parseAlign (s: string) =
        match s.ToLowerInvariant() with
        | "topleft" | "left" | "top" | "start" -> Some(AlignStart)
        | "topright" | "right" | "bottom" | "end" -> Some(AlignEnd)
        | _ -> None

    let private parseDirection (s: string) =
        match s.ToLowerInvariant() with
        | "outside" -> Some(Direction.Outside)
        | "inside" -> Some(Direction.AlwaysInside)
        | _ -> None

    let private parseText (s: string) =
        match s.ToLowerInvariant() with
        | "horizontal" -> Some(SideText.Horizontal)
        | "vertical" -> Some(SideText.Vertical)
        | _ -> None

    /// The values under "TabEdge<name>". Sizes must be above zero and the
    /// overlap not below it; anything else is as if it were not there.
    let valuesOf (root: JObject) (edge: Edge) : EdgeValues =
        match propertyOf root (edgeKey edge) with
        | Some(:? JObject as o) ->
            let positive key = intIn o key |> Option.filter (fun n -> n > 0)
            let distance key = intIn o key |> Option.filter (fun n -> n >= 0)
            { hideMode = stringIn o "hideTabsWhenDownByDefault"
              hideDelay = distance "hideTabsDelayMilliseconds"
              tabHeight = positive "tabHeight"
              tabMaxWidth = positive "tabMaxWidth"
              tabPinnedTabWidth = positive "tabPinnedTabWidth"
              tabPinnedTabWidthIcon = boolIn o "tabPinnedTabWidthIcon"
              tabOverlap = intIn o "tabOverlap" |> Option.filter (fun n -> n >= 0)
              tabShape = stringIn o "tabShape"
              textBottomInside = boolIn o "tabTextBottomInside"
              align = stringIn o "tabAlignment" |> Option.bind parseAlign
              direction = stringIn o "tabDirection" |> Option.bind parseDirection
              text = stringIn o "tabText" |> Option.bind parseText
                     |> Option.filter (fun t -> runsHorizontally edge || offerSideHorizontalText || t <> SideText.Horizontal)
              outsideStart = distance "tabOutsideDistanceStart"
              outsideEnd = distance "tabOutsideDistanceEnd"
              insideStart = distance "tabInsideDistanceStart"
              insideEnd = distance "tabInsideDistanceEnd" }
        | _ -> noValues

    let normalizeHideMode mode =
        match mode with
        | "never" | "down" | "doubleclick" -> mode
        | _ -> "doubleclick"

    /// Missing edge values follow the current top values without being saved.
    /// Mode and delay inherit independently; an explicit value always wins.
    let hideSettings (root: JObject) edge =
        let topMode =
            normalizeHideMode (defaultArg (stringIn root "HideTabsWhenDownByDefault")
                (if defaultArg (boolIn root "HideTabsWhenDownByDefault") false then "down" else "never"))
        let topDelay = defaultArg (intIn root "HideTabsDelayMilliseconds") 3000
        if edge = Edge.Top then topMode, topDelay
        else
            let values = valuesOf root edge
            normalizeHideMode (defaultArg values.hideMode topMode), defaultArg values.hideDelay topDelay

    /// Only enabled edges in delayed mode need a recent-z-order timer.
    /// The top-only path already owns its legacy timer.
    let recentHideEdges allowed (settings: Map<Edge, string * int>) =
        if TabEdges.isTopOnly allowed then []
        else TabEdges.edgesOf allowed |> List.filter (fun edge -> fst settings.[edge] = "down")

    /// An edge's layout: what the file sets, over what the edge starts from
    /// (newEdgeLayout for an edge other than the top). The text belongs to
    /// the left and right edges only.
    let layoutOf (values: EdgeValues) (start: EdgeLayout) =
        { start with
            defaultAlign = defaultArg values.align start.defaultAlign
            direction = defaultArg values.direction start.direction
            outsideDistance =
                (defaultArg values.outsideStart (fst start.outsideDistance),
                 defaultArg values.outsideEnd (snd start.outsideDistance))
            insideDistance =
                (defaultArg values.insideStart (fst start.insideDistance),
                 defaultArg values.insideEnd (snd start.insideDistance))
            text = start.text |> Option.map (fun t -> defaultArg values.text t) }

    /// The layout of an edge other than the top, from the file.
    let edgeLayout (root: JObject) (edge: Edge) = layoutOf (valuesOf root edge) (newEdgeLayout edge)

    // ---------------------------------------------------------- shapes --

    /// The shapes of a left or right strip written across (8): the ones
    /// whose sides stand across the strip - the three lines, turned, and
    /// the four boxes.
    let horizontalShapes =
        [ TabShape.rounded; TabShape.strong; TabShape.angular
          TabShape.rectangle; TabShape.roundedRectangle; TabShape.strongRoundedRectangle; TabShape.angularRectangle ]

    /// The shapes an edge's panel offers for its text: every shape, but the
    /// seven above for a left or right strip written across.
    let shapesFor (text: SideText option) =
        match text with
        | Some(SideText.Horizontal) -> horizontalShapes
        | _ -> TabShape.all

    /// What a shape becomes when the text no longer offers it (8): the
    /// rounded line written across, the S-curve written upwards.
    let fallbackShape (text: SideText option) =
        match text with
        | Some(SideText.Horizontal) -> TabShape.rounded
        | _ -> TabShape.scurve

    /// A shape the text offers: the shape itself, or the text's fallback.
    let shapeFor (text: SideText option) (shape: string) =
        let s = TabShape.normalize shape
        if List.contains s (shapesFor text) then s else fallbackShape text

    /// An edge's shape: the file's, if its text offers it; otherwise a left
    /// or right edge written across starts as the rounded line (8) and the
    /// rest as the program's default.
    let edgeShape (edge: Edge) (values: EdgeValues) (programDefault: string) =
        let text = (layoutOf values (newEdgeLayout edge)).text
        match values.tabShape, text with
        | Some(shape), _ -> shapeFor text shape
        | None, Some(SideText.Horizontal) -> TabShape.rounded
        | None, _ -> shapeFor text programDefault

    /// The values with the shape an edge is drawn with filled in.
    let withShape (edge: Edge) (programDefault: string) (values: EdgeValues) =
        { values with tabShape = Some(edgeShape edge values programDefault) }

    // --------------------------------------------------------- writing --

    // A key is written where the file already has it, whatever its case, so
    // that a value edited by hand is replaced rather than doubled.
    let private put (o: JObject) (key: string) (value: JToken) =
        match o.Properties() |> Seq.tryFind (fun p -> String.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) with
        | Some(p) -> p.Value <- value
        | None -> o.Add(key, value)

    /// Check the edges (the dialog keeps at least one checked).
    let writeListed (root: JObject) (edges: Edge list) =
        let names = priority |> List.filter (fun e -> List.contains e edges) |> List.map edgeName
        put root allowedKey (JArray(names |> List.map (fun n -> JValue(n) :> obj) |> Array.ofList))

    let private edgeObject (root: JObject) (edge: Edge) =
        match propertyOf root (edgeKey edge) with
        | Some(:? JObject as o) -> o
        | _ ->
            let o = JObject()
            put root (edgeKey edge) o
            o

    let writeInt (root: JObject) (edge: Edge) (key: string) (value: int) =
        put (edgeObject root edge) key (JValue(int64 value))

    let writeBool (root: JObject) (edge: Edge) (key: string) (value: bool) =
        put (edgeObject root edge) key (JValue(value))

    let writeString (root: JObject) (edge: Edge) (key: string) (value: string) =
        put (edgeObject root edge) key (JValue(value))

    /// The names the values are written with - read back by valuesOf.
    let alignName (align: Align) = match align with AlignStart -> "TopLeft" | AlignEnd -> "TopRight"
    let directionName (direction: Direction) =
        match direction with
        | Direction.Outside -> "outside"
        | Direction.AlwaysInside -> "inside"
    let textName (text: SideText) =
        match text with
        | SideText.Horizontal -> "horizontal"
        | SideText.Vertical -> "vertical"

    /// Return a copy with the requested appearance applied to the opposite edge.
    /// Side edges also copy text orientation so the shape stays valid.
    let copyOppositeValues (root: JObject) (sourceEdge: Edge) (source: EdgeValues) =
        let result = root.DeepClone() :?> JObject
        let destination =
            match sourceEdge with
            | Edge.Top -> Some Edge.Bottom
            | Edge.Left -> Some Edge.Right
            | Edge.Right -> Some Edge.Left
            | Edge.Bottom -> None
        destination |> Option.iter (fun edge ->
            for key, value in
                [ "hideTabsDelayMilliseconds", source.hideDelay
                  "tabHeight", source.tabHeight; "tabMaxWidth", source.tabMaxWidth
                  "tabPinnedTabWidth", source.tabPinnedTabWidth; "tabOverlap", source.tabOverlap
                  "tabOutsideDistanceStart", source.outsideStart; "tabOutsideDistanceEnd", source.outsideEnd
                  "tabInsideDistanceStart", source.insideStart; "tabInsideDistanceEnd", source.insideEnd ] do
                value |> Option.iter (writeInt result edge key)
            source.hideMode |> Option.iter (writeString result edge "hideTabsWhenDownByDefault")
            source.tabPinnedTabWidthIcon |> Option.iter (writeBool result edge "tabPinnedTabWidthIcon")
            if sourceEdge = Edge.Left || sourceEdge = Edge.Right then
                source.textBottomInside |> Option.iter (writeBool result edge "tabTextBottomInside")
                source.text |> Option.iter (fun text -> writeString result edge "tabText" (textName text))
            source.tabShape |> Option.iter (writeString result edge "tabShape")
            source.align |> Option.iter (fun align -> writeString result edge "tabAlignment" (alignName align))
            source.direction |> Option.iter (fun direction -> writeString result edge "tabDirection" (directionName direction)))
        if sourceEdge = Edge.Bottom then
            source.hideMode |> Option.iter (fun v -> put result "HideTabsWhenDownByDefault" (JValue(v)))
            source.hideDelay |> Option.iter (fun v -> put result "HideTabsDelayMilliseconds" (JValue(v)))
            let appearance =
                match propertyOf result "TabAppearance" with
                | Some(:? JObject as existing) -> existing
                | _ ->
                    let created = JObject()
                    put result "TabAppearance" created
                    created
            for key, value in
                [ "tabHeight", source.tabHeight; "tabMaxWidth", source.tabMaxWidth
                  "tabPinnedTabWidth", source.tabPinnedTabWidth; "tabOverlap", source.tabOverlap
                  "tabIndentNormal", source.outsideStart; "tabIndentNormalRight", source.outsideEnd
                  "tabIndentFlipped", source.insideStart; "tabIndentFlippedRight", source.insideEnd ] do
                value |> Option.iter (fun n -> put appearance key (JValue(int64 n)))
            source.tabPinnedTabWidthIcon |> Option.iter (fun b -> put appearance "tabPinnedTabWidthIcon" (JValue(b)))
            source.tabShape |> Option.iter (fun s -> put appearance "tabShape" (JValue(s)))
            source.align |> Option.iter (fun a -> put result "TabPositionByDefault" (JValue(alignName a)))
            source.direction |> Option.iter (fun d ->
                let mode = if d = Direction.AlwaysInside then TabBehaviorPolicy.verticalAlwaysDown else TabBehaviorPolicy.verticalAuto
                put result "TabVerticalDirection" (JValue(mode)))
        result

    /// A distance rounded to device pixels at the drawing scale.
    let scaledDistance (scale: float) (distance: int) = int (System.Math.Round(float distance * scale))

    /// An edge's distances at `scale`: (outside, inside), each (start, end)
    /// - [left][right] on the bottom edge, [top][bottom] on a left or right
    /// one (8, 10: each edge's values go through the DPI scale).
    let scaledDistances (scale: float) (layout: EdgeLayout) =
        let both (a, b) = scaledDistance scale a, scaledDistance scale b
        both layout.outsideDistance, both layout.insideDistance
