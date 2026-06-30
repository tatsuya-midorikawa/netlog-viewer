/// Port of the core of log_view_painter.js (createLogEntryTablePrinter): renders a
/// source's events as an aligned text trace with t=/st=/+/-/dt= markers and indented
/// parameters. Uses Core.LogGrouper for BEGIN/END nesting and Core.TimeUtil for time.
///
/// NOTE: Phase 5 implements the default parameter writer (with the common special
/// cases). The per-event-type custom writers (cert/proxy/request-headers) are a
/// later refinement; the default JSON formatting handles them readably.
module Netlog.Webview.LogViewPainter

open Fable.Core
open System.Collections.Generic
open Netlog.Core
open Netlog.Core.Model
open Netlog.Webview.TablePrinter

[<Emit("Object.assign({}, $0)")>]
let private shallowClone (o: obj) : obj = jsNative

[<Emit("$0[$1] = $2")>]
let private setProp (o: obj) (k: string) (v: obj) : unit = jsNative

let private kArrow = " --> "
let private kArrowIndentation = "     "

/// Wrapper around a TablePrinter for emitting parameter lines.
type ParameterOutputter(tp: TablePrinter, forSearch: bool) =
    member _.ForSearch = forSearch

    member _.WriteLine(line: string) : Cell =
        tp.AddRow()
        let cell = tp.AddCell(line)
        cell.AllowOverflow <- true
        cell

    member this.WriteArrowKeyValue(key: string, value: string) =
        this.WriteLine(kArrow + key + " = " + value) |> ignore

    member this.WriteArrowKeyValueLink(key: string, value: string, link: string) =
        let cell = this.WriteLine(kArrow + key + " = " + value)
        cell.Link <- Some link

    member this.WriteArrowKey(key: string) = this.WriteLine(kArrow + key + " =") |> ignore

    member this.WriteSpaceIndentedLines(numSpaces: int, lines: string[]) =
        let prefix = String.replicate numSpaces " "
        for l in lines do
            this.WriteLine(prefix + l) |> ignore

    member this.WriteArrowIndentedLines(lines: string[]) =
        if lines.Length > 0 then
            this.WriteLine(kArrow + lines.[0]) |> ignore
            for i in 1 .. lines.Length - 1 do
                this.WriteLine(kArrowIndentation + lines.[i]) |> ignore

    member this.WriteArrowKeyJSONValue(key: string, value: obj) =
        let lines = (Json.stringifyPretty value).Split('\n')
        this.WriteLine(kArrow + key + " = " + lines.[0]) |> ignore
        this.WriteSpaceIndentedLines(5, lines.[1..])

let private toStringArray (arr: obj) : string[] =
    Array.init (Json.length arr) (fun i -> Json.jsString (Json.item arr i))

// --- Binary (bytes) rendering (port of writeHexString / writeBytesAsSearchableString) ---

[<Emit("(function(s){try{var d=atob(s);var a=new Uint8Array(d.length);for(var i=0;i<d.length;i++)a[i]=d.charCodeAt(i);return a;}catch(e){return null;}})($0)")>]
let private tryParseBase64ToBytes (b64: string) : obj = jsNative

let private byteAt (bytes: obj) (i: int) : int = int (Json.toNumber (Json.item bytes i))

let private byteToPaddedHex (b: int) : string =
    let s = sprintf "%X" b
    if s.Length < 2 then "0" + s else s

let private writeBytesAsSearchableString (bytes: obj) (out: ParameterOutputter) =
    let len = Json.length bytes
    let sb = System.Text.StringBuilder()
    sb.Append(' ') |> ignore
    for i in 0 .. len - 1 do
        let cur = byteAt bytes i
        if cur >= 0x20 && cur <= 0x7E then sb.Append(char cur) |> ignore
        elif cur = 0 then sb.Append(' ') |> ignore
        else sb.Append('.') |> ignore
    out.WriteLine(sb.ToString()) |> ignore

let private writeHexString (bytes: obj) (out: ParameterOutputter) =
    let len = Json.length bytes
    let kBytesPerLine = 16
    let formatLine (startIndex: int) : string =
        let sb = System.Text.StringBuilder()
        sb.Append(' ') |> ignore
        for i in 0 .. kBytesPerLine - 1 do
            sb.Append(' ') |> ignore
            if i % 8 = 0 then sb.Append(' ') |> ignore
            let bi = startIndex + i
            if bi < len then sb.Append(byteToPaddedHex (byteAt bytes bi)) |> ignore
            else sb.Append("  ") |> ignore
        sb.Append("   ") |> ignore
        let mutable i = 0
        let mutable brk = false
        while i < kBytesPerLine && not brk do
            let bi = startIndex + i
            if bi >= len then
                brk <- true
            else
                let cur = byteAt bytes bi
                if cur >= 0x20 && cur <= 0x7E then sb.Append(char cur) |> ignore
                elif cur = 0 then sb.Append(' ') |> ignore
                else sb.Append('.') |> ignore
                i <- i + 1
        sb.ToString()
    let mutable i = 0
    while i < len do
        out.WriteLine(formatLine i) |> ignore
        i <- i + kBytesPerLine

