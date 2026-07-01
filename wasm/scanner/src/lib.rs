//! From-scratch, `#![no_std]`, zero-dependency WASM SIMD structural scanner for
//! NetLog JSON dumps -- an accelerator for `Netlog.Core.ByteJsonScanner` (see
//! `src/Core/ByteJsonScanner.fs`), used opportunistically by the Extension Host with
//! automatic fallback to the pure F# scanner on any load/instantiate/scan failure
//! (see `src/Extension/WasmScanner.fs` / `src/Extension/WasmStreamLoader.fs`).
//!
//! Scope: this mirrors `ByteJsonScanner`'s WHOLE-BUFFER semantics exactly (the same
//! shape already used by `ParallelStreamLoader.runPrepass`, which feeds one giant
//! `Push` call over an entire in-memory file) -- NOT the incremental streaming
//! chunk-by-chunk shape. There is no cross-call state to carry, no buffer trimming,
//! so a value's exact byte range can always be found by scanning forward from a
//! known-good starting position within a single `scan` call. Top-level structure:
//! `{ "constants": {...}, "events": [...], <other keys>: ... }` in any order.
//!
//! SIMD strategy (kept deliberately simple over a "clever" bit-trick escaped-quote
//! algorithm, to keep this easy to verify correct): before each scalar step, check
//! whether the next 16 bytes contain ANY of the structurally-significant bytes for
//! the current sub-scan (quote/backslash for strings; quote/backslash/braces/
//! brackets for object-or-array values; comma/close-brace/close-bracket/whitespace
//! for primitives). If none are present, no scanner state (in-string / escape /
//! nesting depth) can possibly change over those 16 bytes, so they are skipped in
//! one vector compare instead of 16 scalar iterations. If any ARE present, fall back
//! to exactly one scalar byte-step (identical logic to the plain scalar algorithm),
//! then immediately retry the 16-byte fast-skip check at the new position. This is
//! correct by construction (it never changes what gets matched, only how fast
//! "boring" stretches -- long string values, digit runs, whitespace -- are skipped)
//! and needs no cross-chunk carry state, unlike simdjson-style approaches.
//!
//! Memory model: no heap allocator (no `alloc` crate). The JS/Node caller reserves
//! space for the input buffer and all output arrays via `alloc` (a trivial bump
//! allocator over this module's own linear memory, grown with `memory.grow` as
//! needed) and writes/reads them directly through the exported `memory`. `reset_alloc`
//! rewinds the bump pointer so memory can be reused across multiple file loads
//! without ever needing to free/reallocate.
#![no_std]

use core::panic::PanicInfo;

#[panic_handler]
fn on_panic(_info: &PanicInfo) -> ! {
    // Trap immediately (WASM `unreachable`) rather than loop forever: a trap
    // surfaces to the JS caller as a catchable `WebAssembly.RuntimeError`, so any
    // (should-never-happen) bug here safely triggers the F# fallback instead of
    // hanging the Extension Host.
    core::arch::wasm32::unreachable()
}

// ---------------------------------------------------------------------------
// Bump allocator: the only memory management this module needs. One arena, reset
// between scans (`reset_alloc`), never freed piecemeal.
// ---------------------------------------------------------------------------

// `__heap_base` is a symbol synthesized by wasm-ld (the linker rustc uses for
// wasm32-unknown-unknown): its ADDRESS (not its value -- it has no meaningful
// value, it's a zero-sized linker marker) is the first byte after all of this
// module's own static data. Handing out bump-allocated memory starting at address
// 0 would silently overlap that static data (including this module's own statics,
// e.g. BUMP_OFFSET below) -- writing the input buffer there would corrupt them.
extern "C" {
    static __heap_base: u8;
}

#[inline]
fn heap_base() -> usize {
    core::ptr::addr_of!(__heap_base) as usize
}

/// 0 is used as a sentinel meaning "not yet initialized from `__heap_base`" --
/// address 0 is always inside Rust's own static data, so it can never be a valid
/// bump offset once initialized.
static mut BUMP_OFFSET: usize = 0;

const PAGE_SIZE: usize = 65536;

