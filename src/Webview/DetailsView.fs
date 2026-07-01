/// Port of details_view.js: renders the selected sources' full event traces (header,
/// description, start time, and the painted log table), sorted by source id.
module Netlog.Webview.DetailsView

open Netlog.Core
open Netlog.Webview.Dom
open Netlog.Webview.LogViewPainter

type DetailSource =
    { Id: int
      TypeName: string
      Description: string
      StartTicks: float
      Events: Model.Event[]
      Total: int
      Truncated: bool }

/// A source to render in the details pane: either its events have arrived (`Ready`)
/// or it was just selected and its on-demand `getSourceEvents` response is still in
/// flight (`Loading`, shown as a lightweight placeholder instead of being skipped so
/// the pane never looks like selection silently did nothing).
type DetailEntry =
    | Ready of DetailSource
    | Loading of Id: int * TypeName: string * Description: string

let private entryId (e: DetailEntry) : int =
    match e with
    | Ready s -> s.Id
    | Loading(id, _, _) -> id

/// A compact HTTP request/response card (method/URL, status, timing, headers) from
/// Core.RequestSummary.extract, so the actual L7 exchange doesn't have to be found
/// by scrolling through the raw trace's DNS/socket/TLS noise -- addresses the
/// "hard to confirm the L7-level exchange" pain point.
let private renderRequestSummary (parent: Element) (ex: RequestSummary.HttpExchange) : unit =
    let card = addNode parent "div"
    card.className <- "nv-request-summary"

    let titleLine = (if ex.Method = "" then "" else ex.Method + " ") + ex.Url
    addNodeWithText card "div" titleLine |> ignore

    match ex.StatusLine with
    | Some status -> (addNodeWithText card "div" status).className <- "nv-request-summary-status"
    | None -> (addNodeWithText card "div" "(no response headers seen for this source)").className <- "nv-detail-truncated"

    match ex.RequestSentTicks, ex.ResponseHeadersTicks with
    | Some sent, Some received ->
        addNodeWithText card "div" (sprintf "Time to response headers: %.0f ms" (received - sent)) |> ignore
    | _ -> ()

    let renderHeaderList (title: string) (headers: string[]) =
        if headers.Length > 0 then
            let details = addNode card "details"
            addNodeWithText details "summary" title |> ignore
            let ul = addNode details "ul"
            ul.className <- "nv-request-summary-headers"
            for h in headers do
                addNodeWithText ul "li" h |> ignore

    renderHeaderList (sprintf "Request headers (%d)" ex.RequestHeaders.Length) ex.RequestHeaders
    renderHeaderList (sprintf "Response headers (%d)" ex.ResponseHeaders.Length) ex.ResponseHeaders

type DetailsView(root: Element) =
    let sourceElements = System.Collections.Generic.Dictionary<int, Element>()

    member _.Root = root

    member _.Clear() = clear root

    member _.SetData
        (c: Constants.Constants, baseTime: float, logCreationTime: float option, entries: DetailEntry list)
        =
        clear root
        sourceElements.Clear()
        let sorted = entries |> List.sortBy entryId
        let mutable first = true

        for entry in sorted do
            if not first then
                addNode root "hr" |> ignore
            first <- false

            let div = addNode root "div"
            div.className <- "nv-log-source-entry"
            sourceElements.[entryId entry] <- div

            match entry with
            | Loading(id, typeName, description) ->
                let p = addNode div "p"
                addNodeWithText p "h4" (string id + ": " + typeName) |> ignore
                if description <> "" then
                    addNodeWithText p "h4" description |> ignore
                let loading = addNode div "div"
                loading.className <- "nv-detail-loading"
                (addNode loading "span").className <- "nv-spinner"
                addText loading " Loading events…"
            | Ready s ->
                let p = addNode div "p"
                addNodeWithText p "h4" (string s.Id + ": " + s.TypeName) |> ignore
                if s.Description <> "" then
                    addNodeWithText p "h4" s.Description |> ignore

                let startDate = TimeUtil.dateToStringMs (TimeUtil.convertTimeTicksToTime s.StartTicks)
                addNodeWithText p "div" ("Start Time: " + startDate) |> ignore

                match RequestSummary.extract s.Events with
                | Some ex -> renderRequestSummary p ex
                | None -> ()

                if s.Truncated then
                    let note =
                        addNodeWithText
                            p
                            "div"
                            (sprintf
                                "Showing the first %d of %d events for this source (raise netlogViewer.maxSourceDetailEvents to see more)."
                                s.Events.Length
                                s.Total)
                    note.className <- "nv-detail-truncated"

                let tp = createLogEntryTablePrinter c s.Events s.StartTicks baseTime logCreationTime false
                tp.ToText(div, 0) |> ignore

    /// Scrolls the details pane to the section for the given source id, if present.
    member _.ScrollToSourceId(id: int) =
        match sourceElements.TryGetValue id with
        | true, el -> el.scrollIntoView()
        | _ -> ()
