/// Minimal dynamic-JSON access layer over Fable's JS interop. Isolates the
/// `obj`-typed JS access (JSON.parse output, raw params/polledData) from the rest
/// of Core so the domain logic stays readable.
module Netlog.Core.Json

open Fable.Core
open Fable.Core.JsInterop

[<Emit("JSON.parse($0)")>]
let parse (s: string) : obj = jsNative

[<Emit("JSON.stringify($0, null, 2)")>]
let stringifyPretty (o: obj) : string = jsNative

[<Emit("typeof $0 === 'object' && $0 !== null")>]
let isObject (o: obj) : bool = jsNative

[<Emit("typeof $0 === 'string'")>]
let isString (o: obj) : bool = jsNative

[<Emit("typeof $0 === 'number'")>]
let isNumber (o: obj) : bool = jsNative

[<Emit("Array.isArray($0)")>]
let isArray (o: obj) : bool = jsNative

[<Emit("$0 !== undefined")>]
let isDefined (o: obj) : bool = jsNative

/// JS truthiness test (mirrors `if (value)` used throughout the original).
[<Emit("!!$0")>]
let isTruthy (o: obj) : bool = jsNative

/// Raw property access: returns `undefined` when absent (callers must guard).
[<Emit("$0 == null ? undefined : $0[$1]")>]
let get (o: obj) (key: string) : obj = jsNative

[<Emit("Object.keys($0)")>]
let keys (o: obj) : string[] = jsNative

[<Emit("$0.length")>]
let length (o: obj) : int = jsNative

[<Emit("$0[$1]")>]
let item (o: obj) (i: int) : obj = jsNative

[<Emit("({})")>]
let emptyObject () : obj = jsNative

/// JS String($0) coercion (mirrors `'' + value`, e.g. undefined -> "undefined").
[<Emit("String($0)")>]
let jsString (o: obj) : string = jsNative

/// JS numeric coercion (`value - 0`), used for tick/string-number values.
[<Emit("$0 - 0")>]
let toNumber (o: obj) : float = jsNative

/// Mirrors main.js isNetLogNumber: a real number, or a string parseable as int.
[<Emit("(typeof $0 === 'number') || (typeof $0 === 'string' && !isNaN(parseInt($0)))")>]
let isNetLogNumber (o: obj) : bool = jsNative

let tryString (o: obj) (key: string) : string option =
    let v = get o key
    if isString v then Some(!!v) else None

let tryNumber (o: obj) (key: string) : float option =
    let v = get o key
    if isNumber v then Some(!!v) else None

/// Reads a `{ name: number }` object into a name -> id map (non-number values skipped).
let toIntMap (o: obj) : Map<string, int> =
    if isObject o then
        keys o
        |> Array.choose (fun k ->
            let v = get o k
            if isNumber v then Some(k, int (toNumber v)) else None)
        |> Map.ofArray
    else
        Map.empty

let invertMap (m: Map<string, int>) : Map<int, string> =
    m |> Map.toSeq |> Seq.map (fun (k, v) -> v, k) |> Map.ofSeq
