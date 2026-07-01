/// Parallel (worker_threads) counterpart to StreamLoader for very large local
/// files: reads the whole file into a SharedArrayBuffer, does one single-threaded
/// pass to find every event's exact byte range (reusing the already-correct
/// ByteJsonScanner, so shard boundaries are never guessed/approximated), then
/// splits those events across a small pool of worker threads that each validate
/// their shard independently (see ParseWorker.fs). Source-grouping only happens
/// once, after merging shards back into original file order, because a source's
/// events can be spread across multiple shards (see Core.LogParser.finalizeFromShards).
///
/// Falls back to the existing single-threaded StreamLoader for small files, gzip
/// files (a gzip stream can't be range-sharded), and single-core machines, so `load`
/// is always safe to call in place of `StreamLoader.load`.
///
/// MEASURED RESULT (2026-07-01, 533MB/694830-event real sample, 10-core machine):
/// this is byte-for-byte correct (verified against StreamLoader.load via
/// scripts/verify-parallel.mjs), but NOT currently wired into NetlogEditor.fs as the
/// active path, because it is not a meaningful net win end to end -- the necessary
/// single-threaded pre-pass scan (~1s here) plus worker spawn/structured-clone
/// transfer overhead (workers must send their full validated Event[], including
/// `Params`, back to the main thread for merging/grouping) roughly cancel out the
/// parallel parsing gain, netting only ~0-10%, run-to-run-noise-level, sometimes
/// even slightly negative. Kept, tested, and documented for future improvement --
/// the two things that would need to change to make it worthwhile are (a) a
/// cheaper/overlapped pre-pass instead of a full serial scan up front, and (b) a
/// more compact worker-output transfer format than structured-cloning full Event
/// objects (e.g. a columnar/typed-array representation, similar in spirit to the
/// wire-payload slimming in Core.Wire).
module Netlog.Extension.ParallelStreamLoader

open Fable.Core.JsInterop
open Netlog.Core

/// Below this file size, worker startup/serialization overhead isn't worth it.
[<Literal>]
let private MinBytesForParallel = 67108864 // 64 MB

/// Avoid spinning up workers for shards too small to matter.
[<Literal>]
let private MinEventsPerWorker = 20000

[<Literal>]
let private MaxWorkers = 8

type private Prepass =
    { ConstantsText: string option
      EventStarts: int[]
      EventEnds: int[]
      PolledData: obj
      TabData: obj
      UserComments: string option
      IsComplete: bool }

/// Single-threaded pass over the whole in-memory file: records the constants text,
/// every event's exact `[start, end)` byte range (no JSON.parse yet -- that's the
/// part sharded out to workers), and any tail values (polledData/tabData/
/// userComments). Reuses ByteJsonScanner as-is, so this can never disagree with the
/// single-threaded path about where an event begins or ends.
let private runPrepass (buf: byte[]) : Prepass =
    let mutable constantsText = None
    let mutable polledData = Json.emptyObject ()
    let mutable tabData = Json.emptyObject ()
    let mutable userComments = None
    let eventStarts = ResizeArray<int>()
    let eventEnds = ResizeArray<int>()

    let scanner =
        ByteJsonScanner.Scanner(
            (fun b s e -> constantsText <- Some(ByteJsonScanner.decodeByteRange b s e)),
            (fun _ s e ->
                eventStarts.Add s
                eventEnds.Add e),
            (fun b key vs ve ->
                try
                    let raw = Json.parse (ByteJsonScanner.decodeByteRange b vs ve)
                    match key with
                    | "polledData" -> if Json.isObject raw then polledData <- raw
                    | "tabData" -> if Json.isObject raw then tabData <- raw
                    | "userComments" -> if Json.isString raw then userComments <- Some(Json.jsString raw)
                    | _ -> ()
                with _ -> ())
        )
    scanner.Push buf
    scanner.Finish()
    { ConstantsText = constantsText
      EventStarts = eventStarts.ToArray()
      EventEnds = eventEnds.ToArray()
      PolledData = polledData
      TabData = tabData
      UserComments = userComments
      IsComplete = scanner.IsComplete }

/// Splits `m` events into up to `numWorkers` roughly-equal, contiguous index ranges
/// `[start, endExclusive)`.
let private computeShardRanges (m: int) (numWorkers: int) : (int * int)[] =
    if m = 0 then
        [||]
    else
        let shardSize = max 1 ((m + numWorkers - 1) / numWorkers)
        [| let mutable s = 0
           while s < m do
               yield (s, min m (s + shardSize))
               s <- s + shardSize |]

/// Flattens this shard's `[start,end)` event byte ranges into `[s0,e0,s1,e1,...]`
/// for handing to a worker (a single small array, not per-event messages).
let private boundariesFor (pre: Prepass) (startIdx: int, endIdxExclusive: int) : int[] =
    let n = endIdxExclusive - startIdx
    let arr = Array.zeroCreate (n * 2)
    for k in 0 .. n - 1 do
        arr.[k * 2] <- pre.EventStarts.[startIdx + k]
        arr.[k * 2 + 1] <- pre.EventEnds.[startIdx + k]
    arr

