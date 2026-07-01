/// Fable -> Node test harness for the Core NetLog parser. Runs at module load,
/// prints results, and exits non-zero on failure (so `npm test` reports failures).
module Netlog.Core.Tests.Main

open Fable.Core
open Netlog.Core

[<Emit("require('fs').readFileSync($0, 'utf8')")>]
let readFile (path: string) : string = jsNative

[<Emit("process.exit($0)")>]
let exitProcess (code: int) : unit = jsNative

let mutable failures = 0

let check (name: string) (cond: bool) : unit =
    if cond then
        printfn "  ok   %s" name
    else
        eprintfn "  FAIL %s" name
        failures <- failures + 1

let checkEq (name: string) (expected: 'a) (actual: 'a) : unit =
    if expected = actual then
        printfn "  ok   %s" name
    else
        eprintfn "  FAIL %s (expected %A, got %A)" name expected actual
        failures <- failures + 1

let isError (r: Result<'a, 'b>) : bool =
    match r with
    | Error _ -> true
    | Ok _ -> false

let wstr (o: obj) (k: string) : string = Json.tryString o k |> Option.defaultValue ""
let wnum (o: obj) (k: string) : float = Json.tryNumber o k |> Option.defaultValue 0.0

let mkFilterSource id typeName desc err active : Netlog.Webview.SourceFilterParser.FilterSource =
    { Id = id
      TypeName = typeName
      Description = desc
      IsError = err
      IsActive = active
      StartTicks = 0.0
      EndTicks = 0.0
      SearchText = (fun () -> "") }

// A truncated --log-net-log style dump (no closing "]}", trailing ",\n").
let truncatedDump =
    "{\"constants\":{\"logFormatVersion\":1,"
    + "\"logEventTypes\":{\"REQUEST_ALIVE\":1},"
    + "\"logEventPhase\":{\"PHASE_NONE\":0,\"PHASE_BEGIN\":1,\"PHASE_END\":2},"
    + "\"logSourceType\":{\"NONE\":0,\"URL_REQUEST\":1},"
    + "\"loadFlag\":{\"NORMAL\":0},\"netError\":{\"ERR_FAILED\":-2},"
    + "\"addressFamily\":{\"UNSPEC\":0},\"timeTickOffset\":\"1700000000000\","
    + "\"clientInfo\":{\"numericDate\":1700000000000}},"
    + "\"events\":[\n"
    + "{\"source\":{\"type\":1,\"id\":1},\"type\":1,\"time\":\"1000\",\"phase\":1,\"params\":{\"url\":\"https://a/\"}},\n"

// --- Valid full dump (samples/sample.netlog.json) ---
printfn "Parser: valid sample"

match LogParser.loadLogFile (readFile "samples/sample.netlog.json") with
| Error e ->
    check "sample loads" false
    eprintfn "%s" e
| Ok log ->
    checkEq "event count" 3 log.Events.Length
    checkEq "source count" 1 log.Sources.Length
    let s = log.Sources.[0]
    checkEq "source type name" "URL_REQUEST" (SourceGrouping.sourceTypeNameOf log.Constants s)
    checkEq "description" "https://example.com/" s.Description
    check "source inactive (completed)" s.IsInactive
    check "no error" (not s.IsError)
    checkEq "startTicks" 1000.0 (SourceGrouping.startTicks s)
    checkEq "duration" 80.0 (SourceGrouping.duration log.LastEventTicks s)
    checkEq "numericDate" 1700000000000.0 log.NumericDate
    checkEq "eventTypeName 1" "REQUEST_ALIVE" (Constants.eventTypeName log.Constants 1)

    // Wire model (host -> webview DTO)
    let wire = Wire.build "sample.netlog.json" log
    checkEq "wire type" "load" (wstr wire "type")
    checkEq "wire fileName" "sample.netlog.json" (wstr wire "fileName")
    checkEq "wire eventCount" 3 (int (wnum (Json.get wire "stats") "eventCount"))
    let wsources = Json.get wire "sources"
    checkEq "wire sources length" 1 (Json.length wsources)
    let ws0 = Json.item wsources 0
    checkEq "wire source typeName" "URL_REQUEST" (wstr ws0 "typeName")
    checkEq "wire source description" "https://example.com/" (wstr ws0 "description")
    check "wire source active=false" (not (Json.isTruthy (Json.get ws0 "isActive")))
    checkEq "wire events length" 3 (Json.length (Json.get wire "events"))
    checkEq
        "wire eventTypeNames[1]"
        "REQUEST_ALIVE"
        (wstr (Json.get (Json.get wire "constants") "eventTypeNames") "1")

