/// Port of log_util.js (loadLogFile / loadLogDump): parses a NetLog file, validates
/// constants + events, groups sources, and synthesizes the export date when absent.
module Netlog.Core.LogParser

open System.Collections.Generic
open Netlog.Core
open Netlog.Core.Model

[<Literal>]
let private SupportedLogFormatVersion = 1

type LoadedLog =
    { Constants: Constants.Constants
      Events: Event[]
      Sources: SourceGrouping.SourceEntry[]
      SourceIndex: Dictionary<int, SourceGrouping.SourceEntry>
      PolledData: obj
      TabData: obj
      UserComments: string option
      NumericDate: float
      LastEventTicks: float
      LoadLog: string }

/// Validates a raw constants object and decodes it, returning the abort message on
/// failure. Shared by the batch (loadLogDump) and streaming (StreamingParser) paths.
let checkConstants (constantsRaw: obj) : Result<Constants.Constants, string> =
    if not (Constants.areValid constantsRaw) then
        Error "Load failed:\n\nInvalid constants object.\n"
    elif Json.isObject constantsRaw && not (Json.isNumber (Json.get constantsRaw "logFormatVersion")) then
        Error "Load failed:\n\nInvalid version number.\n"
    else
        let constants = Constants.decode constantsRaw

        if constants.LogFormatVersion <> SupportedLogFormatVersion then
            Error(
                sprintf
                    "Unable to load different log version. Found %d, Expected %d"
                    constants.LogFormatVersion
                    SupportedLogFormatVersion
            )
        else
            Ok constants

/// Accumulates validated events and their grouped sources as events arrive one at a
/// time. Lets the batch loader and the streaming loader share the exact same
/// per-event validity filter and source-grouping logic.
type IngestState =
    { Constants: Constants.Constants
      ValidEvents: ResizeArray<Event>
      SourceOrder: ResizeArray<SourceGrouping.SourceEntry>
      SourceIndex: Dictionary<int, SourceGrouping.SourceEntry>
      mutable NumDeprecatedPassive: int
      mutable TotalSeen: int }

let newIngest (constants: Constants.Constants) : IngestState =
    { Constants = constants
      ValidEvents = ResizeArray<Event>()
      SourceOrder = ResizeArray<SourceGrouping.SourceEntry>()
      SourceIndex = Dictionary<int, SourceGrouping.SourceEntry>()
      NumDeprecatedPassive = 0
      TotalSeen = 0 }

/// Port of the per-event body of loadLogDump's validity filter: validates a single
/// raw event object and, when valid, appends it and updates its source grouping.
let ingestEvent (st: IngestState) (ev: obj) : unit =
    st.TotalSeen <- st.TotalSeen + 1
    let constants = st.Constants
    let src = Json.get ev "source"
    let timeRaw = Json.get ev "time"
    let typeRaw = Json.get ev "type"
    let phaseRaw = Json.get ev "phase"

    let typeId = if Json.isNumber typeRaw then Some(int (Json.toNumber typeRaw)) else None
    let srcTypeRaw = Json.get src "type"
    let srcTypeId =
        if Json.isObject src && Json.isNumber srcTypeRaw then Some(int (Json.toNumber srcTypeRaw)) else None
    let phaseId = if Json.isNumber phaseRaw then Some(int (Json.toNumber phaseRaw)) else None

    let okSource = Json.isObject src
    let okTime = Json.isString timeRaw
    let okType = match typeId with Some t -> constants.EventTypeNames.ContainsKey t | None -> false
    let okSourceType = match srcTypeId with Some t -> constants.SourceTypeNames.ContainsKey t | None -> false
    let okPhase = match phaseId with Some ph -> constants.ValidPhaseValues.Contains ph | None -> false

    if okSource && okTime && okType && okSourceType && okPhase then
        if Json.isTruthy (Json.get ev "wasPassivelyCaptured") then
            st.NumDeprecatedPassive <- st.NumDeprecatedPassive + 1
        else
            let startTimeRaw = Json.get src "start_time"
            let event =
                { Index = st.ValidEvents.Count
                  Time = Json.toNumber timeRaw
                  Type = typeId.Value
                  Phase = phaseId.Value
                  SourceId = (match Json.tryNumber src "id" with Some idv -> int idv | None -> 0)
                  SourceType = srcTypeId.Value
                  StartTime = (if Json.isDefined startTimeRaw then Some(Json.toNumber startTimeRaw) else None)
                  Params = (let p = Json.get ev "params" in if Json.isObject p then Some p else None) }
            st.ValidEvents.Add event

            match st.SourceIndex.TryGetValue event.SourceId with
            | true, se -> SourceGrouping.update constants st.SourceIndex se event
            | _ ->
                let se = SourceGrouping.create constants st.SourceIndex event
                st.SourceIndex.[event.SourceId] <- se
                st.SourceOrder.Add se