/// Runs one shard per worker thread and calls `onDone` once with either all shard
/// results (in original shard/file order) or the first error encountered (at which
/// point any still-running workers are terminated).
let private runWorkers
    (scriptPath: string)
    (sharedBuf: byte[])
    (constantsText: string)
    (shardBoundaries: int[][])
    (onDone: Result<LogParser.ShardResult[], string> -> unit)
    : unit =
    let n = shardBoundaries.Length
    let results: LogParser.ShardResult option[] = Array.create n None
    let mutable remaining = n
    let mutable settled = false
    let workers = ResizeArray<obj>()

    let succeedIfDone () =
        if not settled && remaining = 0 then
            settled <- true
            onDone (Ok(results |> Array.map Option.get))

    let failOnce (msg: string) =
        if not settled then
            settled <- true
            for w in workers do
                try
                    Node.terminateWorker w
                with _ ->
                    ()
            onDone (Error msg)

    for i in 0 .. n - 1 do
        let data =
            createObj [
                "sharedBuffer" ==> sharedBuf
                "boundaries" ==> shardBoundaries.[i]
                "constantsText" ==> constantsText
            ]

        let worker = Node.createWorker scriptPath data
        workers.Add worker

        Node.on worker "message" (fun msg ->
            let errRaw = Json.get msg "error"
            if Json.isDefined errRaw then
                failOnce (Json.jsString errRaw)
            else
                results.[i] <- Some(LogParser.shardResultFromTransfer msg)
                remaining <- remaining - 1
                succeedIfDone ())

        Node.on worker "error" (fun err -> failOnce ("Worker failed: " + Node.errorMessage err))

/// Decides whether `path` is a good candidate for the parallel path: a plain
/// (non-gzip) local file at least `MinBytesForParallel`, on a machine with more
/// than one CPU. Gzip streams can't be range-sharded, so those always fall back.
let private shouldUseParallel (path: string) : bool =
    try
        let gz =
            try
                Node.isGzipFile path
            with _ ->
                path.EndsWith ".gz"

        (not gz) && Node.fileSize path >= float MinBytesForParallel && Node.cpuCount () > 1
    with _ ->
        false

/// Same contract as StreamLoader.load, plus `workerScriptPath` (the compiled
/// dist/parseWorker.js, resolved by the caller from its own extension-install
/// location -- e.g. NetlogEditor.fs already resolves dist/webview.js the same way
/// via context.extensionUri, so this mirrors that instead of relying on `__dirname`,
/// which only means "the bundled file's own directory" in CJS output and would
/// break direct ESM imports of this module for testing). Streams `path`, calling
/// `onLog` on success or `onError` with a message on failure. Uses the parallel
/// worker-thread path when `shouldUseParallel` says it is worthwhile; otherwise
/// delegates to `StreamLoader.load` unchanged. `maxEvents <= 0` means "no cap".
let load
    (workerScriptPath: string)
    (path: string)
    (maxEvents: int)
    (onLog: LogParser.LoadedLog -> unit)
    (onError: string -> unit)
    : unit =
    if not (shouldUseParallel path) then
        StreamLoader.load path maxEvents onLog onError
    else
        try
            let size = int (Node.fileSize path)
            let sharedBuf = Node.readFileIntoSharedBuffer path size
            let pre = runPrepass sharedBuf

            match pre.ConstantsText with
            | None -> onError "Unable to parse log dump as JSON file."
            | Some constantsText ->
                match LogParser.checkConstants (Json.parse constantsText) with
                | Error e -> onError e
                | Ok constants ->
                    let leadingWarn =
                        if not pre.IsComplete then "Log file truncated.  Events may be missing.\n" else ""

                    let finish (shardResults: LogParser.ShardResult[]) : unit =
                        let log =
                            LogParser.finalizeFromShards
                                constants
                                shardResults
                                maxEvents
                                pre.PolledData
                                pre.TabData
                                pre.UserComments
                                leadingWarn
                        onLog log

                    let m = pre.EventStarts.Length
                    if m = 0 then
                        finish [||]
                    else
                        let numWorkers =
                            [ Node.cpuCount (); MaxWorkers; max 1 (m / MinEventsPerWorker) ] |> List.min |> max 1

                        if numWorkers <= 1 then
                            // Not enough events to justify workers even though the file itself
                            // is large (e.g. very few, very large events) -- validate directly.
                            let rawJsonEvents =
                                Array.init m (fun i ->
                                    ByteJsonScanner.decodeByteRange sharedBuf pre.EventStarts.[i] pre.EventEnds.[i])
                            finish [| LogParser.validateEventBatchFromJson constants rawJsonEvents |]
                        else
                            let shardBoundaries =
                                computeShardRanges m numWorkers |> Array.map (boundariesFor pre)

                            runWorkers workerScriptPath sharedBuf constantsText shardBoundaries (fun result ->
                                match result with
                                | Error e -> onError ("Failed to load: " + e)
                                | Ok shardResults -> finish shardResults)
        with ex ->
            onError ("Failed to load: " + string ex)