// --- Error + fallback paths ---
printfn "Parser: error and fallback paths"

check "non-JSON -> Error" (isError (LogParser.loadLogFile "not json at all"))

match LogParser.loadLogFile "{}" with
| Error e -> check "empty object mentions constants" (e.Contains "Invalid constants object")
| Ok _ -> check "empty object -> Error" false

match LogParser.loadLogFile truncatedDump with
| Ok log ->
    checkEq "truncated event count" 1 log.Events.Length
    check "truncated warning present" (log.LoadLog.Contains "truncated")
| Error e ->
    check "truncated loads" false
    eprintfn "%s" e

// --- Source filter parser ---
printfn "SourceFilterParser"
let urlReq = mkFilterSource 1 "URL_REQUEST" "https://example.com/" false true
let sockErr = mkFilterSource 2 "SOCKET" "1.2.3.4" true false
checkEq "type: matches" true ((Netlog.Webview.SourceFilterParser.parse "type:url_request").Filter urlReq)
checkEq "type: rejects" false ((Netlog.Webview.SourceFilterParser.parse "type:url_request").Filter sockErr)
checkEq "is:error matches" true ((Netlog.Webview.SourceFilterParser.parse "is:error").Filter sockErr)
checkEq "is:error rejects" false ((Netlog.Webview.SourceFilterParser.parse "is:error").Filter urlReq)
checkEq "neg -is:error" false ((Netlog.Webview.SourceFilterParser.parse "-is:error").Filter sockErr)
checkEq "id list matches" true ((Netlog.Webview.SourceFilterParser.parse "id:2,5").Filter sockErr)
checkEq "id list rejects" false ((Netlog.Webview.SourceFilterParser.parse "id:2,5").Filter urlReq)
checkEq "text desc matches" true ((Netlog.Webview.SourceFilterParser.parse "example").Filter urlReq)
checkEq "sort duration" (Some("duration", false)) (Netlog.Webview.SourceFilterParser.parse "sort:duration").Sort
checkEq "sort backwards" (Some("id", true)) (Netlog.Webview.SourceFilterParser.parse "-sort:id").Sort

// --- Source filter: t: (time-range, e.g. from a Timeline double-click) ---
let timedSrc: Netlog.Webview.SourceFilterParser.FilterSource =
    { Id = 3
      TypeName = "SOCKET"
      Description = ""
      IsError = false
      IsActive = true
      StartTicks = 1000.0
      EndTicks = 2000.0
      SearchText = (fun () -> "") }
checkEq "t: inside range matches" true ((Netlog.Webview.SourceFilterParser.parse "t:1500").Filter timedSrc)
checkEq "t: at exact start matches" true ((Netlog.Webview.SourceFilterParser.parse "t:1000").Filter timedSrc)
checkEq "t: at exact end matches" true ((Netlog.Webview.SourceFilterParser.parse "t:2000").Filter timedSrc)
checkEq "t: before range rejects" false ((Netlog.Webview.SourceFilterParser.parse "t:500").Filter timedSrc)
checkEq "t: after range rejects" false ((Netlog.Webview.SourceFilterParser.parse "t:2500").Filter timedSrc)

// --- Source filter: has: (cross-source param search directive extraction) ---
checkEq "has: extracted, not a sync predicate" (Some "secrettoken") (Netlog.Webview.SourceFilterParser.parse "has:secrettoken").ParamSearch
checkEq "no has: -> ParamSearch None" None (Netlog.Webview.SourceFilterParser.parse "type:url_request").ParamSearch
checkEq
    "has: combines with a sync directive"
    true
    ((Netlog.Webview.SourceFilterParser.parse "type:url_request has:token").Filter urlReq)
checkEq
    "has: term does not itself affect the sync Filter"
    false
    ((Netlog.Webview.SourceFilterParser.parse "type:socket has:token").Filter urlReq)

