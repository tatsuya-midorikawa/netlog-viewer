/// Webview entry point. Builds the page shell, registers tab views, and drives the
/// load lifecycle (onLoadLogFinish decides which tabs are shown), mirroring the
/// original MainView/LogUtil flow.
module Netlog.Webview.App

open Fable.Core.JsInterop
open Netlog.Core
open Netlog.Webview.Dom
open Netlog.Webview.View

let private vscode = acquireVsCodeApi ()

let mutable private page = Unchecked.defaultof<Shell.Page>
let mutable private switcher = Unchecked.defaultof<TabSwitcher.TabSwitcher>
let mutable private views: (string * ViewBase) list = []

let private setFileName (name: string) : unit =
    page.FileName.innerText <- name
    document.title <- (if name = "" then "NetLog Viewer" else name + " - NetLog Viewer")

let private onLoad (model: obj) : unit =
    setFileName (Json.tryString model "fileName" |> Option.defaultValue "")
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

let private onMessage (e: obj) : unit =
    let data = Json.get e "data"

    match Json.tryString data "type" |> Option.defaultValue "" with
    | "load" -> onLoad data
    | "error" -> setFileName ("Error: " + (Json.tryString data "message" |> Option.defaultValue ""))
    | _ -> ()

let start () : unit =
    page <- Shell.build ()
    switcher <- TabSwitcher.TabSwitcher(page.TabList, page.Content)

    let tabs: (string * string * ViewBase) list =
        [ ImportView.tabId, ImportView.tabName, (ImportView.create () :> ViewBase)
          ProxyView.tabId, ProxyView.tabName, (ProxyView.create () :> ViewBase)
          EventsView.tabId, EventsView.tabName, (EventsView.create () :> ViewBase)
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
    vscode.postMessage (createObj [ "type" ==> "ready" ])

start ()
