/// Webview entry point. Builds the page shell, registers tab views, and drives the
/// load lifecycle (onLoadLogFinish decides which tabs are shown), mirroring the
/// original MainView/LogUtil flow.
module Netlog.Webview.App

open Fable.Core
open Fable.Core.JsInterop
open Netlog.Core
open Netlog.Webview.Dom
open Netlog.Webview.View

let private vscode = acquireVsCodeApi ()

let private postToHost (m: obj) : unit = vscode.postMessage m

let mutable private page = Unchecked.defaultof<Shell.Page>
let mutable private switcher = Unchecked.defaultof<TabSwitcher.TabSwitcher>
let mutable private views: (string * ViewBase) list = []

// --- Chunked-load reassembly: loadStart -> sourcesChunk* -> eventsChunk* -> loadEnd ---
[<Emit("[]")>]
let private newJsArray () : obj = jsNative

[<Emit("$0.push($1)")>]
let private jsPush (arr: obj) (item: obj) : unit = jsNative

[<Emit("$0.flat()")>]
let private jsFlat (arrayOfArrays: obj) : obj = jsNative

[<Emit("$0[$1] = $2")>]
let private jsSet (o: obj) (key: string) (v: obj) : unit = jsNative

let mutable private pendingModel: obj = null
let mutable private eventChunks: obj = null
let mutable private sourceChunks: obj = null
let mutable private eventsSeen = 0

let private setFileName (name: string) : unit =
    page.FileName.innerText <- name
    document.title <- (if name = "" then "NetLog Viewer" else name + " - NetLog Viewer")

/// Shows a status/warning/error banner under the top bar. Empty text hides it.
let private setBanner (severity: string) (text: string) : unit =
    let b = page.Banner
    if text = "" then
        b.style.display <- "none"
        b.textContent <- ""
    else
        b.className <- "nv-banner nv-banner-" + severity
        b.textContent <- text
        b.style.display <- ""

/// The non-fatal warnings carried in `loadLog` (everything before the trailing
/// "Log loaded."), e.g. truncation / event-cap / invalid-event notices.
let private loadWarnings (model: obj) : string =
    let loadLog = Json.tryString model "loadLog" |> Option.defaultValue ""
    let suffix = "Log loaded."
    let body = if loadLog.EndsWith suffix then loadLog.Substring(0, loadLog.Length - suffix.Length) else loadLog
    body.Trim()

let private onLoad (model: obj) : unit =
    setFileName (Json.tryString model "fileName" |> Option.defaultValue "")
    setBanner "warning" (loadWarnings model)
    TimeUtil.setTimeTickOffset (Json.tryNumber (Json.get model "constants") "timeTickOffset" |> Option.defaultValue 0.0)
    TimeUtil.setBaseTime (Json.tryNumber model "baseTime" |> Option.defaultValue 0.0)

    for (_, v) in views do
        v.OnLoadLogStart model

    let mutable firstShown: string option = None

    for (id, v) in views do
        let show =
            try
                v.OnLoadLogFinish model
            with _ ->
                false
        switcher.ShowTabLink(id, show)
        if show && firstShown.IsNone then
            firstShown <- Some id

    match firstShown with
    | Some id -> switcher.SwitchToTab id
    | None -> ()

let private onLoadStart (data: obj) : unit =
    pendingModel <- data
    eventChunks <- newJsArray ()
    sourceChunks <- newJsArray ()
    eventsSeen <- 0
    setFileName (Json.tryString data "fileName" |> Option.defaultValue "")
    setBanner "info" "Loading log…"

let private onSourcesChunk (data: obj) : unit =
    if not (isNull pendingModel) then
        jsPush sourceChunks (Json.get data "sources")

let private onEventsChunk (data: obj) : unit =
    if not (isNull pendingModel) then
        let arr = Json.get data "events"
        jsPush eventChunks arr
        eventsSeen <- eventsSeen + Json.length arr
        setBanner "info" (sprintf "Loading log… %d events" eventsSeen)

/// Reassembles the streamed chunks into the same model the self-contained `load`
/// message carries, then runs the normal load lifecycle.
let private onLoadEnd () : unit =
    if not (isNull pendingModel) then
        let model = pendingModel
        jsSet model "events" (jsFlat eventChunks)
        jsSet model "sources" (jsFlat sourceChunks)
        pendingModel <- null
        eventChunks <- null
        sourceChunks <- null
        onLoad model

let private onMessage (e: obj) : unit =
    let data = Json.get e "data"

    match Json.tryString data "type" |> Option.defaultValue "" with
    | "loadStart" -> onLoadStart data
    | "sourcesChunk" -> onSourcesChunk data
    | "eventsChunk" -> onEventsChunk data
    | "loadEnd" -> onLoadEnd ()
    | "sourceEvents" ->
        let id = Json.tryNumber data "id" |> Option.defaultValue -1.0 |> int
        let events = Json.get data "events"
        for (_, v) in views do
            v.OnSourceEvents(id, events)
    | "load" -> onLoad data
    | "error" ->
        setFileName "Failed to load"
        setBanner "error" (Json.tryString data "message" |> Option.defaultValue "Failed to load the log.")
    | _ -> ()

let start () : unit =
    page <- Shell.build ()
    switcher <- TabSwitcher.TabSwitcher(page.TabList, page.Content)

    let tabs: (string * string * ViewBase) list =
        [ ImportView.tabId, ImportView.tabName, (ImportView.create () :> ViewBase)
          ProxyView.tabId, ProxyView.tabName, (ProxyView.create () :> ViewBase)
          EventsView.tabId, EventsView.tabName, (EventsView.create postToHost :> ViewBase)
          Timeline.tabId, Timeline.tabName, (Timeline.create () :> ViewBase)
          DnsView.tabId, DnsView.tabName, (DnsView.create () :> ViewBase)
          SocketsView.tabId, SocketsView.tabName, (SocketsView.create () :> ViewBase)
          StreamPoolView.tabId, StreamPoolView.tabName, (StreamPoolView.create () :> ViewBase)
          AltSvcView.tabId, AltSvcView.tabName, (AltSvcView.create () :> ViewBase)
          SpdyView.tabId, SpdyView.tabName, (SpdyView.create () :> ViewBase)
          QuicView.tabId, QuicView.tabName, (QuicView.create () :> ViewBase)
          ReportingView.tabId, ReportingView.tabName, (ReportingView.create () :> ViewBase)
          HttpCacheView.tabId, HttpCacheView.tabName, (HttpCacheView.create () :> ViewBase)
          ModulesView.tabId, ModulesView.tabName, (ModulesView.create () :> ViewBase)
          PrerenderView.tabId, PrerenderView.tabName, (PrerenderView.create () :> ViewBase) ]

    for (id, name, v) in tabs do
        switcher.AddTab(id, name, v)

    views <- tabs |> List.map (fun (id, _, v) -> id, v)

    window.addEventListener ("message", onMessage)
    setFileName ""
    setBanner "info" "Loading log…"
    vscode.postMessage (createObj [ "type" ==> "ready" ])

start ()
