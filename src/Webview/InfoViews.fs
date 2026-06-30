// Port of the 11 "info-dump" tab views (proxy/dns/sockets/stream_pool/alt_svc/
// spdy/quic/reporting/http_cache/modules/prerender). Each reads its blob from
// model.polledData[key] and returns false from OnLoadLogFinish (hiding the tab)
// when the blob is absent. Some deeply-nested structures are rendered as pretty
// JSON for now (noted), with the primary data shown as tables.
namespace Netlog.Webview

open Netlog.Core
open Netlog.Webview.Dom
open Netlog.Webview.View
open Netlog.Webview.TablePrinter

/// Shared helpers for the info-dump views.
module Info =
    let polled (model: obj) (key: string) : obj = Json.get (Json.get model "polledData") key

    let str (o: obj) (k: string) : string = Json.tryString o k |> Option.defaultValue ""

    /// Any JS value coerced to a display string ("" when undefined).
    let valStr (o: obj) (k: string) : string =
        let v = Json.get o k
        if Json.isDefined v then Json.jsString v else ""

    /// A "time ticks" value rendered as a local date string.
    let dateOf (o: obj) (k: string) : string =
        match Json.tryNumber o k with
        | Some t -> TimeUtil.dateToStringMs (TimeUtil.convertTimeTicksToTime t)
        | None -> ""

    /// A wall-clock ms timestamp (number or numeric string) rendered as a date.
    let dateMs (o: obj) (k: string) : string =
        let v = Json.get o k
        if Json.isDefined v then TimeUtil.dateToStringMs (Json.toNumber v) else ""

    let joinArray (o: obj) (k: string) : string =
        let a = Json.get o k
        if Json.isArray a then
            Array.init (Json.length a) (fun i -> Json.jsString (Json.item a i)) |> String.concat ", "
        else
            ""

    let addInfoRow (root: Element) (label: string) (value: string) =
        let row = addNode root "div"
        row.className <- "nv-info-row"
        (addNodeWithText row "span" label).className <- "nv-label"
        (addNodeWithText row "span" value).className <- "nv-value"

    let addTitle (root: Element) (title: string) =
        (addNodeWithText root "h3" title).className <- "nv-section-title"

    let headerCells (tp: TablePrinter) (cells: string list) =
        for h in cells do
            tp.AddHeaderCell h |> ignore

    /// Renders a JS array as a table; `rowFn` produces the cell strings per element.
    let arrayTable (root: Element) (title: string) (headers: string list) (rowFn: obj -> string list) (arr: obj) =
        let tp = TablePrinter()
        if title <> "" then tp.SetTitle title
        headerCells tp headers
        for i in 0 .. Json.length arr - 1 do
            let item = Json.item arr i
            tp.AddRow()
            for c in rowFn item do
                tp.AddCell c |> ignore
        tp.ToHtml(root, "nv-table") |> ignore

module ProxyView =
    let tabId = "proxy"
    let tabName = "Proxy"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let proxySettings = Info.polled model "proxySettings"
            let badProxies = Info.polled model "badProxies"
            if not (Json.isObject proxySettings) || not (Json.isDefined badProxies) then
                false
            else
                Info.addTitle root "Effective proxy settings"
                (addNode root "pre").textContent <- ProxyFormatter.proxySettingsToString (Json.get proxySettings "effective")
                Info.addTitle root "Original proxy settings"
                (addNode root "pre").textContent <- ProxyFormatter.proxySettingsToString (Json.get proxySettings "original")
                if Json.isArray badProxies && Json.length badProxies > 0 then
                    Info.arrayTable
                        root
                        "Recently failed proxies"
                        [ "Bad proxy chain"; "Time for next retry" ]
                        (fun bp -> [ Info.str bp "proxy_chain_uri"; Info.dateOf bp "bad_until" ])
                        badProxies
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-proxy"
        View(root)

