/// Byte-oriented counterpart to JsonStreamScanner. Scans raw UTF-8 bytes directly --
/// never decoding the whole file to a UTF-16 JS string first -- and emits byte
/// RANGES via callbacks rather than decoded strings; callers decode only the exact
/// bytes of each emitted value (see `decodeByteRange`). JSON structural bytes
/// (`{ } [ ] " , : \` and ASCII whitespace) are always single-byte and never collide
/// with UTF-8 continuation/lead bytes (always >= 0x80), so scanning at the byte
/// level is safe even with non-ASCII content (URLs, hostnames, ...) inside strings.
///
/// Same incremental Push/IsComplete/Finish shape and grammar handling as
/// JsonStreamScanner (tolerates a truncated tail), just byte-typed. Kept as a
/// separate module so the original string-based scanner (and its tests) stay intact;
/// this is the one used by the local-file streaming path (StreamLoader.fs), where
/// avoiding a whole-file UTF-16 decode pass matters most.
///
/// Byte access/concat/slice go through small `[<Emit>]` primitives below rather than
/// the `byte[]`/`Array` module (whose generic `item`/`append` helpers add a bounds
/// check + an interpreted per-element copy loop respectively) -- on a hot loop over
/// hundreds of millions of bytes that overhead would erase the point of this module.
///
/// Pure (no Node/DOM): only universal JS globals (`Uint8Array`, `TextDecoder`) are
/// used, so this is unit-tested in Core like the rest of the parser.
module Netlog.Core.ByteJsonScanner

open Fable.Core
open Fable.Core.JsInterop

type private State =
    | BeforeRoot
    | InRoot
    | ExpectColon
    | ExpectValue
    | InEvents
    | Complete

[<Literal>]
let private Quote = 34uy // '"'
[<Literal>]
let private Backslash = 92uy // '\'
[<Literal>]
let private OpenBrace = 123uy // '{'
[<Literal>]
let private CloseBrace = 125uy // '}'
[<Literal>]
let private OpenBracket = 91uy // '['
[<Literal>]
let private CloseBracket = 93uy // ']'
[<Literal>]
let private Comma = 44uy // ','
[<Literal>]
let private Colon = 58uy // ':'
[<Literal>]
let private Space = 32uy
[<Literal>]
let private Tab = 9uy
[<Literal>]
let private Newline = 10uy
[<Literal>]
let private CarriageReturn = 13uy

// --- Raw byte-array primitives (bypass F#'s bounds-checked Array.item / the
// element-by-element Array.append loop; these compile to direct JS at the call
// site, no wrapper function). ---

[<Emit("$0[$1]")>]
let private getByte (buf: byte[]) (i: int) : byte = jsNative

/// New Uint8Array holding `a` followed by `b` (native `.set()` bulk copies, not a
/// per-element loop).
[<Emit("(() => { const r = new Uint8Array($0.length + $1.length); r.set($0, 0); r.set($1, $0.length); return r; })()")>]
let private concatBytes (a: byte[]) (b: byte[]) : byte[] = jsNative

/// A zero-copy view of `buf` from `start` to the end (backed by the same memory).
[<Emit("$0.subarray($1)")>]
let private subarrayFrom (buf: byte[]) (start: int) : byte[] = jsNative

let private isWs (b: byte) : bool = b = Space || b = Newline || b = CarriageReturn || b = Tab

// --- Decoding helper -------------------------------------------------------------
// A single reused TextDecoder instance -- constructing one per call adds up over
// hundreds of thousands of events.

[<Emit("new TextDecoder('utf-8')")>]
let private newDecoder () : obj = jsNative

[<Emit("$0.decode($1.subarray($2, $3))")>]
let private decodeWith (decoder: obj) (buf: byte[]) (start: int) (endExclusive: int) : string = jsNative

let private sharedDecoder = newDecoder ()

/// Decodes buf[start..endExclusive) as UTF-8. Works whether `buf` is a plain
/// Uint8Array or a Node Buffer (both support `.subarray`).
let decodeByteRange (buf: byte[]) (start: int) (endExclusive: int) : string =
    decodeWith sharedDecoder buf start endExclusive

/// Given the opening-quote index `i`, returns the index just past the closing quote,
/// or None if the string is not fully present yet.
let private scanStringEnd (buf: byte[]) (n: int) (i: int) : int option =
    let mutable j = i + 1
    let mutable result = None
    let mutable stop = false
    while not stop && j < n do
        let b = getByte buf j
        if b = Backslash then
            if j + 1 < n then j <- j + 2 else stop <- true
        elif b = Quote then
            result <- Some(j + 1)
            stop <- true
        else
            j <- j + 1
    result

