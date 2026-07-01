/// Builds the structured-clone-safe `{ type: "load", ... }` message sent from the
/// Extension Host to the Webview. Pure (no vscode), so it is unit-tested in Core.
/// Uses plain objects/arrays only; raw `params`/`polledData`/`clientInfo` pass through.
module Netlog.Core.Wire

open Fable.Core
open Fable.Core.JsInterop

[<Emit("$0[$1] = $2")>]
let private setProp (o: obj) (k: string) (v: obj) : unit = jsNative

/// Map<int,string> -> { "id": "NAME" } plain object.
let private intMapToObj (m: Map<int, string>) : obj =
    let o = Json.emptyObject ()
    m |> Map.iter (fun k v -> setProp o (string k) (box v))
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
    setProp o "events" (log.Events |> Array.map eventDto)
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
        let slice = Array.sub events k n |> Array.map eventDto
        post (createObj [ "type" ==> "eventsChunk"; "events" ==> slice ])
        k <- k + n

    post (createObj [ "type" ==> "loadEnd" ])

/// Builds the on-demand response carrying a single source's events, requested by the
/// Events view when a source is selected (so the webview never has to hold every
/// event's params in memory just to render details).
let sourceEventsMessage (id: int) (events: Model.Event seq) : obj =
    createObj [
        "type" ==> "sourceEvents"
        "id" ==> id
        "events" ==> (events |> Seq.map eventDto |> Seq.toArray)
    ]