module DnsView =
    let tabId = "dns"
    let tabName = "DNS"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let info = Info.polled model "hostResolverInfo"
            let cache = Json.get info "cache"
            if not (Json.isObject info) || not (Json.isObject cache) then
                false
            else
                Info.addInfoRow root "Capacity:" (Info.valStr cache "capacity")
                Info.addInfoRow root "Network changes:" (Info.valStr cache "network_changes")

                let dnsConfig = Json.get info "dns_config"
                if Json.isObject dnsConfig then
                    Info.addTitle root "DNS config"
                    if Json.isDefined (Json.get dnsConfig "nameservers") then
                        Info.addInfoRow root "nameservers:" (Info.joinArray dnsConfig "nameservers")
                    for k in Json.keys dnsConfig do
                        if k <> "nameservers" then
                            let v = Json.get dnsConfig k
                            let display = if Json.isArray v then Info.joinArray dnsConfig k else Info.valStr dnsConfig k
                            Info.addInfoRow root (k + ":") display

                let entries = Json.get cache "entries"
                if Json.isArray entries then
                    Info.arrayTable
                        root
                        "Host cache"
                        [ "Hostname"; "Family"; "Addresses"; "TTL"; "Expires"; "Network changes" ]
                        (fun e ->
                            let addresses =
                                let ip = Info.joinArray e "ip_endpoints"
                                if ip <> "" then ip else Info.joinArray e "addresses"
                            [ Info.str e "hostname"
                              Info.valStr e "address_family"
                              addresses
                              Info.valStr e "ttl"
                              Info.dateOf e "expiration"
                              Info.valStr e "network_changes" ])
                        entries
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-dns"
        View(root)

module SocketsView =
    let tabId = "sockets"
    let tabName = "Sockets"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let pools = Info.polled model "socketPoolInfo"
            if not (Json.isArray pools) then
                false
            else
                // Flatten nested pools (port of SocketPoolWrapper.createArrayFrom).
                let flat = ResizeArray<obj>()
                let rec addPool (p: obj) =
                    flat.Add p
                    let nested = Json.get p "nested_pools"
                    if Json.isArray nested then
                        for i in 0 .. Json.length nested - 1 do
                            addPool (Json.item nested i)
                for i in 0 .. Json.length pools - 1 do
                    addPool (Json.item pools i)

                let summary = TablePrinter()
                summary.SetTitle "Socket pools"
                Info.headerCells summary [ "Name"; "Handed Out"; "Idle"; "Connecting"; "Max"; "Max/Group"; "Generation" ]
                for p in flat do
                    summary.AddRow()
                    summary.AddCell(Info.str p "name" + " (" + Info.str p "type" + ")") |> ignore
                    summary.AddCell(Info.valStr p "handed_out_socket_count") |> ignore
                    summary.AddCell(Info.valStr p "idle_socket_count") |> ignore
                    summary.AddCell(Info.valStr p "connecting_socket_count") |> ignore
                    summary.AddCell(Info.valStr p "max_socket_count") |> ignore
                    summary.AddCell(Info.valStr p "max_sockets_per_group") |> ignore
                    summary.AddCell(Info.valStr p "pool_generation_number") |> ignore
                summary.ToHtml(root, "nv-table") |> ignore

                for p in flat do
                    let groups = Json.get p "groups"
                    if Json.isObject groups && Json.keys groups |> Array.isEmpty |> not then
                        let gt = TablePrinter()
                        gt.SetTitle(Info.str p "name")
                        Info.headerCells gt [ "Group"; "Pending"; "Top Priority"; "Active"; "Idle"; "Connect Jobs"; "Backup Timer"; "Stalled" ]
                        for gName in Json.keys groups do
                            let g = Json.get groups gName
                            gt.AddRow()
                            gt.AddCell gName |> ignore
                            gt.AddCell(Info.valStr g "pending_request_count") |> ignore
                            gt.AddCell(Info.valStr g "top_pending_priority") |> ignore
                            gt.AddCell(Info.valStr g "active_socket_count") |> ignore
                            gt.AddCell(Info.joinArray g "idle_sockets") |> ignore
                            gt.AddCell(Info.joinArray g "connect_jobs") |> ignore
                            gt.AddCell(Info.valStr g "backup_job_timer_is_running") |> ignore
                            gt.AddCell(Info.valStr g "is_stalled") |> ignore
                        gt.ToHtml(root, "nv-table") |> ignore
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-sockets"
        View(root)

