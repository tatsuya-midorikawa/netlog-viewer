/// Derives a compact "L7" (HTTP request/response) summary from a single source's own
/// events -- no cross-source correlation. This is deliberately NOT a hardcoded lookup
/// by NetLog event TYPE name (Chromium's exact event names for this vary across
/// versions and aren't all enumerated in this codebase's Constants); it only depends
/// on the PARAM SHAPE Chromium actually emits:
///   - a request-headers event has both `line` (the "METHOD path HTTP/x.y" request
///     line, e.g. from HTTP_TRANSACTION_SEND_REQUEST_HEADERS) and a `headers` array.
///   - a response-headers event has just a `headers` array whose first entry is the
///     "HTTP/x.y NNN reason"-style status line (e.g. from
///     HTTP_TRANSACTION_READ_RESPONSE_HEADERS).
/// Only the first exchange found is used (redirects/retries would have more than
/// one; showing just the first is a deliberate scope limit, see the plan).
module Netlog.Core.RequestSummary

open Netlog.Core
open Netlog.Core.Model

type HttpExchange =
    { Method: string
      Url: string
      RequestLine: string option
      RequestHeaders: string[]
      StatusLine: string option
      ResponseHeaders: string[]
      RequestSentTicks: float option
      ResponseHeadersTicks: float option }

let private asStringArray (value: obj) : string[] option =
    if Json.isArray value then
        Some(Array.init (Json.length value) (fun i -> Json.jsString (Json.item value i)))
    else
        None

let private firstToken (s: string) : string =
    let i = s.IndexOf ' '
    if i < 0 then s else s.Substring(0, i)

/// Extracts the first request/response header exchange found in `events` (assumed to
/// be one source's own events, in chronological order), plus the URL/method from the
/// first event that carries a `url` param. Returns None when no event carries a
/// `url` at all (i.e. this doesn't look like an HTTP request source), so callers
/// don't need their own source-type check to decide whether to show a summary card.
let extract (events: Event[]) : HttpExchange option =
    let url =
        events |> Array.tryPick (fun e -> e.Params |> Option.bind (fun p -> Json.tryString p "url"))

    match url with
    | None -> None
    | Some url ->
        let mutable requestLine = None
        let mutable requestHeaders: string[] = [||]
        let mutable requestTicks = None
        let mutable statusLine = None
        let mutable responseHeaders: string[] = [||]
        let mutable responseTicks = None

        for e in events do
            match e.Params with
            | None -> ()
            | Some p ->
                let headers = asStringArray (Json.get p "headers")
                match Json.tryString p "line", headers with
                | Some line, Some hdrs when requestLine.IsNone ->
                    requestLine <- Some(line.Replace("\r\n", ""))
                    requestHeaders <- hdrs
                    requestTicks <- Some e.Time
                | None, Some hdrs when statusLine.IsNone && hdrs.Length > 0 && hdrs.[0].StartsWith "HTTP/" ->
                    statusLine <- Some hdrs.[0]
                    responseHeaders <- hdrs.[1..]
                    responseTicks <- Some e.Time
                | _ -> ()

        let method =
            match events |> Array.tryPick (fun e -> e.Params |> Option.bind (fun p -> Json.tryString p "method")) with
            | Some m -> m
            | None -> requestLine |> Option.map firstToken |> Option.defaultValue ""

        Some
            { Method = method
              Url = url
              RequestLine = requestLine
              RequestHeaders = requestHeaders
              StatusLine = statusLine
              ResponseHeaders = responseHeaders
              RequestSentTicks = requestTicks
              ResponseHeadersTicks = responseTicks }