// --- ProxyFormatter ---
printfn "ProxyFormatter"
checkEq "proxy direct" "Use DIRECT connections." (ProxyFormatter.proxySettingsToString (Json.parse "{}"))
checkEq
    "proxy single"
    "Proxy server: 1.2.3.4:8080"
    (ProxyFormatter.proxySettingsToString (Json.parse "{\"single_proxy\":\"1.2.3.4:8080\"}"))
checkEq "proxy autodetect" "Auto-detect" (ProxyFormatter.proxySettingsToString (Json.parse "{\"auto_detect\":true}"))

// --- Constants: cert path-builder digest policy ---
printfn "Constants: digest policy"
let digestConstants =
    Constants.decode (Json.parse "{\"certPathBuilderDigestPolicy\":{\"kAllAlgorithms\":1,\"kAllowSha1\":2}}")
checkEq "digest_policy name 2" "kAllowSha1" (Constants.keyWithValue digestConstants.CertPathBuilderDigestPolicy 2)

// --- Source filter: search over painted event text ---
printfn "SourceFilterParser: painted-text search"
let searchableSrc: Netlog.Webview.SourceFilterParser.FilterSource =
    { Id = 9
      TypeName = "URL_REQUEST"
      Description = "https://nomatch/"
      IsError = false
      IsActive = true
      StartTicks = 0.0
      EndTicks = 0.0
      SearchText = (fun () -> "Authorization: Bearer SECRETTOKEN") }
checkEq "text search via painted text" true ((Netlog.Webview.SourceFilterParser.parse "secrettoken").Filter searchableSrc)
checkEq "text search no false-positive" false ((Netlog.Webview.SourceFilterParser.parse "notpresent").Filter searchableSrc)

// --- LogViewPainter: bytes rendering (hex dump vs. searchable text) ---
printfn "LogViewPainter: bytes rendering"

match LogParser.loadLogFile (readFile "samples/sample.netlog.json") with
| Error e ->
    check "painter constants load" false
    eprintfn "%s" e
| Ok log ->
    // "QUJD" is base64 for "ABC".
    let bytesEvent: Model.Event =
        { Index = 0
          Time = 1000.0
          Type = 1
          Phase = 0
          SourceId = 1
          SourceType = 1
          StartTime = None
          Params = Some(Json.parse "{\"bytes\":\"QUJD\"}") }

    let searchTp =
        Netlog.Webview.LogViewPainter.createLogEntryTablePrinter log.Constants [| bytesEvent |] 1000.0 0.0 None true
    check "bytes searchable contains ascii" ((searchTp.ToTextString 0).Contains "ABC")

    let hexTp =
        Netlog.Webview.LogViewPainter.createLogEntryTablePrinter log.Constants [| bytesEvent |] 1000.0 0.0 None false
    let hexText = hexTp.ToTextString 0
    check "bytes hex contains hex octets" (hexText.Contains "41 42 43")
    check "bytes hex contains ascii gutter" (hexText.Contains "ABC")

// --- Streaming parser + JSON stream scanner ---
printfn "StreamingParser + JsonStreamScanner"

// Drives the scanner + streaming parser over `text`, split into `chunkSize` pieces,
// so chunk boundaries fall in the middle of keys/values/strings.
let runStreaming (maxEvents: int) (chunkSize: int) (text: string) : Result<LogParser.LoadedLog, string> =
    let st = StreamingParser.create maxEvents
    let scanner =
        Netlog.Core.JsonStreamScanner.Scanner(
            StreamingParser.pushConstantsJson st,
            StreamingParser.pushEventJson st,
            StreamingParser.pushTailJson st
        )
    let mutable i = 0
    while i < text.Length do
        let len = min chunkSize (text.Length - i)
        scanner.Push(text.Substring(i, len))
        i <- i + len
    scanner.Finish()
    StreamingParser.finish st scanner.IsComplete

let sampleText = readFile "samples/sample.netlog.json"

// checkConstants must (re-)establish TimeUtil's shared tick offset itself, not rely
// on a prior call having left the right value behind -- poison it with an obviously
// wrong sentinel first so this test can't accidentally pass via leftover state from
// an earlier test in this same process.
Netlog.Core.TimeUtil.setTimeTickOffset -999999.0
match LogParser.loadLogFile sampleText with
| Error _ -> check "tickOffset baseline loads" false
| Ok batchForOffset ->
    Netlog.Core.TimeUtil.setTimeTickOffset -999999.0
    match runStreaming 0 1 sampleText with
    | Error _ -> check "tickOffset streaming loads" false
    | Ok s -> checkEq "checkConstants resets TimeUtil tick offset" batchForOffset.NumericDate s.NumericDate