module StreamPoolView =
    let tabId = "streamPool"
    let tabName = "StreamPool"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let info = Info.polled model "httpStreamPoolInfo"
            if not (Json.isObject info) then
                false
            else
                let tp = TablePrinter()
                tp.SetTitle "HTTP Stream Pool"
                Info.headerCells tp [ "Property"; "Value" ]
                let prop (label: string) (key: string) =
                    tp.AddRow()
                    tp.AddCell label |> ignore
                    tp.AddCell(Info.valStr info key) |> ignore
                prop "Connecting sockets" "connecting_socket_count"
                prop "Handed out sockets" "handed_out_socket_count"
                prop "Idle sockets" "idle_socket_count"
                prop "Max sockets" "max_socket_count"
                prop "Max sockets/group" "max_sockets_per_group"
                tp.ToHtml(root, "nv-table") |> ignore

                let groups = Json.get info "groups"
                if Json.isObject groups then
                    let gt = TablePrinter()
                    gt.SetTitle "Groups"
                    Info.headerCells gt [ "Group"; "Active"; "Handed Out"; "Idle"; "Attempt Manager" ]
                    for gName in Json.keys groups do
                        let g = Json.get groups gName
                        gt.AddRow()
                        gt.AddCell gName |> ignore
                        gt.AddCell(Info.valStr g "active_socket_count") |> ignore
                        gt.AddCell(Info.valStr g "handed_out_socket_count") |> ignore
                        gt.AddCell(Info.valStr g "idle_socket_count") |> ignore
                        gt.AddCell(Info.valStr g "attempt_manager_alive") |> ignore
                    gt.ToHtml(root, "nv-table") |> ignore

                    for gName in Json.keys groups do
                        let attempt = Json.get (Json.get groups gName) "attempt_state"
                        if Json.isObject attempt then
                            Info.addTitle root ("Attempt state: " + gName)
                            (addNode root "pre").textContent <- Json.stringifyPretty attempt
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-stream-pool"
        View(root)

module AltSvcView =
    let tabId = "alt-svc"
    let tabName = "Alt-Svc"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let mappings = Info.polled model "altSvcMappings"
            if not (Json.isArray mappings) then
                false
            else
                if Json.length mappings = 0 then
                    Info.addInfoRow root "Alternate service mappings:" "None"
                else
                    Info.arrayTable
                        root
                        "Alternate service mappings"
                        [ "Server"; "Alternative Service" ]
                        (fun m -> [ Info.str m "server"; Info.str m "alternative_service" ])
                        mappings
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-alt-svc"
        View(root)

module SpdyView =
    let tabId = "http2"
    let tabName = "HTTP/2"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let sessions = Info.polled model "spdySessionInfo"
            let status = Info.polled model "spdyStatus"
            if not (Json.isDefined sessions) || not (Json.isObject status) then
                false
            else
                Info.addInfoRow root "HTTP/2 enabled:" (Info.valStr status "enable_http2")
                Info.addInfoRow root "ALPN protocols:" (Info.str status "alpn_protos")
                if Json.isArray sessions && Json.length sessions > 0 then
                    Info.arrayTable
                        root
                        "HTTP/2 sessions"
                        [ "Host"; "Proxy"; "ID"; "Protocol"; "Active"; "Max"; "Initiated"; "Pushed"; "Abandoned"; "Frames"; "Secure"; "Send Window"; "Recv Window"; "Error" ]
                        (fun s ->
                            [ Info.str s "host_port_pair"
                              Info.str s "proxy"
                              Info.valStr s "source_id"
                              Info.str s "negotiated_protocol"
                              Info.valStr s "active_streams"
                              Info.valStr s "max_concurrent_streams"
                              Info.valStr s "streams_initiated_count"
                              Info.valStr s "streams_pushed_count"
                              Info.valStr s "streams_abandoned_count"
                              Info.valStr s "frames_received"
                              Info.valStr s "is_secure"
                              Info.valStr s "send_window_size"
                              Info.valStr s "recv_window_size"
                              Info.valStr s "error" ])
                        sessions
                else
                    Info.addInfoRow root "Sessions:" "None"
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-spdy"
        View(root)

