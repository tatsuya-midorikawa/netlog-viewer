/// Port of timeline_data_series.js + timeline_graph_view.js + timeline_view.js: a
/// zoomable/pannable Canvas2D timeline of network metrics over time. Series match
/// events by resolved type/source NAME. The separate HorizontalScrollbarView is
/// folded into an internal position/range (no scrollbar widget).
module Netlog.Webview.Timeline

open System.Collections.Generic
open Fable.Core
open Netlog.Core
open Netlog.Webview.Dom
open Netlog.Webview.View

let tabId = "timeline"
let tabName = "Timeline"

[<Emit("new Date($0).toLocaleTimeString()")>]
let private localeTime (ms: float) : string = jsNative

[<Emit("$0.toFixed($1)")>]
let private toFixed (n: float) (d: int) : string = jsNative

// Layout constants (from timeline_graph_view.js).
let private DEFAULT_SCALE = 1000.0
let private MIN_SCALE = 5.0
let private MAX_VERTICAL_LABELS = 6
let private LABEL_VERTICAL_SPACING = 4.0
let private LABEL_HORIZONTAL_SPACING = 3.0
let private LABEL_LABEL_HORIZONTAL_SPACING = 25.0
let private Y_AXIS_TICK_LENGTH = 10.0
let private ZOOM_RATE = 1.25
let private DRAG_RATE = 3.0
let private GRID_COLOR = "#CCC"
let private TEXT_COLOR = "#000"
let private BACKGROUND_COLOR = "#FFF"

type DataType =
    | SourceCount
    | BytesPerSecond

[<Struct>]
type DataPoint = { Time: float; Value: float }

let private paramNum (e: Model.Event) (key: string) : float option =
    match e.Params with
    | Some p -> Json.tryNumber p key
    | None -> None

/// Formats one sampled value for the status/hover line, matching the units
/// GraphView.Graph's y-axis labels already use (plain count vs B/s -> kB/s -> MB/s).
let private formatValue (dataType: DataType) (v: float) : string =
    match dataType with
    | SourceCount -> toFixed v 0
    | BytesPerSecond ->
        if v >= 1048576.0 then toFixed (v / 1048576.0) 1 + " MB/s"
        elif v >= 1024.0 then toFixed (v / 1024.0) 1 + " kB/s"
        else toFixed v 0 + " B/s"

[<AbstractClass>]
type DataSeries(dataType: DataType) =
    let points = ResizeArray<DataPoint>()
    member val Color = "red" with get, set
    member val IsVisible = true with get, set
    // The legend label (set once, in View.OnLoadLogFinish's `add` helper below), used
    // by GraphView.DescribeValuesAt to label the hover/status line.
    member val Name = "" with get, set
    member _.DataType = dataType
    member _.Points = points

    member _.AddPoint(timeTicks: float, value: float) =
        points.Add { Time = TimeUtil.convertTimeTicksToTime timeTicks; Value = value }

    abstract OnReceivedLogEntry: Model.Event -> unit

    /// Default: most recent value before each sampled time.
    abstract GetValues: float * float * int -> float[]
    default _.GetValues(startTime, stepSize, count) =
        let values = Array.zeroCreate count
        let mutable nextPoint = 0
        let mutable currentValue = 0.0
        let mutable time = startTime
        for i in 0 .. count - 1 do
            while nextPoint < points.Count && points.[nextPoint].Time < time do
                currentValue <- points.[nextPoint].Value
                nextPoint <- nextPoint + 1
            values.[i] <- currentValue
            time <- time + stepSize
        values

/// Counts sources of a type that have an open BEGIN of an event type.
type SourceCountSeries(sourceTypeId: int option, eventTypeId: int option, phaseBegin: int option, phaseEnd: int option) =
    inherit DataSeries(SourceCount)
    let active = HashSet<int>()

    member this.BeginEvent(id: int, time: float) =
        if active.Add id then
            this.AddPoint(time, float active.Count)

    member this.EndEvent(id: int, time: float) =
        if active.Remove id then
            this.AddPoint(time, float active.Count)

    override this.OnReceivedLogEntry(e) =
        if Some e.SourceType = sourceTypeId && Some e.Type = eventTypeId then
            if Some e.Phase = phaseBegin then this.BeginEvent(e.SourceId, e.Time)
            elif Some e.Phase = phaseEnd then this.EndEvent(e.SourceId, e.Time)

