/// Port of log_util.js (loadLogFile / loadLogDump): parses a NetLog file, validates
/// constants + events, groups sources, and synthesizes the export date when absent.
module Netlog.Core.LogParser

open System.Collections.Generic
open Fable.Core.JsInterop
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
/// failure. Shared by the batch (loadLogDump), streaming (StreamingParser), and
/// parallel (ParallelStreamLoader) paths. Also sets the shared TimeUtil tick offset
/// -- every path needs it before converting any event's ticks to wall-clock time,
/// so it lives here rather than being duplicated (or, worse, forgotten) at each
/// call site.
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
            TimeUtil.setTimeTickOffset constants.TimeTickOffset
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

/// Outcome of validating a single raw event object, before any source-grouping.
/// Source-grouping needs events in original file order, which a parallel/sharded
/// loader only has AFTER merging shards back together (see `finalizeFromShards`),
/// so validation is kept as a separate, shareable step from grouping.
type ValidatedEvent =
    | ValidEvent of Event
    | PassivelyCaptured
    | InvalidEvent

/// Port of the per-event validity filter from loadLogDump, without any
/// source-grouping. The returned event's `Index` is always 0 -- callers stamp in
/// the real (final) index once it is known (immediately, for the sequential path
/// below; after merging shards, for the parallel path).
let validateEvent (constants: Constants.Constants) (ev: obj) : ValidatedEvent =
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
            PassivelyCaptured
        else
            let startTimeRaw = Json.get src "start_time"
            ValidEvent
                { Index = 0
                  Time = Json.toNumber timeRaw
                  Type = typeId.Value
                  Phase = phaseId.Value
                  SourceId = (match Json.tryNumber src "id" with Some idv -> int idv | None -> 0)
                  SourceType = srcTypeId.Value
                  StartTime = (if Json.isDefined startTimeRaw then Some(Json.toNumber startTimeRaw) else None)
                  Params = (let p = Json.get ev "params" in if Json.isObject p then Some p else None) }
    else
        InvalidEvent

/// Port of the per-event body of loadLogDump's validity filter: validates a single
/// raw event object and, when valid, appends it and updates its source grouping.
let ingestEvent (st: IngestState) (ev: obj) : unit =
    st.TotalSeen <- st.TotalSeen + 1
    match validateEvent st.Constants ev with
    | InvalidEvent -> ()
    | PassivelyCaptured -> st.NumDeprecatedPassive <- st.NumDeprecatedPassive + 1
    | ValidEvent unindexed ->
        let event = { unindexed with Index = st.ValidEvents.Count }
        st.ValidEvents.Add event

        match st.SourceIndex.TryGetValue event.SourceId with
        | true, se -> SourceGrouping.update st.Constants st.SourceIndex se event
        | _ ->
            let se = SourceGrouping.create st.Constants st.SourceIndex event
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

/// Per-shard output of the parallel (worker-thread) loading path: this shard's
/// validated events (in original file order, `Index` 0-based WITHIN THE SHARD ONLY)
/// plus its counts of discarded events, ready to be merged by `finalizeFromShards`.
type ShardResult =
    { Events: Event[]
      NumPassive: int
      NumInvalid: int }

/// Validates a batch of raw event objects with no source-grouping and no shared
/// mutable state -- used by the parallel loading path, where each worker
/// independently validates its shard of events (source-grouping needs the events in
/// global original-file order, which only exists after merging shards, so it is not
/// done here; see `finalizeFromShards`).
let validateEventBatch (constants: Constants.Constants) (evs: obj[]) : ShardResult =
    let valid = ResizeArray<Event>()
    let mutable numPassive = 0
    let mutable numInvalid = 0
    for ev in evs do
        match validateEvent constants ev with
        | ValidEvent e -> valid.Add { e with Index = valid.Count }
        | PassivelyCaptured -> numPassive <- numPassive + 1
        | InvalidEvent -> numInvalid <- numInvalid + 1
    { Events = valid.ToArray(); NumPassive = numPassive; NumInvalid = numInvalid }

/// Same as `validateEventBatch`, but takes raw JSON text per event and parses each
/// one itself, with a per-item try/catch so a single malformed record can't take
/// out the rest of the shard (JSON-parse failures are folded into `NumInvalid`,
/// alongside schema-validation failures). Used by the parallel loading path's
/// worker threads, which only have raw byte ranges, not pre-parsed objects.
let validateEventBatchFromJson (constants: Constants.Constants) (rawJsonEvents: string[]) : ShardResult =
    let valid = ResizeArray<Event>()
    let mutable numPassive = 0
    let mutable numInvalid = 0
    for raw in rawJsonEvents do
        try
            match validateEvent constants (Json.parse raw) with
            | ValidEvent e -> valid.Add { e with Index = valid.Count }
            | PassivelyCaptured -> numPassive <- numPassive + 1
            | InvalidEvent -> numInvalid <- numInvalid + 1
        with _ ->
            numInvalid <- numInvalid + 1
    { Events = valid.ToArray(); NumPassive = numPassive; NumInvalid = numInvalid }

