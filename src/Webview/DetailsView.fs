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

type DetailsView(root: Element) =
    let sourceElements = System.Collections.Generic.Dictionary<int, Element>()

    member _.Root = root

    member _.Clear() = clear root

    member _.SetData
        (c: Constants.Constants, baseTime: float, logCreationTime: float option, sources: DetailSource list)
        =
        clear root
        sourceElements.Clear()
        let sorted = sources |> List.sortBy (fun s -> s.Id)
        let mutable first = true

        for s in sorted do
            if not first then
                addNode root "hr" |> ignore
            first <- false

            let div = addNode root "div"
            div.className <- "nv-log-source-entry"
            sourceElements.[s.Id] <- div

            let p = addNode div "p"
            addNodeWithText p "h4" (string s.Id + ": " + s.TypeName) |> ignore
            if s.Description <> "" then
                addNodeWithText p "h4" s.Description |> ignore

            let startDate = TimeUtil.dateToStringMs (TimeUtil.convertTimeTicksToTime s.StartTicks)
            addNodeWithText p "div" ("Start Time: " + startDate) |> ignore

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
