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
    { Id = id; TypeName = typeName; Description = desc; IsError = err; IsActive = active; SearchText = (fun () -> "") }

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

if failures > 0 then
    eprintfn "%d test(s) failed" failures
    exitProcess 1
else
    printfn "All tests passed"
