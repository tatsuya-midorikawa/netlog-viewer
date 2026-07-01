/// Port of import_view.js (the loaded-log metadata pane): export date, capture
/// mode, Chrome build info, OS, command line, and user comments.
module Netlog.Webview.ImportView

open Fable.Core.JsInterop
open Netlog.Core
open Netlog.Webview.Dom
open Netlog.Webview.View

let tabId = "import"
let tabName = "Import"

type ImportView(root: Element, post: obj -> unit) =
    inherit ViewBase(root)

    override _.OnLoadLogFinish(model: obj) : bool =
        clear root
        let clientInfo = Json.get model "clientInfo"
        let constants = Json.get model "constants"
        let str (o: obj) (k: string) = Json.tryString o k |> Option.defaultValue ""

        let addRow (label: string) (value: string) =
            let row = addNode root "div"
            row.className <- "nv-info-row"
            (addNodeWithText row "span" label).className <- "nv-label"
            (addNodeWithText row "span" value).className <- "nv-value"

        /// Same as addRow, plus a "Copy" button for fields long/free-form enough
        /// that selecting them precisely by hand is annoying (command line, comments).
        let addRowWithCopy (label: string) (value: string) =
            let row = addNode root "div"
            row.className <- "nv-info-row"
            (addNodeWithText row "span" label).className <- "nv-label"
            (addNodeWithText row "span" value).className <- "nv-value"
            if value <> "" then
                let btn = addNode row "button"
                btn.setAttribute ("type", "button")
                btn.className <- "nv-report-toggle"
                btn.setAttribute ("aria-label", "Copy " + label.TrimEnd(':') + " to clipboard")
                btn.textContent <- "Copy"
                btn.addEventListener (
                    "click",
                    fun e ->
                        preventDefault e
                        post (createObj [ "type" ==> "copyToClipboard"; "text" ==> value ])
                        btn.textContent <- "Copied!"
                        setTimeout (fun () -> btn.textContent <- "Copy") 1200)

        let numericDate = Json.tryNumber model "numericDate" |> Option.defaultValue 0.0
        addRow "Export date:" (TimeUtil.dateToStringMs numericDate)
        addRow "Capture mode:" (let m = str constants "logCaptureMode" in if m = "" then "(Not Recorded)" else m)
        addRow "Name:" (str clientInfo "name")
        addRow "Version:" (str clientInfo "version")
        addRow "Official:" (str clientInfo "official")
        addRow "CL:" (str clientInfo "cl")
        addRow "Version mod:" (str clientInfo "version_mod")
        addRow "OS:" (str clientInfo "os_type")
        addRowWithCopy "Command line:" (str clientInfo "command_line")

        let comments = Json.tryString model "userComments" |> Option.defaultValue ""
        if comments <> "" then
            addRowWithCopy "User comments:" comments

        true

let create (post: obj -> unit) : ImportView =
    let root = createElement "div"
    root.className <- "nv-view nv-import"
    ImportView(root, post)
