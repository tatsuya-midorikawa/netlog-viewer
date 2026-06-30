module Netlog.Core.Library

/// Phase 0 placeholder. Real NetLog parsing/domain model arrives in Phase 1.
/// Shared by the Extension Host (parsing) and, later, the Webview (rendering helpers).
let describeFile (fileName: string) (byteLength: int) : string =
    $"%s{fileName} (%d{byteLength} bytes)"
