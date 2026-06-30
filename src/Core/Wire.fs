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

/// Builds the load message from a parsed log.
let build (fileName: string) (log: LogParser.LoadedLog) : obj =
    let c = log.Constants
    let now = log.LastEventTicks

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
        "type" ==> "load"
        "fileName" ==> fileName
        "loadLog" ==> log.LoadLog
        "numericDate" ==> log.NumericDate
        "baseTime" ==> TimeUtil.getBaseTime ()
        "lastEventTicks" ==> log.LastEventTicks
        "userComments" ==> Option.toObj log.UserComments
        "clientInfo" ==> c.ClientInfo
        "constants" ==> constantsDto
        "events" ==> (log.Events |> Array.map eventDto)
        "sources" ==> (log.Sources |> Array.map (sourceDto c now))
        "polledData" ==> log.PolledData
        "tabData" ==> log.TabData
        "stats"
        ==> createObj [
            "eventCount" ==> log.Events.Length
            "sourceCount" ==> log.Sources.Length
        ]
    ]
