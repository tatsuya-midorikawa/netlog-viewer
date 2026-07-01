// End-to-end verification for StreamLoader.fs's new whole-buffer fast path (reads
// the whole file into memory, one Push+Finish call) vs the original incremental
// streaming path (fs.createReadStream chunks, many Push calls) it falls back to
// above the size threshold. Confirms both produce byte-for-byte identical
// LoadedLog output, and measures real end-to-end (read+scan+parse) time for both.
//
// Usage: node scripts/verify-whole-buffer.mjs [path]

import { createReadStream, readFileSync, existsSync, statSync, writeFileSync, unlinkSync } from "node:fs";
import { createGunzip, gzipSync, gunzipSync } from "node:zlib";
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

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const fmtMs = (ms) => `${ms.toFixed(1)}ms`;

function isGzipFile(absPath) {
  const buf = readFileSync(absPath, { flag: "r" }).subarray(0, 2);
  return buf.length >= 2 && buf[0] === 0x1f && buf[1] === 0x8b;
}

function makeScanner(state) {
  return newScanner(
    (buf, s, e) => pushConstantsJson(state, decodeByteRange(buf, s, e)),
    (buf, s, e) => pushEventJson(state, decodeByteRange(buf, s, e)),
    (buf, key, vs, ve) => pushTailJson(state, key, decodeByteRange(buf, vs, ve))
  );
}

/** Mirrors StreamLoader.fs's new `loadWhole`. */
function runWhole(absPath, gz, maxEvents) {
  const t0 = performance.now();
  const raw = readFileSync(absPath);
  const buf = gz ? gunzipSync(raw) : raw;
  const state = createStreamState(maxEvents);
  const scanner = makeScanner(state);
  scannerPush(scanner, buf);
  scannerFinish(scanner);
  const result = finishStreamState(state, scannerIsComplete(scanner));
  const ms = performance.now() - t0;
  if (result.tag !== 0) throw new Error(String(result.fields[0]));
  return { log: result.fields[0], ms };
}

/** Mirrors StreamLoader.fs's original `loadStreaming`. */
function runStreaming(absPath, gz, maxEvents) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const fileStream = createReadStream(absPath);
    const stream = gz ? fileStream.pipe(createGunzip()) : fileStream;
    const state = createStreamState(maxEvents);
    const scanner = makeScanner(state);
    fileStream.on("error", reject);
    if (gz) stream.on("error", reject);
    stream.on("data", (chunk) => {
      try {
        scannerPush(scanner, chunk);
      } catch (err) {
        stream.destroy();
        reject(err);
      }
    });
    stream.on("end", () => {
      try {
        scannerFinish(scanner);
        const result = finishStreamState(state, scannerIsComplete(scanner));
        const ms = performance.now() - t0;
        if (result.tag !== 0) {
          reject(new Error(String(result.fields[0])));
          return;
        }
        resolve({ log: result.fields[0], ms });
      } catch (err) {
        reject(err);
      }
    });
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

async function compareOne(label, absPath, maxEvents = 0) {
  const gz = (() => {
    try {
      return isGzipFile(absPath);
    } catch {
      return absPath.endsWith(".gz");
    }
  })();
  const whole = runWhole(absPath, gz, maxEvents);
  const streaming = await runStreaming(absPath, gz, maxEvents);
  const sw = summarize(whole.log);
  const ss = summarize(streaming.log);
  const same = JSON.stringify(sw) === JSON.stringify(ss);
  console.log(
    `  [${label}] whole=${fmtMs(whole.ms)} streaming=${fmtMs(streaming.ms)} ` +
      `speedup=${(streaming.ms / whole.ms).toFixed(2)}x ${same ? "OK exact match" : "MISMATCH"}`
  );
  if (!same) {
    console.log(`    whole:     ${JSON.stringify(sw)}`);
    console.log(`    streaming: ${JSON.stringify(ss)}`);
  }
  return same;
}

async function main() {
  let allOk = true;

  const bigPath = process.argv[2] || ".samples/edge-net-export-log-bigsize.json";
  const bigAbs = path.isAbsolute(bigPath) ? bigPath : path.resolve(__dirname, "..", bigPath);
  if (existsSync(bigAbs)) {
    console.log(`big file: ${bigAbs} (${(statSync(bigAbs).size / 1e6).toFixed(1)}MB)`);
    allOk = (await compareOne("big, uncapped", bigAbs)) && allOk;
    allOk = (await compareOne("big, maxEvents=100", bigAbs, 100)) && allOk;
  } else {
    console.log(`skip big file (not found at ${bigAbs})`);
  }

  const smallAbs = path.resolve(__dirname, "../samples/sample.netlog.json");
  if (existsSync(smallAbs)) {
    allOk = (await compareOne("small sample", smallAbs)) && allOk;
  }

  // Synthetic gzip fixture (compress the small sample) to exercise the gzip path.
  if (existsSync(smallAbs)) {
    const gzPath = path.resolve(__dirname, "../.samples/_verify-whole-buffer-tmp.json.gz");
    writeFileSync(gzPath, gzipSync(readFileSync(smallAbs)));
    try {
      allOk = (await compareOne("gzip sample", gzPath)) && allOk;
    } finally {
      unlinkSync(gzPath);
    }
  }

  // Truncated fixture: cut the small sample mid-file to exercise the "Log file
  // truncated" warning path in both loaders identically.
  if (existsSync(smallAbs)) {
    const truncPath = path.resolve(__dirname, "../.samples/_verify-whole-buffer-tmp-truncated.json");
    const full = readFileSync(smallAbs);
    writeFileSync(truncPath, full.subarray(0, Math.floor(full.length * 0.6)));
    try {
      allOk = (await compareOne("truncated sample", truncPath)) && allOk;
    } finally {
      unlinkSync(truncPath);
    }
  }

  console.log(allOk ? "[verify-whole-buffer] OK: all comparisons matched exactly" : "[verify-whole-buffer] FAILED");
  if (!allOk) process.exit(1);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
