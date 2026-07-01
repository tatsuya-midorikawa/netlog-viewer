/// Builds the page chrome in F# (top bar + sidebar tab list + content area),
/// replacing the original's static netlog_viewer.html / top_bar_view.html layout.
module Netlog.Webview.Shell

open Netlog.Webview.Dom

type Page =
    { TopBar: Element
      FileName: Element
      Banner: Element
      TabList: Element
      Content: Element }

let build () : Page =
    let app = el "app"
    app.innerHTML <- ""
    app.className <- "nv-app"

    let topBar = addNode app "div"
    topBar.className <- "nv-topbar"
    let fileName = addNodeWithText topBar "span" "No log loaded."
    fileName.className <- "nv-filename"

    // Load status / warning banner (hidden until there is something to report).
    let banner = addNode app "div"
    banner.className <- "nv-banner"
    banner.style.display <- "none"

    let body = addNode app "div"
    body.className <- "nv-body"

    let tabList = addNode body "div"
    tabList.className <- "nv-tablist"

    let content = addNode body "div"
    content.className <- "nv-content"

    { TopBar = topBar
      FileName = fileName
      Banner = banner
      TabList = tabList
      Content = content }
