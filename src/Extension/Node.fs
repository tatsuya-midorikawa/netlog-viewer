/// Minimal Node built-in bindings used by the Extension Host (no external deps).
module Netlog.Extension.Node

open Fable.Core

/// Decodes UTF-8 bytes (Uint8Array or Buffer) to a string.
[<Emit("new TextDecoder('utf-8').decode($0)")>]
let decodeUtf8 (bytes: obj) : string = jsNative

/// True when the bytes start with the gzip magic number (0x1f 0x8b).
[<Emit("$0.length >= 2 && $0[0] === 0x1f && $0[1] === 0x8b")>]
let isGzip (bytes: obj) : bool = jsNative

/// Synchronous gunzip via Node's zlib (returns a Buffer).
[<Emit("require('zlib').gunzipSync($0)")>]
let gunzipSync (bytes: obj) : obj = jsNative

// --- Streaming primitives (for loading files too large to hold as one string) ---

/// Peeks the first two bytes of a file to detect the gzip magic number, without
/// reading the whole file. Throws if the file cannot be opened.
[<Emit("(() => { const fs = require('fs'); const fd = fs.openSync($0, 'r'); try { const b = Buffer.alloc(2); const n = fs.readSync(fd, b, 0, 2, 0); return n >= 2 && b[0] === 0x1f && b[1] === 0x8b; } finally { fs.closeSync(fd); } })()")>]
let isGzipFile (path: string) : bool = jsNative

/// Opens a readable stream over a file (default 64 KB chunks).
[<Emit("require('fs').createReadStream($0)")>]
let createReadStream (path: string) : obj = jsNative

/// Creates a zlib gunzip transform stream.
[<Emit("require('zlib').createGunzip()")>]
let createGunzip () : obj = jsNative

/// `src.pipe(dst)` — returns the destination stream.
[<Emit("$0.pipe($1)")>]
let pipe (src: obj) (dst: obj) : obj = jsNative

/// `emitter.on(event, handler)`.
[<Emit("$0.on($1, $2)")>]
let on (emitter: obj) (event: string) (handler: obj -> unit) : unit = jsNative

/// Destroys a stream (stops further reads).
[<Emit("$0.destroy()")>]
let destroyStream (stream: obj) : unit = jsNative

/// A streaming UTF-8 decoder that correctly handles multi-byte characters split
/// across chunk boundaries.
[<Emit("new TextDecoder('utf-8')")>]
let createDecoder () : obj = jsNative

/// Decodes one chunk, keeping any trailing partial code point for the next call.
[<Emit("$0.decode($1, { stream: true })")>]
let decodeChunk (decoder: obj) (chunk: obj) : string = jsNative

/// Flushes any bytes buffered by the streaming decoder at end of input.
[<Emit("$0.decode()")>]
let decoderFlush (decoder: obj) : string = jsNative

/// Extracts a human-readable message from a Node error/value.
[<Emit("String(($0 && $0.message) ? $0.message : $0)")>]
let errorMessage (err: obj) : string = jsNative

// --- Parallel loading primitives (worker_threads + SharedArrayBuffer) ---

/// Number of logical CPUs (used to size the worker pool).
[<Emit("require('node:os').cpus().length")>]
let cpuCount () : int = jsNative

/// Synchronously stats a file, returning its size in bytes.
[<Emit("require('node:fs').statSync($0).size")>]
let fileSize (path: string) : float = jsNative

/// Allocates a SharedArrayBuffer-backed Uint8Array of `size` bytes and synchronously
/// reads the whole file into it (so worker threads can read their assigned byte
/// ranges zero-copy). Only used above the parallel-loading size threshold, where
/// holding the whole file in memory once is an acceptable, bounded cost. Throws on
/// I/O failure.
[<Emit("(() => { const fs = require('node:fs'); const fd = fs.openSync($0, 'r'); try { const buf = new Uint8Array(new SharedArrayBuffer($1)); let off = 0; while (off < $1) { const n = fs.readSync(fd, buf, off, $1 - off, off); if (n <= 0) break; off += n; } return buf; } finally { fs.closeSync(fd); } })()")>]
let readFileIntoSharedBuffer (path: string) (size: int) : byte[] = jsNative

/// True when running as the main (Extension Host) thread, false inside a
/// worker_threads Worker.
[<Emit("require('node:worker_threads').isMainThread")>]
let isMainThread () : bool = jsNative

/// Creates and starts a new worker thread running the script at `scriptPath`,
/// passing `data` as its `workerData` (SharedArrayBuffer instances are passed by
/// reference/zero-copy; everything else is structured-cloned).
[<Emit("new (require('node:worker_threads').Worker)($0, { workerData: $1 })")>]
let createWorker (scriptPath: string) (data: obj) : obj = jsNative

/// The current worker's `workerData` (only meaningful inside a worker thread).
[<Emit("require('node:worker_threads').workerData")>]
let workerData () : obj = jsNative

/// Posts a message from a worker thread back to its parent.
[<Emit("require('node:worker_threads').parentPort.postMessage($0)")>]
let postToParent (msg: obj) : unit = jsNative

/// Forcibly stops a worker thread (used on the failure path to avoid leaking
/// still-running workers when one of them errors).
[<Emit("$0.terminate()")>]
let terminateWorker (worker: obj) : unit = jsNative

