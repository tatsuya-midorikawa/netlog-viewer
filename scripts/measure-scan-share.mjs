// Diagnostic tool (not part of the permanent bench:big harness, but kept for reuse):
// measures what share of the "cpu" bucket is pure structural scanning
// (ByteJsonScanner.Push/Finish with no-op callbacks -- no decode, no JSON.parse, no
// ingest/grouping) vs everything else (decode + JSON.parse + LogParser.ingestEvent +
// SourceGrouping). Used to gate whether Phase 4 (from-scratch WASM SIMD structural
// scanner, see /memories/session/plan.md) has enough headroom to be worth its cost,
// per the plan's own rule: "proceed only if Phase 0 shows scanning is still a large
// slice." Re-run this after any future scanner change before revisiting that decision.
//
// Usage: node scripts/measure-scan-share.mjs [path]

import { existsSync, statSync, createReadStream, openSync, readSync, closeSync } from "node:fs";
import { createGunzip } from "node:zlib";
import { fileURLToPath } from "node:url";
import path from "node:path";

import {
  Scanner_$ctor_788EEBC8 as newScanner,
  Scanner__Push_Z3F6BC7B1 as scannerPush,
  Scanner__Finish as scannerFinish,
} from "../build/extension/Core/ByteJsonScanner.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const fmtMs = (ms) => `${ms.toFixed(1)}ms`;

function isGzipFile(absPath) {
  const buf = Buffer.alloc(2);
  const fd = openSync(absPath, "r");
  try {
    const n = readSync(fd, buf, 0, 2, 0);
    return n >= 2 && buf[0] === 0x1f && buf[1] === 0x8b;
  } finally {
    closeSync(fd);
  }
}

function runScanOnly(absPath) {
  return new Promise((resolve, reject) => {
    let gz = false;
    try {
      gz = isGzipFile(absPath);
    } catch {
      gz = absPath.endsWith(".gz");
    }
    const fileStream = createReadStream(absPath);
    const stream = gz ? fileStream.pipe(createGunzip()) : fileStream;
    // True no-ops: don't touch buf/s/e at all, so this isolates pure brace/bracket/
    // string/escape structural-scan cost from decode+parse+ingest+grouping cost.
    const scanner = newScanner(
      () => {},
      () => {},
      () => {}
    );
    let scanMs = 0;
    fileStream.on("error", reject);
    if (gz) stream.on("error", reject);
    stream.on("data", (chunk) => {
      const t0 = performance.now();
      scannerPush(scanner, chunk);
      scanMs += performance.now() - t0;
    });
    stream.on("end", () => {
      const t0 = performance.now();
      scannerFinish(scanner);
      scanMs += performance.now() - t0;
      resolve(scanMs);
    });
  });
}

async function main() {
  const filePath = process.argv[2] || ".samples/edge-net-export-log-bigsize.json";
  const absPath = path.isAbsolute(filePath) ? filePath : path.resolve(__dirname, "..", filePath);
  if (!existsSync(absPath)) {
    console.log(`[measure-scan-share] Skipping: file not found at ${absPath}`);
    return;
  }
  console.log(`[measure-scan-share] file=${absPath} size=${(statSync(absPath).size / 1e6).toFixed(1)}MB`);
  const runs = 3;
  const results = [];
  for (let i = 1; i <= runs; i += 1) {
    const ms = await runScanOnly(absPath);
    results.push(ms);
    console.log(`  run ${i}/${runs}: scanOnlyMs=${fmtMs(ms)}`);
  }
  const sorted = [...results].sort((a, b) => a - b);
  const med = sorted[Math.floor(sorted.length / 2)];
  console.log(`[measure-scan-share] median scanOnlyMs=${fmtMs(med)}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