/// Sockets in use, with special handling so SSL sockets are freed after handshake.
type SocketsInUseSeries
    (socketTypeId: int option, inUseId: int option, sslConnectId: int option, phaseBegin: int option, phaseEnd: int option) =
    inherit SourceCountSeries(socketTypeId, inUseId, phaseBegin, phaseEnd)

    override this.OnReceivedLogEntry(e) =
        if Some e.Type = sslConnectId && Some e.Phase = phaseEnd then
            this.EndEvent(e.SourceId, e.Time)
        else
            base.OnReceivedLogEntry(e)

[<AbstractClass>]
type TransferRateSeries() =
    inherit DataSeries(BytesPerSecond)

    /// Average bytes/sec over each interval (port of TransferRateDataSeries).
    override this.GetValues(startTime, stepSize, count) =
        let points = this.Points
        let values = Array.zeroCreate count
        let mutable nextPoint = 0
        while nextPoint < points.Count && points.[nextPoint].Time < startTime - stepSize do
            nextPoint <- nextPoint + 1
        let mutable time = startTime
        for i in 0 .. count - 1 do
            let mutable transferred = 0.0
            while nextPoint < points.Count && points.[nextPoint].Time < time do
                transferred <- transferred + points.[nextPoint].Value
                nextPoint <- nextPoint + 1
            values.[i] <- 1000.0 * transferred / stepSize
            time <- time + stepSize
        values

type NetworkTransferRateSeries(tcpId: int option, udpId: int option) =
    inherit TransferRateSeries()

    override this.OnReceivedLogEntry(e) =
        if Some e.Type = tcpId || Some e.Type = udpId then
            match paramNum e "byte_count" with
            | Some n -> this.AddPoint(e.Time, n)
            | None -> ()

type DiskCacheTransferRateSeries(diskCacheTypeId: int option, eventId: int option, phaseEnd: int option) =
    inherit TransferRateSeries()

    override this.OnReceivedLogEntry(e) =
        if Some e.SourceType = diskCacheTypeId && Some e.Type = eventId && Some e.Phase = phaseEnd then
            match paramNum e "bytes_copied" with
            | Some n when n <> 0.0 -> this.AddPoint(e.Time, n)
            | _ -> ()

