// Integration check for ParallelStreamLoader (Phase 2: worker_threads parallel
// parsing). Unlike scripts/verify-big-log.mjs (which drives the Core parser
// pipeline directly, in-process), this exercises the REAL Extension-side
// orchestration end to end, including actually spawning worker_threads workers and
// running dist/parseWorker.js -- the part that can't be unit-tested in Core.Tests.
//
// Compares ParallelStreamLoader.load's result against StreamLoader.load's (the
// existing, already-trusted single-threaded path) for the same file: event/source/
// error counts, busiest sources, and load-log text must match exactly.
//
// Usage:
//   node scripts/verify-parallel.mjs [path]
//   path defaults to .samples/edge-net-export-log-bigsize.json (skips gracefully if
//   absent). Requires `npm run build` first (needs dist/parseWorker.js to exist).

import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";
import path from "node:path";

// Node.fs's [<Emit>] bindings reference the bare `require` identifier, which is a
// real global in CommonJS (e.g. the production dist/extension.js bundle, built with
// --format=cjs) but does not exist in a plain ESM context like this script. Polyfill
// it so importing the raw, unbundled ESM build output works for this integration
// check without needing a separate esbuild bundling step just for testing.
globalThis.require = createRequire(import.meta.url);

import { load as parallelLoad } from "../build/extension/ParallelStreamLoader.js";
import { load as sequentialLoad } from "../build/extension/StreamLoader.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

function runLoader(loadFn, filePath) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    loadFn(filePath, 0, (log) => resolve({ log, ms: performance.now() - t0 }), (err) => reject(new Error(err)));
  });
}

function runParallelLoader(filePath, workerScriptPath) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    parallelLoad(
      workerScriptPath,
      filePath,
      0,
      (log) => resolve({ log, ms: performance.now() - t0 }),
      (err) => reject(new Error(err))
    );
  });
}

function summarize(log) {
  const sources = log.Sources;
  const errorSources = sources.filter((s) => s.IsError).length;
  const busiest = [...sources]
    .sort((a, b) => b.Entries.length - a.Entries.length)
    .slice(0, 3)
    .map((s) => s.Entries.length);
  return {
    events: log.Events.length,
    sources: sources.length,
    errorSources,
    busiest,
    loadLog: log.LoadLog.trim(),
  };
}

async function main() {
  const argPath = process.argv[2] || ".samples/edge-net-export-log-bigsize.json";
  const absPath = path.isAbsolute(argPath) ? argPath : path.resolve(__dirname, "..", argPath);
  const workerScriptPath = path.resolve(__dirname, "..", "dist", "parseWorker.js");

  if (!existsSync(absPath)) {
    console.log(`[verify-parallel] Skipping: file not found at ${absPath}`);
    return;
  }
  if (!existsSync(workerScriptPath)) {
    console.log(`[verify-parallel] Skipping: ${workerScriptPath} not found (run \`npm run build\` first)`);
    return;
  }

  console.log(`[verify-parallel] file=${absPath}`);

  const sequential = await runLoader(sequentialLoad, absPath);
  const seqSummary = summarize(sequential.log);
  console.log(`  sequential: ${sequential.ms.toFixed(1)}ms`, seqSummary);

  const parallel = await runParallelLoader(absPath, workerScriptPath);
  const parSummary = summarize(parallel.log);
  console.log(`  parallel:   ${parallel.ms.toFixed(1)}ms`, parSummary);

  let ok = true;
  const compare = (name, expected, actual) => {
    const pass = JSON.stringify(expected) === JSON.stringify(actual);
    if (!pass) ok = false;
    console.log(`  ${pass ? "ok  " : "FAIL"} ${name}: expected=${JSON.stringify(expected)} actual=${JSON.stringify(actual)}`);
  };
  compare("events", seqSummary.events, parSummary.events);
  compare("sources", seqSummary.sources, parSummary.sources);
  compare("errorSources", seqSummary.errorSources, parSummary.errorSources);
  compare("busiest", seqSummary.busiest, parSummary.busiest);
  compare("loadLog", seqSummary.loadLog, parSummary.loadLog);

  const speedup = sequential.ms / parallel.ms;
  console.log(`  speedup: ${speedup.toFixed(2)}x (sequential ${sequential.ms.toFixed(1)}ms -> parallel ${parallel.ms.toFixed(1)}ms)`);

  if (!ok) {
    console.error("[verify-parallel] FAILED: parallel result does not match sequential baseline");
    process.exitCode = 1;
  } else {
    console.log("[verify-parallel] OK: parallel result matches sequential baseline exactly");
  }
}

main().catch((err) => {
  console.error("[verify-parallel] FAILED:", err);
  process.exitCode = 1;
});
