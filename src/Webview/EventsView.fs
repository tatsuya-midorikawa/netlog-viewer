/// Port of events_view.js (+ source_row.js): the 2-pane Events tab. Left pane is a
/// filterable/sortable source list; right pane is the painted details for the
/// selected sources.
module Netlog.Webview.EventsView

open Netlog.Core
open Netlog.Webview.Dom
open Netlog.Webview.View
open Netlog.Webview.SourceFilterParser
open Netlog.Webview.DetailsView
open Netlog.Webview.ResizableVerticalSplitView

let tabId = "events"
let tabName = "Events"

/// Reconstructs a Core event from its wire (plain-object) form, for grouping/painting.
let private toEvent (w: obj) : Model.Event =
    { Index = Json.tryNumber w "index" |> Option.defaultValue 0.0 |> int
      Time = Json.tryNumber w "time" |> Option.defaultValue 0.0
      Type = Json.tryNumber w "type" |> Option.defaultValue 0.0 |> int
      Phase = Json.tryNumber w "phase" |> Option.defaultValue 0.0 |> int
      SourceId = Json.tryNumber w "sourceId" |> Option.defaultValue 0.0 |> int
      SourceType = Json.tryNumber w "sourceType" |> Option.defaultValue 0.0 |> int
      StartTime = None
      Params = (let p = Json.get w "params" in if Json.isObject p then Some p else None) }

type private SourceVm =
    { Id: int; TypeName: string; Description: string; IsError: bool; IsActive: bool; StartTicks: float; Duration: float; Events: Model.Event[]; Row: Element; Checkbox: Element; mutable Selected: bool; mutable SearchTextCache: string option }

