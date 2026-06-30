# netlog-viewer

`netlog-viewer` is a Visual Studio Code extension for viewing `net-export` (NetLog) logs captured from Chromium-based browsers.

---

## Table of Contents

- [netlog-viewer](#netlog-viewer)
  - [Table of Contents](#table-of-contents)
  - [Features](#features)
  - [Requirements](#requirements)
  - [Installation](#installation)
    - [From the Visual Studio Code Marketplace](#from-the-visual-studio-code-marketplace)
    - [From a VSIX file](#from-a-vsix-file)
  - [Usage](#usage)
  - [Supported Files](#supported-files)
  - [Commands](#commands)
  - [Settings](#settings)
  - [Limitations](#limitations)
  - [Roadmap](#roadmap)
  - [Contributing](#contributing)
  - [Sponsors](#sponsors)
  - [License](#license)

---

## Features

- **NetLog log viewer**: View `.json` logs captured with `net-export` in a custom editor inside VS Code.
- **14-tab layout**:
  - **Import** — Export date and time, capture mode, browser/OS information, command line, and user comments.
  - **Events** — A list of events grouped by source, with filtering (`type:` / `id:` / `is:active|error` / `sort:`), selection, and event trace details (`t=` / `st=` / `[dt=]`).
  - **Timeline** — Time-series data such as socket count, request count, and bytes transferred, rendered on a canvas (scroll to zoom, drag to pan).
  - **Proxy / DNS / Sockets / StreamPool / Alt-Svc / HTTP/2 / QUIC / Reporting / Cache / Modules / Prerender** — Status tables for each subsystem.
- **Read-only and fully offline**: Never writes to files and never communicates over the network.
- **No external runtime libraries**: Both the parser and the viewer are built entirely from scratch using only the standard library (see [DEVELOPMENT.md](./DEVELOPMENT.md) for details).
- **Supports `.json` / `.netlog` / `.gz` (gzip)**.

---

## Requirements

- Visual Studio Code `1.90.0` or later
- **No** additional runtimes or external applications required

---

## Installation

### From the Visual Studio Code Marketplace

1. Open the **Extensions** view from the VS Code sidebar (`Ctrl+Shift+X` / `⌘+Shift+X`).
2. Search for `Netlog Viewer`.
3. Click **Install**.

### From a VSIX file

```bash
code --install-extension netlog-viewer-<version>.vsix
```

Alternatively, run **"Extensions: Install from VSIX..."** from the Command Palette (`Ctrl+Shift+P` / `⌘+Shift+P`) and select the `.vsix` file.

---

## Usage

1. Capture a log in a Chromium-based browser.
   - Chrome: `chrome://net-export/`, Microsoft Edge: `edge://net-export/`
   - Click **Start Logging To Disk**, choose a destination `.json` file, reproduce the issue, and then click **Stop Logging**.
2. Open the captured `.json` file in VS Code.
   - If the file name matches one of the [supported file](#supported-files) patterns, it opens automatically in Netlog Viewer.
   - To open a `.json` file that does not match, right-click the file in the Explorer and choose
     **"Open With…" → Netlog Viewer**, or run the **"Netlog Viewer: Open File"** command.
3. Switch between the tabs on the left to inspect the state of each subsystem. On the Events tab, type a filter to narrow down sources, and select a row to display the event trace in the right pane.

---

## Supported Files

The custom editor automatically associates with the following file name patterns (it does not take over every `.json` file indiscriminately):

- `*.netlog`
- `*.netlog.json` (and `*.netlog.json.gz`)
- `*net-export*.json` (and `*net-export*.json.gz`)
- `*net_log*.json`
- `*netlog*.json`

`.gz` (gzip-compressed) files are decompressed automatically. Files that do not match the patterns above can be opened with the "Netlog Viewer: Open File" command.

---

## Commands

The following command is available from the Command Palette (`Ctrl+Shift+P` / `⌘+Shift+P`).

| Command | Command ID | Description |
| --- | --- | --- |
| Netlog Viewer: Open File | `netlogViewer.openFile` | Opens a NetLog log from a file selection dialog. |

---

## Settings

There are currently no user-configurable settings. Options such as rendering limits and the default tab are being considered in the [Roadmap](#roadmap).

---

## Limitations

- **For viewing exported logs only.** Real-time connection to and capture from a live browser (as with `net-internals`) is not supported. (The information tabs display the `polledData` included in the log, and tabs with no data are hidden automatically.)
- The only supported compression format is **gzip (`.gz`)**; `.zip` is not supported.
- Some information tabs (Proxy settings / DNS settings / Reporting clients and NEL) currently display formatted JSON.
- Because it is read-only, logs cannot be edited or saved.

---

## Roadmap

- [ ] User settings (rendering limits, default tab, etc.)
- [ ] Virtualization and progressive rendering for large logs
- [ ] More detailed formatting of information tabs and links to event sources
- [ ] Support for the `.zip` format

---

## Contributing

For specifications and design as well as build/debug instructions, see [DEVELOPMENT.md](./DEVELOPMENT.md). For publishing to the Marketplace, see [PUBLISHING.md](./PUBLISHING.md).

---

## Sponsors

If you would like to support the development of this project, contributions via [GitHub Sponsors](https://github.com/sponsors/tatsuya-midorikawa) are warmly welcomed.
Your support will be used to improve features and maintain the project over time.

---

## License

This project is released under the [MIT License](./LICENSE).