/// One y-axis-scaled graph for all series of a given data type.
type private Graph(dataType: DataType, alignRight: bool) =
    let series = ResizeArray<DataSeries>()
    let mutable width = 0.0
    let mutable height = 0.0
    let mutable fontHeight = 0.0
    let mutable startTime = 0.0
    let mutable scale = 0.0
    let mutable maxV = 0.0
    let mutable labels: string[] = [||]

    member _.AddSeries(s: DataSeries) = series.Add s

    /// A single sampled value per visible series near `time`, for the hover/status
    /// line (GraphView.DescribeValuesAt) -- reuses each series' own GetValues with a
    /// 1-sample window instead of a whole-canvas-width array.
    member _.ValuesAt(time: float, stepSize: float) : (DataSeries * float) list =
        [ for s in series do
              if s.IsVisible then
                  let vs = s.GetValues(time, stepSize, 1)
                  if vs.Length > 0 then
                      yield (s, vs.[0]) ]

    member private _.GetValues(s: DataSeries) : float[] option =
        if s.IsVisible then Some(s.GetValues(startTime, scale, int width)) else None

    member private _.LayoutLabelsBasic(maxValue: float, maxDecimalDigits: int) =
        labels <- [||]
        if maxValue = 0.0 then
            maxV <- maxValue
        else
            let minLabelSpacing = 2.0 * fontHeight + LABEL_VERTICAL_SPACING
            let mutable maxLabels = 1.0 + height / minLabelSpacing
            if maxLabels < 2.0 then maxLabels <- 2.0
            elif maxLabels > float MAX_VERTICAL_LABELS then maxLabels <- float MAX_VERTICAL_LABELS

            let mutable stepSize = 10.0 ** float (-maxDecimalDigits)
            let mutable stepDigits = maxDecimalDigits
            let mutable go = true
            while go do
                if ceil (maxValue / stepSize) + 1.0 <= maxLabels then go <- false
                elif ceil (maxValue / (stepSize * 2.0)) + 1.0 <= maxLabels then stepSize <- stepSize * 2.0; go <- false
                elif ceil (maxValue / (stepSize * 5.0)) + 1.0 <= maxLabels then stepSize <- stepSize * 5.0; go <- false
                else
                    stepSize <- stepSize * 10.0
                    if stepDigits > 0 then stepDigits <- stepDigits - 1

            maxV <- ceil (maxValue / stepSize) * stepSize
            let ls = ResizeArray<string>()
            let mutable label = maxV
            while label >= 0.0 do
                ls.Add(toFixed label stepDigits)
                label <- label - stepSize
            labels <- ls.ToArray()

    member private this.LayoutLabels(maxValue: float) =
        if dataType <> BytesPerSecond then
            this.LayoutLabelsBasic(maxValue, 0)
        else
            let units = [| "B/s"; "kB/s"; "MB/s"; "GB/s"; "TB/s"; "PB/s" |]
            let mutable unit = 1
            let mutable mv = maxValue / 1024.0
            while unit + 1 < units.Length && mv >= 999.0 do
                mv <- mv / 1024.0
                unit <- unit + 1
            this.LayoutLabelsBasic(mv, 1)
            labels <- labels |> Array.map (fun l -> l + " " + units.[unit])
            maxV <- maxV * (1024.0 ** float unit)

    member this.Layout(w: float, h: float, fh: float, st: float, sc: float) =
        width <- w
        height <- h
        fontHeight <- fh
        startTime <- st
        scale <- sc
        let mutable mx = 0.0
        for s in series do
            match this.GetValues s with
            | Some vs -> for v in vs do (if v > mx then mx <- v)
            | None -> ()
        this.LayoutLabels mx

    member _.DrawTicks(ctx: Context2d) =
        let x1, x2 =
            if alignRight then width - 1.0, width - 1.0 - Y_AXIS_TICK_LENGTH else 0.0, Y_AXIS_TICK_LENGTH
        ctx.fillStyle <- GRID_COLOR
        ctx.beginPath ()
        for i in 1 .. labels.Length - 2 do
            let y = round (height * float i / float (labels.Length - 1))
            ctx.moveTo (x1, y)
            ctx.lineTo (x2, y)
        ctx.stroke ()

    member this.DrawLines(ctx: Context2d) =
        let bottom = height - 1.0
        let scaleY = if maxV <> 0.0 then bottom / maxV else 0.0
        for i in series.Count - 1 .. -1 .. 0 do
            match this.GetValues series.[i] with
            | Some values ->
                ctx.strokeStyle <- series.[i].Color
                ctx.beginPath ()
                for x in 0 .. values.Length - 1 do
                    ctx.lineTo (float x, bottom - round (values.[x] * scaleY))
                ctx.stroke ()
            | None -> ()

    member _.DrawLabels(ctx: Context2d) =
        if labels.Length = 0 then
            ()
        else
            let x =
                if alignRight then
                    width - LABEL_HORIZONTAL_SPACING
                else
                    let mutable mw = 0.0
                    for l in labels do
                        let tw = ctx.measureText(l).width
                        if mw < tw then mw <- tw
                    mw + LABEL_HORIZONTAL_SPACING
            ctx.fillStyle <- TEXT_COLOR
            ctx.textAlign <- "right"
            ctx.textBaseline <- "top"
            ctx.fillText (labels.[0], x, 0.0)
            ctx.textBaseline <- "bottom"
            let step = (height - 1.0) / float (labels.Length - 1)
            for i in 1 .. labels.Length - 1 do
                ctx.fillText (labels.[i], x, step * float i)