module QuicView =
    let tabId = "quic"
    let tabName = "QUIC"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let info = Info.polled model "quicInfo"
            if not (Json.isObject info) then
                false
            else
                Info.addInfoRow root "QUIC enabled:" (Info.valStr info "quic_enabled")
                Info.addInfoRow root "Supported versions:" (Info.str info "supported_versions")
                Info.addInfoRow root "Connection options:" (Info.str info "connection_options")
                Info.addInfoRow root "Max packet length:" (Info.valStr info "max_packet_length")
                Info.addInfoRow root "Idle timeout (s):" (Info.valStr info "idle_connection_timeout_seconds")

                let sessions = Json.get info "sessions"
                if Json.isArray sessions && Json.length sessions > 0 then
                    Info.arrayTable
                        root
                        "QUIC sessions"
                        [ "Aliases"; "Version"; "Peer Address"; "Connection ID"; "Open"; "Active"; "Total"; "Sent"; "Lost"; "Received"; "Connected" ]
                        (fun s ->
                            [ Info.joinArray s "aliases"
                              Info.str s "version"
                              Info.str s "peer_address"
                              Info.str s "connection_id"
                              Info.valStr s "open_streams"
                              Info.joinArray s "active_streams"
                              Info.valStr s "total_streams"
                              Info.valStr s "packets_sent"
                              Info.valStr s "packets_lost"
                              Info.valStr s "packets_received"
                              Info.valStr s "connected" ])
                        sessions
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-quic"
        View(root)

