/// Phase 0 custom editor: reads the opened file in the Extension Host and posts a
/// summary to the Webview. Real parsing + the full wire model arrive in Phase 1-3.
module Netlog.Extension.NetlogEditor

open Fable.Core
open Fable.Core.JsInterop
open Netlog.Core
open Netlog.Extension.Vscode

[<Emit("$0.then($1)")>]
let private promiseThen (p: JS.Promise<'a>) (f: 'a -> unit) : unit = jsNative

[<Emit("$0[$1]")>]
let private getProp (o: obj) (key: string) : 'a = jsNative

[<Emit("Math.random().toString(36).slice(2) + Math.random().toString(36).slice(2)")>]
let private makeNonce () : string = jsNative

let private basename (path: string) : string =
    let parts = path.Split('/')
    parts.[parts.Length - 1]

let private buildHtml (webview: Webview) (nonce: string) (scriptUri: string) (styleUri: string) : string =
    let csp =
        $"default-src 'none'; img-src {webview.cspSource} data:; "
        + $"style-src {webview.cspSource} 'unsafe-inline'; "
        + $"script-src 'nonce-{nonce}'; font-src {webview.cspSource};"

    $"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta http-equiv="Content-Security-Policy" content="{csp}" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <link rel="stylesheet" href="{styleUri}" />
  <title>Netlog Viewer</title>
</head>
<body>
  <div id="app">Loading…</div>
  <script nonce="{nonce}" src="{scriptUri}"></script>
</body>
</html>"""

let private postError (webview: Webview) (message: string) : unit =
    webview.postMessage (createObj [ "type" ==> "error"; "message" ==> message ]) |> ignore

/// Reads the configured event cap (absent/non-number -> default). Files with more
/// events are truncated with a warning so memory stays bounded.
let private readMaxEvents () : int =
    let v = (workspace.getConfiguration "netlogViewer").get ("maxEvents", box 2000000)
    if Json.isNumber v then int (Json.toNumber v) else 2000000

/// Reads the per-source detail-event cap (absent/non-number -> default).
let private readMaxSourceDetailEvents () : int =
    let v = (workspace.getConfiguration "netlogViewer").get ("maxSourceDetailEvents", box 50000)
    if Json.isNumber v then int (Json.toNumber v) else 50000

/// Fallback loader for non-local (virtual) documents: read the whole buffer at once,
/// then hand the parsed log to `onLog`.
let private loadViaBuffer (webview: Webview) (uri: Uri) (onLog: LogParser.LoadedLog -> unit) : unit =
    promiseThen (workspace.fs.readFile uri) (fun bytes ->
        let b = box bytes
        try
            let rawBytes = if Node.isGzip b then Node.gunzipSync b else b
            let text = Node.decodeUtf8 rawBytes
            match LogParser.loadLogFile text with
            | Ok log -> onLog log
            | Error err -> postError webview err
        with ex ->
            postError webview ("Failed to load: " + string ex))

let private handleMessage
    (webview: Webview)
    (document: CustomDocument)
    (logHolder: LogParser.LoadedLog option ref)
    (msg: obj)
    : unit =
    let post (m: obj) : unit = webview.postMessage m |> ignore
    let msgType: string = getProp msg "type"

    match msgType with
    | "ready" ->
        let uri = document.uri
        let fileName = basename uri.path

        let onLog (log: LogParser.LoadedLog) : unit =
            logHolder.Value <- Some log
            Wire.postLoad post fileName log

        if uri.scheme = "file" then
            // Large files: stream from disk so we never build one giant string.
            StreamLoader.load uri.fsPath (readMaxEvents ()) onLog (postError webview)
        else
            // Virtual / non-local documents have no local path to stream; fall back to
            // the whole-buffer read (best effort, bounded by VS Code's own limits).
            loadViaBuffer webview uri onLog

    | "getSourceEvents" ->
        // On-demand details: return just the selected source's events (capped).
        match logHolder.Value with
        | Some log ->
            let id = Json.tryNumber msg "id" |> Option.defaultValue -1.0 |> int
            match log.SourceIndex.TryGetValue id with
            | true, se -> post (Wire.sourceEventsMessage id se.Entries se.Entries.Count (readMaxSourceDetailEvents ()))
            | _ -> ()
        | None -> ()

    | _ -> ()

let private resolve (context: ExtensionContext) (document: CustomDocument) (panel: WebviewPanel) : unit =
    let webview = panel.webview
    webview.options <-
        !!(createObj [
            "enableScripts" ==> true
            "localResourceRoots" ==> ResizeArray [ context.extensionUri ]
        ])

    let scriptUri =
        webview.asWebviewUri(UriStatic.joinPath(context.extensionUri, "dist", "webview.js"))
        |> uriToString

    let styleUri =
        webview.asWebviewUri(UriStatic.joinPath(context.extensionUri, "media", "style.css"))
        |> uriToString

    let nonce = makeNonce ()
    webview.html <- buildHtml webview nonce scriptUri styleUri
    let logHolder: LogParser.LoadedLog option ref = ref None
    webview.onDidReceiveMessage(handleMessage webview document logHolder) |> ignore
    // Release the retained log (which can be large) when the editor is closed.
    panel.onDidDispose(fun () -> logHolder.Value <- None) |> ignore

let create (context: ExtensionContext) : CustomReadonlyEditorProvider =
    { new CustomReadonlyEditorProvider with
        member _.openCustomDocument(uri, _openContext, _token) =
            createObj [ "uri" ==> uri ]

        member _.resolveCustomEditor(document, panel, _token) =
            resolve context document panel
            box () }

let openFileCommand () : unit =
    let options =
        createObj [
            "canSelectMany" ==> false
            "openLabel" ==> "Open NetLog"
        ]
    promiseThen (window.showOpenDialog options) (fun (uris: ResizeArray<Uri>) ->
        if not (isNull (box uris)) && uris.Count > 0 then
            commands.executeCommand("vscode.openWith", box uris.[0], box "netlogViewer.editor")
            |> ignore)
