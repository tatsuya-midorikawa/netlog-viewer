/// Port of time_util.js. Converts NetLog "time ticks" (ms) to wall-clock time and
/// formats dates. Module-level mutable offset/baseTime mirror the original globals;
/// they are set once per loaded log.
module Netlog.Core.TimeUtil

open Fable.Core

let mutable private timeTickOffset: float = 0.0
let mutable private baseTime: float = 0.0

/// offset may be a number or numeric string; `$0 - 0` coerces like the original.
let setTimeTickOffset (offset: float) : unit = timeTickOffset <- offset

let getTimeTickOffset () : float = timeTickOffset

/// timeTicks is the numeric value of a (string) tick count.
let convertTimeTicksToTime (timeTicks: float) : float = timeTickOffset + timeTicks

[<Emit("Date.now()")>]
let getCurrentTime () : float = jsNative

let getCurrentTimeTicks () : float = getCurrentTime () - timeTickOffset

let setBaseTime (firstEventTime: float) : unit = baseTime <- firstEventTime

let getBaseTime () : float = baseTime

let clearBaseTime () : unit = baseTime <- 0.0

let isBaseTimeSet () : bool = baseTime <> 0.0

let convertTimeTicksToRelativeTime (timeTicks: float) : float =
    convertTimeTicksToTime timeTicks - baseTime

// --- Date formatting (local time, matching the original exactly) ---

[<Emit("new Date($0)")>]
let private newDate (ms: float) : obj = jsNative

[<Emit("$0.getFullYear()")>]
let private getFullYear (d: obj) : int = jsNative

[<Emit("$0.getMonth()")>]
let private getMonth (d: obj) : int = jsNative

[<Emit("$0.getDate()")>]
let private getDate (d: obj) : int = jsNative

[<Emit("$0.getHours()")>]
let private getHours (d: obj) : int = jsNative

[<Emit("$0.getMinutes()")>]
let private getMinutes (d: obj) : int = jsNative

[<Emit("$0.getSeconds()")>]
let private getSeconds (d: obj) : int = jsNative

[<Emit("$0.getMilliseconds()")>]
let private getMilliseconds (d: obj) : int = jsNative

let private zeroPad (num: int) (len: int) : string =
    let mutable s = string num
    while s.Length < len do
        s <- "0" + s
    s

/// Formats a wall-clock time (ms since epoch) as "YYYY-MM-DD HH:MM:SS.mmm".
let dateToStringMs (ms: float) : string =
    let d = newDate ms
    let dateStr =
        string (getFullYear d) + "-" + zeroPad (getMonth d + 1) 2 + "-" + zeroPad (getDate d) 2
    let timeStr =
        zeroPad (getHours d) 2 + ":" + zeroPad (getMinutes d) 2 + ":"
        + zeroPad (getSeconds d) 2 + "." + zeroPad (getMilliseconds d) 3
    dateStr + " " + timeStr
