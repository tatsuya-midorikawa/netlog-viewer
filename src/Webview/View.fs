/// Port of view.js (View / DivView). Base class for all tab views.
module Netlog.Webview.View

open Netlog.Webview.Dom

[<AbstractClass>]
type ViewBase(root: Element) =
    member val IsVisible = false with get, set
    member _.Root: Element = root

    /// Called before events are loaded; lets a view clear prior state.
    abstract OnLoadLogStart: obj -> unit
    default _.OnLoadLogStart(_: obj) = ()

    /// Final load step; return true to show the tab, false to hide it.
    abstract OnLoadLogFinish: obj -> bool
    default _.OnLoadLogFinish(_: obj) = false

    /// Delivers a source's events requested on demand (id, wire events array). Only
    /// the Events view acts on this; other views ignore it.
    abstract OnSourceEvents: int * obj -> unit
    default _.OnSourceEvents(_: int, _: obj) = ()

    abstract Show: bool -> unit

    default this.Show(visible: bool) =
        this.IsVisible <- visible
        setNodeDisplay root visible