/// Given the first-byte index `i` of a JSON value, returns the index just past the
/// value, or None if it is not fully present yet. Objects/arrays are matched by
/// string-aware depth; primitives end at the first structural delimiter/whitespace.
let private scanValueEnd (buf: byte[]) (n: int) (i: int) : int option =
    let b = getByte buf i
    if b = Quote then
        scanStringEnd buf n i
    elif b = OpenBrace || b = OpenBracket then
        let mutable depth = 0
        let mutable j = i
        let mutable inStr = false
        let mutable esc = false
        let mutable result = None
        let mutable stop = false
        while not stop && j < n do
            let ch = getByte buf j
            if inStr then
                if esc then esc <- false
                elif ch = Backslash then esc <- true
                elif ch = Quote then inStr <- false
            elif ch = Quote then
                inStr <- true
            elif ch = OpenBrace || ch = OpenBracket then
                depth <- depth + 1
            elif ch = CloseBrace || ch = CloseBracket then
                depth <- depth - 1
                if depth = 0 then
                    result <- Some(j + 1)
                    stop <- true
            j <- j + 1
        result
    else
        let mutable j = i
        let mutable result = None
        let mutable stop = false
        while not stop && j < n do
            let ch = getByte buf j
            if ch = Comma || ch = CloseBrace || ch = CloseBracket || isWs ch then
                result <- Some j
                stop <- true
            else
                j <- j + 1
        result

/// `onConstants buf start end`     — the top-level "constants" object's byte range.
/// `onEvent buf start end`         — one element of the top-level "events" array's byte range.
/// `onTail buf key valStart valEnd` — any other top-level value's byte range (`key`
/// is already decoded -- see below, unlike values it is small and rare enough that
/// decoding it immediately costs nothing).
/// Value ranges are `[start, end)` into `buf`; valid only for the duration of the
/// call (decode/copy anything you need before returning -- `buf` may be replaced on
/// the next `Push`).
type Scanner
    (
        onConstants: byte[] -> int -> int -> unit,
        onEvent: byte[] -> int -> int -> unit,
        onTail: byte[] -> string -> int -> int -> unit
    ) =
    let mutable buf: byte[] = [||]
    let mutable state = BeforeRoot
    // Unlike value ranges, the pending KEY is decoded to a string as soon as it is
    // found (in InRoot below), not kept as byte offsets: the key can be found in one
    // Process() call and only used (in ExpectValue) after a later Push, by which
    // point `buf` may have been trimmed and no longer contain those bytes at all.
    // Keys are short and rare (a handful per file, not per-event), so decoding them
    // immediately is free.
    let mutable pendingKey = ""

    member _.IsComplete: bool = (state = Complete)

    member private _.Process() : unit =
        let n = buf.Length
        let mutable pos = 0
        let mutable needMore = false

        while not needMore && state <> Complete do
            while pos < n && isWs (getByte buf pos) do
                pos <- pos + 1

            if pos >= n then
                needMore <- true
            else
                match state with
                | BeforeRoot ->
                    if getByte buf pos = OpenBrace then
                        pos <- pos + 1
                        state <- InRoot
                    else
                        pos <- pos + 1 // tolerate leading junk before the root object
                | InRoot ->
                    let c = getByte buf pos
                    if c = CloseBrace then
                        pos <- pos + 1
                        state <- Complete
                    elif c = Comma then
                        pos <- pos + 1
                    elif c = Quote then
                        match scanStringEnd buf n pos with
                        | Some endIdx ->
                            pendingKey <- decodeByteRange buf (pos + 1) (endIdx - 1)
                            pos <- endIdx
                            state <- ExpectColon
                        | None -> needMore <- true
                    else
                        pos <- pos + 1 // tolerate stray characters
                | ExpectColon ->
                    if getByte buf pos = Colon then
                        pos <- pos + 1
                        state <- ExpectValue
                    else
                        pos <- pos + 1 // tolerate
                | ExpectValue ->
                    if pendingKey = "events" && getByte buf pos = OpenBracket then
                        pos <- pos + 1
                        state <- InEvents
                    else
                        match scanValueEnd buf n pos with
                        | Some endIdx ->
                            if pendingKey = "constants" then
                                onConstants buf pos endIdx
                            else
                                onTail buf pendingKey pos endIdx
                            pos <- endIdx
                            state <- InRoot
                        | None -> needMore <- true
                | InEvents ->
                    let c = getByte buf pos
                    if c = CloseBracket then
                        pos <- pos + 1
                        state <- InRoot
                    elif c = Comma then
                        pos <- pos + 1
                    else
                        match scanValueEnd buf n pos with
                        | Some endIdx ->
                            onEvent buf pos endIdx
                            pos <- endIdx
                        | None -> needMore <- true
                | Complete -> needMore <- true

        // Drop the consumed prefix so only the (bounded) pending value is retained.
        // `subarrayFrom` is a zero-copy view (no bulk copy needed just to shrink).
        // (`pendingKey` is already a plain string by this point, so it needs no
        // rebasing -- see the comment on the field above.)
        if pos > 0 then
            buf <- subarrayFrom buf pos

    /// Feeds the next chunk of raw bytes (e.g. straight from a Node Buffer/fs stream,
    /// with no decoding step first).
    member this.Push(chunk: byte[]) : unit =
        if state <> Complete then
            buf <- (if buf.Length = 0 then chunk else concatBytes buf chunk)
            this.Process()

    /// Signals end of input. Any incomplete trailing value is left unemitted; callers
    /// should treat `IsComplete = false` as a truncated log.
    member _.Finish() : unit = ()

/// Encodes a JS string as UTF-8 bytes. Used by callers that only have a string (e.g.
/// tests feeding a JSON fixture in as byte chunks); the streaming file-load path
/// (StreamLoader.fs) never needs this since it already gets raw bytes from disk.
[<Emit("new TextEncoder().encode($0)")>]
let encodeUtf8 (s: string) : byte[] = jsNative
