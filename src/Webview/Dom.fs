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

type Rect =
    abstract top: float
    abstract bottom: float
    abstract left: float
    abstract right: float
    abstract width: float
    abstract height: float

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
    abstract scrollTop: float with get, set
    abstract style: Style
    abstract classList: ClassList
    abstract parentNode: Element
    abstract appendChild: Element -> Element
    abstract setAttribute: string * string -> unit
    abstract getAttribute: string -> string
    abstract scrollIntoView: unit -> unit
    abstract getBoundingClientRect: unit -> Rect
    abstract contains: Element -> bool
    abstract addEventListener: string * (obj -> unit) -> unit
    abstract tabIndex: int with get, set
    abstract focus: unit -> unit

type Range =
    abstract setStart: Element * int -> unit
    abstract setEnd: Element * int -> unit
    abstract getBoundingClientRect: unit -> Rect

type Highlight =
    abstract add: Range -> unit
    abstract clear: unit -> unit

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

[<Emit("$0.ctrlKey")>]
let eventCtrl (e: obj) : bool = jsNative

[<Emit("$0.metaKey")>]
let eventMeta (e: obj) : bool = jsNative

[<Emit("$0.shiftKey")>]
let eventShift (e: obj) : bool = jsNative

[<Emit("document.createRange()")>]
let newRange () : Range = jsNative

[<Emit("new Highlight()")>]
let newHighlight () : Highlight = jsNative

[<Emit("typeof CSS !== 'undefined' && !!CSS.highlights && typeof Highlight !== 'undefined'")>]
let supportsHighlightApi () : bool = jsNative

[<Emit("CSS.highlights.set($0, $1)")>]
let cssHighlightsSet (name: string) (highlight: Highlight) : unit = jsNative

[<Emit("CSS.highlights.delete($0)")>]
let cssHighlightsDelete (name: string) : unit = jsNative

/// Used for the brief "Copied!" -> "Copy" button-text revert (ImportView) and for
/// debouncing the Events tab's `has:` param-search request. Returns the timer
/// handle so a pending call can be cancelled via clearTimeout.
[<Emit("setTimeout($0, $1)")>]
let setTimeout (f: unit -> unit) (ms: int) : obj = jsNative

[<Emit("clearTimeout($0)")>]
let clearTimeout (handle: obj) : unit = jsNative

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

[<Emit("Array.from($0.querySelectorAll($1))")>]
let querySelectorAllArray (node: Element) (selector: string) : Element[] = jsNative

[<Emit("(() => { const walker = document.createTreeWalker($0, NodeFilter.SHOW_TEXT); const nodes = []; let node; while ((node = walker.nextNode())) nodes.push(node); return nodes; })()")>]
let collectTextNodes (node: Element) : Element[] = jsNative

[<Emit("$0.nodeValue || ''")>]
let nodeValue (node: Element) : string = jsNative

[<Emit("$0.select()")>]
let selectInput (node: Element) : unit = jsNative

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