let private netErrorName (c: Constants.Constants) (code: int) : string = Constants.keyWithValue c.NetError code

let private quicErrorName (c: Constants.Constants) (code: int) : string = Constants.keyWithValue c.QuicError code

let private quicRstStreamErrorName (c: Constants.Constants) (code: int) : string =
    Constants.keyWithValue c.QuicRstStreamError code

/// Port of getSymbolicString: the flag names whose bit is set, joined by " | ".
let private getSymbolicString (bitmask: int) (valueToName: Map<string, int>) (zeroName: string) : string =
    let matched =
        valueToName
        |> Map.toList
        |> List.filter (fun (_, v) -> bitmask &&& v <> 0)
        |> List.map fst
    if List.isEmpty matched then zeroName else String.concat " | " matched

/// Port of defaultWriteParameter (common special cases + JSON fallback).
let private defaultWriteParameter (c: Constants.Constants) (key: string) (value: obj) (out: ParameterOutputter) =
    if key = "headers" && Json.isArray value then
        out.WriteArrowIndentedLines(toStringArray value)
    elif key = "source_dependency" && Json.isObject value then
        let id = Json.tryNumber value "id" |> Option.defaultValue 0.0 |> int
        let typeId = Json.tryNumber value "type" |> Option.defaultValue 0.0 |> int
        out.WriteArrowKeyValueLink(key, string id + " (" + Constants.sourceTypeName c typeId + ")", "nv-source:" + string id)
    elif key = "net_error" && Json.isNumber value then
        let code = Json.toNumber value |> int
        out.WriteArrowKeyValue(key, string code + " (" + netErrorName c code + ")")
    elif key = "quic_error" && Json.isNumber value then
        let code = Json.toNumber value |> int
        out.WriteArrowKeyValue(key, string code + " (" + quicErrorName c code + ")")
    elif key = "quic_rst_stream_error" && Json.isNumber value then
        let code = Json.toNumber value |> int
        out.WriteArrowKeyValue(key, string code + " (" + quicRstStreamErrorName c code + ")")
    elif key = "quic_crypto_handshake_message" && Json.isString value then
        out.WriteArrowIndentedLines((Json.jsString value).Split('\n'))
    elif key = "load_flags" && Json.isNumber value then
        let n = Json.toNumber value |> int
        out.WriteArrowKeyValue(key, string n + " (" + getSymbolicString n c.LoadFlag (Constants.keyWithValue c.LoadFlag n) + ")")
    elif key = "load_state" && Json.isNumber value then
        let n = Json.toNumber value |> int
        out.WriteArrowKeyValue(key, string n + " (" + Constants.keyWithValue c.LoadState n + ")")
    elif key = "bytes" && Json.isString value then
        let bytes = tryParseBase64ToBytes (Json.jsString value)
        if isNull bytes then
            out.WriteArrowKeyJSONValue(key, value)
        elif out.ForSearch then
            writeBytesAsSearchableString bytes out
        else
            out.WriteArrowKey(key)
            writeHexString bytes out
    else
        out.WriteArrowKeyJSONValue(key, value)

/// Port of reformatHeaders: a headers object (non-array) becomes "key: value" lines.
let private reformatHeaders (paramsObj: obj) : obj =
    let headers = Json.get paramsObj "headers"
    if Json.isObject headers && not (Json.isArray headers) then
        let arr = Json.keys headers |> Array.map (fun k -> k + ": " + Json.jsString (Json.get headers k))
        let clone = shallowClone paramsObj
        setProp clone "headers" (box arr)
        clone
    else
        paramsObj

// --- Per-event-type parameter writers (port of the writeParamsFor* family) ---

let private writeIndentedMultiLineParam
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    (lines: string[])
    (paramName: string)
    =
    out.WriteArrowKey(paramName)
    out.WriteSpaceIndentedLines(8, lines)
    consumed.Add paramName |> ignore

let private writeCertificateParam
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    (certsContainer: obj)
    (paramName: string)
    =
    let certs = Json.get certsContainer "certificates"
    if Json.isArray certs then
        let lines =
            [| for i in 0 .. Json.length certs - 1 do
                   yield! (Json.jsString (Json.item certs i)).Split('\n') |]
        writeIndentedMultiLineParam out consumed lines paramName

