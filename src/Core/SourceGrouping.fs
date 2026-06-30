/// Port of source_tracker.js + source_entry.js: groups events by source id and
/// derives each source's description, error/active state, and timing.
module Netlog.Core.SourceGrouping

open System.Collections.Generic
open Netlog.Core
open Netlog.Core.Constants
open Netlog.Core.Model

type SourceEntry =
    { Entries: ResizeArray<Event>
      mutable Description: string
      mutable IsError: bool
      mutable IsInactive: bool }

let sourceId (se: SourceEntry) : int = se.Entries.[0].SourceId
let sourceType (se: SourceEntry) : int = se.Entries.[0].SourceType
let sourceTypeNameOf (c: Constants) (se: SourceEntry) : string = sourceTypeName c (sourceType se)

let private isEndPhase (c: Constants) (phase: int) : bool =
    match c.PhaseEnd with
    | Some pe -> phase = pe
    | None -> false

let private findByType (c: Constants) (se: SourceEntry) (typeName: string) : Event option =
    se.Entries |> Seq.tryFind (fun e -> eventTypeName c e.Type = typeName)

let private findLastStartByType (c: Constants) (se: SourceEntry) (typeName: string) : Event option =
    let mutable result = None
    let mutable i = se.Entries.Count - 1
    while i >= 0 && result.IsNone do
        let e = se.Entries.[i]
        if eventTypeName c e.Type = typeName && not (isEndPhase c e.Phase) then
            result <- Some e
        i <- i - 1
    result

/// Port of SourceEntry.getStartEntry_ (skips REQUEST_ALIVE wrappers, handles
/// FILESTREAM/DOWNLOAD and a few socket/cert start markers).
let getStartEntry (c: Constants) (se: SourceEntry) : Event option =
    if se.Entries.Count < 1 then
        None
    else
        let e0 = se.Entries.[0]
        let st = sourceTypeName c e0.SourceType

        let fileStreamHit = if st = "FILESTREAM" then findByType c se "FILE_STREAM_OPEN" else None

        let downloadHit =
            if st = "DOWNLOAD" then
                match findLastStartByType c se "DOWNLOAD_FILE_RENAMED" with
                | Some e -> Some e
                | None ->
                    match findByType c se "DOWNLOAD_FILE_OPENED" with
                    | Some e -> Some e
                    | None -> findByType c se "DOWNLOAD_ITEM_ACTIVE"
            else
                None

        match fileStreamHit with
        | Some e -> Some e
        | None ->
            match downloadHit with
            | Some e -> Some e
            | None ->
                if se.Entries.Count >= 2 then
                    let t1 = eventTypeName c se.Entries.[1].Type
                    if t1 = "UDP_CONNECT" || t1 = "IPV6_PROBE_RUNNING"
                       || t1 = "SOCKET_POOL_CONNECT_JOB_CREATED" || t1 = "CERT_VERIFY_PROC" then
                        Some se.Entries.[1]
                    else
                        Some e0
                else
                    Some e0

let private descOf (index: Dictionary<int, SourceEntry>) (id: int) : string =
    match index.TryGetValue id with
    | true, se -> se.Description
    | _ -> ""

let private parentDepId (p: obj) : int option =
    let dep = Json.get p "source_dependency"
    if Json.isObject dep then Json.tryNumber dep "id" |> Option.map int else None

let private streamKeyDestination (p: obj) : string =
    let sk = Json.get p "stream_key"
    if Json.isObject sk then Json.tryString sk "destination" |> Option.defaultValue "" else ""

