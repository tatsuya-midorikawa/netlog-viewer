/// Streaming counterpart to LogParser.loadLogFile. Instead of parsing the whole file
/// as one string (which hits V8's ~512 MB string limit on large logs), it consumes
/// the raw JSON substrings emitted by JsonStreamScanner, parses each one individually,
/// and feeds events through the shared LogParser ingest. A configurable event cap
/// keeps memory bounded regardless of file size.
///
/// Pure (no Node/DOM): the Extension Host supplies the bytes/stream, this drives the
/// parse. Unit-tested in Core against the batch loadLogDump for parity.
module Netlog.Core.StreamingParser

open Netlog.Core

type StreamState =
    { mutable Ingest: LogParser.IngestState option
      mutable PolledData: obj
      mutable TabData: obj
      mutable UserComments: string option
      mutable Fatal: string option
      MaxEvents: int
      mutable CapReached: bool
      mutable MalformedEvents: int }

/// `maxEvents <= 0` means "no cap".
let create (maxEvents: int) : StreamState =
    { Ingest = None
      PolledData = Json.emptyObject ()
      TabData = Json.emptyObject ()
      UserComments = None
      Fatal = None
      MaxEvents = (if maxEvents > 0 then maxEvents else System.Int32.MaxValue)
      CapReached = false
      MalformedEvents = 0 }

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
/// first (guaranteed by the NetLog format). Malformed events are skipped and counted;
/// events beyond the cap are ignored.
let pushEventJson (st: StreamState) (json: string) : unit =
    if st.Fatal.IsNone then
        match st.Ingest with
        | Some ig ->
            if ig.ValidEvents.Count >= st.MaxEvents then
                st.CapReached <- true
            else
                try
                    LogParser.ingestEvent ig (Json.parse json)
                with _ ->
                    st.MalformedEvents <- st.MalformedEvents + 1
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
