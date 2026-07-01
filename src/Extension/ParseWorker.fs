/// Worker-thread entry point for parallel NetLog parsing (Phase 2 of the big-file
/// load performance work). This module's top-level code only runs inside a
/// worker_threads Worker spawned by ParallelStreamLoader -- never in the main
/// Extension Host thread -- and does exactly one thing: validate its assigned shard
/// of events (byte ranges into the shared file buffer) and post the results back.
///
/// Deliberately does NOT do source-grouping: a source's events can be split across
/// multiple shards, so grouping only produces correct descriptions/active-state once
/// every shard's events are merged back into original file order on the main thread
/// (see ParallelStreamLoader.fs / Core.LogParser.finalizeFromShards).
module Netlog.Extension.ParseWorker

open Fable.Core.JsInterop
open Netlog.Core

if not (Node.isMainThread ()) then
    try
        let data = Node.workerData ()
        let sharedBuf: byte[] = data?sharedBuffer
        let boundaries: int[] = data?boundaries
        let constantsText: string = data?constantsText

        match LogParser.checkConstants (Json.parse constantsText) with
        | Error e -> Node.postToParent (createObj [ "error" ==> e ])
        | Ok constants ->
            let n = boundaries.Length / 2
            let rawJsonEvents =
                Array.init n (fun i ->
                    let s = boundaries.[i * 2]
                    let e = boundaries.[i * 2 + 1]
                    ByteJsonScanner.decodeByteRange sharedBuf s e)
            let result = LogParser.validateEventBatchFromJson constants rawJsonEvents
            Node.postToParent (LogParser.shardResultToTransfer result)
    with ex ->
        Node.postToParent (createObj [ "error" ==> string ex ])