/// Port of the tail of loadLogDump: sets base time, synthesises the export date,
/// tallies discarded events, and assembles the LoadedLog. `leadingWarn` carries any
/// warnings collected upstream (e.g. truncation), preserving the original ordering.
let finalizeIngest
    (st: IngestState)
    (polledData: obj)
    (tabData: obj)
    (userComments: string option)
    (leadingWarn: string)
    : LoadedLog =
    let events = st.ValidEvents.ToArray()

    // Base time = wall-clock of the first event (for relative time display).
    if events.Length > 0 then
        TimeUtil.setBaseTime (TimeUtil.convertTimeTicksToTime events.[0].Time)

    let mutable errorString = leadingWarn

    // Determine export date (port of the numericDate synthesis).
    let numericDate =
        match Json.tryNumber st.Constants.ClientInfo "numericDate" with
        | Some d -> d
        | None ->
            if events.Length > 0 then
                TimeUtil.convertTimeTicksToTime events.[events.Length - 1].Time
            else
                errorString <- errorString + "Can't guess export date as there are no events.\n"
                0.0

    let sources = st.SourceOrder.ToArray()

    let numInvalid = st.TotalSeen - (events.Length + st.NumDeprecatedPassive)
    if numInvalid > 0 then
        errorString <- errorString + sprintf "Unable to load %d events, due to invalid data.\n\n" numInvalid
    if st.NumDeprecatedPassive > 0 then
        errorString <-
            errorString
            + sprintf "Discarded %d passively collected events. Use an older version of Chrome to load this dump if you want to see them.\n\n" st.NumDeprecatedPassive

    let lastTicks = if events.Length > 0 then events.[events.Length - 1].Time else 0.0

    { Constants = st.Constants
      Events = events
      Sources = sources
      SourceIndex = st.SourceIndex
      PolledData = polledData
      TabData = tabData
      UserComments = userComments
      NumericDate = numericDate
      LastEventTicks = lastTicks
      LoadLog = errorString + "Log loaded." }

/// Port of loadLogDump. Returns Error with the abort message, or Ok with the
/// loaded log (whose LoadLog field carries any non-fatal warnings).
let loadLogDump (logDump: obj) : Result<LoadedLog, string> =
    if not (Json.isObject logDump) then
        Error "Load failed.  Top level JSON data is not an object."
    else

    let constantsRaw = Json.get logDump "constants"
    let eventsRaw = Json.get logDump "events"

    let mutable errorString = ""
    if not (Constants.areValid constantsRaw) then
        errorString <- errorString + "Invalid constants object.\n"
    if not (Json.isObject eventsRaw) then
        errorString <- errorString + "NetLog events missing.\n"
    if Json.isObject constantsRaw && not (Json.isNumber (Json.get constantsRaw "logFormatVersion")) then
        errorString <- errorString + "Invalid version number.\n"

    if errorString.Length > 0 then
        Error("Load failed:\n\n" + errorString)
    else

    let constants = Constants.decode constantsRaw

    if constants.LogFormatVersion <> SupportedLogFormatVersion then
        Error(
            sprintf
                "Unable to load different log version. Found %d, Expected %d"
                constants.LogFormatVersion
                SupportedLogFormatVersion
        )
    else

    let polledData =
        let p = Json.get logDump "polledData"
        if Json.isObject p then p else Json.emptyObject ()

    let tabData =
        let t = Json.get logDump "tabData"
        if Json.isObject t then t else Json.emptyObject ()

    TimeUtil.setTimeTickOffset constants.TimeTickOffset

    let st = newIngest constants
    let total = if Json.isArray eventsRaw then Json.length eventsRaw else 0

    for i in 0 .. total - 1 do
        ingestEvent st (Json.item eventsRaw i)

    Ok(finalizeIngest st polledData tabData (Json.tryString logDump "userComments") "")

/// Port of loadLogFile: parse JSON, with a fallback for truncated --log-net-log files.
let loadLogFile (text: string) : Result<LoadedLog, string> =
    let mutable warn = ""

    let parsed =
        try
            Some(Json.parse text)
        with _ ->
            let endIdx = max (text.LastIndexOf(",\n")) (text.LastIndexOf(",\r"))
            if endIdx <> -1 then
                try
                    warn <- "Log file truncated.  Events may be missing.\n"
                    Some(Json.parse (text.Substring(0, endIdx) + "]}"))
                with _ ->
                    None
            else
                None

    match parsed with
    | None -> Error "Unable to parse log dump as JSON file."
    | Some raw ->
        match loadLogDump raw with
        | Ok log -> Ok { log with LoadLog = warn + log.LoadLog }
        | Error e -> Error e