module ReportingView =
    let tabId = "reporting"
    let tabName = "Reporting"

    let private firstDefined (o: obj) (keys: string list) : string =
        keys
        |> List.tryPick (fun k ->
            let v = Json.get o k
            if Json.isDefined v then Some(Json.jsString v) else None)
        |> Option.defaultValue ""

    let private networkKey (o: obj) : string =
        firstDefined o [ "network_anonymization_key"; "network_isolation_key"; "NetworkAnonymizationKey"; "networkIsolationKey" ]

    let private valueOrDefault (o: obj) (key: string) (defaultValue: string) : string =
        let v = Json.get o key
        if Json.isDefined v then Json.jsString v else defaultValue

    let private numberOrDefault (o: obj) (key: string) (defaultValue: float) : float =
        Json.tryNumber o key |> Option.defaultValue defaultValue

    let private percent (value: float) : string = sprintf "%.2f%%" (value * 100.0)

    let private tableWithHeaders (root: Element) (title: string) (headers: string list) : Element =
        Info.addTitle root title
        let table = addNode root "table"
        table.className <- "nv-table"
        let thead = addNode table "thead"
        let tr = addNode thead "tr"
        for h in headers do
            addNodeWithText tr "th" h |> ignore
        addNode table "tbody"

    let private addCell (tr: Element) (text: string) : Element = addNodeWithText tr "td" text

    let private addClassCell (tr: Element) (text: string) (className: string) : Element =
        let td = addCell tr text
        td.className <- className
        td

    let private addRowSpanCell (tr: Element) (text: string) (rowSpan: int) : Element =
        let td = addCell tr text
        td.setAttribute("rowspan", string rowSpan)
        td

    let private addRowSpanClassCell (tr: Element) (text: string) (rowSpan: int) (className: string) : Element =
        let td = addRowSpanCell tr text rowSpan
        td.className <- className
        td

    let private addExpiryText (td: Element) (ticks: float) (nowMs: float option) =
        let expiresMs = TimeUtil.convertTimeTicksToTime ticks
        addText td (TimeUtil.dateToStringMs expiresMs)
        match nowMs with
        | Some now when expiresMs < now ->
            (addNodeWithText td "span" " [expired]").className <- "nv-warning"
        | _ -> ()

    let private addExpiryCell (tr: Element) (o: obj) (key: string) (nowMs: float option) : Element =
        let td = addNode tr "td"
        match Json.tryNumber o key with
        | Some ticks -> addExpiryText td ticks nowMs
        | None -> ()
        td

    let private addUploadCount (tr: Element) (counts: obj) =
        let uploads = numberOrDefault counts "uploads" 0.0 |> int
        let reports = numberOrDefault counts "reports" 0.0 |> int
        let text = if uploads = 0 && reports = 0 then "-" else string uploads + " (" + string reports + ")"
        addClassCell tr text "nv-centered" |> ignore

    let private addContentSections (contentNode: Element) (showText: string) (hideText: string) =
        let summary = addNode contentNode "span"
        summary.className <- "nv-report-summary"
        let button = addNodeWithText contentNode "button" showText
        button.className <- "nv-report-toggle"
        let detail = addNode contentNode "div"
        detail.className <- "nv-report-detail"
        setNodeDisplay detail false
        button.addEventListener(
            "click",
            fun _ ->
                let show = detail.style.display = "none"
                setNodeDisplay detail show
                button.textContent <- if show then hideText else showText)
        summary, detail

    let private addReportContent (tr: Element) (report: obj) =
        let contentNode = addNode tr "td"
        let summary, detail = addContentSections contentNode "Show raw report" "Hide raw report"
        if Info.str report "type" = "network-error" then
            let body = Json.get report "body"
            addText summary (Info.str body "type")
            match Json.tryNumber body "status_code" with
            | Some status when status <> 0.0 -> addText summary (" (" + string (int status) + ")")
            | _ -> ()
        (addNodeWithText detail "pre" (Json.stringifyPretty report)) |> ignore

    let private renderReports (root: Element) (reports: obj) =
        if Json.isArray reports && Json.length reports > 0 then
            let tbody =
                tableWithHeaders
                    root
                    "Queued reports"
                    [ "Queued"; "URL"; "Status"; "Type"; "Network key"; "Content" ]
            for i in 0 .. Json.length reports - 1 do
                let report = Json.item reports i
                let tr = addNode tbody "tr"
                addExpiryCell tr report "queued" None |> ignore
                addCell tr (Info.str report "url") |> ignore
                let status =
                    let baseText = Info.str report "status" + " (" + Info.str report "group"
                    let depth = Json.tryNumber report "depth" |> Option.filter (fun d -> d > 0.0)
                    let attempts = Json.tryNumber report "attempts" |> Option.filter (fun a -> a > 0.0)
                    let withDepth =
                        match depth with
                        | Some d -> baseText + ", depth: " + string (int d)
                        | None -> baseText
                    let withAttempts =
                        match attempts with
                        | Some a -> withDepth + ", attempts: " + string (int a)
                        | None -> withDepth
                    withAttempts + ")"
                addCell tr status |> ignore
                addCell tr (Info.str report "type") |> ignore
                addCell tr (networkKey report) |> ignore
                addReportContent tr report
        else
            Info.addInfoRow root "Queued reports:" "none"

    let private renderClients (root: Element) (clients: obj) (nowMs: float option) =
        let clientHeight (client: obj) =
            let groups = Json.get client "groups"
            if not (Json.isArray groups) then
                0
            else
                [ 0 .. Json.length groups - 1 ]
                |> List.sumBy (fun gi ->
                    let endpoints = Json.get (Json.item groups gi) "endpoints"
                    if Json.isArray endpoints then Json.length endpoints else 0)

        let totalRows =
            if not (Json.isArray clients) then
                0
            else
                [ 0 .. Json.length clients - 1 ] |> List.sumBy (fun i -> clientHeight (Json.item clients i))

        if totalRows = 0 then
            Info.addInfoRow root "Clients:" "none"
        else
            let tbody =
                tableWithHeaders
                    root
                    "Clients"
                    [ "Origin"; "Network key"; "Group"; "Subdomains"; "Expires"; "Endpoint"; "Priority"; "Weight"; "Uploads succeeded"; "Uploads failed" ]

            for i in 0 .. Json.length clients - 1 do
                let client = Json.item clients i
                let height = clientHeight client
                if height > 0 then
                    let groups = Json.get client "groups"
                    for gi in 0 .. Json.length groups - 1 do
                        let group = Json.item groups gi
                        let endpoints = Json.get group "endpoints"
                        let endpointCount = if Json.isArray endpoints then Json.length endpoints else 0
                        if endpointCount > 0 then
                            for ei in 0 .. endpointCount - 1 do
                                let endpoint = Json.item endpoints ei
                                let tr = addNode tbody "tr"
                                if gi = 0 && ei = 0 then
                                    addRowSpanCell tr (Info.str client "origin") height |> ignore
                                    addRowSpanCell tr (networkKey client) height |> ignore
                                if ei = 0 then
                                    addRowSpanCell tr (Info.str group "name") endpointCount |> ignore
                                    addRowSpanClassCell tr (if Json.isTruthy (Json.get group "includeSubdomains") then "yes" else "no") endpointCount "nv-centered" |> ignore
                                    let expires = addNode tr "td"
                                    expires.setAttribute("rowspan", string endpointCount)
                                    match Json.tryNumber group "expires" with
                                    | Some ticks -> addExpiryText expires ticks nowMs
                                    | None -> ()
                                addCell tr (Info.str endpoint "url") |> ignore
                                addClassCell tr (valueOrDefault endpoint "priority" "0") "nv-centered" |> ignore
                                addClassCell tr (valueOrDefault endpoint "weight" "1") "nv-centered" |> ignore
                                addUploadCount tr (Json.get endpoint "successful")
                                addUploadCount tr (Json.get endpoint "failed")

    let private renderNelPolicies (root: Element) (nel: obj) (nowMs: float option) =
        if not (Json.isObject nel) then
            Info.addInfoRow root "Network Error Logging:" "not present"
        else
            let policies = Json.get nel "originPolicies"
            if not (Json.isArray policies) || Json.length policies = 0 then
                Info.addInfoRow root "NEL policies:" "none"
            else
                let tbody =
                    tableWithHeaders
                        root
                        "Network Error Logging policies"
                        [ "Origin"; "Subdomains"; "Expires"; "Report-To"; "Network key"; "Success"; "Failure" ]
                for i in 0 .. Json.length policies - 1 do
                    let policy = Json.item policies i
                    let tr = addNode tbody "tr"
                    addCell tr (Info.str policy "origin") |> ignore
                    addClassCell tr (if Json.isTruthy (Json.get policy "includeSubdomains") then "yes" else "no") "nv-centered" |> ignore
                    addExpiryCell tr policy "expires" nowMs |> ignore
                    addCell tr (Info.str policy "reportTo") |> ignore
                    addCell tr (networkKey policy) |> ignore
                    addClassCell tr (percent (numberOrDefault policy "successFraction" 0.0)) "nv-right" |> ignore
                    addClassCell tr (percent (numberOrDefault policy "failureFraction" 0.0)) "nv-right" |> ignore

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let info = Info.polled model "reportingInfo"
            if not (Json.isObject info) then
                false
            elif not (Json.isTruthy (Json.get info "reportingEnabled")) then
                Info.addInfoRow root "Reporting:" "disabled"
                true
            else
                let nowMs = Json.tryNumber model "numericDate"
                let reports = Json.get info "reports"
                renderReports root reports
                let clients = Json.get info "clients"
                renderClients root clients nowMs
                let nel = Json.get info "networkErrorLogging"
                renderNelPolicies root nel nowMs
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-reporting"
        View(root)