// Streaming must match the batch loadLogFile for the same document, at every chunk size.
match LogParser.loadLogFile sampleText with
| Error _ -> check "batch baseline loads" false
| Ok batch ->
    for chunkSize in [ 1; 3; 64; sampleText.Length ] do
        match runStreaming 0 chunkSize sampleText with
        | Error e ->
            check (sprintf "streaming loads (chunk=%d)" chunkSize) false
            eprintfn "%s" e
        | Ok s ->
            checkEq (sprintf "streaming event count (chunk=%d)" chunkSize) batch.Events.Length s.Events.Length
            checkEq (sprintf "streaming source count (chunk=%d)" chunkSize) batch.Sources.Length s.Sources.Length
            checkEq
                (sprintf "streaming description (chunk=%d)" chunkSize)
                batch.Sources.[0].Description
                s.Sources.[0].Description
            checkEq (sprintf "streaming numericDate (chunk=%d)" chunkSize) batch.NumericDate s.NumericDate
            checkEq
                (sprintf "streaming duration (chunk=%d)" chunkSize)
                (SourceGrouping.duration batch.LastEventTicks batch.Sources.[0])
                (SourceGrouping.duration s.LastEventTicks s.Sources.[0])
            check (sprintf "streaming clean load (chunk=%d)" chunkSize) (not (s.LoadLog.Contains "truncated"))

// A well-formed dump with trailing polledData + escaped-quote userComments.
let streamingDump =
    "{\"constants\":{\"logFormatVersion\":1,"
    + "\"logEventTypes\":{\"REQUEST_ALIVE\":1},"
    + "\"logEventPhase\":{\"PHASE_NONE\":0,\"PHASE_BEGIN\":1,\"PHASE_END\":2},"
    + "\"logSourceType\":{\"NONE\":0,\"URL_REQUEST\":1},"
    + "\"loadFlag\":{\"NORMAL\":0},\"netError\":{\"ERR_FAILED\":-2},"
    + "\"addressFamily\":{\"UNSPEC\":0},\"timeTickOffset\":\"1700000000000\","
    + "\"clientInfo\":{\"numericDate\":1700000000000}},\n"
    + "\"events\":[\n"
    + "{\"source\":{\"type\":1,\"id\":1},\"type\":1,\"time\":\"1000\",\"phase\":1,\"params\":{\"url\":\"https://a/\"}},\n"
    + "{\"source\":{\"type\":1,\"id\":1},\"type\":1,\"time\":\"1080\",\"phase\":2}\n"
    + "],\n"
    + "\"polledData\":{\"foo\":1},\n"
    + "\"userComments\":\"hello, \\\"world\\\"\"}\n"

match runStreaming 0 1 streamingDump with
| Error e ->
    check "streamingDump loads" false
    eprintfn "%s" e
| Ok s ->
    checkEq "streamingDump events" 2 s.Events.Length
    checkEq "streamingDump sources" 1 s.Sources.Length
    checkEq "streamingDump description" "https://a/" s.Sources.[0].Description
    checkEq "streamingDump polledData.foo" 1.0 (wnum s.PolledData "foo")
    checkEq "streamingDump userComments (unescaped)" (Some "hello, \"world\"") s.UserComments
    check "streamingDump not truncated" (not (s.LoadLog.Contains "truncated"))

// Cap: only the first N events are kept, with a warning.
match runStreaming 1 5 streamingDump with
| Error _ -> check "cap load" false
| Ok s ->
    checkEq "cap event count" 1 s.Events.Length
    check "cap warning present" (s.LoadLog.Contains "maximum")

// Truncated --log-net-log stream: last event incomplete, root never closes.
match runStreaming 0 5 truncatedDump with
| Error _ -> check "truncated stream load" false
| Ok s ->
    checkEq "truncated stream event count" 1 s.Events.Length
    check "truncated stream warning present" (s.LoadLog.Contains "truncated")

