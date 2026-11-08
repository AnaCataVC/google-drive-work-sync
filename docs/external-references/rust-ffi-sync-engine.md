> **Created:** 2026-11-07
> **Last Updated:** 2026-11-07

# Rust Native Sync Engine (`cdylib` FFI + `blake3` + `rayon`) Research

## 1. Objective & Scope

Evaluate and specify the integration of a high-performance native Rust library (`rust_sync_engine.dll`) into **Google Drive Work Sync** (`.NET 9` / unpackaged WinUI 3) via source-generated P/Invoke (`[LibraryImport]`). The native engine accelerates:
1. **Local Directory Tree Indexing:** Parallel breadth/depth traversal with folder exclusion, extension filtering, NTFS reparse point (junction/symlink) skipping, and explicit unreadable directory/file error reporting.
2. **Cryptographic Content Hashing (`blake3`):** SIMD-accelerated (AVX2 / AVX-512 / SSE4.1) and multithreaded (`rayon`) hashing for change detection, orphan rename verification, and multi-tier duplicate detection (16 KB header hash + full stream hash).
3. **Incremental State Diffing:** Fast-path metadata comparison (`LastWriteTimeUtcTicks` + `FileSize`) and batch diffing against the local hash index before staging uploads to Google Apps Script.

---

## 2. Crate Ecosystem & Version Benchmarks (2026)

| Crate | Recommended Version | Role & Technical Rationale |
| :--- | :--- | :--- |
| **`blake3`** | `1.8.x` (`features = ["rayon"]`) | Official Rust implementation of the BLAKE3 cryptographic hash function. Achieves 4x–10x higher throughput than SHA-256 via runtime CPU feature detection (AVX-512, AVX2, SSE4.1, NEON) and unlimited Merkle-tree parallelism. |
| **`rayon`** | `1.10+` | Work-stealing data-parallelism library used to parallelize file hashing and multi-tier duplicate bucket verification across CPU cores. |
| **`ignore` / `walkdir`** | `ignore = "0.4"` / `walkdir = "2.5"` | Maintained by Andrew Gallant (`BurntSushi`, author of `ripgrep`). Note: `jwalk` (`0.6.1`) is unmaintained/yanked on crates.io and should be avoided in favor of `ignore::WalkBuilder` or `walkdir` + `rayon`. |
| **`serde` + `serde_json`** | `1.0` | Deterministic UTF-8 JSON serialization across the C-ABI boundary for complex batch payloads (scan results, diff requests, duplicate groups), avoiding brittle nested `#[repr(C)]` struct array marshalling. |

### Reference Links
- [BLAKE3 Official Rust Crate (`docs.rs/blake3`)](https://docs.rs/blake3/latest/blake3/)
- [Rayon Parallel Iterators (`docs.rs/rayon`)](https://docs.rs/rayon/latest/rayon/)
- [Ignore Crate (`docs.rs/ignore`)](https://docs.rs/ignore/latest/ignore/)
- [.NET 9 Source-Generated P/Invoke (`LibraryImportAttribute`)](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)

---

## 3. FFI Boundary & Memory Safety Patterns (`.NET 9` <-> Rust `cdylib`)

### 3.1 Batch JSON FFI vs. Per-File P/Invoke
- Calling P/Invoke per file (`compute_hash(path)`) incurs UTF-8 marshalling overhead on every call and prevents Rust from parallelizing file I/O across its own `rayon` thread pool.
- **Recommended Pattern:** Expose both:
  1. **Single-file primitive entry points** (`rust_compute_blake3_file`, `rust_compute_blake3_header`) writing directly into a caller-allocated `Span<byte>` buffer in C# (zero native heap allocation, zero `free` call needed).
  2. **Batch engine entry points** (`rust_scan_and_diff_json`, `rust_detect_duplicates_json`) that accept a UTF-8 JSON request pointer + atomic cancellation flag (`*const AtomicBool`) and return a Rust-allocated UTF-8 JSON buffer (`*mut c_char`), freed deterministically in a C# `finally` block via `rust_free_string`.

### 3.2 Panic Safety Across `extern "C"`
- Unwinding a Rust `panic!` across an `extern "C"` boundary into the .NET CLR is Undefined Behavior (UB) and terminates the process without running managed `finally` blocks or `App.UnhandledException`.
- Every `#[no_mangle] pub extern "C" fn` MUST wrap its body in `std::panic::catch_unwind` and the release profile in `Cargo.toml` must configure clean optimization settings.

### 3.3 Windows NTFS File Sharing & `mmap` Avoidance
- Using memory-mapped files (`memmap2`) on Windows locks the mapped file against truncation and can raise hardware `EXCEPTION_IN_PAGE_ERROR` if an external process modifies the file concurrently.
- Instead, open files on Windows using `std::os::windows::fs::OpenOptionsExt::share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)` (`0x00000001 | 0x00000002 | 0x00000004`) matching C#'s `FileShare.ReadWrite`, reading into a 64 KB buffer (or feeding chunks to `blake3::Hasher`).