module HttpCacheView =
    let tabId = "httpCache"
    let tabName = "Cache"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let info = Info.polled model "httpCacheInfo"
            if not (Json.isObject info) then
                false
            else
                let stats = Json.get info "stats"
                Info.addTitle root "HTTP cache statistics"
                let ul = addNode root "ul"
                if Json.isObject stats then
                    for k in Json.keys stats do
                        addNodeWithText ul "li" (k + ": " + Info.valStr stats k) |> ignore
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-http-cache"
        View(root)

module ModulesView =
    let tabId = "modules"
    let tabName = "Modules"

    let private valueWithKey (m: Map<string, string>) (o: obj) (key: string) : string =
        let raw = Info.valStr o key
        m |> Map.tryFind raw |> Option.defaultValue raw

    let private socketTypes =
        Map.ofList
            [ "1", "SOCK_STREAM"
              "2", "SOCK_DGRAM"
              "3", "SOCK_RAW"
              "4", "SOCK_RDM"
              "5", "SOCK_SEQPACKET" ]

    let private protocolTypes =
        Map.ofList
            [ "1", "IPPROTO_ICMP"
              "6", "IPPROTO_TCP"
              "17", "IPPROTO_UDP"
              "58", "IPPROTO_ICMPV6" ]

    let private namespaceProviderTypes =
        Map.ofList
            [ "12", "NS_DNS"
              "15", "NS_NLA"
              "16", "NS_BTH"
              "32", "NS_NTDS"
              "37", "NS_EMAIL"
              "38", "NS_PNRPNAME"
              "39", "NS_PNRPCLOUD" ]

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let ext = Info.polled model "extensionInfo"
            let sp = Info.polled model "serviceProviders"
            let hasExt = Json.isArray ext && Json.length ext > 0
            let hasSp = Json.isObject sp
            if not hasExt && not hasSp then
                false
            else
                if hasExt then
                    Info.arrayTable
                        root
                        "Extensions"
                        [ "ID"; "App"; "Enabled"; "Name"; "Version"; "Description" ]
                        (fun e ->
                            [ Info.str e "id"
                              Info.valStr e "packagedApp"
                              Info.valStr e "enabled"
                              Info.str e "name"
                              Info.str e "version"
                              Info.str e "description" ])
                        ext

                if hasSp then
                    let lsp = Json.get sp "service_providers"
                    if Json.isArray lsp then
                        Info.arrayTable
                            root
                            "Layered Service Providers"
                            [ "Name"; "Version"; "Type"; "Socket Type"; "Protocol" ]
                            (fun p ->
                                let chain = Json.tryNumber p "chain_length" |> Option.defaultValue 0.0 |> int
                                let typ = if chain = 0 then "Layer" elif chain = 1 then "Base" else "Chain"
                                [ Info.str p "name"
                                  Info.valStr p "version"
                                  typ
                                  (valueWithKey socketTypes p "socket_type")
                                  (valueWithKey protocolTypes p "socket_protocol") ])
                            lsp
                    let nsp = Json.get sp "namespace_providers"
                    if Json.isArray nsp then
                        Info.arrayTable
                            root
                            "Namespace Providers"
                            [ "Name"; "Version"; "Type"; "Active" ]
                            (fun p ->
                                [ Info.str p "name"
                                  Info.valStr p "version"
                                  (valueWithKey namespaceProviderTypes p "type")
                                  Info.valStr p "active" ])
                            nsp
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-modules"
        View(root)

