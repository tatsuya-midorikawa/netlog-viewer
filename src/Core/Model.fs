/// Core domain types shared across parsing, grouping, and (later) the wire model.
module Netlog.Core.Model

/// A validated NetLog event. `Params` keeps the raw JS object (for the painter /
/// wire model); numeric ids are resolved to names via the loaded Constants.
type Event =
    { Index: int
      Time: float
      Type: int
      Phase: int
      SourceId: int
      SourceType: int
      StartTime: float option
      Params: obj option }
