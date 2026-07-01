/// Streams a local NetLog file (optionally gzip-compressed) through the incremental
/// ByteJsonScanner + StreamingParser, then hands the wire "load" message back. It
/// never materialises the whole file as a single string, so it is not bound by V8's
/// ~512 MB string limit, and the configurable event cap keeps memory bounded for
/// arbitrarily large files. Raw stream bytes are scanned directly (no whole-file
/// UTF-8-to-UTF-16 decode pass); only each emitted value's own byte range is decoded.
///
/// Below `WholeBufferThreshold`, `load` instead reads the whole (decompressed) file
/// into one Buffer and feeds it to the SAME scanner/parser pipeline in a single
/// `Push` + `Finish` call (see `loadWhole`). This is measurably faster than the
/// incremental path (avoids many repeated buffer-trim/grow allocations as chunks
/// arrive -- see /memories/repo/verification.md's whole-buffer-vs-streaming finding)
/// at the cost of holding the whole file (and, for `.gz`, the decompressed bytes
/// too) in memory at once -- an acceptable, bounded trade-off well under this
/// threshold. Larger files fall back to `loadStreaming`, unchanged from before.
module Netlog.Extension.StreamLoader

open Fable.Core.JsInterop
open Netlog.Core

/// Above this on-disk file size, memory-bounded incremental streaming is used
/// instead of reading the whole file at once (matches the plan's own "~1.5GB"
/// guidance for where whole-buffer reads stop being an acceptable trade-off).
[<Literal>]
let private WholeBufferThreshold = 1610612736 // 1.5 GB

/// Runs the scanner/parser over one complete in-memory buffer (already
/// decompressed if the source was gzip) in a single `Push` + `Finish` call.
let private runWhole (buf: byte[]) (maxEvents: int) : Result<LogParser.LoadedLog, string> =
    let st = StreamingParser.create maxEvents

    let scanner =
        ByteJsonScanner.Scanner(
            (fun b s e -> StreamingParser.pushConstantsJson st (ByteJsonScanner.decodeByteRange b s e)),
            (fun b s e -> StreamingParser.pushEventJson st (ByteJsonScanner.decodeByteRange b s e)),
            (fun b key vs ve -> StreamingParser.pushTailJson st key (ByteJsonScanner.decodeByteRange b vs ve))
        )

    scanner.Push(buf)
    scanner.Finish()
    StreamingParser.finish st scanner.IsComplete

/// Whole-buffer fast path: reads (and, for `.gz`, decompresses) the entire file
/// synchronously, then scans/parses it in one pass. Only called below
/// `WholeBufferThreshold`, so the transient extra memory (input bytes, plus
/// decompressed bytes for `.gz`) is bounded.
let private loadWhole
    (path: string)
    (gz: bool)
    (maxEvents: int)
    (onLog: LogParser.LoadedLog -> unit)
    (onError: string -> unit)
    : unit =
    try
        let raw = Node.readFileSyncBytes path
        let buf = if gz then unbox<byte[]> (Node.gunzipSync (box raw)) else raw

        match runWhole buf maxEvents with
        | Ok log -> onLog log
        | Error e -> onError e
    with ex ->
        onError ("Failed to load: " + string ex)

/// Incremental streaming path (unchanged): never materialises the whole file as one
/// buffer/string, so it remains the fallback for files at or above
/// `WholeBufferThreshold`.
let private loadStreaming
    (path: string)
    (gz: bool)
    (maxEvents: int)
    (onLog: LogParser.LoadedLog -> unit)
    (onError: string -> unit)
    : unit =
    let mutable finished = false

    let fail (msg: string) : unit =
        if not finished then
            finished <- true
            onError msg

    try
        let fileStream = Node.createReadStream path
        let stream = if gz then Node.pipe fileStream (Node.createGunzip ()) else fileStream

        let st = StreamingParser.create maxEvents

        let scanner =
            ByteJsonScanner.Scanner(
                (fun buf s e -> StreamingParser.pushConstantsJson st (ByteJsonScanner.decodeByteRange buf s e)),
                (fun buf s e -> StreamingParser.pushEventJson st (ByteJsonScanner.decodeByteRange buf s e)),
                (fun buf key vs ve -> StreamingParser.pushTailJson st key (ByteJsonScanner.decodeByteRange buf vs ve))
            )

        Node.on fileStream "error" (fun err -> fail ("Failed to read file: " + Node.errorMessage err))

        if gz then
            Node.on stream "error" (fun err -> fail ("Failed to decompress: " + Node.errorMessage err))

        Node.on stream "data" (fun chunk ->
            if not finished then
                try
                    scanner.Push(unbox<byte[]> chunk)
                with ex ->
                    Node.destroyStream stream
                    fail ("Failed to parse: " + string ex))

        Node.on stream "end" (fun _ ->
            if not finished then
                try
                    scanner.Finish()

                    match StreamingParser.finish st scanner.IsComplete with
                    | Ok log ->
                        if not finished then
                            finished <- true
                            onLog log
                    | Error e -> fail e
                with ex ->
                    fail ("Failed to load: " + string ex))
    with ex ->
        fail ("Failed to load: " + string ex)

/// Streams `path` and hands the parsed log to `onLog` on success (the caller decides
/// how to deliver it to the webview and whether to retain it), or calls `onError`
/// with a message on failure. `maxEvents <= 0` means "no cap".
let load
    (path: string)
    (maxEvents: int)
    (onLog: LogParser.LoadedLog -> unit)
    (onError: string -> unit)
    : unit =
    try
        let gz =
            try Node.isGzipFile path
            with _ -> path.EndsWith ".gz"

        let size = try Node.fileSize path with _ -> float WholeBufferThreshold

        if size < float WholeBufferThreshold then
            loadWhole path gz maxEvents onLog onError
        else
            loadStreaming path gz maxEvents onLog onError
    with ex ->
        onError ("Failed to load: " + string ex)