/// Port of SourceEntry.updateDescription_.
let private updateDescription (c: Constants) (index: Dictionary<int, SourceEntry>) (se: SourceEntry) : unit =
    se.Description <-
        match getStartEntry c se with
        | None -> ""
        | Some e ->
            let st = sourceTypeName c e.SourceType
            if st = "NONE" then
                eventTypeName c e.Type
            else
                match e.Params with
                | None -> ""
                | Some p ->
                    match st with
                    | "URL_REQUEST" | "DOH_URL_REQUEST" | "HTTP_STREAM_JOB"
                    | "HTTP_STREAM_JOB_CONTROLLER" | "BIDIRECTIONAL_STREAM" ->
                        Json.tryString p "url" |> Option.defaultValue ""
                    | "TRANSPORT_CONNECT_JOB" | "SSL_CONNECT_JOB" | "SOCKS_CONNECT_JOB"
                    | "HTTP_PROXY_CONNECT_JOB" | "WEB_SOCKET_TRANSPORT_CONNECT_JOB" ->
                        match Json.tryString p "group_id" with
                        | Some g -> g
                        | None -> Json.tryString p "group_name" |> Option.defaultValue ""
                    | "TCP_STREAM_ATTEMPT" -> Json.tryString p "ip_endpoint" |> Option.defaultValue ""
                    | "TLS_STREAM_ATTEMPT" -> Json.tryString p "host_port" |> Option.defaultValue ""
                    | "HTTP_STREAM_POOL_GROUP" | "HTTP_STREAM_POOL_JOB" -> streamKeyDestination p
                    | "HTTP_STREAM_POOL_ATTEMPT_MANAGER" ->
                        match parentDepId p with
                        | Some pid -> descOf index pid
                        | None -> streamKeyDestination p
                    | "HTTP_STREAM_POOL_QUIC_TASK" ->
                        match parentDepId p with
                        | Some pid -> descOf index pid
                        | None -> ""
                    | "HOST_RESOLVER_IMPL_JOB" | "HOST_RESOLVER_IMPL_PROC_TASK" ->
                        Json.tryString p "host" |> Option.defaultValue ""
                    | "DISK_CACHE_ENTRY" | "MEMORY_CACHE_ENTRY" ->
                        Json.tryString p "key" |> Option.defaultValue ""
                    | "CERT_VERIFIER_JOB" | "CERT_VERIFIER_TASK" | "QUIC_SESSION" | "QUIC_SESSION_POOL_JOB" ->
                        Json.tryString p "host" |> Option.defaultValue ""
                    | "HTTP2_SESSION" ->
                        match Json.tryString p "host" with
                        | Some h -> h + " (" + Json.jsString (Json.get p "proxy") + ")"
                        | None -> ""
                    | "HTTP_PIPELINED_CONNECTION" ->
                        Json.tryString p "host_and_port" |> Option.defaultValue ""
                    | "SOCKET" | "PROXY_CLIENT_SOCKET" ->
                        match parentDepId p with
                        | Some pid -> descOf index pid
                        | None -> ""
                    | "UDP_SOCKET" ->
                        let addrRaw = Json.get p "address"
                        if not (Json.isDefined addrRaw) then
                            ""
                        else
                            let mutable d = Json.jsString addrRaw
                            let e0 = se.Entries.[0]
                            if eventTypeName c e0.Type = "SOCKET_ALIVE" then
                                match e0.Params with
                                | Some p0 ->
                                    match parentDepId p0 with
                                    | Some parentId ->
                                        match index.TryGetValue parentId with
                                        | true, parent ->
                                            if sourceTypeNameOf c parent = "HOST_RESOLVER_IMPL_JOB"
                                               && parent.Description.Length > 0 then
                                                d <- d + " [" + parent.Description + "]"
                                        | _ -> ()
                                    | None -> ()
                                | None -> ()
                            d
                    | "ASYNC_HOST_RESOLVER_REQUEST" | "DNS_TRANSACTION" | "DNS_OVER_HTTPS" ->
                        Json.tryString p "hostname" |> Option.defaultValue ""
                    | "DOWNLOAD" ->
                        match eventTypeName c e.Type with
                        | "DOWNLOAD_FILE_RENAMED" -> Json.tryString p "new_filename" |> Option.defaultValue ""
                        | "DOWNLOAD_FILE_OPENED" -> Json.tryString p "file_name" |> Option.defaultValue ""
                        | "DOWNLOAD_ITEM_ACTIVE" -> Json.tryString p "file_name" |> Option.defaultValue ""
                        | _ -> ""
                    | "WEB_TRANSPORT_CLIENT" -> Json.tryString p "url" |> Option.defaultValue ""
                    | _ -> ""

/// Port of SourceEntry.update.
let update (c: Constants) (index: Dictionary<int, SourceEntry>) (se: SourceEntry) (logEntry: Event) : unit =
    if not se.IsInactive
       && isEndPhase c logEntry.Phase
       && se.Entries.Count > 0
       && logEntry.Type = se.Entries.[0].Type then
        se.IsInactive <- true

    match logEntry.Params with
    | Some p ->
        match Json.tryNumber p "net_error" with
        | Some code when code <> 0.0 ->
            let isCacheOpen = eventTypeName c logEntry.Type = "HTTP_CACHE_OPEN_ENTRY"
            let isErrFailed =
                match netErrorId c "ERR_FAILED" with
                | Some ef -> int code = ef
                | None -> false
            if not (isCacheOpen && isErrFailed) then
                se.IsError <- true
        | _ -> ()
    | None -> ()

    let prevStart = getStartEntry c se
    se.Entries.Add logEntry
    let curStart = getStartEntry c se

    let startChanged =
        match prevStart, curStart with
        | None, None -> false
        | Some a, Some b -> a.Index <> b.Index
        | _ -> true

    if startChanged then
        updateDescription c index se

/// Port of SourceEntry constructor.
let create (c: Constants) (index: Dictionary<int, SourceEntry>) (first: Event) : SourceEntry =
    let isInactive =
        match c.PhaseBegin with
        | Some pb -> first.Phase <> pb
        | None -> true

    let se =
        { Entries = ResizeArray<Event>()
          Description = ""
          IsError = false
          IsInactive = isInactive }

    update c index se first
    se

// --- Timing (port of getStartTicks / getEndTicks / getDuration) ---

let startTicks (se: SourceEntry) : float =
    match se.Entries.[0].StartTime with
    | Some s -> s
    | None -> se.Entries.[0].Time

/// `nowTicks` is the reference "current time" for still-active sources; for a
/// static log the caller passes the last event's ticks.
let endTicks (nowTicks: float) (se: SourceEntry) : float =
    if not se.IsInactive then nowTicks
    else se.Entries.[se.Entries.Count - 1].Time

let duration (nowTicks: float) (se: SourceEntry) : float = endTicks nowTicks se - startTicks se

/// Builds the ordered source list and the id -> SourceEntry index from events
/// (mirrors SourceTracker.onReceivedLogEntries processing in event order).
let buildSources
    (c: Constants)
    (events: Event[])
    : SourceEntry[] * Dictionary<int, SourceEntry> =
    let index = Dictionary<int, SourceEntry>()
    let order = ResizeArray<SourceEntry>()

    for ev in events do
        match index.TryGetValue ev.SourceId with
        | true, se -> update c index se ev
        | _ ->
            let se = create c index ev
            index.[ev.SourceId] <- se
            order.Add se

    order.ToArray(), index
