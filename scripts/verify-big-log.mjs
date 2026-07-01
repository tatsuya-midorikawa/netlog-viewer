// Headless benchmark harness for the Core NetLog streaming parser (Phase 0 of the
// big-file load performance plan). Drives the same read -> decode -> scan -> parse
// -> ingest -> wire pipeline that StreamLoader.fs / NetlogEditor.fs run inside the
// Extension Host, without needing VS Code itself. Used to get a repeatable baseline
// before Phase 1+ changes, and to catch regressions after each change.
//
// Requires a build first: `npm run build` (or at least `npm run fable:extension`),
// so that build/extension/Core/*.js exists.
//
// Usage:
//   npm run bench:big -- [path] [--runs=N] [--maxEvents=N]
//   node scripts/verify-big-log.mjs [path] [--runs=N] [--maxEvents=N]
//
//   path          defaults to .samples/edge-net-export-log-bigsize.json (gitignored,
//                 user-provided; the harness skips gracefully if it is absent).
//   --runs=N      number of repetitions within this process (default 3). The first
//                 run includes JIT warmup, so both the first run and the median of
//                 all runs are reported.
//   --maxEvents=N 0 (default) = uncapped, matching the historical baseline.
//
// This measures three coarse stages. Since StreamLoader.fs's `load` reads the
// whole (decompressed) file into memory and scans it in one pass below its
// ~1.5GB whole-buffer threshold (see StreamLoader.fs's WholeBufferThreshold),
// falling back to true chunk-by-chunk streaming only above that, this harness
// mirrors the SAME size-based dispatch so it measures whichever path production
// actually takes for the given file -- see the tooling-lessons note in
// /memories/tooling.md about hand-replicated harnesses silently going stale when
// the production pipeline they mirror changes.
//   - ioWaitMs: time to read (and, for `.gz`, decompress) the whole file into
//             memory -- I/O-bound. (Only present in the streaming fallback path
//             is a truer overlapped I/O-wait; below the threshold this is a
//             synchronous, non-overlapped read, reported the same way for
//             continuity with historical numbers.)
//   - cpuMs:  time spent scanning + parsing (ByteJsonScanner + StreamingParser,
//             which synchronously triggers JSON.parse + LogParser.ingestEvent +
//             SourceGrouping) plus the end-of-stream finalize call. This is the
//             CPU-bound portion.
//   - wireMs: time to build the wire messages (Core.Wire.postLoad), measured
//             separately since it runs synchronously after ingest finishes.
// For a finer flame-graph style breakdown, run with Node's built-in profiler:
//   node --cpu-prof scripts/verify-big-log.mjs
// and load the resulting .cpuprofile in a profiler view.

import { existsSync, statSync, openSync, readSync, closeSync, createReadStream, readFileSync } from "node:fs";
import { createGunzip, gunzipSync } from "node:zlib";
import { fileURLToPath } from "node:url";
import path from "node:path";

