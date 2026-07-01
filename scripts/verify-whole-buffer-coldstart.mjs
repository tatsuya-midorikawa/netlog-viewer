// Cold-start (fresh process, single call) comparison of StreamLoader.fs's new
// whole-buffer path vs the original incremental streaming path, to confirm the
// improvement seen in scripts/verify-whole-buffer.mjs (which runs both in one
// process) isn't an artifact of call ordering / JIT warmup carrying over. Not part
// of the permanent test suite -- delete after use.
//
// Usage: node scripts/verify-whole-buffer-coldstart.mjs <whole|streaming> [path]

import { createReadStream, readFileSync, existsSync } from "node:fs";
import { createGunzip } from "node:zlib";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

function isGzipFile(absPath) {
  const buf = readFileSync(absPath, { flag: "r" }).subarray(0, 2);
  return buf.length >= 2 && buf[0] === 0x1f && buf[1] === 0x8b;
}

async function main() {
  const mode = process.argv[2];
  const filePath = process.argv[3] || ".samples/edge-net-export-log-bigsize.json";
  const absPath = path.isAbsolute(filePath) ? filePath : path.resolve(__dirname, "..", filePath);
  if (!existsSync(absPath)) {
    console.log(`skip: file not found at ${absPath}`);
    return;
  }
  const gz = isGzipFile(absPath);

  const {
    Scanner_$ctor_788EEBC8: newScanner,
    Scanner__Push_Z3F6BC7B1: scannerPush,
    Scanner__Finish: scannerFinish,
    Scanner__get_IsComplete: scannerIsComplete,
    decodeByteRange,
  } = await import("../build/extension/Core/ByteJsonScanner.js");
  const {
    create: createStreamState,
    pushConstantsJson,
    pushEventJson,
    pushTailJson,
    finish: finishStreamState,
  } = await import("../build/extension/Core/StreamingParser.js");

  const makeScanner = (state) =>
    newScanner(
      (buf, s, e) => pushConstantsJson(state, decodeByteRange(buf, s, e)),
      (buf, s, e) => pushEventJson(state, decodeByteRange(buf, s, e)),
      (buf, key, vs, ve) => pushTailJson(state, key, decodeByteRange(buf, vs, ve))
    );

  if (mode === "whole") {
    const t0 = performance.now();
    const { gunzipSync } = await import("node:zlib");
    const raw = readFileSync(absPath);
    const buf = gz ? gunzipSync(raw) : raw;
    const state = createStreamState(0);
    const scanner = makeScanner(state);
    scannerPush(scanner, buf);
    scannerFinish(scanner);
    const result = finishStreamState(state, scannerIsComplete(scanner));
    const ms = performance.now() - t0;
    console.log(`whole coldMs=${ms.toFixed(1)} events=${result.fields[0].Events.length}`);
  } else if (mode === "streaming") {
    const t0 = performance.now();
    await new Promise((resolve, reject) => {
      const fileStream = createReadStream(absPath);
      const stream = gz ? fileStream.pipe(createGunzip()) : fileStream;
      const state = createStreamState(0);
      const scanner = makeScanner(state);
      fileStream.on("error", reject);
      stream.on("data", (chunk) => scannerPush(scanner, chunk));
      stream.on("end", () => {
        scannerFinish(scanner);
        const result = finishStreamState(state, scannerIsComplete(scanner));
        const ms = performance.now() - t0;
        console.log(`streaming coldMs=${ms.toFixed(1)} events=${result.fields[0].Events.length}`);
        resolve();
      });
    });
  } else {
    console.log("usage: node scripts/verify-whole-buffer-coldstart.mjs <whole|streaming> [path]");
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
