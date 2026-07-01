/// Minimal, hand-written bindings for the subset of the VS Code extension API
/// used by the Netlog Viewer Extension Host. Per the project's no-external-runtime-
/// libraries policy, we do not depend on generated TypeScript typings.
module Netlog.Extension.Vscode

open Fable.Core
open Fable.Core.JsInterop

type Uri =
    abstract path: string
    abstract fsPath: string
    abstract scheme: string

type Disposable =
    abstract dispose: unit -> unit

type WebviewOptions =
    abstract enableScripts: bool with get, set
    abstract localResourceRoots: ResizeArray<Uri> with get, set

type Webview =
    abstract html: string with get, set
    abstract options: WebviewOptions with get, set
    abstract cspSource: string
    abstract asWebviewUri: Uri -> Uri
    abstract postMessage: obj -> JS.Promise<bool>
    abstract onDidReceiveMessage: (obj -> unit) -> Disposable

type WebviewPanel =
    abstract webview: Webview
    abstract onDidDispose: (unit -> unit) -> Disposable

type CustomDocument =
    abstract uri: Uri

type ExtensionContext =
    abstract subscriptions: ResizeArray<Disposable>
    abstract extensionUri: Uri

/// Implemented as an F# object expression and handed to registerCustomEditorProvider.
type CustomReadonlyEditorProvider =
    abstract openCustomDocument: Uri * obj * obj -> obj
    abstract resolveCustomEditor: CustomDocument * WebviewPanel * obj -> obj

type IWindow =
    abstract registerCustomEditorProvider: string * obj * obj -> Disposable
    abstract showErrorMessage: string -> JS.Promise<obj>
    abstract showOpenDialog: obj -> JS.Promise<ResizeArray<Uri>>

type ICommands =
    abstract registerCommand: string * (obj -> unit) -> Disposable
    abstract executeCommand: string * obj * obj -> JS.Promise<obj>

type IFileSystem =
    abstract readFile: Uri -> JS.Promise<JS.Uint8Array>

type IWorkspaceConfiguration =
    /// `get(section, default)` — returns the configured value or the default.
    abstract get: string * obj -> obj

type IWorkspace =
    abstract fs: IFileSystem
    abstract getConfiguration: string -> IWorkspaceConfiguration

type IUriStatic =
    abstract joinPath: Uri * string * string -> Uri

[<Import("window", "vscode")>]
let window: IWindow = jsNative

[<Import("commands", "vscode")>]
let commands: ICommands = jsNative

[<Import("workspace", "vscode")>]
let workspace: IWorkspace = jsNative

[<Import("Uri", "vscode")>]
let UriStatic: IUriStatic = jsNative

[<Emit("$0.toString()")>]
let uriToString (uri: Uri) : string = jsNative