let private writeParamsForRequestHeaders
    (_c: Constants.Constants)
    (p: obj)
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    =
    let line = Json.get p "line"
    let headers = Json.get p "headers"
    if Json.isString line && Json.isArray headers then
        let lineNoCrlf = (Json.jsString line).Replace("\r\n", "")
        out.WriteArrowIndentedLines(Array.append [| lineNoCrlf |] (toStringArray headers))
        consumed.Add "line" |> ignore
        consumed.Add "headers" |> ignore

let private writeParamsForCertificates
    (c: Constants.Constants)
    (p: obj)
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    =
    writeCertificateParam out consumed p "certificates"
    let verified = Json.get p "verified_cert"
    if Json.isObject verified then
        writeCertificateParam out consumed verified "verified_cert"
    match Json.tryString p "ocsp_response" with
    | Some s -> writeIndentedMultiLineParam out consumed (s.Split('\n')) "ocsp_response"
    | None -> ()
    match Json.tryString p "sct_list" with
    | Some s -> writeIndentedMultiLineParam out consumed (s.Split('\n')) "sct_list"
    | None -> ()
    match Json.tryNumber p "cert_status" with
    | Some n ->
        let code = int n
        out.WriteArrowKeyValue("cert_status", string code + " (" + getSymbolicString code c.CertStatusFlag "" + ")")
        consumed.Add "cert_status" |> ignore
    | None -> ()
    match Json.tryNumber p "verifier_flags" with
    | Some n ->
        let code = int n
        out.WriteArrowKeyValue("verifier_flags", string code + " (" + getSymbolicString code c.CertVerifierFlags "" + ")")
        consumed.Add "verifier_flags" |> ignore
    | None -> ()

let private writeParamsForCertVerifyProc
    (c: Constants.Constants)
    (p: obj)
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    =
    match Json.tryNumber p "cert_status" with
    | Some n ->
        let code = int n
        out.WriteArrowKeyValue("cert_status", string code + " (" + getSymbolicString code c.CertStatusFlag "" + ")")
        consumed.Add "cert_status" |> ignore
    | None -> ()
    match Json.tryNumber p "verify_flags" with
    | Some n ->
        let code = int n
        out.WriteArrowKeyValue("verify_flags", string code + " (" + getSymbolicString code c.CertVerifyFlags "" + ")")
        consumed.Add "verify_flags" |> ignore
    | None -> ()

let private writeParamsForCertVerifyProcPathBuilt
    (_c: Constants.Constants)
    (p: obj)
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    =
    match Json.tryString p "errors" with
    | Some s -> writeIndentedMultiLineParam out consumed (s.Split('\n')) "errors"
    | None -> ()

let private writeParamsForCheckedCertificates
    (_c: Constants.Constants)
    (p: obj)
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    =
    let cert = Json.get p "certificate"
    if Json.isObject cert then
        writeCertificateParam out consumed cert "certificate"

let private writeParamsForProxyConfigChanged
    (_c: Constants.Constants)
    (p: obj)
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    =
    let newConfig = Json.get p "new_config"
    if Json.isObject newConfig then
        let oldConfig = Json.get p "old_config"
        if Json.isObject oldConfig then
            out.WriteArrowKey("old_config")
            out.WriteSpaceIndentedLines(8, (ProxyFormatter.proxySettingsToString oldConfig).Split('\n'))
            consumed.Add "old_config" |> ignore
        out.WriteArrowKey("new_config")
        out.WriteSpaceIndentedLines(8, (ProxyFormatter.proxySettingsToString newConfig).Split('\n'))
        consumed.Add "new_config" |> ignore

let private writeParamsForCertVerifyProcPathBuildAttempt
    (c: Constants.Constants)
    (p: obj)
    (out: ParameterOutputter)
    (consumed: HashSet<string>)
    =
    match Json.tryNumber p "digest_policy" with
    | Some n ->
        let code = int n
        out.WriteArrowKeyValue("digest_policy", string code + " (" + Constants.keyWithValue c.CertPathBuilderDigestPolicy code + ")")
        consumed.Add "digest_policy" |> ignore
    | None -> ()

