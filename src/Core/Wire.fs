/// Builds the structured-clone-safe `{ type: "load", ... }` message sent from the
/// Extension Host to the Webview. Pure (no vscode), so it is unit-tested in Core.
/// Uses plain objects/arrays only; raw `params`/`polledData`/`clientInfo` pass through.
module Netlog.Core.Wire

open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop

[<Emit("$0[$1] = $2")>]
let private setProp (o: obj) (k: string) (v: obj) : unit = jsNative

/// Dictionary<int,string> -> { "id": "NAME" } plain object.
let private intMapToObj (m: Dictionary<int, string>) : obj =
    let o = Json.emptyObject ()
    for kvp in m do
        setProp o (string kvp.Key) (box kvp.Value)
    o

let private eventDto (e: Model.Event) : obj =
    createObj [
        "index" ==> e.Index
        "time" ==> e.Time
        "type" ==> e.Type
        "phase" ==> e.Phase
        "sourceId" ==> e.SourceId
        "sourceType" ==> e.SourceType
        "params" ==> Option.toObj e.Params
    ]

/// Event TYPE names whose `params` the Timeline tab reads directly off the bulk
/// events stream (Webview/Timeline.fs's NetworkTransferRateSeries reads `byte_count`,
/// DiskCacheTransferRateSeries reads `bytes_copied`). Every other view either doesn't
/// touch event params at all (it only uses `sources`/`polledData`) or fetches full
/// params on demand per source via `sourceEventsMessage` -- so every other event's
/// `params` can be dropped from the bulk stream. This matters because `params` blobs
/// (URLs, headers, host names, ...) dominate the bulk payload size.
let private timelineParamEventNames =
    [| "SOCKET_BYTES_RECEIVED"; "UDP_BYTES_RECEIVED"
       "SOCKET_BYTES_SENT"; "UDP_BYTES_SENT"
       "ENTRY_READ_DATA"; "ENTRY_WRITE_DATA" |]

let private timelineParamEventTypeIds (c: Constants.Constants) : Set<int> =
    timelineParamEventNames |> Array.choose (fun name -> Map.tryFind name c.EventTypes) |> Set.ofArray

/// Same shape as `eventDto`, but omits `params` for every event type except the
/// handful Timeline needs -- used for the bulk `events`/`eventsChunk` stream only.
/// `sourceEventsMessage` (on-demand per-source detail) always uses `eventDto` above.
let private eventDtoBulk (keepParamsFor: Set<int>) (e: Model.Event) : obj =
    createObj [
        "index" ==> e.Index
        "time" ==> e.Time
        "type" ==> e.Type
        "phase" ==> e.Phase
        "sourceId" ==> e.SourceId
        "sourceType" ==> e.SourceType
        "params" ==> Option.toObj (if Set.contains e.Type keepParamsFor then e.Params else None)
    ]

let private sourceDto (c: Constants.Constants) (now: float) (s: SourceGrouping.SourceEntry) : obj =
    createObj [
        "id" ==> SourceGrouping.sourceId s
        "type" ==> SourceGrouping.sourceType s
        "typeName" ==> SourceGrouping.sourceTypeNameOf c s
        "description" ==> s.Description
        "isError" ==> s.IsError
        "isActive" ==> (not s.IsInactive)
        "startTicks" ==> SourceGrouping.startTicks s
        "endTicks" ==> SourceGrouping.endTicks now s
        "duration" ==> SourceGrouping.duration now s
        "eventCount" ==> s.Entries.Count
    ]

/// Builds the shared metadata object (everything except the events/sources arrays).
/// `msgType` distinguishes the self-contained `load` message from the streamed
/// `loadStart` message.
let private metaObj (msgType: string) (fileName: string) (log: LogParser.LoadedLog) : obj =
    let c = log.Constants

    let constantsDto =
        createObj [
            "eventTypeNames" ==> intMapToObj c.EventTypeNames
            "sourceTypeNames" ==> intMapToObj c.SourceTypeNames
            "timeTickOffset" ==> c.TimeTickOffset
            "logFormatVersion" ==> c.LogFormatVersion
            "logCaptureMode" ==> Option.toObj c.LogCaptureMode
            "raw" ==> c.Raw
        ]

    createObj [
        "type" ==> msgType
        "fileName" ==> fileName
        "loadLog" ==> log.LoadLog
        "numericDate" ==> log.NumericDate
        "baseTime" ==> TimeUtil.getBaseTime ()
        "lastEventTicks" ==> log.LastEventTicks
        "userComments" ==> Option.toObj log.UserComments
        "clientInfo" ==> c.ClientInfo
        "constants" ==> constantsDto
        "polledData" ==> log.PolledData
        "tabData" ==> log.TabData
        "stats"
        ==> createObj [
            "eventCount" ==> log.Events.Length
            "sourceCount" ==> log.Sources.Length
        ]
    ]

/// Builds the single, self-contained load message (used by tests and the non-local
/// fallback loader). Carries all events + sources inline.
let build (fileName: string) (log: LogParser.LoadedLog) : obj =
    let o = metaObj "load" fileName log
    let keepParamsFor = timelineParamEventTypeIds log.Constants
    setProp o "events" (log.Events |> Array.map (eventDtoBulk keepParamsFor))
    setProp o "sources" (log.Sources |> Array.map (sourceDto log.Constants log.LastEventTicks))
    o

[<Literal>]
let private ChunkSize = 50000

/// Streams the load to the webview as a sequence of messages
/// (`loadStart` -> `sourcesChunk*` -> `eventsChunk*` -> `loadEnd`) so no single
/// postMessage has to structured-clone the entire (possibly huge) events array at
/// once, and so the webview can report load progress. The webview reassembles the
/// chunks into the same model the self-contained `build` message would have produced.
let postLoad (post: obj -> unit) (fileName: string) (log: LogParser.LoadedLog) : unit =
    let c = log.Constants
    let now = log.LastEventTicks
    let keepParamsFor = timelineParamEventTypeIds c
    post (metaObj "loadStart" fileName log)

    let sources = log.Sources
    let mutable i = 0
    while i < sources.Length do
        let n = min ChunkSize (sources.Length - i)
        let slice = Array.sub sources i n |> Array.map (sourceDto c now)
        post (createObj [ "type" ==> "sourcesChunk"; "sources" ==> slice ])
        i <- i + n

    let events = log.Events
    let mutable k = 0
    while k < events.Length do
        let n = min ChunkSize (events.Length - k)
        let slice = Array.sub events k n |> Array.map (eventDtoBulk keepParamsFor)
        post (createObj [ "type" ==> "eventsChunk"; "events" ==> slice ])
        k <- k + n

    post (createObj [ "type" ==> "loadEnd" ])

/// Builds the on-demand response carrying a single source's events, requested by the
/// Events view when a source is selected (so the webview never has to hold every
/// event's params in memory just to render details). At most `cap` events are sent;
/// `total` is the source's full event count so the view can note any truncation.
let sourceEventsMessage (id: int) (events: Model.Event seq) (total: int) (cap: int) : obj =
    let taken =
        if cap > 0 then events |> Seq.truncate cap |> Seq.map eventDto |> Seq.toArray
        else events |> Seq.map eventDto |> Seq.toArray
    createObj [
        "type" ==> "sourceEvents"
        "id" ==> id
        "events" ==> taken
        "total" ==> total
        "truncated" ==> (cap > 0 && total > cap)
    ]
