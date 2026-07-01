/// Streams a local NetLog file (optionally gzip-compressed) through the incremental
/// ByteJsonScanner + StreamingParser, then hands the wire "load" message back. It
/// never materialises the whole file as a single string, so it is not bound by V8's
/// ~512 MB string limit, and the configurable event cap keeps memory bounded for
/// arbitrarily large files. Raw stream bytes are scanned directly (no whole-file
/// UTF-8-to-UTF-16 decode pass); only each emitted value's own byte range is decoded.
module Netlog.Extension.StreamLoader

open Fable.Core.JsInterop
open Netlog.Core

/// Streams `path` and hands the parsed log to `onLog` on success (the caller decides
/// how to deliver it to the webview and whether to retain it), or calls `onError`
/// with a message on failure. `maxEvents <= 0` means "no cap".
let load
    (path: string)
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
        let gz =
            try Node.isGzipFile path
            with _ -> path.EndsWith ".gz"

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
