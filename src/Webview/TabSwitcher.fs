/// Port of tab_switcher_view.js. Manages the sidebar tab links and the active
/// content view. Uses internal state (not window.location.hash, which is special
/// inside a webview).
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

type TabSwitcher(tabListNode: Element, contentNode: Element) =
    let tabs = Dictionary<string, Tab>()
    let order = ResizeArray<string>()
    let mutable activeId: string option = None

    member this.AddTab(id: string, name: string, view: ViewBase) =
        let link = addNodeWithText tabListNode "a" name
        link.className <- "nv-tab-link"
        link.setAttribute ("href", "#")
        link.addEventListener ("click", (fun e -> preventDefault e; this.SwitchToTab id))
        contentNode.appendChild view.Root |> ignore
        view.Show false
        tabs.[id] <- { Id = id; Name = name; View = view; Link = link; Visible = true }
        order.Add id

    member _.GetView(id: string) : ViewBase = tabs.[id].View

    member this.SwitchToTab(id: string) =
        match activeId with
        | Some old when tabs.ContainsKey old ->
            tabs.[old].Link.classList.remove "selected"
            tabs.[old].View.Show false
        | _ -> ()

        activeId <- Some id
        tabs.[id].Link.classList.add "selected"
        tabs.[id].View.Show true

    member this.ShowTabLink(id: string, visible: bool) =
        let t = tabs.[id]
        t.Visible <- visible
        setNodeDisplay t.Link visible

        if activeId = Some id && not visible then
            match order |> Seq.tryFind (fun i -> tabs.[i].Visible) with
            | Some firstVisible -> this.SwitchToTab firstVisible
            | None -> ()

    member _.FirstVisible: string option =
        order |> Seq.tryFind (fun i -> tabs.[i].Visible)