// --- Byte-oriented scanner (ByteJsonScanner): parity with the string-based one ---
// This is the scanner StreamLoader.fs actually uses for local files (bytes straight
// off the fs stream, no whole-file UTF-16 decode pass first).
printfn "ByteJsonScanner (byte-level streaming)"

// Drives ByteJsonScanner + StreamingParser over `text`, split into `chunkSize`-BYTE
// pieces (not characters) -- so, unlike the char-based `runStreaming` above, chunk
// boundaries can fall in the middle of a multi-byte UTF-8 character.
let runStreamingBytes (maxEvents: int) (chunkSize: int) (text: string) : Result<LogParser.LoadedLog, string> =
    let st = StreamingParser.create maxEvents
    let scanner =
        Netlog.Core.ByteJsonScanner.Scanner(
            (fun buf s e -> StreamingParser.pushConstantsJson st (ByteJsonScanner.decodeByteRange buf s e)),
            (fun buf s e -> StreamingParser.pushEventJson st (ByteJsonScanner.decodeByteRange buf s e)),
            (fun buf key vs ve -> StreamingParser.pushTailJson st key (ByteJsonScanner.decodeByteRange buf vs ve))
        )
    let bytes = ByteJsonScanner.encodeUtf8 text
    let mutable i = 0
    while i < bytes.Length do
        let len = min chunkSize (bytes.Length - i)
        scanner.Push(Array.sub bytes i len)
        i <- i + len
    scanner.Finish()
    StreamingParser.finish st scanner.IsComplete

match LogParser.loadLogFile sampleText with
| Error _ -> check "byte-scanner batch baseline loads" false
| Ok batch ->
    for chunkSize in [ 1; 3; 64; 760 ] do
        match runStreamingBytes 0 chunkSize sampleText with
        | Error e ->
            check (sprintf "byte-streaming loads (chunk=%d)" chunkSize) false
            eprintfn "%s" e
        | Ok s ->
            checkEq (sprintf "byte-streaming event count (chunk=%d)" chunkSize) batch.Events.Length s.Events.Length
            checkEq (sprintf "byte-streaming source count (chunk=%d)" chunkSize) batch.Sources.Length s.Sources.Length
            checkEq
                (sprintf "byte-streaming description (chunk=%d)" chunkSize)
                batch.Sources.[0].Description
                s.Sources.[0].Description
            checkEq (sprintf "byte-streaming numericDate (chunk=%d)" chunkSize) batch.NumericDate s.NumericDate
            check (sprintf "byte-streaming clean load (chunk=%d)" chunkSize) (not (s.LoadLog.Contains "truncated"))

match runStreamingBytes 0 1 streamingDump with
| Error e ->
    check "byte streamingDump loads" false
    eprintfn "%s" e
| Ok s ->
    checkEq "byte streamingDump events" 2 s.Events.Length
    checkEq "byte streamingDump sources" 1 s.Sources.Length
    checkEq "byte streamingDump description" "https://a/" s.Sources.[0].Description
    checkEq "byte streamingDump polledData.foo" 1.0 (wnum s.PolledData "foo")
    checkEq "byte streamingDump userComments (unescaped)" (Some "hello, \"world\"") s.UserComments
    check "byte streamingDump not truncated" (not (s.LoadLog.Contains "truncated"))

match runStreamingBytes 1 5 streamingDump with
| Error _ -> check "byte cap load" false
| Ok s ->
    checkEq "byte cap event count" 1 s.Events.Length
    check "byte cap warning present" (s.LoadLog.Contains "maximum")

match runStreamingBytes 0 5 truncatedDump with
| Error _ -> check "byte truncated stream load" false
| Ok s ->
    checkEq "byte truncated stream event count" 1 s.Events.Length
    check "byte truncated stream warning present" (s.LoadLog.Contains "truncated")

