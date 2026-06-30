/// Port of the constants handling in main.js (areValidConstants / ConstantsObserver)
/// and net_constants.js. Decodes the loaded log's `constants` block into typed maps.
module Netlog.Core.Constants

open Netlog.Core

type Constants =
    { Raw: obj
      EventTypes: Map<string, int>
      EventTypeNames: Map<int, string>
      EventPhase: Map<string, int>
      SourceTypes: Map<string, int>
      SourceTypeNames: Map<int, string>
      NetError: Map<string, int>
      LoadFlag: Map<string, int>
      LoadState: Map<string, int>
      CertStatusFlag: Map<string, int>
      CertVerifierFlags: Map<string, int>
      CertVerifyFlags: Map<string, int>
      QuicError: Map<string, int>
      QuicRstStreamError: Map<string, int>
      CertPathBuilderDigestPolicy: Map<string, int>
      TimeTickOffset: float
      LogFormatVersion: int
      ClientInfo: obj
      LogCaptureMode: string option
      PhaseBegin: int option
      PhaseEnd: int option
      PhaseNone: int option
      ValidPhaseValues: Set<int> }

/// Port of main.js areValidConstants.
let areValid (c: obj) : bool =
    Json.isObject c
    && Json.isObject (Json.get c "logEventTypes")
    && Json.isObject (Json.get c "clientInfo")
    && Json.isObject (Json.get c "logEventPhase")
    && Json.isObject (Json.get c "logSourceType")
    && Json.isObject (Json.get c "loadFlag")
    && Json.isObject (Json.get c "netError")
    && Json.isObject (Json.get c "addressFamily")
    && Json.isNetLogNumber (Json.get c "timeTickOffset")
    && Json.isNumber (Json.get c "logFormatVersion")

let decode (c: obj) : Constants =
    let eventTypes = Json.toIntMap (Json.get c "logEventTypes")
    let sourceTypes = Json.toIntMap (Json.get c "logSourceType")
    let phase = Json.toIntMap (Json.get c "logEventPhase")
    let netError = Json.toIntMap (Json.get c "netError")
    let loadFlag = Json.toIntMap (Json.get c "loadFlag")
    let loadState = Json.toIntMap (Json.get c "loadState")
    let certStatusFlag = Json.toIntMap (Json.get c "certStatusFlag")
    let certVerifierFlags = Json.toIntMap (Json.get c "certVerifierFlags")
    let certVerifyFlags = Json.toIntMap (Json.get c "certVerifyFlags")
    let quicError = Json.toIntMap (Json.get c "quicError")
    let quicRstStreamError = Json.toIntMap (Json.get c "quicRstStreamError")
    let certPathBuilderDigestPolicy = Json.toIntMap (Json.get c "certPathBuilderDigestPolicy")

    { Raw = c
      EventTypes = eventTypes
      EventTypeNames = Json.invertMap eventTypes
      EventPhase = phase
      SourceTypes = sourceTypes
      SourceTypeNames = Json.invertMap sourceTypes
      NetError = netError
      LoadFlag = loadFlag
      LoadState = loadState
      CertStatusFlag = certStatusFlag
      CertVerifierFlags = certVerifierFlags
      CertVerifyFlags = certVerifyFlags
      QuicError = quicError
      QuicRstStreamError = quicRstStreamError
      CertPathBuilderDigestPolicy = certPathBuilderDigestPolicy
      TimeTickOffset = Json.toNumber (Json.get c "timeTickOffset")
      LogFormatVersion = int (Json.toNumber (Json.get c "logFormatVersion"))
      ClientInfo = Json.get c "clientInfo"
      LogCaptureMode = Json.tryString c "logCaptureMode"
      PhaseBegin = Map.tryFind "PHASE_BEGIN" phase
      PhaseEnd = Map.tryFind "PHASE_END" phase
      PhaseNone = Map.tryFind "PHASE_NONE" phase
      ValidPhaseValues = phase |> Map.toSeq |> Seq.map snd |> Set.ofSeq }

/// id -> name; "" when unknown (matches the original which would yield undefined,
/// then defaults to "").
let eventTypeName (c: Constants) (id: int) : string =
    Map.tryFind id c.EventTypeNames |> Option.defaultValue ""

let sourceTypeName (c: Constants) (id: int) : string =
    Map.tryFind id c.SourceTypeNames |> Option.defaultValue ""

let netErrorId (c: Constants) (name: string) : int option = Map.tryFind name c.NetError

/// Returns the key whose value equals `value`, or "?" (port of getKeyWithValue).
let keyWithValue (m: Map<string, int>) (value: int) : string =
    m |> Map.tryPick (fun k v -> if v = value then Some k else None) |> Option.defaultValue "?"
