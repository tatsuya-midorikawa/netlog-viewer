/// Shared two-pane vertical splitter used by views that need a resizable left
/// pane and flexible right pane.
module Netlog.Webview.ResizableVerticalSplitView

open Netlog.Core
open Netlog.Webview.Dom

type SplitView(parent: Element, minLeftWidth: float) =
    let root = addNode parent "div"
    let leftPane = addNode root "div"
    let splitter = addNode root "div"
    let rightPane = addNode root "div"
    let mutable dragging = false
    let mutable startX = 0.0
    let mutable startWidth = 0.0

    do
        root.className <- "nv-split"
        leftPane.className <- "nv-split-left"
        splitter.className <- "nv-splitter"
        rightPane.className <- "nv-split-right"

        splitter.addEventListener(
            "mousedown",
            fun e ->
                preventDefault e
                dragging <- true
                startX <- Json.toNumber (Json.get e "clientX")
                startWidth <- float leftPane.clientWidth)

        window.addEventListener(
            "mousemove",
            fun e ->
                if dragging then
                    let x = Json.toNumber (Json.get e "clientX")
                    let newWidth = max minLeftWidth (startWidth + (x - startX))
                    leftPane.style.width <- string (int newWidth) + "px")

        window.addEventListener("mouseup", fun _ -> dragging <- false)

    member _.Root = root
    member _.LeftPane = leftPane
    member _.RightPane = rightPane
    member _.Splitter = splitter