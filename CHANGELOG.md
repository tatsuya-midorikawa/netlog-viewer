# Changelog

All notable changes to the **Netlog Viewer** extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Streaming loader for large NetLog files. Files are now read from disk incrementally instead of being loaded as a single string, so logs that previously failed with `Cannot create a string longer than 0x1fffffe8 characters` (the ~512 MB V8 string limit) now open. Both plain `.json` and gzip `.gz` files are streamed and decompressed on the fly.
- `netlogViewer.maxEvents` setting (default `2000000`) that caps how many events are loaded from a single file so memory stays bounded on very large logs. When the cap is reached the log is truncated with a warning; set it to `0` to load every event.
- `netlogViewer.maxSourceDetailEvents` setting (default `50000`) that caps how many events are shown in the detail pane for a single selected source, so selecting a very active source in a large log stays responsive.
- Load progress and a status banner: while a log streams in, the viewer shows a “Loading… N events” indicator, and truncation / event-cap / invalid-event warnings (and load errors) are surfaced in a banner instead of silently.

### Changed

- Loading large NetLog files is significantly faster: the streaming loader now scans raw bytes directly instead of decoding the whole file to text up front, and the initial event stream sent to the viewer now omits parameter data that only the Events tab's on-demand detail view needs.
- The batch and streaming loaders now share a single per-event validation/grouping path, so both produce identical results.
- The parsed log is delivered to the viewer as a sequence of chunks (`loadStart` → source/event chunks → `loadEnd`) instead of one large message, avoiding a single oversized transfer for big logs.
- The Events tab now loads a source's event details on demand (when it is selected) instead of keeping every event in the viewer, keeping memory low for large logs.
- The Events filter's free-text search now matches a source's id, type, and description; it no longer scans each source's full event/parameter text.
- The Events source list now materializes at most a few thousand matching rows at a time (the rest stay reachable through the filter), so the tab stays responsive for logs with very many sources.
- The parsed log kept for on-demand detail lookups is released when the editor is closed.

### Fixed

- Opening very large (multi-hundred-MB and multi-GB) NetLog files no longer aborts with the V8 string-length error.

## [0.0.1] - 2026-07-01

### Added

- Initial release.
- Custom read-only editor for Chromium `net-export` (NetLog) logs.
- 14-tab layout: Import, Events, Timeline, Proxy, DNS, Sockets, StreamPool, Alt-Svc, HTTP/2, QUIC, Reporting, Cache, Modules, and Prerender.
- Events tab with source grouping, filtering (`type:` / `id:` / `is:active|error` / `sort:`), selection, and event-trace details.
- Timeline tab rendered on a canvas (scroll to zoom, drag to pan).
- Automatic association with NetLog file-name patterns and the **Netlog Viewer: Open File** command.
- Support for `.json` / `.netlog` files and automatic gzip (`.gz`) decompression.
- Fully offline and read-only; built from scratch with no external runtime libraries.

[Unreleased]: https://github.com/tatsuya-midorikawa/netlog-viewer/compare/v0.0.1...HEAD
[0.0.1]: https://github.com/tatsuya-midorikawa/netlog-viewer/releases/tag/v0.0.1