/// The canvas graph: holds the time range/scale/position and draws everything.
type private GraphView() =
    let mutable startTime = 0.0
    let mutable endTime = 1.0
    let mutable scale = DEFAULT_SCALE
    let mutable position = 0.0
    let mutable range = 0.0
    let mutable canvasWidth = 0.0
    let mutable graphs = [ Graph(BytesPerSecond, true); Graph(SourceCount, false) ]

    member private _.GetLength() = floor ((endTime - startTime) / scale)

    member this.UpdateRange(width: float) =
        canvasWidth <- width
        range <- max 0.0 (this.GetLength() - width)
        if position > range then position <- range

    member this.Resize(width: float) =
        this.UpdateRange width
        position <- range

    member _.ScrolledToRightEdge = position = range

    member this.SetDateRange(startMs: float, endMs: float) =
        startTime <- startMs
        endTime <- endMs
        if endTime <= startTime then startTime <- endTime - 1.0
        scale <- DEFAULT_SCALE
        position <- range

    /// Restores the default zoom level and scrolls to the right edge, without
    /// touching the loaded date range -- used by the "Reset zoom" button/Home key.
    member this.ResetZoom() =
        scale <- DEFAULT_SCALE
        this.UpdateRange canvasWidth
        position <- range

    member _.SetDataSeries(dataSeries: DataSeries list) =
        let bytes = Graph(BytesPerSecond, true)
        let count = Graph(SourceCount, false)
        for s in dataSeries do
            (if s.DataType = BytesPerSecond then bytes else count).AddSeries s
        graphs <- [ bytes; count ]

    member this.HorizontalScroll(delta: float) =
        let mutable np = position + round delta
        if np < 0.0 then np <- 0.0
        elif np > range then np <- range
        position <- np

    member this.Zoom(ratio: float) =
        let oldScale = scale
        scale <- scale * ratio
        if scale < MIN_SCALE then scale <- MIN_SCALE
        if scale <> oldScale then
            if this.ScrolledToRightEdge then
                this.UpdateRange canvasWidth
                position <- range
            else
                let oldMaxTime = oldScale * (position + canvasWidth)
                let newMaxTime = round (oldMaxTime / scale)
                let newPosition = newMaxTime - canvasWidth
                this.UpdateRange canvasWidth
                this.HorizontalScroll(newPosition - position)

    member private _.DrawTimeLabels(ctx: Context2d, width: float, height: float, textHeight: float, startT: float) =
        let sampleText = localeTime startT
        let targetSpacing = ctx.measureText(sampleText).width + LABEL_LABEL_HORIZONTAL_SPACING
        let timeStepValues =
            [| 1000.0; 5000.0; 30000.0; 60000.0; 300000.0; 1800000.0; 3600000.0; 18000000.0 |]

        let mutable timeStep = 0.0
        let mutable found = false
        let mutable i = 0
        while not found && i < timeStepValues.Length do
            if timeStepValues.[i] / scale >= targetSpacing then
                timeStep <- timeStepValues.[i]
                found <- true
            i <- i + 1

        if found then
            let mutable time = ceil (startT / timeStep) * timeStep
            ctx.textBaseline <- "bottom"
            ctx.textAlign <- "center"
            ctx.fillStyle <- TEXT_COLOR
            ctx.strokeStyle <- GRID_COLOR
            let mutable go = true
            while go do
                let x = round ((time - startT) / scale)
                if x >= width then
                    go <- false
                else
                    ctx.fillText (localeTime time, x, textHeight)
                    ctx.beginPath ()
                    ctx.moveTo (x, 0.0)
                    ctx.lineTo (x, height)
                    ctx.stroke ()
                    time <- time + timeStep

    /// The wall-clock time at screen x=0, shared by Repaint (drawing) and the
    /// status/hover text below so they can never disagree with what's on screen.
    member private this.VisibleStartTime(width: float) =
        let pos = if range = 0.0 then this.GetLength() - width else position
        startTime + pos * scale

    member private _.DescribeScale() : string =
        if scale >= 1000.0 then toFixed (scale / 1000.0) 1 + " s/px" else toFixed scale 0 + " ms/px"

    /// The always-visible "what am I looking at" line (visible time range + current
    /// zoom level) -- addresses the "can't tell the current zoom/time range" gap.
    member this.DescribeView() : string =
        let visibleStart = this.VisibleStartTime(canvasWidth)
        let visibleEnd = visibleStart + scale * canvasWidth
        localeTime visibleStart + " \u2013 " + localeTime visibleEnd + " (" + this.DescribeScale() + ")"

    /// Same as DescribeView, but for a specific screen x (hover position): appends
    /// each visible series' sampled value near that time, e.g. for a status-line
    /// tooltip that doesn't need any floating/positioned DOM element.
    member this.DescribeValuesAt(screenX: float) : string =
        let time = this.VisibleStartTime(canvasWidth) + screenX * scale
        let values = graphs |> List.collect (fun g -> g.ValuesAt(time, scale))
        if List.isEmpty values then
            this.DescribeView()
        else
            let parts =
                values
                |> List.filter (fun (s, _) -> s.Name <> "")
                |> List.map (fun (s, v) -> s.Name + ": " + formatValue s.DataType v)
            localeTime time + " \u2014 " + String.concat ", " parts

    /// The wall-clock time (same space as source StartTicks/EndTicks once converted
    /// via TimeUtil.convertTimeTicksToTime) at a given screen x -- used by the
    /// double-click-to-jump-to-Events handler in View below.
    member this.TimeAtScreenX(screenX: float) : float = this.VisibleStartTime(canvasWidth) + screenX * scale

    member this.Repaint(canvas: Canvas) =
        let width = float canvas.width
        let mutable height = float canvas.height
        let ctx = canvas.getContext "2d"

        ctx.fillStyle <- BACKGROUND_COLOR
        ctx.fillRect (0.0, 0.0, width, height)
        ctx.font <- "12px sans-serif"
        let fontHeight = 12.0

        if fontHeight * 4.0 <= height && width >= 50.0 then
            ctx.save ()
            ctx.translate (0.5, 0.5)

            let visibleStartTime = this.VisibleStartTime(width)

            let textHeight = height
            height <- height - (fontHeight + LABEL_VERTICAL_SPACING)
            this.DrawTimeLabels(ctx, width, height, textHeight, visibleStartTime)

            ctx.strokeStyle <- GRID_COLOR
            ctx.strokeRect (0.0, 0.0, width - 1.0, height - 1.0)

            for g in graphs do
                g.Layout(width, height, fontHeight, visibleStartTime, scale)
                g.DrawTicks ctx
            for g in graphs do
                g.DrawLines ctx
            for g in graphs do
                g.DrawLabels ctx

            ctx.restore ()

