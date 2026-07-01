/// Port of source_filter_parser.js. Parses the events filter query into a predicate
/// and an optional sort directive. Operates on a minimal source projection.
module Netlog.Webview.SourceFilterParser

type FilterSource =
    { Id: int
      TypeName: string
      Description: string
      IsError: bool
      IsActive: bool
      // Wall-clock ms (same space as TimeUtil.convertTimeTicksToTime), used only by
      // the `t:` directive below (e.g. from a Timeline double-click). Callers that
      // don't care about time filtering can pass 0.0/0.0.
      StartTicks: float
      EndTicks: float
      SearchText: unit -> string }

type ParsedFilter =
    { Filter: FilterSource -> bool
      Sort: (string * bool) option
      // The `has:<text>` directive's term, if present. Unlike every other directive,
      // this can't be evaluated client-side (event params aren't all loaded up
      // front -- see Wire.fs's on-demand design) -- the caller is expected to ask
      // the extension host (a "searchParams" message) which source ids match, then
      // additionally restrict by that result once it arrives.
      ParamSearch: string option }

type private Token = { Parsed: string; Negated: bool }

/// Tokenizes the filter text honoring quotes, backslash escapes, and a leading '-'.
let private parseTokens (filterText: string) : Token list =
    let tokens = ResizeArray<Token>()
    let n = filterText.Length
    let mutable position = 0

    while position < n do
        let mutable inQuote = false
        let sb = System.Text.StringBuilder()
        let mutable negated = false
        let startPosition = position
        let mutable brk = false

        while position < n && not brk do
            let ch = filterText.[position]
            position <- position + 1

            if ch = '\\' && position < n then
                sb.Append(filterText.[position]) |> ignore
                position <- position + 1
            elif ch = '"' then
                inQuote <- not inQuote
            elif (not inQuote) && System.Char.IsWhiteSpace ch then
                brk <- true
            elif ch = '-' && startPosition = position - 1 then
                negated <- true
            else
                sb.Append(ch) |> ignore

        let element = sb.ToString()
        if element.Length > 0 then
            tokens.Add { Parsed = element.ToLower(); Negated = negated }

    List.ofSeq tokens

let private trySort (parsed: string) (backwards: bool) : (string * bool) option =
    if parsed.StartsWith "sort:" then Some(parsed.Substring 5, backwards) else None

let private tryRestrict (parsed: string) : (FilterSource -> bool) option =
    if parsed.StartsWith "is:" then
        match parsed.Substring 3 with
        | "active" -> Some(fun s -> s.IsActive)
        | "error" -> Some(fun s -> s.IsError)
        | _ -> None
    else
        None

let private tryStringDirective (parsed: string) : (FilterSource -> bool) option =
    let idx = parsed.IndexOf ':'
    if idx < 0 then
        None
    else
        let key = parsed.Substring(0, idx)
        let parameters = parsed.Substring(idx + 1).Split(',') |> Array.filter (fun x -> x.Length > 0)
        match key with
        | "type" ->
            Some(fun s ->
                let t = s.TypeName.ToLower()
                parameters |> Array.exists (fun p -> t.Contains p))
        | "id" -> Some(fun s -> parameters |> Array.contains (string s.Id))
        | _ -> None

/// `t:<wallClockMs>` -- sources active at that instant (StartTicks <= t <= EndTicks,
/// both already wall-clock-converted by the caller). Primarily produced by
/// double-clicking the Timeline graph (see Timeline.fs/App.fs), not hand-typed, but
/// still a plain filter-text token like everything else.
let private tryTimeAt (parsed: string) : (FilterSource -> bool) option =
    if parsed.StartsWith "t:" then
        match System.Double.TryParse(parsed.Substring 2) with
        | true, t -> Some(fun s -> s.StartTicks <= t && t <= s.EndTicks)
        | false, _ -> None
    else
        None

let private textPredicate (text: string) : FilterSource -> bool =
    fun s ->
        s.Description.ToLower().Contains text
        || s.TypeName.ToLower().Contains text
        || (string s.Id).Contains text
        || (s.SearchText()).ToLower().Contains text

/// `has:<text>` extraction (see ParsedFilter.ParamSearch's doc comment for why this
/// isn't a synchronous predicate like everything else here).
let private tryParamSearch (parsed: string) : string option =
    if parsed.StartsWith "has:" then
        let term = parsed.Substring 4
        if term.Length > 0 then Some term else None
    else
        None

let parse (filterText: string) : ParsedFilter =
    let mutable sort = None
    let mutable paramSearch = None
    let preds = ResizeArray<FilterSource -> bool>()

    for tok in parseTokens filterText do
        match trySort tok.Parsed tok.Negated with
        | Some s -> sort <- Some s
        | None ->
            match tryParamSearch tok.Parsed with
            | Some term -> paramSearch <- Some term
            | None ->
                let directive =
                    match tryRestrict tok.Parsed with
                    | Some f -> Some f
                    | None ->
                        match tryStringDirective tok.Parsed with
                        | Some f -> Some f
                        | None -> tryTimeAt tok.Parsed

                match directive with
                | Some f -> preds.Add(if tok.Negated then (fun s -> not (f s)) else f)
                | None ->
                    let p = textPredicate tok.Parsed
                    preds.Add(if tok.Negated then (fun s -> not (p s)) else p)

    { Filter = (fun s -> preds |> Seq.forall (fun p -> p s))
      Sort = sort
      ParamSearch = paramSearch }
