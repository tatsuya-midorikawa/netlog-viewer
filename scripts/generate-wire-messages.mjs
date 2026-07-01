// Generates the exact wire messages Wire.postLoad would send to the webview for a
// real file load, and saves them as one JSON array -- used by
// scripts/webview-bench/ to measure webview-side reassembly + tab-render time in a
// real browser DOM (Playwright), the one piece of "real end-to-end" time this
// project's benchmarks have never covered (see verify-big-log.mjs's own doc
// comment: "does NOT yet include postMessage IPC or webview reassembly/render").
//
// Usage: node scripts/generate-wire-messages.mjs [path] [outFile]
//   outFile defaults to build/_wire-messages.json (build/ is already gitignored;
//   this keeps generated benchmark artifacts out of .samples/, which is reserved
//   for genuine user-provided sample files).

import { readFileSync, writeFileSync, existsSync, statSync } from "node:fs";
import { gunzipSync } from "node:zlib";
import path from "node:path";
import { fileURLToPath } from "node:url";

import {
  Scanner_$ctor_788EEBC8 as newScanner,
  Scanner__Push_Z3F6BC7B1 as scannerPush,
  Scanner__Finish as scannerFinish,
  Scanner__get_IsComplete as scannerIsComplete,
  decodeByteRange,
} from "../build/extension/Core/ByteJsonScanner.js";
import {
  create as createStreamState,
  pushConstantsJson,
  pushEventJson,
  pushTailJson,
  finish as finishStreamState,
} from "../build/extension/Core/StreamingParser.js";
import { postLoad } from "../build/extension/Core/Wire.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

function isGzipFile(absPath) {
  const buf = readFileSync(absPath, { flag: "r" }).subarray(0, 2);
  return buf.length >= 2 && buf[0] === 0x1f && buf[1] === 0x8b;
}

async function main() {
  const filePath = process.argv[2] || ".samples/edge-net-export-log-bigsize.json";
  const outFile = process.argv[3] || "build/_wire-messages.json";
  const absPath = path.isAbsolute(filePath) ? filePath : path.resolve(__dirname, "..", filePath);
  const outAbs = path.isAbsolute(outFile) ? outFile : path.resolve(__dirname, "..", outFile);
  if (!existsSync(absPath)) {
    console.log(`skip: file not found at ${absPath}`);
    return;
  }

  const gz = isGzipFile(absPath);
  const raw = readFileSync(absPath);
  const buf = gz ? gunzipSync(raw) : raw;

  const state = createStreamState(0);
  const scanner = newScanner(
    (b, s, e) => pushConstantsJson(state, decodeByteRange(b, s, e)),
    (b, s, e) => pushEventJson(state, decodeByteRange(b, s, e)),
    (b, key, vs, ve) => pushTailJson(state, key, decodeByteRange(b, vs, ve))
  );
  scannerPush(scanner, buf);
  scannerFinish(scanner);
  const result = finishStreamState(state, scannerIsComplete(scanner));
  if (result.tag !== 0) throw new Error(String(result.fields[0]));
  const log = result.fields[0];

  const messages = [];
  postLoad((msg) => messages.push(msg), path.basename(absPath), log);

  console.log(`generated ${messages.length} messages, writing to ${outAbs} ...`);
  writeFileSync(outAbs, JSON.stringify(messages));
  const sizeMb = (statSync(outAbs).size / 1e6).toFixed(1);
  console.log(`done: ${outAbs} (${sizeMb}MB)`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
