/// Incremental, string-aware scanner for the NetLog top-level JSON document. Fed
/// decoded text in arbitrary chunks, it emits the top-level `constants` object, each
/// element of the top-level `events` array, and any other top-level values
/// (polledData / tabData / userComments) as raw JSON substrings — without ever
/// materialising the whole file as one string. It tolerates a truncated tail (e.g.
/// a `--log-net-log` capture cut mid-event): incomplete trailing values are dropped
/// and `IsComplete` reports whether the root object closed cleanly.
///
/// Pure (no Node/DOM), so it is unit-tested in Core. Only the individual constants
/// object and each event object are ever buffered, so memory stays bounded.
module Netlog.Core.JsonStreamScanner

type private State =
    | BeforeRoot
    | InRoot
    | ExpectColon
    | ExpectValue
    | InEvents
    | Complete

let private isWs (c: char) : bool = c = ' ' || c = '\n' || c = '\r' || c = '\t'

/// Given the opening quote index `i`, returns the index just past the closing quote,
/// or None if the string is not fully present yet.
let private scanStringEnd (s: string) (i: int) : int option =
    let n = s.Length
    let mutable j = i + 1
    let mutable result = None
    let mutable stop = false
    while not stop && j < n do
        let c = s.[j]
        if c = '\\' then
            if j + 1 < n then j <- j + 2 else stop <- true
        elif c = '"' then
            result <- Some(j + 1)
            stop <- true
        else
            j <- j + 1
    result

/// Given the first-character index `i` of a JSON value, returns the index just past
/// the value, or None if it is not fully present yet. Objects/arrays are matched by
/// string-aware depth; primitives end at the first structural delimiter/whitespace
/// (so an unterminated trailing number waits for more input).
let private scanValueEnd (s: string) (i: int) : int option =
    let n = s.Length
    let c = s.[i]
    if c = '"' then
        scanStringEnd s i
    elif c = '{' || c = '[' then
        let mutable depth = 0
        let mutable j = i
        let mutable inStr = false
        let mutable esc = false
        let mutable result = None
        let mutable stop = false
        while not stop && j < n do
            let ch = s.[j]
            if inStr then
                if esc then esc <- false
                elif ch = '\\' then esc <- true
                elif ch = '"' then inStr <- false
            elif ch = '"' then
                inStr <- true
            elif ch = '{' || ch = '[' then
                depth <- depth + 1
            elif ch = '}' || ch = ']' then
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
            let ch = s.[j]
            if ch = ',' || ch = '}' || ch = ']' || isWs ch then
                result <- Some j
                stop <- true
            else
                j <- j + 1
        result

/// `onConstants raw`   — the top-level "constants" object as a JSON substring.
/// `onEvent raw`       — one element of the top-level "events" array.
/// `onTail key raw`    — any other top-level value (e.g. polledData/tabData/userComments).
type Scanner(onConstants: string -> unit, onEvent: string -> unit, onTail: string -> string -> unit) =
    let mutable buf = ""
    let mutable state = BeforeRoot
    let mutable pendingKey = ""

    member _.IsComplete : bool = (state = Complete)

    member private _.Process() : unit =
        let mutable pos = 0
        let mutable needMore = false

        while not needMore && state <> Complete do
            while pos < buf.Length && isWs buf.[pos] do
                pos <- pos + 1

            if pos >= buf.Length then
                needMore <- true
            else
                match state with
                | BeforeRoot ->
                    if buf.[pos] = '{' then
                        pos <- pos + 1
                        state <- InRoot
                    else
                        pos <- pos + 1 // tolerate leading junk before the root object
                | InRoot ->
                    let c = buf.[pos]
                    if c = '}' then
                        pos <- pos + 1
                        state <- Complete
                    elif c = ',' then
                        pos <- pos + 1
                    elif c = '"' then
                        match scanStringEnd buf pos with
                        | Some endIdx ->
                            // strip the surrounding quotes; NetLog keys are plain ASCII
                            pendingKey <- buf.Substring(pos + 1, endIdx - pos - 2)
                            pos <- endIdx
                            state <- ExpectColon
                        | None -> needMore <- true
                    else
                        pos <- pos + 1 // tolerate stray characters
                | ExpectColon ->
                    if buf.[pos] = ':' then
                        pos <- pos + 1
                        state <- ExpectValue
                    else
                        pos <- pos + 1 // tolerate
                | ExpectValue ->
                    if pendingKey = "events" && buf.[pos] = '[' then
                        pos <- pos + 1
                        state <- InEvents
                    else
                        match scanValueEnd buf pos with
                        | Some endIdx ->
                            let raw = buf.Substring(pos, endIdx - pos)
                            if pendingKey = "constants" then onConstants raw else onTail pendingKey raw
                            pos <- endIdx
                            state <- InRoot
                        | None -> needMore <- true
                | InEvents ->
                    let c = buf.[pos]
                    if c = ']' then
                        pos <- pos + 1
                        state <- InRoot
                    elif c = ',' then
                        pos <- pos + 1
                    else
                        match scanValueEnd buf pos with
                        | Some endIdx ->
                            onEvent (buf.Substring(pos, endIdx - pos))
                            pos <- endIdx
                        | None -> needMore <- true
                | Complete -> needMore <- true

        // Drop the consumed prefix so only the (bounded) pending value is retained.
        if pos > 0 then
            buf <- buf.Substring(pos)

    /// Feeds the next decoded chunk of the document.
    member this.Push(chunk: string) : unit =
        if state <> Complete then
            buf <- buf + chunk
            this.Process()

    /// Signals end of input. Any incomplete trailing value is left unemitted; callers
    /// should treat `IsComplete = false` as a truncated log.
    member _.Finish() : unit = ()
