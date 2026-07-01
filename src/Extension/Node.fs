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

