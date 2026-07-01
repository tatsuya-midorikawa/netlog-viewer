// Realistic "cold start" comparison: a real file load calls the scanner exactly
// ONCE per fresh Extension Host process (V8 JIT/WASM-tier state starts fresh each
// time VS Code's extension host restarts). Runs each scanner exactly once per
// process invocation and repeats via a shell loop to get a distribution, instead of
// measuring steady-state (already-warmed-up) performance in a single process. Not
// part of the permanent test suite -- delete after use.
//
// Usage: node scripts/compare-wasm-scanner-coldstart.mjs <fsharp|wasm> [path]

import { readFileSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

async function main() {
  const mode = process.argv[2];
  const filePath = process.argv[3] || ".samples/edge-net-export-log-bigsize.json";
  const absPath = path.isAbsolute(filePath) ? filePath : path.resolve(__dirname, "..", filePath);
  if (!existsSync(absPath)) {
    console.log(`skip: file not found at ${absPath}`);
    return;
  }
  const buf = readFileSync(absPath);

  if (mode === "fsharp") {
    const {
      Scanner_$ctor_788EEBC8: newScanner,
      Scanner__Push_Z3F6BC7B1: scannerPush,
    } = await import("../build/extension/Core/ByteJsonScanner.js");
    const t0 = performance.now();
    let n = 0;
    const scanner = newScanner(
      () => {},
      () => {
        n++;
      },
      () => {}
    );
    scannerPush(scanner, buf);
    console.log(`fsharp coldMs=${(performance.now() - t0).toFixed(1)} events=${n}`);
  } else if (mode === "wasm") {
    const wasmPath = path.resolve(__dirname, "../wasm/scanner/target/wasm32-unknown-unknown/release/netlog_scanner.wasm");
    const bytes = readFileSync(wasmPath);
    const t0 = performance.now();
    const { instance } = await WebAssembly.instantiate(bytes, {});
    const { memory, alloc, reset_alloc, scan } = instance.exports;
    const tInst = performance.now();
    reset_alloc();
    const inPtr = alloc(buf.length);
    new Uint8Array(memory.buffer, inPtr, buf.length).set(buf);
    const eventCap = Math.max(100000, Math.ceil(buf.length / 20));
    const eventStartsPtr = alloc(eventCap * 4);
    const eventEndsPtr = alloc(eventCap * 4);
    const tailPtr = alloc(64 * 4 * 4);
    const outPtr = alloc(6 * 4);
    const tScanStart = performance.now();
    scan(inPtr, buf.length, eventStartsPtr, eventEndsPtr, eventCap, tailPtr, 64, outPtr);
    const tEnd = performance.now();
    const out = new Uint32Array(memory.buffer, outPtr, 6);
    console.log(
      `wasm instantiateMs=${(tInst - t0).toFixed(1)} allocMs=${(tScanStart - tInst).toFixed(1)} ` +
        `scanMs=${(tEnd - tScanStart).toFixed(1)} coldMs=${(tEnd - t0).toFixed(1)} events=${out[0]}`
    );
  } else {
    console.log("usage: node scripts/compare-wasm-scanner-coldstart.mjs <fsharp|wasm> [path]");
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