import {
  Scanner_$ctor_788EEBC8 as newScanner,
  Scanner__Push_Z3F6BC7B1 as scannerPush,
  Scanner__get_IsComplete as scannerIsComplete,
  Scanner__Finish as scannerFinish,
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

function parseArgs(argv) {
  let filePath = ".samples/edge-net-export-log-bigsize.json";
  let runs = 3;
  let maxEvents = 0;
  const positional = [];
  for (const arg of argv) {
    if (arg.startsWith("--runs=")) runs = Number(arg.slice("--runs=".length));
    else if (arg.startsWith("--maxEvents=")) maxEvents = Number(arg.slice("--maxEvents=".length));
    else positional.push(arg);
  }
  if (positional.length > 0) filePath = positional[0];
  return { filePath, runs, maxEvents };
}

function isGzipFile(absPath) {
  const fd = openSync(absPath, "r");
  try {
    const head = Buffer.alloc(2);
    const n = readSync(fd, head, 0, 2, 0);
    return n >= 2 && head[0] === 0x1f && head[1] === 0x8b;
  } finally {
    closeSync(fd);
  }
}

function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 !== 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

const fmtMs = (ms) => `${ms.toFixed(1)}ms`;
const fmtMB = (bytes) => `${(bytes / (1024 * 1024)).toFixed(1)}MB`;

// Matches StreamLoader.fs's `WholeBufferThreshold` -- above this, production falls
// back to true chunk-by-chunk streaming instead of reading the whole file at once.
const WHOLE_BUFFER_THRESHOLD = 1610612736; // 1.5 GB

function makeScanner(state) {
  return newScanner(
    (buf, s, e) => pushConstantsJson(state, decodeByteRange(buf, s, e)),
    (buf, s, e) => pushEventJson(state, decodeByteRange(buf, s, e)),
    (buf, key, vs, ve) => pushTailJson(state, key, decodeByteRange(buf, vs, ve))
  );
}

function buildWireStats(absPath, log) {
  const wireT0 = performance.now();
  let chunkCount = 0;
  let payloadBytes = 0;
  postLoad(
    (msg) => {
      chunkCount += 1;
      payloadBytes += Buffer.byteLength(JSON.stringify(msg));
    },
    path.basename(absPath),
    log
  );
  return { wireMs: performance.now() - wireT0, chunkCount, payloadBytes };
}

/** Mirrors StreamLoader.fs's `loadWhole`: read (+decompress) the whole file, then
 *  one Push+Finish call -- this is what production actually does below the
 *  whole-buffer threshold, which covers ordinary "big" sample files. */
function runWhole(absPath, gz, maxEvents) {
  const t0 = performance.now();
  const raw = readFileSync(absPath);
  const buf = gz ? gunzipSync(raw) : raw;
  const ioWaitMs = performance.now() - t0;

  const cpuT0 = performance.now();
  const state = createStreamState(maxEvents);
  const scanner = makeScanner(state);
  scannerPush(scanner, buf);
  scannerFinish(scanner);
  const result = finishStreamState(state, scannerIsComplete(scanner));
  const cpuMs = performance.now() - cpuT0;

  if (result.tag !== 0) throw new Error(String(result.fields[0]));
  const log = result.fields[0];
  const { wireMs, chunkCount, payloadBytes } = buildWireStats(absPath, log);
  const ingestWallMs = ioWaitMs + cpuMs;
  return {
    cpuMs,
    ioWaitMs,
    ingestWallMs,
    wireMs,
    totalMs: ingestWallMs + wireMs,
    log,
    chunkCount,
    payloadBytes,
  };
}

/** Mirrors StreamLoader.fs's `loadStreaming` fallback (used above the whole-buffer
 *  threshold): fs.createReadStream chunks, many smaller Push calls. */
function runStreaming(absPath, gz, maxEvents) {
  return new Promise((resolve, reject) => {
    let finished = false;
    const fail = (err) => {
      if (!finished) {
        finished = true;
        reject(err instanceof Error ? err : new Error(String(err)));
      }
    };

    const t0 = performance.now();
    let cpuMs = 0;

    const fileStream = createReadStream(absPath);
    const stream = gz ? fileStream.pipe(createGunzip()) : fileStream;
    const state = createStreamState(maxEvents);
    const scanner = makeScanner(state);

    fileStream.on("error", fail);
    if (gz) stream.on("error", fail);

    stream.on("data", (chunk) => {
      if (finished) return;
      const cbT0 = performance.now();
      try {
        scannerPush(scanner, chunk);
      } catch (err) {
        stream.destroy();
        fail(err);
        return;
      }
      cpuMs += performance.now() - cbT0;
    });

    stream.on("end", () => {
      if (finished) return;
      const cbT0 = performance.now();
      try {
        scannerFinish(scanner);
        const result = finishStreamState(state, scannerIsComplete(scanner));
        cpuMs += performance.now() - cbT0;
        const ingestWallMs = performance.now() - t0;

        if (result.tag !== 0) {
          fail(new Error(String(result.fields[0])));
          return;
        }
        const log = result.fields[0];
        const { wireMs, chunkCount, payloadBytes } = buildWireStats(absPath, log);

        finished = true;
        resolve({
          cpuMs,
          ioWaitMs: Math.max(0, ingestWallMs - cpuMs),
          ingestWallMs,
          wireMs,
          totalMs: ingestWallMs + wireMs,
          log,
          chunkCount,
          payloadBytes,
        });
      } catch (err) {
        fail(err);
      }
    });
  });
}

/** Runs the parse pipeline once end-to-end, dispatching by size exactly like
 *  StreamLoader.fs's `load`, and resolves with timings + the loaded log. */
async function runOnce(absPath, maxEvents) {
  let gz = false;
  try {
    gz = isGzipFile(absPath);
  } catch {
    gz = absPath.endsWith(".gz");
  }

  let size;
  try {
    size = statSync(absPath).size;
  } catch {
    size = WHOLE_BUFFER_THRESHOLD;
  }

  return size < WHOLE_BUFFER_THRESHOLD ? runWhole(absPath, gz, maxEvents) : runStreaming(absPath, gz, maxEvents);
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
  const { filePath, runs, maxEvents } = parseArgs(process.argv.slice(2));
  const absPath = path.isAbsolute(filePath) ? filePath : path.resolve(__dirname, "..", filePath);

  if (!existsSync(absPath)) {
    console.log(`[verify-big-log] Skipping: file not found at ${absPath}`);
    return;
  }

  const sizeBytes = statSync(absPath).size;
  console.log(
    `[verify-big-log] file=${absPath} size=${fmtMB(sizeBytes)} runs=${runs} maxEvents=${maxEvents || "unlimited"}`
  );

  const timings = [];
  let lastSummary = null;
  let lastMemory = null;

  for (let i = 1; i <= runs; i += 1) {
    if (typeof global.gc === "function") global.gc(); // node --expose-gc for cleaner per-run samples
    const result = await runOnce(absPath, maxEvents);
    const after = process.memoryUsage();
    lastSummary = summarize(result.log);
    lastMemory = after;
    // Keep only the numeric fields across iterations -- retaining `result.log` (the
    // full parsed model, all events/params included) for every run would hold N
    // loads alive at once and make memory numbers reflect harness overhead instead
    // of a single real load.
    const { cpuMs, ioWaitMs, ingestWallMs, wireMs, totalMs, chunkCount, payloadBytes } = result;
    timings.push({ cpuMs, ioWaitMs, ingestWallMs, wireMs, totalMs, chunkCount, payloadBytes });

    console.log(
      `  run ${i}/${runs}: cpu=${fmtMs(result.cpuMs)} ioWait=${fmtMs(result.ioWaitMs)} ` +
        `wire=${fmtMs(result.wireMs)} total=${fmtMs(result.totalMs)} rss=${fmtMB(after.rss)} ` +
        `heapUsed=${fmtMB(after.heapUsed)} chunks=${result.chunkCount} wireBytes=${fmtMB(result.payloadBytes)}`
    );
  }

  const cpuMedian = median(timings.map((t) => t.cpuMs));
  const ioWaitMedian = median(timings.map((t) => t.ioWaitMs));
  const wireMedian = median(timings.map((t) => t.wireMs));
  const totalMedian = median(timings.map((t) => t.totalMs));
  const last = timings[timings.length - 1];

  console.log("");
  console.log("[verify-big-log] summary (median across runs; first run includes JIT warmup):");
  console.log(
    `  cpu_ms_median=${cpuMedian.toFixed(1)} ioWait_ms_median=${ioWaitMedian.toFixed(1)} ` +
      `wire_ms_median=${wireMedian.toFixed(1)} total_ms_median=${totalMedian.toFixed(1)}`
  );
  console.log(`  first_run_total_ms=${timings[0].totalMs.toFixed(1)}`);
  console.log(
    `  events=${lastSummary.events} sources=${lastSummary.sources} errorSources=${lastSummary.errorSources} ` +
      `busiestSources=${lastSummary.busiest.join("/")}`
  );
  console.log(`  loadLog=${JSON.stringify(lastSummary.loadLog)}`);
  console.log(
    `  peak_rss_mb=${(lastMemory.rss / (1024 * 1024)).toFixed(1)} ` +
      `peak_heapUsed_mb=${(lastMemory.heapUsed / (1024 * 1024)).toFixed(1)}`
  );
  console.log("");
  console.log(
    JSON.stringify({
      file: path.basename(absPath),
      sizeBytes,
      runs,
      maxEvents: maxEvents || null,
      cpuMsMedian: cpuMedian,
      ioWaitMsMedian: ioWaitMedian,
      wireMsMedian: wireMedian,
      totalMsMedian: totalMedian,
      firstRunTotalMs: timings[0].totalMs,
      events: lastSummary.events,
      sources: lastSummary.sources,
      errorSources: lastSummary.errorSources,
      busiestSources: lastSummary.busiest,
      peakRssMb: lastMemory.rss / (1024 * 1024),
      peakHeapUsedMb: lastMemory.heapUsed / (1024 * 1024),
      wireChunkCount: last.chunkCount,
      wirePayloadBytes: last.payloadBytes,
    })
  );
}

main().catch((err) => {
  console.error("[verify-big-log] FAILED:", err);
  process.exitCode = 1;
});