// Multi-byte UTF-8 content fed one BYTE at a time (chunkSize=1): every possible
// split point of every multi-byte character is exercised, proving the scanner
// reassembles them correctly before decoding rather than corrupting/dropping bytes.
let utf8Dump =
    "{\"constants\":{\"logFormatVersion\":1,"
    + "\"logEventTypes\":{\"REQUEST_ALIVE\":1},"
    + "\"logEventPhase\":{\"PHASE_NONE\":0,\"PHASE_BEGIN\":1,\"PHASE_END\":2},"
    + "\"logSourceType\":{\"NONE\":0,\"URL_REQUEST\":1},"
    + "\"loadFlag\":{\"NORMAL\":0},\"netError\":{\"ERR_FAILED\":-2},"
    + "\"addressFamily\":{\"UNSPEC\":0},\"timeTickOffset\":\"1700000000000\","
    + "\"clientInfo\":{\"numericDate\":1700000000000}},\n"
    + "\"events\":[\n"
    + "{\"source\":{\"type\":1,\"id\":1},\"type\":1,\"time\":\"1000\",\"phase\":1,"
    + "\"params\":{\"url\":\"https://例え.jp/日本語/café\"}}\n"
    + "],\n"
    + "\"polledData\":{}}\n"

match runStreamingBytes 0 1 utf8Dump with
| Error e ->
    check "byte utf8Dump loads" false
    eprintfn "%s" e
| Ok s -> checkEq "byte utf8Dump description (multibyte, chunk=1)" "https://例え.jp/日本語/café" s.Sources.[0].Description

// --- Parallel-loading core helpers: validateEventBatch + finalizeFromShards ---
// (Core.fsproj-only logic used by the worker-thread loading path; the actual worker
// orchestration lives in the Extension project and isn't unit-testable here, but the
// merge/regroup correctness -- the part most likely to have subtle bugs, since a
// source's events can be split across shards -- is fully covered by comparing
// against the sequential `loadLogFile` baseline at several different shard counts.)
printfn "Parallel ingest (validateEventBatch + finalizeFromShards)"

let runSharded (numShards: int) (maxEvents: int) (text: string) : Result<LogParser.LoadedLog, string> =
    let raw = Json.parse text
    match LogParser.checkConstants (Json.get raw "constants") with
    | Error e -> Error e
    | Ok constants ->
        let eventsRaw = Json.get raw "events"
        let total = if Json.isArray eventsRaw then Json.length eventsRaw else 0
        let evs = Array.init total (fun i -> Json.item eventsRaw i)
        let shardSize = max 1 ((evs.Length + numShards - 1) / numShards)
        let shards =
            [| for i in 0 .. numShards - 1 do
                   let start = i * shardSize
                   if start < evs.Length then
                       let len = min shardSize (evs.Length - start)
                       yield LogParser.validateEventBatch constants (Array.sub evs start len) |]
        let polledData =
            let p = Json.get raw "polledData"
            if Json.isObject p then p else Json.emptyObject ()
        let tabData =
            let t = Json.get raw "tabData"
            if Json.isObject t then t else Json.emptyObject ()
        Ok(
            LogParser.finalizeFromShards
                constants
                shards
                maxEvents
                polledData
                tabData
                (Json.tryString raw "userComments")
                ""
        )

match LogParser.loadLogFile sampleText with
| Error _ -> check "sharded batch baseline loads" false
| Ok batch ->
    for numShards in [ 1; 2; 3; 7 ] do
        match runSharded numShards 0 sampleText with
        | Error e ->
            check (sprintf "sharded loads (n=%d)" numShards) false
            eprintfn "%s" e
        | Ok s ->
            checkEq (sprintf "sharded event count (n=%d)" numShards) batch.Events.Length s.Events.Length
            checkEq (sprintf "sharded source count (n=%d)" numShards) batch.Sources.Length s.Sources.Length
            checkEq (sprintf "sharded numericDate (n=%d)" numShards) batch.NumericDate s.NumericDate
            check (sprintf "sharded clean load (n=%d)" numShards) (not (s.LoadLog.Contains "truncated"))
            // Every source's derived state must match the sequential baseline exactly,
            // regardless of how its events were split across shards -- this is the
            // property most at risk when grouping is deferred to a post-merge pass.
            checkEq
                (sprintf "sharded per-source parity (n=%d)" numShards)
                (batch.Sources |> Array.map (fun se -> se.Description, se.IsError, se.IsInactive, se.Entries.Count))
                (s.Sources |> Array.map (fun se -> se.Description, se.IsError, se.IsInactive, se.Entries.Count))

// Cap enforcement must still be exact when events are split across shards: the
// merge concatenates shards in original order before truncating, so the kept
// events are always the first `maxEvents` in FILE order, not shard-arrival order.
match runSharded 2 1 streamingDump with
| Error _ -> check "sharded cap load" false
| Ok s ->
    checkEq "sharded cap event count" 1 s.Events.Length
    check "sharded cap warning present" (s.LoadLog.Contains "maximum")