/// Reserves `size` bytes in this module's linear memory (growing it if needed) and
/// returns a byte offset usable as a pointer from JS (`memory.buffer` at that
/// offset). 8-byte aligned so callers can treat the result as a `u32`/`f64` array
/// base pointer safely.
#[no_mangle]
pub extern "C" fn alloc(size: u32) -> u32 {
    unsafe {
        if BUMP_OFFSET == 0 {
            BUMP_OFFSET = heap_base();
        }
        let aligned = (BUMP_OFFSET + 7) & !7usize;
        let end = aligned + size as usize;
        let current_bytes = core::arch::wasm32::memory_size(0) * PAGE_SIZE;
        if end > current_bytes {
            let extra = end - current_bytes;
            let extra_pages = (extra + PAGE_SIZE - 1) / PAGE_SIZE;
            if core::arch::wasm32::memory_grow(0, extra_pages) == usize::MAX {
                // Growth failed (out of memory) -- trap so the caller falls back.
                core::arch::wasm32::unreachable();
            }
        }
        BUMP_OFFSET = end;
        aligned as u32
    }
}

/// Rewinds the bump pointer so memory can be reused by the next `scan` call.
#[no_mangle]
pub extern "C" fn reset_alloc() {
    unsafe {
        BUMP_OFFSET = 0;
    }
}

// ---------------------------------------------------------------------------
// Byte constants (mirrors ByteJsonScanner.fs exactly).
// ---------------------------------------------------------------------------

const QUOTE: u8 = 34; // '"'
const BACKSLASH: u8 = 92; // '\'
const OPEN_BRACE: u8 = 123; // '{'
const CLOSE_BRACE: u8 = 125; // '}'
const OPEN_BRACKET: u8 = 91; // '['
const CLOSE_BRACKET: u8 = 93; // ']'
const COMMA: u8 = 44; // ','
const COLON: u8 = 58; // ':'
const SPACE: u8 = 32;
const TAB: u8 = 9;
const NEWLINE: u8 = 10;
const CR: u8 = 13;

#[inline]
fn is_ws(b: u8) -> bool {
    b == SPACE || b == NEWLINE || b == CR || b == TAB
}

// ---------------------------------------------------------------------------
// SIMD128 16-byte "does this window contain any of these special bytes" checks.
// ---------------------------------------------------------------------------

#[inline]
unsafe fn load16(buf: &[u8], pos: usize) -> core::arch::wasm32::v128 {
    core::arch::wasm32::v128_load(buf.as_ptr().add(pos) as *const core::arch::wasm32::v128)
}

#[inline]
fn any_byte_eq(v: core::arch::wasm32::v128, needle: u8) -> bool {
    use core::arch::wasm32::*;
    let n = i8x16_splat(needle as i8);
    i8x16_bitmask(i8x16_eq(v, n)) != 0
}

/// True if any byte in buf[pos..pos+16) is `"` or `\`.
#[inline]
fn chunk_has_quote_or_backslash(buf: &[u8], pos: usize) -> bool {
    unsafe {
        let v = load16(buf, pos);
        any_byte_eq(v, QUOTE) || any_byte_eq(v, BACKSLASH)
    }
}

/// True if any byte in buf[pos..pos+16) is `"`, `\`, `{`, `}`, `[`, or `]`.
#[inline]
fn chunk_has_string_or_bracket(buf: &[u8], pos: usize) -> bool {
    unsafe {
        let v = load16(buf, pos);
        any_byte_eq(v, QUOTE)
            || any_byte_eq(v, BACKSLASH)
            || any_byte_eq(v, OPEN_BRACE)
            || any_byte_eq(v, CLOSE_BRACE)
            || any_byte_eq(v, OPEN_BRACKET)
            || any_byte_eq(v, CLOSE_BRACKET)
    }
}

/// True if any byte in buf[pos..pos+16) is `,`, `}`, `]`, space, tab, CR, or LF.
#[inline]
fn chunk_has_terminator(buf: &[u8], pos: usize) -> bool {
    unsafe {
        let v = load16(buf, pos);
        any_byte_eq(v, COMMA)
            || any_byte_eq(v, CLOSE_BRACE)
            || any_byte_eq(v, CLOSE_BRACKET)
            || any_byte_eq(v, SPACE)
            || any_byte_eq(v, TAB)
            || any_byte_eq(v, NEWLINE)
            || any_byte_eq(v, CR)
    }
}

// ---------------------------------------------------------------------------
// Structural scanning, mirroring ByteJsonScanner.fs's scanStringEnd/scanValueEnd.
// ---------------------------------------------------------------------------