let private getParamWriter (typeName: string) =
    match typeName with
    | "HTTP_TRANSACTION_SEND_REQUEST_HEADERS"
    | "HTTP_TRANSACTION_SEND_TUNNEL_HEADERS"
    | "HTTP_CACHE_CALLER_REQUEST_HEADERS" -> Some writeParamsForRequestHeaders
    | "PROXY_CONFIG_CHANGED" -> Some writeParamsForProxyConfigChanged
    | "CERT_VERIFIER_JOB"
    | "SSL_CERTIFICATES_RECEIVED" -> Some writeParamsForCertificates
    | "CERT_VERIFY_PROC" -> Some writeParamsForCertVerifyProc
    | "CERT_VERIFY_PROC_PATH_BUILD_ATTEMPT" -> Some writeParamsForCertVerifyProcPathBuildAttempt
    | "CERT_VERIFY_PROC_PATH_BUILT" -> Some writeParamsForCertVerifyProcPathBuilt
    | "CERT_CT_COMPLIANCE_CHECKED"
    | "EV_CERT_CT_COMPLIANCE_CHECKED" -> Some writeParamsForCheckedCertificates
    | _ -> None

let private writeParameters (c: Constants.Constants) (event: Event) (out: ParameterOutputter) =
    match event.Params with
    | None -> ()
    | Some p0 ->
        let p = reformatHeaders p0
        let consumed = HashSet<string>()
        match getParamWriter (Constants.eventTypeName c event.Type) with
        | Some writer -> writer c p out consumed
        | None -> ()
        for k in Json.keys p do
            if not (consumed.Contains k) then
                defaultWriteParameter c k (Json.get p k) out

let private addRowWithTime (tp: TablePrinter) (eventTime: float) (startTime: float) =
    tp.AddRow()
    tp.AddCell("t=") |> ignore
    (tp.AddCell(string eventTime)).AlignRight <- true
    tp.AddCell(" [st=") |> ignore
    (tp.AddCell(string (eventTime - startTime))).AlignRight <- true
    tp.AddCell("] ") |> ignore

/// True if a BEGIN can be collapsed with its immediately-following END.
let private canCollapse (c: Constants.Constants) (groups: LogGrouper.GroupEntry[]) (b: LogGrouper.GroupEntry) : bool =
    LogGrouper.isBegin c b
    && (match b.EndIndex with
        | Some ei -> ei = b.Index + 1 && (b.Orig.Params.IsNone || groups.[ei].Orig.Params.IsNone)
        | None -> false)

let private getTextForEvent (c: Constants.Constants) (collapsible: bool) (g: LogGrouper.GroupEntry) : string =
    let prefix =
        if LogGrouper.isBegin c g && collapsible then " "
        elif LogGrouper.isBegin c g then "+"
        elif LogGrouper.isEnd c g then "-"
        else " "
    prefix + Constants.eventTypeName c g.Orig.Type

/// Builds the event-trace TablePrinter for a source.
let createLogEntryTablePrinter
    (c: Constants.Constants)
    (events: Event[])
    (sourceStartTicks: float)
    (baseTime: float)
    (logCreationTime: float option)
    (forSearch: bool)
    : TablePrinter =
    let groups = LogGrouper.createArrayFrom c events
    let tp = TablePrinter()
    let out = ParameterOutputter(tp, forSearch)

    if groups.Length = 0 then
        tp
    else
        let startTime = TimeUtil.convertTimeTicksToTime sourceStartTicks

        for g in groups do
            let collapseThisEnd =
                LogGrouper.isEnd c g
                && (match g.BeginIndex with
                    | Some bi -> canCollapse c groups groups.[bi]
                    | None -> false)

            if not collapseThisEnd then
                let entryTime = TimeUtil.convertTimeTicksToTime g.Orig.Time
                addRowWithTime tp (entryTime - baseTime) (startTime - baseTime)

                for _ in 1 .. g.Depth do
                    tp.AddCell("  ") |> ignore

                let mutable eventText = getTextForEvent c (canCollapse c groups g) g

                if LogGrouper.isBegin c g then
                    let dt =
                        match g.EndIndex with
                        | Some ei -> string (groups.[ei].Orig.Time - g.Orig.Time)
                        | None ->
                            match logCreationTime with
                            | Some lct -> string (lct - entryTime) + "+"
                            | None -> "?"
                    eventText <- eventText + "  [dt=" + dt + "]"

                (tp.AddCell(eventText)).AllowOverflow <- true

            match g.Orig.Params with
            | Some _ ->
                tp.SetNewRowCellIndent(5 + g.Depth)
                writeParameters c g.Orig out
                tp.SetNewRowCellIndent(0)
            | None -> ()

        let lastG = groups.[groups.Length - 1]
        let isSourceActive = lastG.Depth <> 0 || LogGrouper.isBegin c lastG

        match logCreationTime with
        | Some lct when isSourceActive -> addRowWithTime tp (lct - baseTime) (startTime - baseTime)
        | _ -> ()

        tp
