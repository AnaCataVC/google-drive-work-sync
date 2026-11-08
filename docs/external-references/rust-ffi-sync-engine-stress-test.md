> **Created:** 2026-11-07
> **Last Updated:** 2026-11-07
> **Status:** Active
> **Scope:** Rust Native FFI Sync Engine (`rust-sync-engine` <-> `DriveSyncService`)

# Adversarial Stress-Test: Hybrid WinUI 3 + Rust (`blake3` / Parallel FS / Diffing) Engine

## 1. Premortem & Operational Failure Modes

### [Critical / Blocker] 1.1 Silent Full Re-Upload Storm & 64-Hex Collision Between SHA-256 and BLAKE3
- **Attack Vector:** Both `SHA-256` and `BLAKE3` produce 256-bit digests represented as 64-character lowercase hexadecimal strings. Existing user installations already have `sync_hashes.json` populated with `SHA-256` hashes, along with critical `FileId` and `DriveRelativePath` metadata.
  - If the engine switches to `BLAKE3` without hash-algorithm disambiguation or migration:
    1. Any file whose timestamp is touched (`LastWriteTimeUtcTicks` changes) without content modification will have its new `BLAKE3` hash compared against its cached `SHA-256` hash. They will never match, causing a false-positive re-upload.
    2. In `DetectDuplicatesInternal` (Tier 2 vs Tier 3B) and `DetectOrphansInternalAsync` (sibling rename detection), cached entries hold `SHA-256` while newly hashed files on disk produce `BLAKE3`. Two identical files will fail to match hashes, silently breaking duplicate detection and rename detection.
    3. Conversely, if `sync_hashes.json` is naively wiped on upgrade, the user loses all cached `FileId` bindings and re-uploads 100% of their workspace to Google Drive over slow Apps Script HTTP batches.
- **Hardening Countermeasure:**
  - **In-Place Lazy/Startup Hash Migration & Compatibility:** When loading or comparing against `sync_hashes.json`, if an entry's `LastWriteTimeUtcTicks` and `FileSize` still match the local file on disk, the file is provably unchanged. When a full hash comparison is required (or during a one-time index upgrade pass), recompute the `BLAKE3` hash locally for existing valid entries on disk and update `sync_hashes.json` **in-place** while preserving `FileId`, `DriveRelativePath`, and `LocalFilePath` — zero network uploads triggered.

### [Critical / Blocker] 1.2 Violation of Sync Invariant #2 ("Could Not Read Is an Error, Not Unchanged")
- **Attack Vector:** Standard Rust parallel directory walkers (`ignore`, `walkdir`) and `rayon::par_iter().filter_map(Result::ok)` make it dangerously easy to silently drop `Err(io::Error)` entries when a directory denies read permission (`ERROR_ACCESS_DENIED`) or a file is exclusively locked by another Windows process (`ERROR_SHARING_VIOLATION` / `0x80070020`). If dropped, `DriveSyncService` never records them in `sync_errors.json` and reports the sync as *"Al día"* while files are missing in Drive.
- **Hardening Countermeasure:**
  - The Rust scan/diff engine MUST explicitly partition outcomes into `scanned_files`, `unreadable_directories`, and `unreadable_files` (carrying the raw Win32 OS error code `err.raw_os_error()`), mapping directly to `DriveSyncService.CategorizeError` (`"Carpeta sin acceso"`, `"Archivo en uso / Bloqueado"`, `"Permiso denegado"`).

### [Major / Hardening Required] 1.3 Violation of Sync Invariant #1 ("Capture Metadata Before Hashing")
- **Attack Vector:** If the Rust engine hashes the file stream first and queries `fs::metadata()` afterwards, a file modified mid-hash will record the post-modification `LastWriteTimeUtcTicks` alongside the pre-modification hash. Subsequent sync runs will hit the metadata fast-path and permanently skip the newer content.
- **Hardening Countermeasure:**
  - In Rust, always capture `(file_size, last_write_time_ticks)` from `fs::metadata` **before** opening and hashing the file stream. Convert Windows `FILETIME` (100-nanosecond intervals since January 1, 1601 UTC) to .NET `DateTime.UtcNow.Ticks` (`filetime_ticks + 504911232000000000`) with exact bit-for-bit parity with `FileInfo.LastWriteTimeUtc.Ticks`.

---

## 2. Concurrency, Race Conditions & State Drift