/// Given the opening-quote index `i`, returns the index just past the closing
/// quote, or `None` if the string does not close within the buffer (truncated).
fn scan_string_end(buf: &[u8], n: usize, i: usize) -> Option<usize> {
    let mut j = i + 1;
    loop {
        while j + 16 <= n && !chunk_has_quote_or_backslash(buf, j) {
            j += 16;
        }
        if j >= n {
            return None;
        }
        let b = buf[j];
        if b == BACKSLASH {
            if j + 1 < n {
                j += 2;
            } else {
                return None;
            }
        } else if b == QUOTE {
            return Some(j + 1);
        } else {
            j += 1;
        }
    }
}

/// Given the first-byte index `i` of a JSON value, returns the index just past the
/// value, or `None` if it is not fully present (truncated). Objects/arrays are
/// matched by string-aware depth; primitives end at the first structural
/// delimiter/whitespace -- exact mirror of ByteJsonScanner.fs's `scanValueEnd`.
fn scan_value_end(buf: &[u8], n: usize, i: usize) -> Option<usize> {
    let b = buf[i];
    if b == QUOTE {
        scan_string_end(buf, n, i)
    } else if b == OPEN_BRACE || b == OPEN_BRACKET {
        let mut depth: i32 = 0;
        let mut j = i;
        let mut in_str = false;
        let mut esc = false;
        loop {
            while j + 16 <= n && !in_str && !esc && !chunk_has_string_or_bracket(buf, j) {
                j += 16;
            }
            if j >= n {
                return None;
            }
            let ch = buf[j];
            if in_str {
                if esc {
                    esc = false;
                } else if ch == BACKSLASH {
                    esc = true;
                } else if ch == QUOTE {
                    in_str = false;
                }
            } else if ch == QUOTE {
                in_str = true;
            } else if ch == OPEN_BRACE || ch == OPEN_BRACKET {
                depth += 1;
            } else if ch == CLOSE_BRACE || ch == CLOSE_BRACKET {
                depth -= 1;
                if depth == 0 {
                    return Some(j + 1);
                }
            }
            j += 1;
        }
    } else {
        let mut j = i;
        loop {
            while j + 16 <= n && !chunk_has_terminator(buf, j) {
                j += 16;
            }
            if j >= n {
                return None;
            }
            let ch = buf[j];
            if ch == COMMA || ch == CLOSE_BRACE || ch == CLOSE_BRACKET || is_ws(ch) {
                return Some(j);
            }
            j += 1;
        }
    }
}

#[inline]
fn matches_literal(buf: &[u8], start: usize, end: usize, lit: &[u8]) -> bool {
    if end - start != lit.len() {
        return false;
    }
    let mut k = 0;
    while k < lit.len() {
        if buf[start + k] != lit[k] {
            return false;
        }
        k += 1;
    }
    true
}

// ---------------------------------------------------------------------------
// Top-level whole-buffer scan (mirrors ByteJsonScanner.Scanner's Process() state
// machine: BeforeRoot -> InRoot -> ExpectColon -> ExpectValue{ / InEvents} -> ...).
// ---------------------------------------------------------------------------

#[derive(PartialEq, Clone, Copy)]
enum St {
    BeforeRoot,
    InRoot,
    ExpectColon,
    ExpectValue,
    InEvents,
}

#[inline]
unsafe fn out_u32(ptr: u32, idx: usize) -> *mut u32 {
    (ptr as *mut u32).add(idx)
}