// validateEventBatchFromJson: a malformed JSON string in the batch must be counted
// as invalid, not crash the whole shard (this is what a worker thread actually
// calls, since it only has raw byte ranges, not pre-parsed objects).
match LogParser.checkConstants (Json.get (Json.parse streamingDump) "constants") with
| Error _ -> check "validateEventBatchFromJson constants" false
| Ok constants ->
    let rawEvents =
        [| "{\"source\":{\"type\":1,\"id\":1},\"type\":1,\"time\":\"1000\",\"phase\":1,\"params\":{\"url\":\"https://a/\"}}"
           "{not valid json"
           "{\"source\":{\"type\":1,\"id\":1},\"type\":1,\"time\":\"1080\",\"phase\":2}" |]
    let result = LogParser.validateEventBatchFromJson constants rawEvents
    checkEq "validateEventBatchFromJson valid count" 2 result.Events.Length
    checkEq "validateEventBatchFromJson invalid count" 1 result.NumInvalid

// eventToTransfer/eventFromTransfer must round-trip exactly (worker_threads
// boundary): StartTime (Some and None) and Params must survive intact.
let transferSample: Model.Event =
    { Index = 5
      Time = 1234.5
      Type = 2
      Phase = 1
      SourceId = 9
      SourceType = 3
      StartTime = Some 999.0
      Params = Some(Json.parse "{\"url\":\"https://example/\"}") }

let transferRoundTrip = LogParser.eventFromTransfer (LogParser.eventToTransfer transferSample)
checkEq "eventTransfer round-trip (Some StartTime)" transferSample transferRoundTrip

let transferSampleNoStart = { transferSample with StartTime = None; Params = None }
let transferRoundTripNoStart = LogParser.eventFromTransfer (LogParser.eventToTransfer transferSampleNoStart)
checkEq "eventTransfer round-trip (None StartTime/Params)" transferSampleNoStart transferRoundTripNoStart

// --- Wire.postLoad chunked protocol (loadStart -> chunks -> loadEnd) ---
printfn "Wire.postLoad (chunked)"

match LogParser.loadLogFile sampleText with
| Error _ -> check "postLoad baseline loads" false
| Ok log ->
    let msgs = ResizeArray<obj>()
    Wire.postLoad (fun m -> msgs.Add m) "sample.netlog.json" log
    check "postLoad emits messages" (msgs.Count >= 2)
    checkEq "postLoad first is loadStart" "loadStart" (wstr msgs.[0] "type")
    checkEq "postLoad last is loadEnd" "loadEnd" (wstr msgs.[msgs.Count - 1] "type")

    let mutable evTotal = 0
    let mutable srcTotal = 0
    for m in msgs do
        match wstr m "type" with
        | "eventsChunk" -> evTotal <- evTotal + Json.length (Json.get m "events")
        | "sourcesChunk" -> srcTotal <- srcTotal + Json.length (Json.get m "sources")
        | _ -> ()
    checkEq "postLoad reassembled event count" log.Events.Length evTotal
    checkEq "postLoad reassembled source count" log.Sources.Length srcTotal
    checkEq "postLoad loadStart eventCount" log.Events.Length (int (wnum (Json.get msgs.[0] "stats") "eventCount"))
    check "postLoad loadStart omits events array" (not (Json.isArray (Json.get msgs.[0] "events")))
    check "postLoad loadStart carries constants" (Json.isObject (Json.get msgs.[0] "constants"))

// --- Wire.sourceEventsMessage (on-demand details) ---
printfn "Wire.sourceEventsMessage"

