/// Port of tab_switcher_view.js. Manages the sidebar tab links and the active
/// content view. Uses internal state (not window.location.hash, which is special
/// inside a webview).
///
/// Accessible as a WAI-ARIA tablist: each link is role="tab" with a roving tabIndex
/// (only the active tab is a Tab-stop; ArrowUp/Down/Left/Right/Home/End move within
/// it, matching the "automatic activation" tab pattern), each view root is a matching
/// role="tabpanel". Tabs hidden because a log has no data for them (`ShowTabLink`)
/// are noted via a small "N tabs hidden" hint appended after the last tab link.
module Netlog.Webview.TabSwitcher

open System.Collections.Generic
open Netlog.Webview.Dom
open Netlog.Webview.View

type private Tab =
    { Id: string
      Name: string
      View: ViewBase
      Link: Element
      mutable Visible: bool }

type TabSwitcher(tabListNode: Element, contentNode: Element, onActivate: string -> unit) as this =
    let tabs = Dictionary<string, Tab>()
    let order = ResizeArray<string>()
    let mutable activeId: string option = None
    let mutable hintNode: Element option = None

    do
        tabListNode.addEventListener(
            "keydown",
            fun e ->
                match eventKey e with
                | "ArrowDown" | "ArrowRight" -> preventDefault e; this.MoveFocus 1
                | "ArrowUp" | "ArrowLeft" -> preventDefault e; this.MoveFocus -1
                | "Home" -> preventDefault e; this.FocusFirst()
                | "End" -> preventDefault e; this.FocusLast()
                | _ -> ())

    member this.AddTab(id: string, name: string, view: ViewBase) =
        let link = addNodeWithText tabListNode "a" name
        link.className <- "nv-tab-link"
        link.id <- "nv-tab-" + id
        link.setAttribute ("href", "#")
        link.setAttribute ("title", name)
        link.setAttribute ("role", "tab")
        link.setAttribute ("aria-selected", "false")
        link.setAttribute ("aria-controls", "nv-tabpanel-" + id)
        // Roving tabIndex: only the first tab is a Tab-stop until something is
        // actually selected (SwitchToTab below then takes over).
        link.tabIndex <- (if order.Count = 0 then 0 else -1)
        link.addEventListener ("click", (fun e -> preventDefault e; this.SwitchToTab id))

        view.Root.id <- "nv-tabpanel-" + id
        view.Root.setAttribute ("role", "tabpanel")
        view.Root.setAttribute ("aria-labelledby", "nv-tab-" + id)
        contentNode.appendChild view.Root |> ignore
        view.Show false

        tabs.[id] <- { Id = id; Name = name; View = view; Link = link; Visible = true }
        order.Add id

    member _.GetView(id: string) : ViewBase = tabs.[id].View

    /// The currently active tab's id, if any (nothing is active before the first load).
    member _.ActiveTabId: string option = activeId

    member this.SwitchToTab(id: string) =
        match activeId with
        | Some old when tabs.ContainsKey old ->
            tabs.[old].Link.classList.remove "selected"
            tabs.[old].Link.setAttribute ("aria-selected", "false")
            tabs.[old].Link.tabIndex <- -1
            tabs.[old].View.Show false
        | _ -> ()

        activeId <- Some id
        let t = tabs.[id]
        t.Link.classList.add "selected"
        t.Link.setAttribute ("aria-selected", "true")
        t.Link.tabIndex <- 0
        t.View.Show true
        onActivate id

    /// Switches to `id` only if it is a known, currently-visible tab; returns whether
    /// it did. Used to restore a persisted active tab without risking a lookup
    /// failure on a stale/hidden id (e.g. from an older extension version's state).
    member this.TrySwitchToTab(id: string) : bool =
        match tabs.TryGetValue id with
        | true, t when t.Visible ->
            this.SwitchToTab id
            true
        | _ -> false

    member private this.FocusAndActivate(id: string) =
        this.SwitchToTab id
        tabs.[id].Link.focus ()

    member private this.MoveFocus(delta: int) =
        match activeId with
        | None -> ()
        | Some cur ->
            let visibleOrder = order |> Seq.filter (fun i -> tabs.[i].Visible) |> Seq.toArray
            if visibleOrder.Length > 0 then
                match Array.tryFindIndex ((=) cur) visibleOrder with
                | Some i ->
                    let next = ((i + delta) % visibleOrder.Length + visibleOrder.Length) % visibleOrder.Length
                    this.FocusAndActivate visibleOrder.[next]
                | None -> ()

    member private this.FocusFirst() =
        match order |> Seq.tryFind (fun i -> tabs.[i].Visible) with
        | Some id -> this.FocusAndActivate id
        | None -> ()

    member private this.FocusLast() =
        match order |> Seq.filter (fun i -> tabs.[i].Visible) |> Seq.tryLast with
        | Some id -> this.FocusAndActivate id
        | None -> ()

    /// Creates (on first use, so it lands after every tab link already added) or
    /// updates the "N tabs hidden (no data)" hint at the bottom of the tab list.
    member private _.UpdateHint() =
        let hiddenCount = order |> Seq.filter (fun i -> not tabs.[i].Visible) |> Seq.length

        let node =
            match hintNode with
            | Some n -> n
            | None ->
                let n = addNode tabListNode "div"
                n.className <- "nv-tablist-hint"
                hintNode <- Some n
                n

        if hiddenCount > 0 then
            node.textContent <-
                string hiddenCount + (if hiddenCount = 1 then " tab hidden (no data)" else " tabs hidden (no data)")
            setNodeDisplay node true
        else
            setNodeDisplay node false

    member this.ShowTabLink(id: string, visible: bool) =
        let t = tabs.[id]
        t.Visible <- visible
        setNodeDisplay t.Link visible

        if activeId = Some id && not visible then
            match order |> Seq.tryFind (fun i -> tabs.[i].Visible) with
            | Some firstVisible -> this.SwitchToTab firstVisible
            | None -> ()

        this.UpdateHint()

    member _.FirstVisible: string option =
        order |> Seq.tryFind (fun i -> tabs.[i].Visible)