/// Scans `buf[0..buf_len)` (already written into this module's memory at `buf_ptr`,
/// e.g. via `alloc` + a JS-side `Uint8Array` write) for the top-level "constants"
/// value, every "events" array element, and every other ("tail") top-level
/// key/value pair.
///
/// Output layout (all offsets are into `buf`, i.e. directly comparable with the
/// original file bytes):
/// - `event_starts`/`event_ends` (each `event_cap` x u32): one entry per element of
///   the top-level "events" array, in file order. Only the first `min(found,
///   event_cap)` entries are valid; if more were found than `event_cap`, `out[5]`
///   (overflow) is set to 1 and the caller should fall back to the F# scanner.
/// - `tail` (`tail_cap` x 4 u32s: keyStart, keyEnd, valStart, valEnd): one entry per
///   top-level key other than "constants"/"events". Same overflow behavior.
/// - `out` (6 x u32): `[eventCount, constStart, constEnd, tailCount, isComplete,
///   overflow]`. `constStart`/`constEnd` are `u32::MAX` if no "constants" key was found.
#[no_mangle]
pub unsafe extern "C" fn scan(
    buf_ptr: u32,
    buf_len: u32,
    event_starts_ptr: u32,
    event_ends_ptr: u32,
    event_cap: u32,
    tail_ptr: u32,
    tail_cap: u32,
    out_ptr: u32,
) {
    let buf = core::slice::from_raw_parts(buf_ptr as *const u8, buf_len as usize);
    let n = buf.len();

    let mut pos: usize = 0;
    let mut event_count: u32 = 0;
    let mut tail_count: u32 = 0;
    let mut const_start: u32 = u32::MAX;
    let mut const_end: u32 = u32::MAX;
    let mut overflow = false;
    let mut is_complete = false;

    let mut state = St::BeforeRoot;
    let mut key_start: usize = 0;
    let mut key_end: usize = 0;
    let mut key_is_events = false;
    let mut key_is_constants = false;

    'scan: loop {
        while pos < n && is_ws(buf[pos]) {
            pos += 1;
        }
        if pos >= n {
            break;
        }
        match state {
            St::BeforeRoot => {
                if buf[pos] == OPEN_BRACE {
                    pos += 1;
                    state = St::InRoot;
                } else {
                    pos += 1; // tolerate leading junk before the root object
                }
            }
            St::InRoot => {
                let c = buf[pos];
                if c == CLOSE_BRACE {
                    is_complete = true;
                    break 'scan;
                } else if c == COMMA {
                    pos += 1;
                } else if c == QUOTE {
                    match scan_string_end(buf, n, pos) {
                        Some(end_idx) => {
                            key_start = pos + 1;
                            key_end = end_idx - 1;
                            key_is_events = matches_literal(buf, key_start, key_end, b"events");
                            key_is_constants =
                                !key_is_events && matches_literal(buf, key_start, key_end, b"constants");
                            pos = end_idx;
                            state = St::ExpectColon;
                        }
                        None => break 'scan,
                    }
                } else {
                    pos += 1; // tolerate stray characters
                }
            }
            St::ExpectColon => {
                if buf[pos] == COLON {
                    pos += 1;
                    state = St::ExpectValue;
                } else {
                    pos += 1; // tolerate
                }
            }
            St::ExpectValue => {
                if key_is_events && buf[pos] == OPEN_BRACKET {
                    pos += 1;
                    state = St::InEvents;
                } else {
                    match scan_value_end(buf, n, pos) {
                        Some(end_idx) => {
                            if key_is_constants {
                                const_start = pos as u32;
                                const_end = end_idx as u32;
                            } else {
                                if tail_count < tail_cap {
                                    let base = out_u32(tail_ptr, (tail_count * 4) as usize);
                                    *base.add(0) = key_start as u32;
                                    *base.add(1) = key_end as u32;
                                    *base.add(2) = pos as u32;
                                    *base.add(3) = end_idx as u32;
                                } else {
                                    overflow = true;
                                }
                                tail_count += 1;
                            }
                            pos = end_idx;
                            state = St::InRoot;
                        }
                        None => break 'scan,
                    }
                }
            }
            St::InEvents => {
                let c = buf[pos];
                if c == CLOSE_BRACKET {
                    pos += 1;
                    state = St::InRoot;
                } else if c == COMMA {
                    pos += 1;
                } else {
                    match scan_value_end(buf, n, pos) {
                        Some(end_idx) => {
                            if event_count < event_cap {
                                *out_u32(event_starts_ptr, event_count as usize) = pos as u32;
                                *out_u32(event_ends_ptr, event_count as usize) = end_idx as u32;
                            } else {
                                overflow = true;
                            }
                            event_count += 1;
                            pos = end_idx;
                        }
                        None => break 'scan,
                    }
                }
            }
        }
    }

    *out_u32(out_ptr, 0) = event_count;
    *out_u32(out_ptr, 1) = const_start;
    *out_u32(out_ptr, 2) = const_end;
    *out_u32(out_ptr, 3) = tail_count;
    *out_u32(out_ptr, 4) = if is_complete { 1 } else { 0 };
    *out_u32(out_ptr, 5) = if overflow { 1 } else { 0 };
}