match LogParser.loadLogFile sampleText with
| Error _ -> check "sourceEvents baseline loads" false
| Ok log ->
    match log.SourceIndex.TryGetValue 1 with
    | true, se ->
        let msg = Wire.sourceEventsMessage 1 se.Entries se.Entries.Count 50000
        checkEq "sourceEvents type" "sourceEvents" (wstr msg "type")
        checkEq "sourceEvents id" 1 (int (wnum msg "id"))
        checkEq "sourceEvents event count" se.Entries.Count (Json.length (Json.get msg "events"))
        checkEq "sourceEvents total" se.Entries.Count (int (wnum msg "total"))
        check "sourceEvents not truncated" (not (Json.isTruthy (Json.get msg "truncated")))
        let ev0 = Json.item (Json.get msg "events") 0
        checkEq "sourceEvents ev0 sourceId" 1 (int (wnum ev0 "sourceId"))

        let capped = Wire.sourceEventsMessage 1 se.Entries se.Entries.Count 2
        checkEq "sourceEvents capped count" 2 (Json.length (Json.get capped "events"))
        checkEq "sourceEvents capped total" se.Entries.Count (int (wnum capped "total"))
        check "sourceEvents capped truncated" (Json.isTruthy (Json.get capped "truncated"))
    | _ -> check "source id 1 present" false

printfn ""

// --- samples/sample-request-chain.netlog.json parity ---
// A small hand-written fixture with a full source_dependency chain (URL_REQUEST ->
// HTTP_STREAM_JOB -> SOCKET -> HOST_RESOLVER_IMPL_JOB), a real HTTP request/response
// header exchange, and one failed (net_error) request -- see that file's own shape
// for the exact events.
printfn "samples/sample-request-chain.netlog.json parity"

let chainText = readFile "samples/sample-request-chain.netlog.json"

match LogParser.loadLogFile chainText with
| Error e ->
    check "request-chain sample loads" false
    eprintfn "%s" e
| Ok chainLog ->
    checkEq "request-chain event count" 14 chainLog.Events.Length
    checkEq "request-chain source count" 5 chainLog.Sources.Length

    match chainLog.SourceIndex.TryGetValue 3 with
    | true, socket ->
        checkEq "SOCKET description (via DNS dependency)" "example.com" socket.Description
        check "SOCKET stays active (never closed)" (not socket.IsInactive)
    | _ -> check "source id 3 present" false

    match chainLog.SourceIndex.TryGetValue 5 with
    | true, failed -> check "failed request flagged as error" failed.IsError
    | _ -> check "source id 5 present" false

// --- RequestSummary.extract (pure L7 request/response summary) ---
printfn "RequestSummary.extract"

match LogParser.loadLogFile chainText with
| Error _ -> check "request-chain baseline loads" false
| Ok chainLog ->
    match chainLog.SourceIndex.TryGetValue 1 with
    | true, urlRequest ->
        match RequestSummary.extract (urlRequest.Entries.ToArray()) with
        | None -> check "extracts an exchange for the successful request" false
        | Some ex ->
            checkEq "method" "GET" ex.Method
            checkEq "url" "https://example.com/" ex.Url
            checkEq "request line" (Some "GET / HTTP/1.1") ex.RequestLine
            checkEq
                "request headers"
                [| "Host: example.com"; "Accept: */*"; "User-Agent: netlog-viewer-sample/1.0" |]
                ex.RequestHeaders
            checkEq "status line" (Some "HTTP/1.1 200 OK") ex.StatusLine
            checkEq
                "response headers"
                [| "Content-Type: text/html; charset=utf-8"; "Content-Length: 1256"; "Server: ExampleServer/1.0" |]
                ex.ResponseHeaders
            checkEq "request sent ticks" (Some 1160.0) ex.RequestSentTicks
            checkEq "response headers ticks" (Some 1220.0) ex.ResponseHeadersTicks
    | _ -> check "source id 1 present" false

    match chainLog.SourceIndex.TryGetValue 5 with
    | true, failedRequest ->
        match RequestSummary.extract (failedRequest.Entries.ToArray()) with
        | None -> check "extracts a partial summary for the failed request" false
        | Some ex ->
            checkEq "failed request method (from method param, no headers seen)" "GET" ex.Method
            checkEq "failed request url" "https://nonexistent.invalid/" ex.Url
            check "no request line (no header exchange happened)" ex.RequestLine.IsNone
            check "no status line (no response ever arrived)" ex.StatusLine.IsNone
    | _ -> check "source id 5 present" false

    match chainLog.SourceIndex.TryGetValue 4 with
    | true, dnsJob -> check "no summary for a non-HTTP source (no url param at all)" (RequestSummary.extract (dnsJob.Entries.ToArray())).IsNone
    | _ -> check "source id 4 present" false

printfn ""

if failures > 0 then
    eprintfn "%d test(s) failed" failures
    exitProcess 1
else
    printfn "All tests passed"
