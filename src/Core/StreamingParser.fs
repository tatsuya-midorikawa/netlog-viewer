/// Streaming counterpart to LogParser.loadLogFile. Instead of parsing the whole file
/// as one string (which hits V8's ~512 MB string limit on large logs), it consumes
/// the raw JSON substrings emitted by JsonStreamScanner and feeds events through the
/// shared LogParser ingest. A configurable event cap keeps memory bounded regardless
/// of file size.
///
/// Events are parsed in batches (see `flushPending`) rather than one `JSON.parse`
/// call per event: far fewer native call-boundary crossings for the common case
/// where every event in a batch is valid, with a per-item fallback so one malformed
/// record never takes out the rest of its batch.
///
/// Pure (no Node/DOM): the Extension Host supplies the bytes/stream, this drives the
/// parse. Unit-tested in Core against the batch loadLogDump for parity.
module Netlog.Core.StreamingParser

open Netlog.Core

/// Number of raw event substrings accumulated before parsing them as one batch.
/// Trades a small, bounded overshoot of `MaxEvents` (at most this many events, since
/// the cap is only rechecked at flush boundaries) for far fewer `JSON.parse` calls.
[<Literal>]
let private BatchSize = 1000

type StreamState =
    { mutable Ingest: LogParser.IngestState option
      mutable PolledData: obj
      mutable TabData: obj
      mutable UserComments: string option
      mutable Fatal: string option
      MaxEvents: int
      mutable CapReached: bool
      mutable MalformedEvents: int
      /// Raw event JSON substrings not yet parsed; flushed in batches (see `flushPending`).
      Pending: ResizeArray<string> }

/// `maxEvents <= 0` means "no cap".
let create (maxEvents: int) : StreamState =
    { Ingest = None
      PolledData = Json.emptyObject ()
      TabData = Json.emptyObject ()
      UserComments = None
      Fatal = None
      MaxEvents = (if maxEvents > 0 then maxEvents else System.Int32.MaxValue)
      CapReached = false
      MalformedEvents = 0
      Pending = ResizeArray<string>() }

/// Parses and ingests all pending raw event substrings accumulated since the last
/// flush. Fast path: synthesize `[e1,e2,...]` and call `JSON.parse` once for the
/// whole batch. If that throws (one bad record breaks the whole array), falls back
/// to parsing each pending item individually -- exactly like the original per-event
/// implementation -- so malformed-event counting stays precise. `MaxEvents` is
/// re-checked per item (not just per batch) so a batch that crosses the cap still
/// ingests exactly up to the cap, matching the original one-event-at-a-time semantics.
let private flushPending (st: StreamState) (ig: LogParser.IngestState) : unit =
    if st.Pending.Count > 0 then
        let ingestCapped (ev: obj) : unit =
            if ig.ValidEvents.Count >= st.MaxEvents then
                st.CapReached <- true
            else
                LogParser.ingestEvent ig ev
        (try
            let batch = Json.parse ("[" + String.concat "," st.Pending + "]")
            let n = Json.length batch
            for i in 0 .. n - 1 do
                ingestCapped (Json.item batch i)
         with _ ->
            for raw in st.Pending do
                try
                    ingestCapped (Json.parse raw)
                with _ ->
                    st.MalformedEvents <- st.MalformedEvents + 1)
        st.Pending.Clear()

/// Handles the top-level "constants" object. A parse/validation failure is fatal.
let pushConstantsJson (st: StreamState) (json: string) : unit =
    if st.Fatal.IsNone && st.Ingest.IsNone then
        try
            let raw = Json.parse json
            match LogParser.checkConstants raw with
            | Ok c -> st.Ingest <- Some(LogParser.newIngest c)
            | Error e -> st.Fatal <- Some e
        with _ ->
            st.Fatal <- Some "Load failed:\n\nInvalid constants object.\n"

/// Handles one element of the top-level "events" array. Constants must have arrived
/// first (guaranteed by the NetLog format). Events are accumulated and parsed in
/// batches (see `flushPending`); events beyond the cap are ignored.
let pushEventJson (st: StreamState) (json: string) : unit =
    if st.Fatal.IsNone then
        match st.Ingest with
        | Some ig ->
            if ig.ValidEvents.Count >= st.MaxEvents then
                st.CapReached <- true
            else
                st.Pending.Add json
                if st.Pending.Count >= BatchSize then
                    flushPending st ig
        | None -> ()

/// Handles any other top-level value (polledData / tabData / userComments).
let pushTailJson (st: StreamState) (key: string) (json: string) : unit =
    if st.Fatal.IsNone then
        try
            let raw = Json.parse json
            match key with
            | "polledData" -> if Json.isObject raw then st.PolledData <- raw
            | "tabData" -> if Json.isObject raw then st.TabData <- raw
            | "userComments" -> if Json.isString raw then st.UserComments <- Some(Json.jsString raw)
            | _ -> ()
        with _ ->
            () // non-fatal: metadata tails are optional

/// Assembles the final LoadedLog. `scannerComplete` is JsonStreamScanner.IsComplete;
/// when false the log was truncated. Warnings (truncation / cap / malformed) are
/// prepended so they surface ahead of the invalid/passive tallies, mirroring the
/// original loadLogFile ordering.
let finish (st: StreamState) (scannerComplete: bool) : Result<LogParser.LoadedLog, string> =
    match st.Fatal with
    | Some e -> Error e
    | None ->
        match st.Ingest with
        | None -> Error "Unable to parse log dump as JSON file."
        | Some ig ->
            flushPending st ig
            let mutable warn = ""
            if not scannerComplete then
                warn <- warn + "Log file truncated.  Events may be missing.\n"
            if st.CapReached then
                warn <-
                    warn
                    + sprintf
                        "Reached the maximum of %d events; the rest were not loaded. Increase the \"netlogViewer.maxEvents\" setting to load more.\n\n"
                        st.MaxEvents
            if st.MalformedEvents > 0 then
                warn <- warn + sprintf "Skipped %d unparseable event records.\n\n" st.MalformedEvents
            Ok(LogParser.finalizeIngest ig st.PolledData st.TabData st.UserComments warn)
