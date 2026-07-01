/// Minimal, hand-written DOM/Webview bindings. Per the no-external-runtime-libraries
/// policy we avoid Fable.Browser.* packages and bind only what we use.
module Netlog.Webview.Dom

open Fable.Core

type Style =
    abstract display: string with get, set
    abstract width: string with get, set
    abstract height: string with get, set
    abstract left: string with get, set
    abstract top: string with get, set

type ClassList =
    abstract add: string -> unit
    abstract remove: string -> unit
    abstract toggle: string -> unit
    abstract contains: string -> bool

type Element =
    abstract textContent: string with get, set
    abstract innerText: string with get, set
    abstract innerHTML: string with get, set
    abstract id: string with get, set
    abstract className: string with get, set
    abstract href: string with get, set
    abstract value: string with get, set
    abstract colSpan: int with get, set
    abstract clientWidth: int
    abstract clientHeight: int
    abstract style: Style
    abstract classList: ClassList
    abstract parentNode: Element
    abstract appendChild: Element -> Element
    abstract setAttribute: string * string -> unit
    abstract getAttribute: string -> string
    abstract scrollIntoView: unit -> unit
    abstract addEventListener: string * (obj -> unit) -> unit
    abstract tabIndex: int with get, set
    abstract focus: unit -> unit

type Document =
    abstract getElementById: string -> Element
    abstract createElement: string -> Element
    abstract createTextNode: string -> Element
    abstract title: string with get, set
    abstract body: Element

type Window =
    abstract addEventListener: string * (obj -> unit) -> unit

type TextMetrics =
    abstract width: float

type Context2d =
    abstract fillStyle: string with get, set
    abstract strokeStyle: string with get, set
    abstract font: string with get, set
    abstract textAlign: string with get, set
    abstract textBaseline: string with get, set
    abstract fillRect: float * float * float * float -> unit
    abstract strokeRect: float * float * float * float -> unit
    abstract fillText: string * float * float -> unit
    abstract measureText: string -> TextMetrics
    abstract beginPath: unit -> unit
    abstract moveTo: float * float -> unit
    abstract lineTo: float * float -> unit
    abstract stroke: unit -> unit
    abstract save: unit -> unit
    abstract restore: unit -> unit
    abstract translate: float * float -> unit

type Canvas =
    abstract width: int with get, set
    abstract height: int with get, set
    abstract getContext: string -> Context2d

[<Global>]
let document: Document = jsNative

[<Global>]
let window: Window = jsNative

/// The messaging bridge VS Code injects into webviews. Must be acquired exactly once.
type VsCodeApi =
    abstract postMessage: obj -> unit
    abstract setState: obj -> unit
    abstract getState: unit -> obj

[<Emit("acquireVsCodeApi()")>]
let acquireVsCodeApi () : VsCodeApi = jsNative

// --- DOM helpers (port of util.js / ui_webui_resources_js_util.js) ---

[<Emit("$0.preventDefault()")>]
let preventDefault (e: obj) : unit = jsNative

/// Used for the brief "Copied!" -> "Copy" button-text revert (ImportView).
[<Emit("setTimeout($0, $1)")>]
let setTimeout (f: unit -> unit) (ms: int) : unit = jsNative

/// A MouseEvent's x position relative to its target element's padding box (used by
/// Timeline's hover status line instead of clientX, which is viewport-relative).
[<Emit("$0.offsetX")>]
let eventOffsetX (e: obj) : float = jsNative

let el (id: string) : Element = document.getElementById id

let createElement (tag: string) : Element = document.createElement tag

let addNode (parent: Element) (tag: string) : Element =
    let e = document.createElement tag
    parent.appendChild e |> ignore
    e

let addText (parent: Element) (text: string) : unit =
    document.createTextNode text |> parent.appendChild |> ignore

let addNodeWithText (parent: Element) (tag: string) (text: string) : Element =
    let e = addNode parent tag
    addText e text
    e

let setNodeDisplay (node: Element) (visible: bool) : unit =
    node.style.display <- (if visible then "" else "none")

let clear (node: Element) : unit = node.innerHTML <- ""

[<Emit("$0.checked")>]
let getChecked (node: Element) : bool = jsNative

[<Emit("$0.checked = $1")>]
let setChecked (node: Element) (value: bool) : unit = jsNative

[<Emit("$0")>]
let asCanvas (node: Element) : Canvas = jsNative

[<Emit("$0.target")>]
let eventTarget (e: obj) : Element = jsNative

/// The key name from a KeyboardEvent (e.g. "ArrowDown", "Enter", " "), used for the
/// keyboard-navigation handlers added across the shell/tabs/tables.
[<Emit("$0.key")>]
let eventKey (e: obj) : string = jsNative
