// Correctness + performance comparison: F# ByteJsonScanner (single-shot, whole
// in-memory buffer, exactly like ParallelStreamLoader.runPrepass) vs the new WASM
// SIMD scanner (wasm/scanner), over the same real file. Verifies byte-for-byte
// parity on every event/constants/tail range -- this is the authoritative
// correctness check for the scanner.
//
// CAVEAT (do not use this script's timing numbers alone to judge real-world
// performance): this runs each scanner several times back-to-back IN ONE PROCESS,
// so later WASM runs benefit from V8 tiering the wasm function up from the Liftoff
// baseline compiler to TurboFan -- a real file load only ever calls the scanner
// ONCE per fresh Extension Host process, so it never gets that in-process warmup.
// See scripts/compare-wasm-scanner-coldstart.mjs for the realistic single-call
// number (spawns a fresh process per call, like a real file load).
//
// Usage: node scripts/compare-wasm-scanner.mjs [path]

import { readFileSync, existsSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import {
  Scanner_$ctor_788EEBC8 as newScanner,
  Scanner__Push_Z3F6BC7B1 as scannerPush,
  Scanner__get_IsComplete as scannerIsComplete,
} from "../build/extension/Core/ByteJsonScanner.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const fmtMs = (ms) => `${ms.toFixed(1)}ms`;

function runFSharpScan(buf) {
  const eventStarts = [];
  const eventEnds = [];
  let constants = null;
  const tail = [];
  const t0 = performance.now();
  const scanner = newScanner(
    (b, s, e) => {
      constants = [s, e];
    },
    (b, s, e) => {
      eventStarts.push(s);
      eventEnds.push(e);
    },
    (b, key, vs, ve) => {
      tail.push([key, vs, ve]);
    }
  );
  scannerPush(scanner, buf);
  const isComplete = scannerIsComplete(scanner);
  const ms = performance.now() - t0;
  return { eventStarts, eventEnds, constants, tail, isComplete, ms };
}

async function loadWasm() {
  const wasmPath = path.resolve(__dirname, "../wasm/scanner/target/wasm32-unknown-unknown/release/netlog_scanner.wasm");
  const bytes = readFileSync(wasmPath);
  const { instance } = await WebAssembly.instantiate(bytes, {});
  return instance.exports;
}

function runWasmScan(exports, buf) {
  const { memory, alloc, reset_alloc, scan } = exports;
  reset_alloc();
  const t0 = performance.now();
  const inPtr = alloc(buf.length);
  new Uint8Array(memory.buffer, inPtr, buf.length).set(buf);

  const eventCap = Math.max(100000, Math.ceil(buf.length / 20));
  const eventStartsPtr = alloc(eventCap * 4);
  const eventEndsPtr = alloc(eventCap * 4);
  const tailCap = 64;
  const tailPtr = alloc(tailCap * 4 * 4);
  const outPtr = alloc(6 * 4);

  scan(inPtr, buf.length, eventStartsPtr, eventEndsPtr, eventCap, tailPtr, tailCap, outPtr);
  const ms = performance.now() - t0;

  // Re-read memory.buffer AFTER the call -- memory_grow inside wasm can detach/
  // resize the ArrayBuffer, invalidating any typed array views created before the call.
  const out = new Uint32Array(memory.buffer, outPtr, 6);
  const [eventCount, constStart, constEnd, tailCount, isComplete, overflow] = out;
  const n = Math.min(eventCount, eventCap);
  const eventStarts = Array.from(new Uint32Array(memory.buffer, eventStartsPtr, n));
  const eventEnds = Array.from(new Uint32Array(memory.buffer, eventEndsPtr, n));
  const tailArr = new Uint32Array(memory.buffer, tailPtr, Math.min(tailCount, tailCap) * 4);
  const dec = (s, e) => new TextDecoder().decode(new Uint8Array(memory.buffer, inPtr + s, e - s));
  const tail = [];
  for (let i = 0; i < Math.min(tailCount, tailCap); i++) {
    const ks = tailArr[i * 4],
      ke = tailArr[i * 4 + 1],
      vs = tailArr[i * 4 + 2],
      ve = tailArr[i * 4 + 3];
    tail.push([dec(ks, ke), vs, ve]);
  }
  return {
    eventCount,
    eventStarts,
    eventEnds,
    constants: constStart === 0xffffffff ? null : [constStart, constEnd],
    tail,
    isComplete: !!isComplete,
    overflow: !!overflow,
    ms,
  };
}

async function main() {
  const filePath = process.argv[2] || ".samples/edge-net-export-log-bigsize.json";
  const absPath = path.isAbsolute(filePath) ? filePath : path.resolve(__dirname, "..", filePath);
  if (!existsSync(absPath)) {
    console.log(`[compare-wasm-scanner] Skipping: file not found at ${absPath}`);
    return;
  }
  console.log(`[compare-wasm-scanner] file=${absPath} size=${(statSync(absPath).size / 1e6).toFixed(1)}MB`);

  const readT0 = performance.now();
  const buf = readFileSync(absPath);
  console.log(`  read into Buffer: ${fmtMs(performance.now() - readT0)}`);

  const wasmExports = await loadWasm();

  // Warmup + 3 timed runs each, alternating to reduce ordering bias.
  const fsRuns = [];
  const wasmRuns = [];
  let fsResult, wasmResult;
  for (let i = 0; i < 4; i++) {
    fsResult = runFSharpScan(buf);
    if (i > 0) fsRuns.push(fsResult.ms);
    wasmResult = runWasmScan(wasmExports, buf);
    if (i > 0) wasmRuns.push(wasmResult.ms);
  }

  const median = (arr) => {
    const s = [...arr].sort((a, b) => a - b);
    return s[Math.floor(s.length / 2)];
  };

  console.log(`  F# scan runs:   ${fsRuns.map(fmtMs).join(", ")} (median ${fmtMs(median(fsRuns))})`);
  console.log(`  WASM scan runs: ${wasmRuns.map(fmtMs).join(", ")} (median ${fmtMs(median(wasmRuns))})`);
  console.log(`  speedup: ${(median(fsRuns) / median(wasmRuns)).toFixed(2)}x`);

  // ---- Correctness: exact parity check ----
  let ok = true;
  const check = (label, cond) => {
    if (!cond) {
      ok = false;
      console.log(`  MISMATCH: ${label}`);
    }
  };
  check("isComplete", fsResult.isComplete === wasmResult.isComplete);
  check("overflow", wasmResult.overflow === false);
  check("event count", fsResult.eventStarts.length === wasmResult.eventCount);
  check(
    "constants range",
    JSON.stringify(fsResult.constants) === JSON.stringify(wasmResult.constants)
  );
  const n = Math.min(fsResult.eventStarts.length, wasmResult.eventStarts.length);
  let firstMismatch = -1;
  for (let i = 0; i < n; i++) {
    if (fsResult.eventStarts[i] !== wasmResult.eventStarts[i] || fsResult.eventEnds[i] !== wasmResult.eventEnds[i]) {
      firstMismatch = i;
      break;
    }
  }
  check(`event ranges identical (n=${n})`, firstMismatch === -1);
  if (firstMismatch !== -1) {
    console.log(
      `    first mismatch at index ${firstMismatch}: fs=[${fsResult.eventStarts[firstMismatch]},${fsResult.eventEnds[firstMismatch]}] wasm=[${wasmResult.eventStarts[firstMismatch]},${wasmResult.eventEnds[firstMismatch]}]`
    );
  }
  const fsTailSorted = [...fsResult.tail].sort((a, b) => (a[0] < b[0] ? -1 : 1));
  const wasmTailSorted = [...wasmResult.tail]
    .map(([k, s, e]) => [k, s, e])
    .sort((a, b) => (a[0] < b[0] ? -1 : 1));
  check(
    "tail entries",
    JSON.stringify(fsTailSorted) === JSON.stringify(wasmTailSorted.map(([k, s, e]) => [k, s, e]))
  );

  console.log(ok ? "[compare-wasm-scanner] OK: exact parity" : "[compare-wasm-scanner] FAILED: parity mismatch");
  if (!ok) process.exit(1);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
