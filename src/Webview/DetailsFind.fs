module Netlog.Webview.DetailsFind

open System
open System.Text.RegularExpressions
open Netlog.Webview.Dom

[<Literal>]
let DefaultMaxMatches = 5000

[<Literal>]
let private MaxTextNodes = 50000

[<Literal>]
let private SearchDebounceMs = 120

[<Literal>]
let private MatchHighlightName = "nv-find-match"

[<Literal>]
let private CurrentHighlightName = "nv-find-current"

type FindMatch = { Start: int; End: int }

type FindResult =
    { Matches: FindMatch list
      Truncated: bool
      Error: string option }

type TextRun =
    { Start: int
      Length: int }

let private emptyResult = { Matches = []; Truncated = false; Error = None }

let private takeWithLimit (maxMatches: int) (matches: FindMatch seq) : FindResult =
    let limit = max 0 maxMatches
    if limit = 0 then
        { emptyResult with Truncated = true }
    else
        let kept = ResizeArray<FindMatch>()
        use e = matches.GetEnumerator()
        let mutable truncated = false
        let mutable keepGoing = true
        while keepGoing && kept.Count < limit do
            if e.MoveNext() then
                kept.Add e.Current
            else
                keepGoing <- false
        if keepGoing && e.MoveNext() then
            truncated <- true
        { Matches = Seq.toList kept; Truncated = truncated; Error = None }

let findMatches (text: string) (query: string) (caseSensitive: bool) (useRegex: bool) (maxMatches: int) : FindResult =
    if query = "" then
        emptyResult
    else
        try
            let pattern = if useRegex then query else Regex.Escape query
            let options = if caseSensitive then RegexOptions.None else RegexOptions.IgnoreCase
            let regex = Regex(pattern, options)
            regex.Matches(text)
            |> Seq.cast<Match>
            |> Seq.choose (fun m ->
                if m.Length = 0 then
                    None
                else
                    Some { Start = m.Index; End = m.Index + m.Length })
            |> takeWithLimit maxMatches
        with ex ->
            { Matches = []; Truncated = false; Error = Some ex.Message }

let tryMapOffset (runs: TextRun[]) (offset: int) : (int * int) option =
    let mutable found = None
    let mutable i = 0
    while found.IsNone && i < runs.Length do
        let run = runs.[i]
        let runEnd = run.Start + run.Length
        if offset >= run.Start && offset < runEnd then
            found <- Some(i, offset - run.Start)
        elif offset = runEnd && run.Length = 0 then
            found <- Some(i, 0)
        i <- i + 1
    found

let tryMapEndOffset (runs: TextRun[]) (offset: int) : (int * int) option =
    let mutable found = None
    let mutable i = 0
    while found.IsNone && i < runs.Length do
        let run = runs.[i]
        let runEnd = run.Start + run.Length
        if offset > run.Start && offset <= runEnd then
            found <- Some(i, offset - run.Start)
        elif offset = run.Start && run.Length = 0 then
            found <- Some(i, 0)
        i <- i + 1
    if found.IsNone && runs.Length > 0 then
        let last = runs.[runs.Length - 1]
        if offset = last.Start + last.Length then
            found <- Some(runs.Length - 1, last.Length)
    found

type private DomTextRun =
    { Node: Element
      Start: int
      Length: int }

let private deleteHighlights () : unit =
    if supportsHighlightApi () then
        cssHighlightsDelete MatchHighlightName
        cssHighlightsDelete CurrentHighlightName

let private setButtonPressed (button: Element) (pressed: bool) =
    button.setAttribute ("aria-pressed", if pressed then "true" else "false")
    if pressed then button.classList.add "active" else button.classList.remove "active"