module PrerenderView =
    let tabId = "prerender"
    let tabName = "Prerender"

    type View(root: Element) =
        inherit ViewBase(root)

        override _.OnLoadLogFinish(model: obj) : bool =
            clear root
            let info = Info.polled model "prerenderInfo"
            if not (Json.isObject info) then
                false
            else
                Info.addInfoRow root "Prerender enabled:" (Info.valStr info "enabled")
                Info.addInfoRow root "Enabled note:" (Info.str info "enabled_note")
                Info.addInfoRow root "Omnibox enabled:" (Info.valStr info "omnibox_enabled")

                let active = Json.get info "active"
                if Json.isArray active then
                    Info.arrayTable
                        root
                        "Active prerender pages"
                        [ "URL"; "Duration"; "Loaded" ]
                        (fun a -> [ Info.str a "url"; Info.str a "duration"; Info.valStr a "is_loaded" ])
                        active

                let history = Json.get info "history"
                if Json.isArray history then
                    Info.arrayTable
                        root
                        "Prerender history"
                        [ "Origin"; "URL"; "Final Status"; "Time" ]
                        (fun h ->
                            [ Info.str h "origin"; Info.str h "url"; Info.str h "final_status"; Info.dateMs h "end_time" ])
                        history
                true

    let create () : View =
        let root = createElement "div"
        root.className <- "nv-view nv-prerender"
        View(root)
