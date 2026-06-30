/// Extension Host entry point. Fable compiles `activate`/`deactivate` to module
/// exports; esbuild then wraps them as CommonJS exports for the VS Code runtime.
module Netlog.Extension.Extension

open Fable.Core.JsInterop
open Netlog.Extension.Vscode

let activate (context: ExtensionContext) : unit =
    let provider = NetlogEditor.create context

    let registerOptions =
        createObj [
            "webviewOptions" ==> createObj [ "retainContextWhenHidden" ==> true ]
            "supportsMultipleEditorsPerDocument" ==> false
        ]

    window.registerCustomEditorProvider("netlogViewer.editor", provider, registerOptions)
    |> context.subscriptions.Add

    commands.registerCommand("netlogViewer.openFile", fun _ -> NetlogEditor.openFileCommand ())
    |> context.subscriptions.Add

let deactivate () : unit = ()