/// Plain-object DTO for transferring one validated Event across the worker_threads
/// boundary (structured clone). Distinct from Core.Wire's webview DTO: this one
/// round-trips `StartTime` (needed for correct SourceGrouping once shards are merged
/// back on the main thread, unlike the webview which never needs it) and always
/// carries full `Params` (grouping/description-derivation needs them too).
let eventToTransfer (e: Event) : obj =
    createObj [
        "index" ==> e.Index
        "time" ==> e.Time
        "type" ==> e.Type
        "phase" ==> e.Phase
        "sourceId" ==> e.SourceId
        "sourceType" ==> e.SourceType
        "startTime" ==> (match e.StartTime with Some t -> box t | None -> null)
        "params" ==> Option.toObj e.Params
    ]

/// Inverse of `eventToTransfer`.
let eventFromTransfer (o: obj) : Event =
    { Index = int (Json.toNumber (Json.get o "index"))
      Time = Json.toNumber (Json.get o "time")
      Type = int (Json.toNumber (Json.get o "type"))
      Phase = int (Json.toNumber (Json.get o "phase"))
      SourceId = int (Json.toNumber (Json.get o "sourceId"))
      SourceType = int (Json.toNumber (Json.get o "sourceType"))
      StartTime = (let st = Json.get o "startTime" in if Json.isNumber st then Some(Json.toNumber st) else None)
      Params = (let p = Json.get o "params" in if Json.isObject p then Some p else None) }

/// Plain-object DTO for one shard's worker output (see `eventToTransfer`).
let shardResultToTransfer (r: ShardResult) : obj =
    createObj [
        "events" ==> (r.Events |> Array.map eventToTransfer)
        "numPassive" ==> r.NumPassive
        "numInvalid" ==> r.NumInvalid
    ]

/// Inverse of `shardResultToTransfer`.
let shardResultFromTransfer (o: obj) : ShardResult =
    let eventsRaw = Json.get o "events"
    { Events = Array.init (Json.length eventsRaw) (fun i -> eventFromTransfer (Json.item eventsRaw i))
      NumPassive = int (Json.toNumber (Json.get o "numPassive"))
      NumInvalid = int (Json.toNumber (Json.get o "numInvalid")) }

/// Merges shard results from the parallel loading path (`shards` must already be in
/// original shard order, i.e. the order the shards appear in the source file) into a
/// single LoadedLog. Unlike the sequential path, source-grouping runs once AFTER
/// merging (over the fully reassembled, correctly-ordered event stream) via
/// `SourceGrouping.buildSources`, rather than incrementally per shard -- a source's
/// events can be spread across multiple shards, so grouping only produces correct
/// descriptions/active-state once every shard's events are back in original order.
/// `maxEvents <= 0` means "no cap"; when the cap is hit it is applied here (after all
/// shards are parsed) rather than aborting shards early, trading a little wasted
/// parse work in the last shard for much simpler cap bookkeeping.
let finalizeFromShards
    (constants: Constants.Constants)
    (shards: ShardResult[])
    (maxEvents: int)
    (polledData: obj)
    (tabData: obj)
    (userComments: string option)
    (leadingWarn: string)
    : LoadedLog =
    let totalPassive = shards |> Array.sumBy (fun s -> s.NumPassive)
    let totalInvalid = shards |> Array.sumBy (fun s -> s.NumInvalid)
    let allEvents = shards |> Array.collect (fun s -> s.Events)

    let capReached = maxEvents > 0 && allEvents.Length > maxEvents
    let events = if capReached then Array.sub allEvents 0 maxEvents else allEvents
    for i in 0 .. events.Length - 1 do
        events.[i] <- { events.[i] with Index = i }

    if events.Length > 0 then
        TimeUtil.setBaseTime (TimeUtil.convertTimeTicksToTime events.[0].Time)

    let mutable errorString = leadingWarn
    if capReached then
        errorString <-
            errorString
            + sprintf
                "Reached the maximum of %d events; the rest were not loaded. Increase the \"netlogViewer.maxEvents\" setting to load more.\n\n"
                maxEvents

    let numericDate =
        match Json.tryNumber constants.ClientInfo "numericDate" with
        | Some d -> d
        | None ->
            if events.Length > 0 then
                TimeUtil.convertTimeTicksToTime events.[events.Length - 1].Time
            else
                errorString <- errorString + "Can't guess export date as there are no events.\n"
                0.0

    let sources, sourceIndex = SourceGrouping.buildSources constants events

    if totalInvalid > 0 then
        errorString <- errorString + sprintf "Unable to load %d events, due to invalid data.\n\n" totalInvalid
    if totalPassive > 0 then
        errorString <-
            errorString
            + sprintf "Discarded %d passively collected events. Use an older version of Chrome to load this dump if you want to see them.\n\n" totalPassive

    let lastTicks = if events.Length > 0 then events.[events.Length - 1].Time else 0.0

    { Constants = constants
      Events = events
      Sources = sources
      SourceIndex = sourceIndex
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
