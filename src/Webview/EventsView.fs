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
open Netlog.Webview.DetailsFind
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
    { Id: int
      TypeName: string
      Description: string
      IsError: bool
      IsActive: bool
      StartTicks: float
      Duration: float
      mutable Selected: bool }

type private RenderedRow = { Vm: SourceVm; Row: Element; Checkbox: Element }

/// True for any token that sets the sort directive (`sort:x` / `-sort:x`). Used by
/// `withSortToken` below so a column-header click can find-and-replace the active
/// sort token while leaving the rest of the filter text untouched -- sort state
/// lives entirely in the filter text; header-click is just a convenience for it.
let private isSortToken (token: string) : bool =
    let low = token.ToLower()
    low.StartsWith "sort:" || low.StartsWith "-sort:"

/// Replaces any existing sort:/-sort: token in `filterText` with one for `method`
/// (prefixed with `-` when `backwards`), preserving every other token as-is.
let private withSortToken (filterText: string) (method: string) (backwards: bool) : string =
    let kept = filterText.Split(' ') |> Array.filter (fun t -> t.Length > 0 && not (isSortToken t))
    let token = (if backwards then "-" else "") + "sort:" + method
    String.concat " " (Array.append kept [| token |])

type EventsView(root: Element, post: obj -> unit, onFilterChanged: string -> unit) as this =
    inherit ViewBase(root)

    let mutable filterInput = Unchecked.defaultof<Element>
    let mutable tableBody = Unchecked.defaultof<Element>
    let mutable countNote = Unchecked.defaultof<Element>
    let mutable details = Unchecked.defaultof<DetailsView>
    let mutable detailsFind = Unchecked.defaultof<DetailsFindController>
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
    let sourceById = System.Collections.Generic.Dictionary<int, SourceVm>()
    // A source-dependency link may target a source whose events aren't loaded yet;
    // remember it and scroll once its events arrive.
    let mutable pendingScrollId: int option = None
    let mutable errorChipButton = Unchecked.defaultof<Element>
    let mutable idTh = Unchecked.defaultof<Element>
    let mutable typeTh = Unchecked.defaultof<Element>
    let mutable descTh = Unchecked.defaultof<Element>
    // Cross-source `has:` param search (debounced request to the extension host --
    // see NetlogEditor.fs's "searchParams" handler -- since the webview only ever
    // holds a selected source's full params). `paramSearchMatches` caches the ids
    // for the last query we actually got a response for; a query only restricts the
    // list once its own matching response has arrived, to avoid flashing empty.
    let mutable paramSearchTimer: obj = null
    let mutable paramSearchMatches: (string * Set<int>) option = None
    let mutable lastRequestedParamQuery: string option = None

    do this.BuildLayout()

    member private this.BuildLayout() =
        clear root

        let filterBar = addNode root "div"
        filterBar.className <- "nv-filter-bar"
        (addNodeWithText filterBar "span" "Filter:").className <- "nv-label"

        let input = addNode filterBar "input"
        input.setAttribute ("type", "text")
        input.className <- "nv-filter-input"
        input.setAttribute ("placeholder", "type:url_request   is:error   sort:duration")
        input.setAttribute ("aria-label", "Filter sources")
        filterInput <- input

        let helpButton = addNode filterBar "button"
        helpButton.setAttribute ("type", "button")
        helpButton.className <- "nv-icon-button"
        helpButton.setAttribute ("aria-label", "Filter syntax help")
        helpButton.textContent <- "?"

        let helpPopover = addNode filterBar "div"
        helpPopover.className <- "nv-popover"
        helpPopover.style.display <- "none"
        helpPopover.setAttribute ("role", "note")
        this.BuildFilterHelp helpPopover

        helpButton.addEventListener (
            "click",
            fun e ->
                preventDefault e
                setNodeDisplay helpPopover (helpPopover.style.display = "none"))

        let errorChip = addNode filterBar "button"
        errorChip.setAttribute ("type", "button")
        errorChip.className <- "nv-error-chip"
        errorChip.style.display <- "none"
        errorChip.addEventListener ("click", (fun e -> preventDefault e; this.ToggleErrorFilter()))
        errorChipButton <- errorChip

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
        idTh <- this.BuildSortableHeader (htr, "ID", "id")
        typeTh <- this.BuildSortableHeader (htr, "Type", "type")
        descTh <- this.BuildSortableHeader (htr, "Description", "desc")
        tableBody <- addNode table "tbody"

        let right = split.RightPane
        details <- DetailsView(addNode right "div")
        detailsFind <- DetailsFindController(split.Root, right, details.Root)
        right.addEventListener ("click", this.OnDetailsClick)

        window.addEventListener (
            "keydown",
            fun e ->
                let key = eventKey e
                if
                    detailsFind.IsSupported
                    && this.IsVisible
                    && (key = "f" || key = "F")
                    && (eventCtrl e || eventMeta e)
                then
                    preventDefault e
                    detailsFind.Open())

        input.addEventListener ("input", (fun _ -> this.ApplyFilter()))

    /// Renders the full SourceFilterParser syntax (kept in one place so the popover
    /// can't silently drift from the parser) into the filter-help popover.
    member private _.BuildFilterHelp(root: Element) =
        addNodeWithText root "div" "Filter syntax" |> ignore
        let ul = addNode root "ul"
        for line in
            [ "type:name1,name2 - source type contains name1 or name2"
              "id:1,2 - exact source id"
              "is:active / is:error - state filters"
              "has:text - search inside event parameters across all sources (may take a moment on large logs)"
              "sort:time|id|active|desc|duration|type - sort (also via column headers)"
              "-token - negate any directive or word above"
              "\"quoted text\" - match a phrase containing spaces"
              "plain text - substring match on id, type, and description" ] do
            addNodeWithText ul "li" line |> ignore

    /// A clickable, keyboard-operable <th> that rewrites the sort: token in the
    /// filter text (see withSortToken) instead of tracking separate sort state.
    member private this.BuildSortableHeader(row: Element, label: string, method: string) : Element =
        let th = addNodeWithText row "th" label
        th.className <- "nv-sortable-th"
        th.setAttribute ("role", "button")
        th.setAttribute ("aria-sort", "none")
        th.tabIndex <- 0
        let activate () = this.OnHeaderSortClick method
        th.addEventListener ("click", (fun _ -> activate ()))
        th.addEventListener (
            "keydown",
            fun e ->
                match eventKey e with
                | "Enter"
                | " " ->
                    preventDefault e
                    activate ()
                | _ -> ())
        th

    member private this.OnHeaderSortClick(method: string) =
        let parsed = parse filterInput.value
        let backwards =
            match parsed.Sort with
            | Some(m, b) when m = method -> not b
            | _ -> false
        filterInput.value <- withSortToken filterInput.value method backwards
        this.ApplyFilter()

    /// Toggles `is:error` in the filter text (the error-count chip's quick filter).
    member private this.ToggleErrorFilter() =
        let tokens = filterInput.value.Split(' ') |> Array.filter (fun t -> t.Length > 0)
        let hasErrorToken = tokens |> Array.exists (fun t -> t.ToLower() = "is:error")
        filterInput.value <-
            if hasErrorToken then
                tokens |> Array.filter (fun t -> t.ToLower() <> "is:error") |> String.concat " "
            else
                String.concat " " (Array.append tokens [| "is:error" |])
        this.ApplyFilter()

    /// Always-visible error count over ALL sources (not just the current filter
    /// match), so a user can tell errors exist somewhere even while filtered to
    /// something else. Clicking it toggles the is:error quick filter.
    member private _.UpdateErrorChip() =
        let errorCount = sources |> Seq.filter (fun s -> s.IsError) |> Seq.length
        if errorCount > 0 then
            let isActive = filterInput.value.Split(' ') |> Array.exists (fun t -> t.ToLower() = "is:error")
            errorChipButton.textContent <- sprintf "\u26A0 %d error%s" errorCount (if errorCount = 1 then "" else "s")
            if isActive then errorChipButton.classList.add "active" else errorChipButton.classList.remove "active"
            setNodeDisplay errorChipButton true
        else
            setNodeDisplay errorChipButton false

    /// Reflects the parsed sort directive as `aria-sort` on the matching column
    /// header (and a CSS-drawn arrow via [aria-sort] in style.css).
    member private _.UpdateSortIndicators(sort: (string * bool) option) =
        let apply (th: Element) (method: string) =
            match sort with
            | Some(m, backwards) when m = method ->
                th.setAttribute ("aria-sort", (if backwards then "descending" else "ascending"))
            | _ -> th.setAttribute ("aria-sort", "none")
        apply idTh "id"
        apply typeTh "type"
        apply descTh "desc"

    member private this.OnSelectionChanged() =
        // Request events for any selected source we haven't fetched yet; render what is
        // already available (the rest fill in as their responses arrive).
        for s in sources do
            if s.Selected && not (fetched.ContainsKey s.Id) then
                post (createObj [ "type" ==> "getSourceEvents"; "id" ==> s.Id ])
        this.RenderDetails()

    member private _.RenderDetails() =
        let entries =
            sources
            |> Seq.filter (fun s -> s.Selected)
            |> Seq.map (fun s ->
                match fetched.TryGetValue s.Id with
                | true, evs ->
                    let total =
                        match sourceTotal.TryGetValue s.Id with
                        | true, t -> t
                        | _ -> evs.Length
                    DetailsView.Ready
                        { DetailSource.Id = s.Id
                          TypeName = s.TypeName
                          Description = s.Description
                          StartTicks = s.StartTicks
                          Events = evs
                          Total = total
                          Truncated = total > evs.Length }
                | _ -> DetailsView.Loading(s.Id, s.TypeName, s.Description))
            |> Seq.toList
        details.SetData(constants, baseTime, logCreationTime, entries)
        match pendingScrollId with
        | Some id when fetched.ContainsKey id ->
            details.ScrollToSourceId id
            pendingScrollId <- None
        | _ -> ()
        detailsFind.Refresh()

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
        let rowIndex = rendered.Count
        let tr = addNode tableBody "tr"
        let selTd = addNode tr "td"
        let checkbox = addNode selTd "input"
        checkbox.setAttribute ("type", "checkbox")
        setChecked checkbox vm.Selected

        let idTd = addNode tr "td"
        addText idTd (string vm.Id)
        let typeTd = addNode tr "td"
        // Non-color state cues (error/inactive) are real visible text, not just a CSS
        // color, so they read the same for colorblind users and screen readers.
        let statusPrefix =
            if vm.IsError then "\u26A0 "
            elif not vm.IsActive then "\u25CB "
            else ""
        addText typeTd (statusPrefix + vm.TypeName)
        let descTd = addNode tr "td"
        addText descTd vm.Description

        tr.classList.add ("source-" + vm.TypeName.ToLower().Replace("_", "-"))
        if vm.IsError then tr.classList.add "error"
        elif not vm.IsActive then tr.classList.add "inactive"
        if vm.Selected then tr.classList.add "selected"

        // Roving tabIndex across rendered rows: only one row is ever a Tab-stop;
        // Arrow/Home/End move it (FocusRow below), matching the tablist pattern.
        tr.tabIndex <- (if rowIndex = 0 then 0 else -1)
        tr.addEventListener (
            "keydown",
            fun e ->
                match eventKey e with
                | "ArrowDown" ->
                    preventDefault e
                    this.FocusRow(rowIndex + 1)
                | "ArrowUp" ->
                    preventDefault e
                    this.FocusRow(rowIndex - 1)
                | "Home" ->
                    preventDefault e
                    this.FocusRow 0
                | "End" ->
                    preventDefault e
                    this.FocusRow(rendered.Count - 1)
                | "Enter"
                | " " ->
                    preventDefault e
                    this.OnRowClicked vm
                | _ -> ())

        checkbox.addEventListener ("change", (fun _ -> this.OnCheckboxToggled(vm, checkbox, tr)))
        let onClick = (fun (_: obj) -> this.OnRowClicked vm)
        idTd.addEventListener ("click", onClick)
        typeTd.addEventListener ("click", onClick)
        descTd.addEventListener ("click", onClick)
        rendered.Add { Vm = vm; Row = tr; Checkbox = checkbox }

    /// Moves the roving tabIndex + focus to `index` within the currently rendered
    /// rows (used by the row keydown handler above).
    member private _.FocusRow(index: int) =
        if index >= 0 && index < rendered.Count then
            for i in 0 .. rendered.Count - 1 do
                rendered.[i].Row.tabIndex <- (if i = index then 0 else -1)
            rendered.[index].Row.focus ()

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
        this.UpdateSortIndicators parsed.Sort
        this.UpdateErrorChip()

        match parsed.ParamSearch with
        | Some term when lastRequestedParamQuery <> Some term ->
            lastRequestedParamQuery <- Some term
            if not (isNull paramSearchTimer) then
                clearTimeout paramSearchTimer
            paramSearchTimer <- setTimeout (fun () -> post (createObj [ "type" ==> "searchParams"; "query" ==> term ])) 300
        | None -> lastRequestedParamQuery <- None
        | _ -> ()

        let ordered =
            match parsed.Sort with
            | Some(method, backwards) -> this.SortSources(method, backwards)
            | None -> this.SortSources("time", false)

        let matched =
            ordered
            |> List.filter (fun vm ->
                let baseMatch =
                    parsed.Filter
                        { FilterSource.Id = vm.Id
                          TypeName = vm.TypeName
                          Description = vm.Description
                          IsError = vm.IsError
                          IsActive = vm.IsActive
                          StartTicks = TimeUtil.convertTimeTicksToTime vm.StartTicks
                          EndTicks = TimeUtil.convertTimeTicksToTime (vm.StartTicks + vm.Duration)
                          SearchText = (fun () -> "") }
                let paramMatch =
                    match parsed.ParamSearch with
                    | None -> true
                    | Some term ->
                        match paramSearchMatches with
                        | Some(cachedTerm, ids) when cachedTerm = term -> ids.Contains vm.Id
                        | _ -> true
                baseMatch && paramMatch)

        // Selection is intentionally NOT cleared for sources that fall out of the
        // filtered/matched set: refining the filter to look at something else
        // shouldn't silently lose the detail pane's contents. Selections are only
        // cleared by an explicit user action (selecting another row, unchecking).

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
        onFilterChanged filterInput.value

    /// The filter text, so it can be persisted (see App.fs's setState/getState use).
    member _.GetFilterText() : string = filterInput.value

    /// Restores a previously-persisted filter text and re-applies it.
    member this.SetFilterText(text: string) : unit =
        filterInput.value <- text
        this.ApplyFilter()

    /// The cross-source `has:` param-search response: only actually re-filters if
    /// the filter text's current `has:` term still matches (a slower-arriving
    /// response for an already-superseded query is simply discarded).
    override this.OnSearchParamsResult(query: string, ids: int[]) =
        paramSearchMatches <- Some(query, Set.ofArray ids)
        if (parse filterInput.value).ParamSearch = Some query then
            this.ApplyFilter()

    override this.Show(visible: bool) =
        base.Show visible
        if not visible then
            detailsFind.Close()

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

let create (post: obj -> unit) (onFilterChanged: string -> unit) : EventsView =
    let root = createElement "div"
    root.className <- "nv-view nv-events"
    EventsView(root, post, onFilterChanged)
