/// Port of table_printer.js. Formats tabular data as an HTML table or aligned text.
/// `ToTextString` is pure (no DOM) so it stays unit-testable.
module Netlog.Webview.TablePrinter

open Netlog.Webview.Dom

type Cell(value: string) =
    member val Text = value with get, set
    member val Link: string option = None with get, set
    member val AlignRight = false with get, set
    member val AllowOverflow = false with get, set

type TablePrinter() =
    let rows = ResizeArray<ResizeArray<Cell>>()
    let mutable hasHeaderRow = false
    let mutable title: string option = None
    let mutable newRowCellIndent = 0

    member _.SetNewRowCellIndent(n: int) = newRowCellIndent <- n

    member this.AddRow() =
        rows.Add(ResizeArray<Cell>())
        for _ in 1..newRowCellIndent do
            this.AddCell("") |> ignore

    member _.AddCell(text: string) : Cell =
        let cell = Cell(text)
        rows.[rows.Count - 1].Add cell
        cell

    member _.SetTitle(t: string) = title <- Some t

    member _.AddHeaderCell(text: string) : Cell =
        if not hasHeaderRow then
            rows.Insert(0, ResizeArray<Cell>())
            hasHeaderRow <- true
        let cell = Cell(text)
        rows.[0].Add cell
        cell

    member _.NumColumns: int =
        let mutable n = 0
        for r in rows do
            n <- max n r.Count
        n

    member _.GetCell(r: int, c: int) : Cell option =
        if r >= rows.Count then
            None
        else
            let row = rows.[r]
            if c >= row.Count then None else Some row.[c]

    member this.Search(searchString: string) : bool =
        let s = searchString.ToLower()
        let numColumns = this.NumColumns
        let mutable found = false
        let mutable r = 0
        while not found && r < rows.Count do
            let mutable c = 0
            while not found && c < numColumns do
                match this.GetCell(r, c) with
                | Some cell when cell.Text.ToLower().Contains s -> found <- true
                | _ -> ()
                c <- c + 1
            r <- r + 1
        found

    member this.ToTextString(spacing: int) : string =
        let numColumns = this.NumColumns
        let columnWidths = Array.zeroCreate numColumns

        let addedSpacer =
            if hasHeaderRow then
                let spacerRow = ResizeArray<Cell>()
                for c in 0 .. numColumns - 1 do
                    match this.GetCell(0, c) with
                    | Some cell -> spacerRow.Add(Cell(String.replicate cell.Text.Length "-"))
                    | None -> ()
                rows.Insert(1, spacerRow)
                true
            else
                false

        for c in 0 .. numColumns - 1 do
            for r in 0 .. rows.Count - 1 do
                match this.GetCell(r, c) with
                | Some cell when not cell.AllowOverflow -> columnWidths.[c] <- max columnWidths.[c] cell.Text.Length
                | _ -> ()

        let sb = System.Text.StringBuilder()

        match title with
        | Some t ->
            let ts = String.replicate t.Length "-"
            sb.Append(ts).Append('\n').Append(t).Append('\n').Append(ts).Append('\n') |> ignore
        | None -> ()

        let spacingStr = String.replicate spacing " "

        for r in 0 .. rows.Count - 1 do
            for c in 0 .. numColumns - 1 do
                match this.GetCell(r, c) with
                | Some cell ->
                    let padding = max 0 (columnWidths.[c] - cell.Text.Length)
                    let paddingStr = String.replicate padding " "
                    if cell.AlignRight then sb.Append(paddingStr) |> ignore
                    sb.Append(cell.Text) |> ignore
                    if not cell.AlignRight then sb.Append(paddingStr) |> ignore
                    sb.Append(spacingStr) |> ignore
                | None -> ()
            sb.Append('\n') |> ignore

        if addedSpacer then rows.RemoveAt(1)
        sb.ToString()

    member this.ToText(parent: Element, spacing: int) : Element =
        let pre = addNode parent "pre"
        let numColumns = this.NumColumns
        let columnWidths = Array.zeroCreate numColumns

        let addedSpacer =
            if hasHeaderRow then
                let spacerRow = ResizeArray<Cell>()
                for c in 0 .. numColumns - 1 do
                    match this.GetCell(0, c) with
                    | Some cell -> spacerRow.Add(Cell(String.replicate cell.Text.Length "-"))
                    | None -> ()
                rows.Insert(1, spacerRow)
                true
            else
                false

        for c in 0 .. numColumns - 1 do
            for r in 0 .. rows.Count - 1 do
                match this.GetCell(r, c) with
                | Some cell when not cell.AllowOverflow -> columnWidths.[c] <- max columnWidths.[c] cell.Text.Length
                | _ -> ()

        let sb = System.Text.StringBuilder()
        let flush () =
            if sb.Length > 0 then
                addText pre (sb.ToString())
                sb.Clear() |> ignore

        match title with
        | Some t ->
            let ts = String.replicate t.Length "-"
            sb.Append(ts).Append('\n').Append(t).Append('\n').Append(ts).Append('\n') |> ignore
        | None -> ()

        let spacingStr = String.replicate spacing " "

        for r in 0 .. rows.Count - 1 do
            for c in 0 .. numColumns - 1 do
                match this.GetCell(r, c) with
                | Some cell ->
                    let padding = max 0 (columnWidths.[c] - cell.Text.Length)
                    let paddingStr = String.replicate padding " "
                    if cell.AlignRight then sb.Append(paddingStr) |> ignore
                    match cell.Link with
                    | Some link ->
                        flush ()
                        let a = addNodeWithText pre "a" cell.Text
                        a.href <- link
                    | None -> sb.Append(cell.Text) |> ignore
                    if not cell.AlignRight then sb.Append(paddingStr) |> ignore
                    sb.Append(spacingStr) |> ignore
                | None -> ()
            sb.Append('\n') |> ignore

        if addedSpacer then rows.RemoveAt(1)
        flush ()
        pre

    /// Shared by ToHtml/ToInteractiveHtml: builds the <table>, plus (for the latter)
    /// the header <th> cells and each body row's node + cell texts, so sorting/
    /// filtering can be wired up without re-parsing the DOM afterwards.
    member private this.BuildHtml
        (parent: Element, style: string)
        : Element * Element * ResizeArray<Element> * ResizeArray<Element * string[]> =
        let numColumns = this.NumColumns
        let table = addNode parent "table"
        table.setAttribute ("class", style)
        let thead = addNode table "thead"
        let tbody = addNode table "tbody"
        let headerCells = ResizeArray<Element>()
        let bodyRows = ResizeArray<Element * string[]>()

        match title with
        | Some t ->
            let titleRow = addNode thead "tr"
            let titleCell = addNodeWithText titleRow "th" t
            titleCell.colSpan <- numColumns
            titleCell.classList.add "title"
        | None -> ()

        for r in 0 .. rows.Count - 1 do
            let isHeader = (r = 0 && hasHeaderRow)
            let rowNode = addNode (if isHeader then thead else tbody) "tr"
            let cellType = if isHeader then "th" else "td"
            let texts = ResizeArray<string>()
            for c in 0 .. numColumns - 1 do
                match this.GetCell(r, c) with
                | Some cell ->
                    let tableCell = addNode rowNode cellType
                    if cell.AllowOverflow && (this.GetCell(r, c + 1)).IsNone then
                        tableCell.colSpan <- numColumns - c
                    match cell.Link with
                    | Some link ->
                        let a = addNodeWithText tableCell "a" cell.Text
                        a.href <- link
                    | None -> addText tableCell cell.Text
                    texts.Add cell.Text
                    if isHeader then headerCells.Add tableCell
                | None -> texts.Add ""
            if not isHeader then bodyRows.Add(rowNode, texts.ToArray())

        table, tbody, headerCells, bodyRows

    member this.ToHtml(parent: Element, style: string) : Element =
        let table, _, _, _ = this.BuildHtml(parent, style)
        table

    /// Same table as ToHtml, plus a free-text row filter and click-to-sort column
    /// headers (both pure client-side DOM manipulation -- no new wire messages).
    member this.ToInteractiveHtml(parent: Element, style: string) : Element =
        let wrapper = addNode parent "div"
        wrapper.className <- "nv-interactive-table"

        let searchInput = addNode wrapper "input"
        searchInput.setAttribute ("type", "text")
        searchInput.className <- "nv-filter-input"
        searchInput.setAttribute ("placeholder", "Filter rows\u2026")
        searchInput.setAttribute ("aria-label", "Filter table rows")

        let _, tbody, headerCells, bodyRows = this.BuildHtml(wrapper, style)

        searchInput.addEventListener (
            "input",
            fun _ ->
                let q = searchInput.value.ToLower()
                for (row, texts) in bodyRows do
                    let matches = q = "" || texts |> Array.exists (fun t -> t.ToLower().Contains q)
                    setNodeDisplay row matches)

        let mutable sortColumn = -1
        let mutable sortDescending = false

        for i in 0 .. headerCells.Count - 1 do
            let th = headerCells.[i]
            th.classList.add "nv-sortable-th"
            th.setAttribute ("role", "button")
            th.setAttribute ("aria-sort", "none")
            th.tabIndex <- 0

            let activate () =
                sortDescending <- (sortColumn = i && not sortDescending)
                sortColumn <- i
                for j in 0 .. headerCells.Count - 1 do
                    headerCells.[j].setAttribute (
                        "aria-sort",
                        (if j <> i then "none"
                         elif sortDescending then "descending"
                         else "ascending")
                    )
                let key (texts: string[]) = if i < texts.Length then texts.[i].ToLower() else ""
                let sorted = bodyRows |> Seq.sortBy (fun (_, t) -> key t) |> Seq.toList
                let ordered = if sortDescending then List.rev sorted else sorted
                for (row, _) in ordered do
                    tbody.appendChild row |> ignore

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

        wrapper

