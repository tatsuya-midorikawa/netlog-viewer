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