let private toEvent (w: obj) : Model.Event =
    { Index = Json.tryNumber w "index" |> Option.defaultValue 0.0 |> int
      Time = Json.tryNumber w "time" |> Option.defaultValue 0.0
      Type = Json.tryNumber w "type" |> Option.defaultValue 0.0 |> int
      Phase = Json.tryNumber w "phase" |> Option.defaultValue 0.0 |> int
      SourceId = Json.tryNumber w "sourceId" |> Option.defaultValue 0.0 |> int
      SourceType = Json.tryNumber w "sourceType" |> Option.defaultValue 0.0 |> int
      StartTime = None
      Params = (let p = Json.get w "params" in if Json.isObject p then Some p else None) }

type View(root: Element, onJumpToTime: float -> unit) as this =
    inherit ViewBase(root)

    let graph = GraphView()
    let series = ResizeArray<DataSeries>()
    let mutable graphDiv = Unchecked.defaultof<Element>
    let mutable canvasEl = Unchecked.defaultof<Element>
    let mutable legendUl = Unchecked.defaultof<Element>
    let mutable statusEl = Unchecked.defaultof<Element>
    let mutable isDragging = false
    let mutable dragX = 0.0

    do this.BuildLayout()

    member private _.Canvas: Canvas = asCanvas canvasEl

    member private _.BuildLayout() =
        clear root
        legendUl <- addNode root "ul"
        legendUl.className <- "nv-timeline-legend"

        let toolbar = addNode root "div"
        toolbar.className <- "nv-timeline-toolbar"
        let resetBtn = addNode toolbar "button"
        resetBtn.setAttribute ("type", "button")
        resetBtn.className <- "nv-report-toggle"
        resetBtn.textContent <- "Reset zoom"
        resetBtn.addEventListener (
            "click",
            fun e ->
                preventDefault e
                graph.ResetZoom()
                this.Repaint())
        let status = addNode toolbar "span"
        status.className <- "nv-timeline-status"
        statusEl <- status

        graphDiv <- addNode root "div"
        graphDiv.className <- "nv-timeline-graph"
        canvasEl <- addNode graphDiv "canvas"
        canvasEl.tabIndex <- 0
        canvasEl.setAttribute ("role", "img")
        canvasEl.setAttribute (
            "aria-label",
            "Network activity timeline. Arrow keys pan, plus and minus zoom, Home resets. Hover for values. Double-click to jump to the Events tab filtered to that time.")
        canvasEl.addEventListener ("wheel", this.OnWheel)
        canvasEl.addEventListener ("mousedown", this.OnMouseDown)
        canvasEl.addEventListener ("mousemove", this.OnMouseMove)
        canvasEl.addEventListener ("mouseup", (fun _ -> isDragging <- false))
        canvasEl.addEventListener ("mouseout", (fun _ -> isDragging <- false; this.Repaint()))
        canvasEl.addEventListener ("keydown", this.OnKeyDown)
        canvasEl.addEventListener ("dblclick", this.OnDoubleClick)

    /// Repaints the canvas and refreshes the always-visible time-range/scale status
    /// line, so the two can never show stale/inconsistent info relative to each other.
    member private this.Repaint() =
        graph.Repaint this.Canvas
        statusEl.textContent <- graph.DescribeView()

    /// Double-click = "jump to Events at this point in time" (addresses the "can't
    /// correlate a timeline spike with the events that caused it" gap), rather than
    /// overloading a plain click, which would be ambiguous with drag-to-pan.
    member private this.OnDoubleClick(e: obj) =
        preventDefault e
        onJumpToTime (graph.TimeAtScreenX(eventOffsetX e))

    member private this.ResizeAndRepaint() =
        let c = this.Canvas
        c.width <- max 10 graphDiv.clientWidth
        c.height <- max 10 graphDiv.clientHeight
        graph.Resize(float c.width)
        this.Repaint()

    member private this.OnWheel(e: obj) =
        preventDefault e
        graph.HorizontalScroll(Json.toNumber (Json.get e "deltaX"))
        graph.Zoom(ZOOM_RATE ** (Json.toNumber (Json.get e "deltaY") / 120.0))
        this.Repaint()

    member private this.OnMouseDown(e: obj) =
        preventDefault e
        isDragging <- true
        dragX <- Json.toNumber (Json.get e "clientX")

    member private this.OnMouseMove(e: obj) =
        if isDragging then
            preventDefault e
            let cx = Json.toNumber (Json.get e "clientX")
            graph.HorizontalScroll(DRAG_RATE * (cx - dragX))
            dragX <- cx
            this.Repaint()
        else
            statusEl.textContent <- graph.DescribeValuesAt(eventOffsetX e)

    /// Keyboard alternative to wheel-zoom/drag-pan (the canvas is a plain <canvas>,
    /// mouse-only otherwise -- a real accessibility gap for trackpad-less/assistive
    /// tech users).
    member private this.OnKeyDown(e: obj) =
        match eventKey e with
        | "ArrowLeft" ->
            preventDefault e
            graph.HorizontalScroll(-50.0)
            this.Repaint()
        | "ArrowRight" ->
            preventDefault e
            graph.HorizontalScroll 50.0
            this.Repaint()
        | "+"
        | "=" ->
            preventDefault e
            graph.Zoom(1.0 / ZOOM_RATE)
            this.Repaint()
        | "-" ->
            preventDefault e
            graph.Zoom ZOOM_RATE
            this.Repaint()
        | "Home" ->
            preventDefault e
            graph.ResetZoom()
            this.Repaint()
        | _ -> ()

    override this.Show(visible: bool) =
        base.Show(visible)
        if visible then this.ResizeAndRepaint()

    override this.OnLoadLogFinish(model: obj) : bool =
        let c = Constants.decode (Json.get (Json.get model "constants") "raw")
        let srcId name = Map.tryFind name c.SourceTypes
        let evId name = Map.tryFind name c.EventTypes
        let pb = c.PhaseBegin
        let pe = c.PhaseEnd

        series.Clear()
        clear legendUl

        let add (color: string) (label: string) (s: DataSeries) =
            s.Color <- color
            s.IsVisible <- true
            s.Name <- label
            series.Add s
            let li = addNode legendUl "li"
            let lbl = addNode li "label"
            let cb = addNode lbl "input"
            cb.setAttribute ("type", "checkbox")
            setChecked cb true
            let swatch = addNode lbl "span"
            swatch.className <- "nv-timeline-swatch"
            swatch.setAttribute ("style", "background:" + color)
            addText lbl (" " + label)
            cb.addEventListener ("change", (fun _ ->
                s.IsVisible <- getChecked cb
                this.Repaint()))

        add "#4e79a7" "Open sockets" (SourceCountSeries(srcId "SOCKET", evId "SOCKET_ALIVE", pb, pe))
        add "#59a14f" "In-use sockets" (SocketsInUseSeries(srcId "SOCKET", evId "SOCKET_IN_USE", evId "SSL_CONNECT", pb, pe))
        add "#e15759" "URL requests" (SourceCountSeries(srcId "URL_REQUEST", evId "REQUEST_ALIVE", pb, pe))
        add "#b07aa1" "DNS jobs" (SourceCountSeries(srcId "HOST_RESOLVER_IMPL_JOB", evId "HOST_RESOLVER_IMPL_JOB", pb, pe))
        add "#f28e2b" "Bytes received" (NetworkTransferRateSeries(evId "SOCKET_BYTES_RECEIVED", evId "UDP_BYTES_RECEIVED"))
        add "#9c755f" "Bytes sent" (NetworkTransferRateSeries(evId "SOCKET_BYTES_SENT", evId "UDP_BYTES_SENT"))
        add "#76b7b2" "Disk cache read" (DiskCacheTransferRateSeries(srcId "DISK_CACHE_ENTRY", evId "ENTRY_READ_DATA", pe))
        add "#ff9da7" "Disk cache write" (DiskCacheTransferRateSeries(srcId "DISK_CACHE_ENTRY", evId "ENTRY_WRITE_DATA", pe))

        let wireEvents = Json.get model "events"
        let n = Json.length wireEvents
        let mutable firstTime = 0.0
        let mutable lastTime = 0.0
        for i in 0 .. n - 1 do
            let e = toEvent (Json.item wireEvents i)
            if i = 0 then firstTime <- e.Time
            lastTime <- e.Time
            for s in series do
                s.OnReceivedLogEntry e

        graph.SetDataSeries(List.ofSeq series)
        if n > 0 then
            graph.SetDateRange(TimeUtil.convertTimeTicksToTime firstTime, TimeUtil.convertTimeTicksToTime lastTime)
        if this.IsVisible then this.ResizeAndRepaint()
        true

let create (onJumpToTime: float -> unit) : View =
    let root = createElement "div"
    root.className <- "nv-view nv-timeline"
    View(root, onJumpToTime)
