# Changelog

All notable changes to the **Netlog Viewer** extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