type EventsView(root: Element) as this =
    inherit ViewBase(root)

    let mutable filterInput = Unchecked.defaultof<Element>
    let mutable tableBody = Unchecked.defaultof<Element>
    let mutable details = Unchecked.defaultof<DetailsView>
    let mutable constants = Unchecked.defaultof<Constants.Constants>
    let mutable baseTime = 0.0
    let mutable logCreationTime: float option = None
    let sources = ResizeArray<SourceVm>()

    do this.BuildLayout()

    member private _.BuildLayout() =
        clear root

        let filterBar = addNode root "div"
        filterBar.className <- "nv-filter-bar"
        (addNodeWithText filterBar "span" "Filter:").className <- "nv-label"

        let input = addNode filterBar "input"
        input.setAttribute ("type", "text")
        input.className <- "nv-filter-input"
        input.setAttribute ("placeholder", "type:url_request   is:error   sort:duration")
        filterInput <- input

        let split = SplitView(root, 120.0)
        let left = split.LeftPane
        let table = addNode left "table"
        table.setAttribute ("class", "nv-source-table")
        let thead = addNode table "thead"
        let htr = addNode thead "tr"
        addNodeWithText htr "th" "" |> ignore
        addNodeWithText htr "th" "ID" |> ignore
        addNodeWithText htr "th" "Type" |> ignore
        addNodeWithText htr "th" "Description" |> ignore
        tableBody <- addNode table "tbody"

        let right = split.RightPane
        details <- DetailsView(addNode right "div")
        right.addEventListener ("click", this.OnDetailsClick)

        input.addEventListener ("input", (fun _ -> this.ApplyFilter()))

    member private _.OnSelectionChanged() =
        let detailSources =
            sources
            |> Seq.filter (fun s -> s.Selected)
            |> Seq.map (fun s ->
                { DetailSource.Id = s.Id
                  TypeName = s.TypeName
                  Description = s.Description
                  StartTicks = s.StartTicks
                  Events = s.Events })
            |> Seq.toList
        details.SetData(constants, baseTime, logCreationTime, detailSources)

    member private this.OnRowClicked(vm: SourceVm) =
        for s in sources do
            if s.Selected then
                s.Selected <- false
                setChecked s.Checkbox false
                s.Row.classList.remove "selected"
        vm.Selected <- true
        setChecked vm.Checkbox true
        vm.Row.classList.add "selected"
        this.OnSelectionChanged()

    /// Adds a source to the current selection without clearing others (port of
    /// setParameters({s}) -> sourceRow.setSelected(true)). Used by source_dependency links.
    member private this.SelectSourceAdditive(vm: SourceVm) =
        if not vm.Selected then
            vm.Selected <- true
            setChecked vm.Checkbox true
            vm.Row.classList.add "selected"
            this.OnSelectionChanged()

    member private this.OnDetailsClick(e: obj) =
        let href = (eventTarget e).getAttribute "href"
        if not (isNull href) && href.StartsWith "nv-source:" then
            preventDefault e
            let idStr = href.Substring 10
            match sources |> Seq.tryFind (fun s -> string s.Id = idStr) with
            | Some vm ->
                this.SelectSourceAdditive vm
                details.ScrollToSourceId vm.Id
            | None -> ()

    member private this.OnCheckboxToggled(vm: SourceVm) =
        vm.Selected <- getChecked vm.Checkbox
        if vm.Selected then vm.Row.classList.add "selected" else vm.Row.classList.remove "selected"
        this.OnSelectionChanged()

    member private _.AddSourceRow(s: obj, id: int, evs: Model.Event[]) : SourceVm =
        let tr = addNode tableBody "tr"
        let selTd = addNode tr "td"
        let checkbox = addNode selTd "input"
        checkbox.setAttribute ("type", "checkbox")

        let idTd = addNode tr "td"
        addText idTd (string id)
        let typeName = Json.tryString s "typeName" |> Option.defaultValue ""
        let typeTd = addNode tr "td"
        addText typeTd typeName
        let desc = Json.tryString s "description" |> Option.defaultValue ""
        let descTd = addNode tr "td"
        addText descTd desc

        let vm =
            { Id = id
              TypeName = typeName
              Description = desc
              IsError = Json.isTruthy (Json.get s "isError")
              IsActive = Json.isTruthy (Json.get s "isActive")
              StartTicks = Json.tryNumber s "startTicks" |> Option.defaultValue 0.0
              Duration = Json.tryNumber s "duration" |> Option.defaultValue 0.0
              Events = evs
              Row = tr
              Checkbox = checkbox
              Selected = false
              SearchTextCache = None }

        tr.classList.add ("source-" + typeName.ToLower().Replace("_", "-"))
        if vm.IsError then tr.classList.add "error"
        elif not vm.IsActive then tr.classList.add "inactive"

        checkbox.addEventListener ("change", (fun _ -> this.OnCheckboxToggled vm))
        let onClick = (fun (_: obj) -> this.OnRowClicked vm)
        idTd.addEventListener ("click", onClick)
        typeTd.addEventListener ("click", onClick)
        descTd.addEventListener ("click", onClick)
        vm

    member private _.SortSources(method: string, backwards: bool) : SourceVm list =
        let sorted =
            match method with
            | "time" -> sources |> Seq.sortBy (fun s -> s.StartTicks, s.Id)
            | "id" -> sources |> Seq.sortBy (fun s -> s.Id)
            | "active" -> sources |> Seq.sortBy (fun s -> if s.IsActive then 0 else 1)
            | "desc" -> sources |> Seq.sortBy (fun s -> s.Description)
            | "duration" -> sources |> Seq.sortBy (fun s -> s.Duration)
            | "type" -> sources |> Seq.sortBy (fun s -> s.TypeName)
            | _ -> sources |> Seq.sortBy (fun s -> s.StartTicks, s.Id)

        let lst = Seq.toList sorted
        if backwards then List.rev lst else lst

    /// The painted event-trace text for a source (lowercased, cached), for text search.
    member private _.SearchTextOf(vm: SourceVm) : string =
        match vm.SearchTextCache with
        | Some s -> s
        | None ->
            let tp =
                LogViewPainter.createLogEntryTablePrinter constants vm.Events vm.StartTicks baseTime logCreationTime true
            let text = (tp.ToTextString 0).ToLower()
            vm.SearchTextCache <- Some text
            text

    member private _.ApplyFilter() =
        let parsed = parse filterInput.value

        let ordered =
            match parsed.Sort with
            | Some(method, backwards) -> this.SortSources(method, backwards)
            | None -> this.SortSources("time", false)

        for vm in ordered do
            let fs =
                { FilterSource.Id = vm.Id
                  TypeName = vm.TypeName
                  Description = vm.Description
                  IsError = vm.IsError
                  IsActive = vm.IsActive
                  SearchText = (fun () -> this.SearchTextOf vm) }
            let matched = parsed.Filter fs
            setNodeDisplay vm.Row matched
            if not matched && vm.Selected then
                vm.Selected <- false
                setChecked vm.Checkbox false
                vm.Row.classList.remove "selected"
            tableBody.appendChild vm.Row |> ignore

        this.OnSelectionChanged()

    override _.OnLoadLogFinish(model: obj) : bool =
        constants <- Constants.decode (Json.get (Json.get model "constants") "raw")
        baseTime <- Json.tryNumber model "baseTime" |> Option.defaultValue 0.0
        logCreationTime <- Json.tryNumber model "numericDate"

        // Group events by source id (for the details pane).
        let wireEvents = Json.get model "events"
        let eventsBySource = System.Collections.Generic.Dictionary<int, ResizeArray<Model.Event>>()
        for i in 0 .. Json.length wireEvents - 1 do
            let ev = toEvent (Json.item wireEvents i)
            match eventsBySource.TryGetValue ev.SourceId with
            | true, lst -> lst.Add ev
            | _ ->
                let lst = ResizeArray<Model.Event>()
                lst.Add ev
                eventsBySource.[ev.SourceId] <- lst

        sources.Clear()
        clear tableBody
        let wireSources = Json.get model "sources"
        for i in 0 .. Json.length wireSources - 1 do
            let s = Json.item wireSources i
            let id = Json.tryNumber s "id" |> Option.defaultValue 0.0 |> int
            let evs =
                match eventsBySource.TryGetValue id with
                | true, lst -> lst.ToArray()
                | _ -> [||]
            sources.Add(this.AddSourceRow(s, id, evs))

        this.ApplyFilter()
        true

let create () : EventsView =
    let root = createElement "div"
    root.className <- "nv-view nv-events"
    EventsView(root)