### [Major / Hardening Required] 2.1 Uninterruptible Native Call Blocking `CancellationToken`
- **Attack Vector:** When the user clicks "Cancelar" in the UI or closes the app during a scan/hash of a massive folder tree, C# triggers `CancellationTokenSource.Cancel()`. If execution is inside a blocking `extern "C"` Rust call running `rayon` across all CPU cores, C# cannot abort the native thread without corrupting the process.
- **Hardening Countermeasure:**
  - Pass an atomic cancellation pointer (`*const AtomicU8` / `AtomicBool`) or callback into the Rust batch FFI functions. Poll the cancellation flag inside the directory walker and between 64 KB read chunks in the `blake3` hasher, returning immediately when signaled.

### [Major / Hardening Required] 2.2 Windows File Locking Hazard with `memmap2` (`mmap`)
- **Attack Vector:** Using memory-mapped I/O (`memmap2` + `blake3::Hasher::update_mmap_rayon`) on Windows creates an OS section object that prevents other processes (such as Word, Excel, VS Code, or Git) from truncating or overwriting the file while mapped, and raises an uncatchable hardware SEH `STATUS_IN_PAGE_ERROR` if a network/cloud drive disconnects mid-read.
- **Hardening Countermeasure:**
  - Ban `memmap2` on Windows. Use standard `std::fs::OpenOptions` with `share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)` (`0x07`) and buffered reads (64 KB–128 KB chunks), using `blake3::Hasher::update_rayon` for in-memory buffers of large files (`>= 256 KB`).

---

## 3. Cost & Resource Explosion (Memory & Performance Vectors)

### [Major / Hardening Required] 3.1 Native Heap Leaks Across FFI (`CString::into_raw`)
- **Attack Vector:** Returning variable-length JSON payloads from Rust via `CString::into_raw()` allocates memory on the Rust allocator (`malloc` / `HeapAlloc`). If C# marshals the string via `Marshal.PtrToStringUTF8` without calling back into Rust's `rust_free_string(ptr)` (or if an exception occurs before freeing), every sync preview leaks megabytes of unmanaged RAM.
- **Hardening Countermeasure:**
  - For single-file hash calls (`ComputeBlake3`, `ComputeHeaderBlake3`), pass a stack-allocated C# `Span<byte>` (`stackalloc byte[32]`) directly to Rust so Rust writes the 32-byte digest with **zero heap allocation**.
  - For batch JSON calls, wrap the returned `IntPtr` in a `try / finally` block calling `NativeSyncEngine.FreeString(ptr)` immediately after `Marshal.PtrToStringUTF8`.

---

## 4. Security, Authorization & Abuse Vectors

### [Major / Hardening Required] 4.1 Infinite Recursion & Arbitrary File Read via NTFS Junctions / Reparse Points
- **Attack Vector:** Windows directory junctions (`mklink /J`) and symbolic links can form directory cycles or point outside the monitored workspace (e.g., into `%USERPROFILE%\.ssh`).
- **Hardening Countermeasure:**
  - Enforce `FILE_ATTRIBUTE_REPARSE_POINT` (`0x0400`) inspection on every directory entry in Rust (`metadata.file_attributes() & 0x0400 != 0`) and never descend into reparse points, preserving exact parity with `DriveSyncService.ScanFolder`.

### [Critical / Blocker] 4.2 Uncaught Rust `panic!` Unwinding Across `extern "C"` ABI
- **Attack Vector:** Any unexpected `.unwrap()` or out-of-bounds slice in Rust that panics across `extern "C"` into the .NET 9 runtime causes instant Undefined Behavior (`STATUS_STACK_BUFFER_OVERRUN` / abrupt process death) bypassing `DiagnosticLogger`.
- **Hardening Countermeasure:**
  - Every `extern "C"` function MUST wrap its implementation in `std::panic::catch_unwind`, logging/returning an error code on panic, and forbid `.unwrap()` in production code paths.

---

## 5. Developer Friction & Backward Compatibility Breakage

### [Major / Hardening Required] 5.1 Missing Native DLL in Test Runner / CI / Unsupported Architectures
- **Attack Vector:** If `rust_sync_engine.dll` is not copied to `GoogleDriveWorkSync.Tests/bin/...` or fails to load, all unit tests in `GoogleDriveWorkSync.Tests` fail with `DllNotFoundException`.
- **Hardening Countermeasure:**
  - Implement a managed fallback in `NativeSyncEngine` (`IsNativeAvailable` probe on startup) so if `rust_sync_engine.dll` is absent on a non-x64 target, the system transparently falls back to managed C# (`BLAKE3` / `SHA-256` + `ScanFolder`), while MSBuild automatically copies `rust_sync_engine.dll` to the output directory of both `GoogleDriveWorkSync` and `GoogleDriveWorkSync.Tests`.
