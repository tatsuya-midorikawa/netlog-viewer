/// Port of log_grouper.js (LogGroupEntry). Wraps a source's events to expose
/// BEGIN/END matching and nesting depth, used by the (later) log painter.
module Netlog.Core.LogGrouper

open System.Collections.Generic
open Netlog.Core.Constants
open Netlog.Core.Model

type GroupEntry =
    { Orig: Event
      Index: int
      mutable Depth: int
      mutable ParentIndex: int option
      mutable BeginIndex: int option
      mutable EndIndex: int option }

let isBegin (c: Constants) (g: GroupEntry) : bool =
    match c.PhaseBegin with
    | Some pb -> g.Orig.Phase = pb
    | None -> false

let isEnd (c: Constants) (g: GroupEntry) : bool =
    match c.PhaseEnd with
    | Some pe -> g.Orig.Phase = pe
    | None -> false

/// Port of LogGroupEntry.createArrayFrom.
let createArrayFrom (c: Constants) (origEntries: Event[]) : GroupEntry[] =
    let grouped =
        origEntries
        |> Array.mapi (fun i e ->
            { Orig = e
              Index = i
              Depth = 0
              ParentIndex = None
              BeginIndex = None
              EndIndex = None })

    let parentStack = List<GroupEntry>()

    let findParentIndex (eventType: int) : int =
        let mutable res = -1
        let mutable i = parentStack.Count - 1
        while i >= 0 && res = -1 do
            if parentStack.[i].Orig.Type = eventType then
                res <- i
            i <- i - 1
        res

    for g in grouped do
        if isEnd c g then
            let parentIndex = findParentIndex g.Orig.Type
            if parentIndex <> -1 then
                g.BeginIndex <- Some parentStack.[parentIndex].Index
                // Terminate all open BEGINs up to and including parentIndex.
                while parentIndex < parentStack.Count do
                    let p = parentStack.[parentStack.Count - 1]
                    parentStack.RemoveAt(parentStack.Count - 1)
                    p.EndIndex <- Some g.Index

        if parentStack.Count > 0 then
            g.ParentIndex <- Some parentStack.[parentStack.Count - 1].Index

        if isBegin c g then
            parentStack.Add g

    // Compute nesting depth by walking the parent chain (port of getDepth).
    for g in grouped do
        let mutable depth = 0
        let mutable p = g.ParentIndex
        while p.IsSome do
            depth <- depth + 1
            p <- grouped.[p.Value].ParentIndex
        g.Depth <- depth

    grouped
