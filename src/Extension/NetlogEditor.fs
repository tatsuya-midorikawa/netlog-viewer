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

let private handleMessage (webview: Webview) (document: CustomDocument) (msg: obj) : unit =
    let msgType: string = getProp msg "type"
    if msgType = "ready" then
        promiseThen (workspace.fs.readFile document.uri) (fun bytes ->
            let fileName = basename document.uri.path
            let b = box bytes
            try
                let rawBytes = if Node.isGzip b then Node.gunzipSync b else b
                let text = Node.decodeUtf8 rawBytes
                match LogParser.loadLogFile text with
                | Ok log -> webview.postMessage (Wire.build fileName log) |> ignore
                | Error err ->
                    webview.postMessage (createObj [ "type" ==> "error"; "message" ==> err ]) |> ignore
            with ex ->
                webview.postMessage (createObj [ "type" ==> "error"; "message" ==> ("Failed to load: " + string ex) ])
                |> ignore)

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
    webview.onDidReceiveMessage(handleMessage webview document) |> ignore

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
