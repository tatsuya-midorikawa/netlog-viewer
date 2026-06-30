/// Port of proxySettingsToString from log_view_painter.js. Pure (operates on the
/// raw proxy-config object), reused by the Proxy view and the PROXY_CONFIG_CHANGED
/// parameter writer.
module Netlog.Core.ProxyFormatter

open Netlog.Core

let private getProxyListString (proxies: obj) : string =
    if Json.isArray proxies then
        let arr = Array.init (Json.length proxies) (fun i -> Json.jsString (Json.item proxies i))
        let listString = String.concat ", " arr
        if arr.Length > 1 then "[" + listString + "]" else listString
    else
        Json.jsString proxies

let private indentLines (start: string) (lines: string list) : string =
    let pad = String.replicate start.Length " "
    start + String.concat ("\n" + pad) lines

let proxySettingsToString (config: obj) : string =
    if not (Json.isObject config) then
        ""
    else
        let modes = ResizeArray<string list>()

        if Json.isTruthy (Json.get config "override_rules") then
            let jsonLines = (Json.stringifyPretty (Json.get config "override_rules")).Split('\n') |> List.ofArray
            modes.Add("Override rules:" :: jsonLines)

        if Json.isTruthy (Json.get config "auto_detect") then
            modes.Add [ "Auto-detect" ]

        if Json.isTruthy (Json.get config "pac_url") then
            modes.Add [ "PAC script: " + Json.jsString (Json.get config "pac_url") ]

        let singleProxy = Json.get config "single_proxy"
        let perScheme = Json.get config "proxy_per_scheme"

        if Json.isTruthy singleProxy || Json.isTruthy perScheme then
            let lines = ResizeArray<string>()

            if Json.isTruthy singleProxy then
                lines.Add("Proxy server: " + getProxyListString singleProxy)
            elif Json.isObject perScheme then
                for scheme in Json.keys perScheme do
                    if scheme <> "fallback" then
                        lines.Add("Proxy server for " + scheme.ToUpper() + ": " + getProxyListString (Json.get perScheme scheme))
                let fallback = Json.get perScheme "fallback"
                if Json.isTruthy fallback then
                    lines.Add("Proxy server for everything else: " + getProxyListString fallback)

            let bypass = Json.get config "bypass_list"
            if Json.isArray bypass then
                lines.Add(if Json.isTruthy (Json.get config "reverse_bypass") then "Reversed bypass list: " else "Bypass list: ")
                for i in 0 .. Json.length bypass - 1 do
                    lines.Add("  " + Json.jsString (Json.item bypass i))

            modes.Add(List.ofSeq lines)

        let result = ResizeArray<string>()

        if modes.Count < 1 then
            result.Add "Use DIRECT connections."
        elif modes.Count = 1 then
            result.Add(String.concat "\n" modes.[0])
        else
            for i in 0 .. modes.Count - 1 do
                result.Add(indentLines ("(" + string (i + 1) + ") ") modes.[i])

        match Json.tryString config "source" with
        | Some src when src <> "UNKNOWN" -> result.Add("Source: " + src)
        | _ -> ()

        String.concat "\n" result