type DetailsFindController(anchor: Element, scrollContainer: Element, searchRoot: Element) as this =
    let supported = supportsHighlightApi ()
    let openButton = addNode anchor "button"
    let bar = addNode anchor "div"
    let input = addNode bar "input"
    let countNode = addNode bar "span"
    let prevButton = addNode bar "button"
    let nextButton = addNode bar "button"
    let caseButton = addNode bar "button"
    let regexButton = addNode bar "button"
    let closeButton = addNode bar "button"
    let mutable isOpen = false
    let mutable caseSensitive = false
    let mutable useRegex = false
    let mutable matches: Range[] = [||]
    let mutable currentIndex = -1
    let mutable truncated = false
    let mutable debounceTimer: obj = null
    let mutable generation = 0

    do
        openButton.setAttribute ("type", "button")
        openButton.className <- "nv-icon-button nv-find-open"
        openButton.setAttribute ("aria-label", "Find in event trace")
        openButton.textContent <- "Find"

        bar.className <- "nv-find-bar"
        bar.setAttribute ("role", "search")
        setNodeDisplay bar false

        input.setAttribute ("type", "text")
        input.className <- "nv-find-input"
        input.setAttribute ("aria-label", "Find in event trace")
        input.setAttribute ("placeholder", "Find in trace")

        countNode.className <- "nv-find-count"
        countNode.textContent <- "0/0"

        for button, label, text in
            [ prevButton, "Previous match", "\u25B2"
              nextButton, "Next match", "\u25BC"
              caseButton, "Match case", "Aa"
              regexButton, "Use regular expression", ".*"
              closeButton, "Close find", "\u00D7" ] do
            button.setAttribute ("type", "button")
            button.className <- "nv-icon-button"
            button.setAttribute ("aria-label", label)
            button.textContent <- text

        setButtonPressed caseButton caseSensitive
        setButtonPressed regexButton useRegex
        setNodeDisplay openButton supported

        openButton.addEventListener ("click", fun e -> preventDefault e; this.Open())
        prevButton.addEventListener ("click", fun e -> preventDefault e; this.Previous())
        nextButton.addEventListener ("click", fun e -> preventDefault e; this.Next())
        closeButton.addEventListener ("click", fun e -> preventDefault e; this.Close())
        caseButton.addEventListener (
            "click",
            fun e ->
                preventDefault e
                caseSensitive <- not caseSensitive
                setButtonPressed caseButton caseSensitive
                this.RunSearch(true))
        regexButton.addEventListener (
            "click",
            fun e ->
                preventDefault e
                useRegex <- not useRegex
                setButtonPressed regexButton useRegex
                this.RunSearch(true))

        input.addEventListener ("input", fun _ -> this.ScheduleSearch())
        input.addEventListener (
            "keydown",
            fun e ->
                match eventKey e with
                | "Enter" ->
                    preventDefault e
                    if eventShift e then this.Previous() else this.Next()
                | "Escape" ->
                    preventDefault e
                    this.Close()
                | _ -> ())

    member _.IsSupported = supported

    member private _.SetCount(total: int, error: string option) =
        match error with
        | Some _ ->
            input.classList.add "error"
            countNode.textContent <- "Invalid"
        | None ->
            input.classList.remove "error"
            if total = 0 then
                countNode.textContent <- "0/0"
            else
                let totalText = if truncated then string total + "+" else string total
                countNode.textContent <- sprintf "%d/%s" (currentIndex + 1) totalText

    member private _.CollectRuns(pre: Element) : string * DomTextRun[] option =
        let textNodes = collectTextNodes pre
        if textNodes.Length > MaxTextNodes then
            "", None
        else
            let sb = System.Text.StringBuilder()
            let runs = ResizeArray<DomTextRun>()
            for node in textNodes do
                let text = nodeValue node
                let start = sb.Length
                sb.Append text |> ignore
                runs.Add { Node = node; Start = start; Length = text.Length }
            sb.ToString(), Some(runs.ToArray())

    member private _.CreateRange(runs: DomTextRun[]) (m: FindMatch) : Range option =
        let pureRuns = runs |> Array.map (fun r -> { TextRun.Start = r.Start; Length = r.Length })
        match tryMapOffset pureRuns m.Start, tryMapEndOffset pureRuns m.End with
        | Some(startIndex, startOffset), Some(endIndex, endOffset) ->
            let range = newRange ()
            range.setStart(runs.[startIndex].Node, startOffset)
            range.setEnd(runs.[endIndex].Node, endOffset)
            Some range
        | _ -> None

    member private _.BuildRanges() : Range[] * bool * string option =
        let allRanges = ResizeArray<Range>()
        let mutable anyTruncated = false
        let mutable error: string option = None
        let mutable stop = false
        let preNodes = querySelectorAllArray searchRoot "pre"
        let query = input.value
        let mutable preIndex = 0

        while not stop && preIndex < preNodes.Length do
            let text, runOpt = this.CollectRuns preNodes.[preIndex]
            match runOpt with
            | None ->
                error <- Some "Trace is too large to search."
                stop <- true
            | Some runs ->
                let remaining = DefaultMaxMatches - allRanges.Count
                if remaining <= 0 then
                    anyTruncated <- true
                    stop <- true
                else
                    let result = findMatches text query caseSensitive useRegex remaining
                    match result.Error with
                    | Some msg ->
                        error <- Some msg
                        stop <- true
                    | None ->
                        for m in result.Matches do
                            match this.CreateRange runs m with
                            | Some range -> allRanges.Add range
                            | None -> ()
                        if result.Truncated then
                            anyTruncated <- true
                            stop <- true
            preIndex <- preIndex + 1

        allRanges.ToArray(), anyTruncated, error

    member private _.ApplyHighlights() =
        deleteHighlights ()
        if supported && matches.Length > 0 then
            let all = newHighlight ()
            let current = newHighlight ()
            for i in 0 .. matches.Length - 1 do
                if i = currentIndex then current.add matches.[i] else all.add matches.[i]
            cssHighlightsSet MatchHighlightName all
            cssHighlightsSet CurrentHighlightName current

    member private _.ScrollCurrentIntoView() =
        if currentIndex >= 0 && currentIndex < matches.Length then
            let rangeRect = matches.[currentIndex].getBoundingClientRect()
            let containerRect = scrollContainer.getBoundingClientRect()
            let target =
                scrollContainer.scrollTop
                + (rangeRect.top - containerRect.top)
                - ((float scrollContainer.clientHeight - rangeRect.height) / 2.0)
            scrollContainer.scrollTop <- max 0.0 target

    member private this.RunSearch(resetIndex: bool) =
        generation <- generation + 1
        let query = input.value
        if query = "" then
            matches <- [||]
            currentIndex <- -1
            truncated <- false
            deleteHighlights ()
            this.SetCount(0, None)
        else
            let newMatches, isTruncated, error = this.BuildRanges()
            matches <- newMatches
            truncated <- isTruncated
            currentIndex <- if matches.Length = 0 then -1 elif resetIndex || currentIndex < 0 then 0 else min currentIndex (matches.Length - 1)
            this.ApplyHighlights()
            this.SetCount(matches.Length, error)
            if error.IsNone then
                this.ScrollCurrentIntoView()

    member private this.ScheduleSearch() =
        if not (isNull debounceTimer) then
            clearTimeout debounceTimer
        generation <- generation + 1
        let expectedGeneration = generation
        debounceTimer <-
            setTimeout
                (fun () ->
                    if expectedGeneration = generation then
                        debounceTimer <- null
                        this.RunSearch(true))
                SearchDebounceMs

    member this.Open() =
        if supported then
            isOpen <- true
            setNodeDisplay bar true
            openButton.classList.add "active"
            input.focus ()
            selectInput input
            if input.value <> "" then
                this.RunSearch(false)
            else
                this.SetCount(0, None)

    member this.Close() =
        isOpen <- false
        if not (isNull debounceTimer) then
            clearTimeout debounceTimer
            debounceTimer <- null
        setNodeDisplay bar false
        openButton.classList.remove "active"
        matches <- [||]
        currentIndex <- -1
        truncated <- false
        deleteHighlights ()
        this.SetCount(0, None)

    member this.Next() =
        if matches.Length > 0 then
            currentIndex <- (currentIndex + 1) % matches.Length
            this.ApplyHighlights()
            this.SetCount(matches.Length, None)
            this.ScrollCurrentIntoView()

    member this.Previous() =
        if matches.Length > 0 then
            currentIndex <- if currentIndex <= 0 then matches.Length - 1 else currentIndex - 1
            this.ApplyHighlights()
            this.SetCount(matches.Length, None)
            this.ScrollCurrentIntoView()

    member this.Refresh() =
        generation <- generation + 1
        if isOpen && input.value <> "" then
            this.RunSearch(false)
        elif isOpen then
            deleteHighlights ()
            matches <- [||]
            currentIndex <- -1
            truncated <- false
            this.SetCount(0, None)
