// Minimal static file server for scripts/webview-bench/index.html, used to
// measure real webview-side (browser DOM) reassembly + tab-render time via
// Playwright -- serves the actual dist/webview.js + media/style.css build
// artifacts, plus the pre-generated wire messages from
// scripts/generate-wire-messages.mjs, over plain HTTP so the page can `fetch()`
// them (file:// URLs hit CORS/fetch restrictions for local file reads).
//
// Usage: node scripts/webview-bench/server.mjs [port]

import { createServer } from "node:http";
import { createReadStream, existsSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, "../..");

const routes = {
  "/": path.join(__dirname, "index.html"),
  "/dist/webview.js": path.join(root, "dist/webview.js"),
  "/media/style.css": path.join(root, "media/style.css"),
  "/messages": path.join(root, "build/_wire-messages.json"),
};

const contentTypes = {
  ".html": "text/html",
  ".js": "text/javascript",
  ".css": "text/css",
  ".json": "application/json",
};

const port = Number(process.argv[2] || 8734);

const server = createServer((req, res) => {
  const filePath = routes[req.url];
  if (!filePath || !existsSync(filePath)) {
    res.writeHead(404);
    res.end("not found");
    return;
  }
  const ext = path.extname(filePath);
  res.writeHead(200, {
    "Content-Type": contentTypes[ext] || "application/octet-stream",
    "Content-Length": statSync(filePath).size,
  });
  createReadStream(filePath).pipe(res);
});

server.listen(port, () => {
  console.log(`[webview-bench] serving on http://localhost:${port}/`);
});
