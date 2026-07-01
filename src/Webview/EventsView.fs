/// Port of events_view.js (+ source_row.js): the 2-pane Events tab. Left pane is a
/// filterable/sortable source list; right pane is the painted details for the
/// selected sources.
module Netlog.Webview.EventsView

open Fable.Core.JsInterop
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

/// Cap on how many source rows are materialized in the DOM at once. All sources are
/// kept in memory and remain filterable/sortable; only the first this-many matches are
/// rendered, so a log with hundreds of thousands of sources stays responsive.
[<Literal>]
let private MaxRenderedRows = 2000

type private SourceVm =
    { Id: int; TypeName: string; Description: string; IsError: bool; IsActive: bool; StartTicks: float; Duration: float; mutable Selected: bool }

type private RenderedRow = { Vm: SourceVm; Row: Element; Checkbox: Element }

type EventsView(root: Element, post: obj -> unit) as this =
    inherit ViewBase(root)

    let mutable filterInput = Unchecked.defaultof<Element>
    let mutable tableBody = Unchecked.defaultof<Element>
    let mutable countNote = Unchecked.defaultof<Element>
    let mutable details = Unchecked.defaultof<DetailsView>
    let mutable constants = Unchecked.defaultof<Constants.Constants>
    let mutable baseTime = 0.0
    let mutable logCreationTime: float option = None
    let sources = ResizeArray<SourceVm>()
    // Only the currently-visible (filtered, capped) rows exist in the DOM.
    let rendered = ResizeArray<RenderedRow>()
    // On-demand event cache: sourceId -> its events (fetched when first selected).
    let fetched = System.Collections.Generic.Dictionary<int, Model.Event[]>()
    // sourceId -> the source's full event count (so details can note any truncation).
    let sourceTotal = System.Collections.Generic.Dictionary<int, int>()
    // A source-dependency link may target a source whose events aren't loaded yet;
    // remember it and scroll once its events arrive.
    let mutable pendingScrollId: int option = None

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

        let note = addNode root "div"
        note.className <- "nv-source-count"
        note.style.display <- "none"
        countNote <- note

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

    member private this.OnSelectionChanged() =
        // Request events for any selected source we haven't fetched yet; render what is
        // already available (the rest fill in as their responses arrive).
        for s in sources do
            if s.Selected && not (fetched.ContainsKey s.Id) then
                post (createObj [ "type" ==> "getSourceEvents"; "id" ==> s.Id ])
        this.RenderDetails()

    member private _.RenderDetails() =
        let detailSources =
            sources
            |> Seq.filter (fun s -> s.Selected)
            |> Seq.choose (fun s ->
                match fetched.TryGetValue s.Id with
                | true, evs ->
                    let total =
                        match sourceTotal.TryGetValue s.Id with
                        | true, t -> t
                        | _ -> evs.Length
                    Some
                        { DetailSource.Id = s.Id
                          TypeName = s.TypeName
                          Description = s.Description
                          StartTicks = s.StartTicks
                          Events = evs
                          Total = total
                          Truncated = total > evs.Length }
                | _ -> None)
            |> Seq.toList
        details.SetData(constants, baseTime, logCreationTime, detailSources)
        match pendingScrollId with
        | Some id when fetched.ContainsKey id ->
            details.ScrollToSourceId id
            pendingScrollId <- None
        | _ -> ()

    member private this.OnRowClicked(vm: SourceVm) =
        for s in sources do
            s.Selected <- false
        vm.Selected <- true
        this.RefreshRenderedSelection()
        this.OnSelectionChanged()

    /// Re-applies selection state to the currently-rendered rows (rows are recreated on
    /// every filter/sort, so selection lives on the data, not the DOM).
    member private _.RefreshRenderedSelection() =
        for r in rendered do
            setChecked r.Checkbox r.Vm.Selected
            if r.Vm.Selected then r.Row.classList.add "selected" else r.Row.classList.remove "selected"

    /// Adds a source to the current selection without clearing others (port of
    /// setParameters({s}) -> sourceRow.setSelected(true)). Used by source_dependency links.
    member private this.SelectSourceAdditive(vm: SourceVm) =
        if not vm.Selected then
            vm.Selected <- true
            this.RefreshRenderedSelection()
            this.OnSelectionChanged()

    member private this.OnDetailsClick(e: obj) =
        let href = (eventTarget e).getAttribute "href"
        if not (isNull href) && href.StartsWith "nv-source:" then
            preventDefault e
            let idStr = href.Substring 10
            match sources |> Seq.tryFind (fun s -> string s.Id = idStr) with
            | Some vm ->
                pendingScrollId <- Some vm.Id
                this.SelectSourceAdditive vm
                this.RenderDetails()
            | None -> ()

    member private this.OnCheckboxToggled(vm: SourceVm, checkbox: Element, row: Element) =
        vm.Selected <- getChecked checkbox
        if vm.Selected then row.classList.add "selected" else row.classList.remove "selected"
        this.OnSelectionChanged()

    member private this.RenderRow(vm: SourceVm) =
        let tr = addNode tableBody "tr"
        let selTd = addNode tr "td"
        let checkbox = addNode selTd "input"
        checkbox.setAttribute ("type", "checkbox")
        setChecked checkbox vm.Selected

        let idTd = addNode tr "td"
        addText idTd (string vm.Id)
        let typeTd = addNode tr "td"
        addText typeTd vm.TypeName
        let descTd = addNode tr "td"
        addText descTd vm.Description

        tr.classList.add ("source-" + vm.TypeName.ToLower().Replace("_", "-"))
        if vm.IsError then tr.classList.add "error"
        elif not vm.IsActive then tr.classList.add "inactive"
        if vm.Selected then tr.classList.add "selected"

        checkbox.addEventListener ("change", (fun _ -> this.OnCheckboxToggled(vm, checkbox, tr)))
        let onClick = (fun (_: obj) -> this.OnRowClicked vm)
        idTd.addEventListener ("click", onClick)
        typeTd.addEventListener ("click", onClick)
        descTd.addEventListener ("click", onClick)
        rendered.Add { Vm = vm; Row = tr; Checkbox = checkbox }

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

    member private this.ApplyFilter() =
        let parsed = parse filterInput.value

        let ordered =
            match parsed.Sort with
            | Some(method, backwards) -> this.SortSources(method, backwards)
            | None -> this.SortSources("time", false)

        let matched =
            ordered
            |> List.filter (fun vm ->
                parsed.Filter
                    { FilterSource.Id = vm.Id
                      TypeName = vm.TypeName
                      Description = vm.Description
                      IsError = vm.IsError
                      IsActive = vm.IsActive
                      SearchText = (fun () -> "") })

        // Deselect any source that no longer matches the filter.
        let matchedIds = System.Collections.Generic.HashSet<int>()
        for vm in matched do matchedIds.Add vm.Id |> ignore
        for vm in sources do
            if vm.Selected && not (matchedIds.Contains vm.Id) then vm.Selected <- false

        // Materialize only the first MaxRenderedRows matches to keep the DOM bounded.
        clear tableBody
        rendered.Clear()
        for vm in matched |> List.truncate MaxRenderedRows do
            this.RenderRow vm

        let total = List.length matched
        if total > MaxRenderedRows then
            countNote.textContent <-
                sprintf
                    "Showing the first %d of %d matching sources. Refine the filter to narrow the list."
                    MaxRenderedRows
                    total
            setNodeDisplay countNote true
        else
            setNodeDisplay countNote false

        this.OnSelectionChanged()

    override _.OnLoadLogFinish(model: obj) : bool =
        constants <- Constants.decode (Json.get (Json.get model "constants") "raw")
        baseTime <- Json.tryNumber model "baseTime" |> Option.defaultValue 0.0
        logCreationTime <- Json.tryNumber model "numericDate"

        fetched.Clear()
        sourceTotal.Clear()
        sources.Clear()
        rendered.Clear()
        clear tableBody
        let wireSources = Json.get model "sources"
        for i in 0 .. Json.length wireSources - 1 do
            let s = Json.item wireSources i
            sources.Add
                { Id = Json.tryNumber s "id" |> Option.defaultValue 0.0 |> int
                  TypeName = Json.tryString s "typeName" |> Option.defaultValue ""
                  Description = Json.tryString s "description" |> Option.defaultValue ""
                  IsError = Json.isTruthy (Json.get s "isError")
                  IsActive = Json.isTruthy (Json.get s "isActive")
                  StartTicks = Json.tryNumber s "startTicks" |> Option.defaultValue 0.0
                  Duration = Json.tryNumber s "duration" |> Option.defaultValue 0.0
                  Selected = false }

        this.ApplyFilter()
        true

    /// On-demand details response: cache the source's events and (re)render selection.
    override this.OnSourceEvents(id: int, data: obj) =
        let wireEvents = Json.get data "events"
        let n = Json.length wireEvents
        fetched.[id] <- Array.init n (fun i -> toEvent (Json.item wireEvents i))
        sourceTotal.[id] <- Json.tryNumber data "total" |> Option.defaultValue (float n) |> int
        this.RenderDetails()

let create (post: obj -> unit) : EventsView =
    let root = createElement "div"
    root.className <- "nv-view nv-events"
    EventsView(root, post)
