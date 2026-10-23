> **Created:** 2026-10-23
> **Authors:** Antigravity & ami-tech-lead
> **Context:** Google Drive Work Sync (.NET 9, WinUI 3, C# 13)

# Architectural Investigation & Design: Advanced Deduplication, Orphan Reconciliation & Move Detection

This document specifies the architecture, data contracts, and design trade-offs for comprehensive enhancements to duplicate file management, remote Google Drive orphan tracking, and heuristic rename/move detection.

---

## 1. Architectural Diagnostics & Problem Statements

### 1.1 The Orphan Lifecycle Destruction Flaw (P0)
- **Current Behavior:** In `DriveSyncService.cs:323`, `RunSyncAsync` executes `PurgeOrphanHashes()` unconditionally upon completing incremental synchronization.
- **Root Cause:** `PurgeOrphanHashes()` iterates `_hashIndex` and drops all entries where `!File.Exists(localPath)`.
- **Systemic Defect:** If a user deletes a file locally and a scheduled or manual sync executes, the file's metadata (`FileId`, `DriveRelativePath`, `Hash`) is purged from `sync_hashes.json`. When the user later opens "Sugerencias de limpieza en Drive" (`DetectOrphansAsync`), the discrepancy is gone: the remote file remains permanently stranded in Google Drive without user awareness.
- **Architectural Solution:**
  - Remove `PurgeOrphanHashes()` from the post-sync pipeline of `RunSyncAsync`.
  - Maintain orphan entries in `_hashIndex` until either:
    1. The user selects them and sends them to the Google Drive Trash via `TrashOrphansAsync`, receiving confirmed remote status (`trashed` or `not_found`).
    2. The user explicitly dismisses them from the tracking index via `DismissOrphans` ("Descartar del índice").

### 1.2 Google Apps Script Execution Limits & Chunking (P0)
- **Current Behavior:** `TrashOrphansAsync` sends the entire list of selected orphans in a single HTTP POST request.
- **Constraint:** Google Apps Script Web Apps have a strict execution time cap of 30 seconds for consumer Google accounts and 6 minutes for Google Workspace. Each `DriveApp.getFileById(id).setTrashed(true)` call incurs between 150 ms and 350 ms of server-side latency. An array of 100+ files will trigger `Exceeded maximum execution time` and drop the request.
- **Architectural Solution:**
  - Introduce chunking with a default batch ceiling: `const int DefaultTrashChunkSize = 25`.
  - When `orphanList.Count <= DefaultTrashChunkSize`, execute 1 request (maintaining 100% compatibility with existing unit test mocks).
  - When `orphanList.Count > DefaultTrashChunkSize`, partition the collection into chunks of 25, transmitting them sequentially, accumulating metrics in `TrashResultSummary`, and reporting intermediate progress.

### 1.3 Duplicate Scanner Filter Blindness (P0)
- **Current Behavior:** `DetectDuplicatesAsync` instantiates `new SyncFilterOptions { MaxFileSizeBytes = long.MaxValue }` with empty exclusion sets.
- **Defect:** In software development directories, folders like `.git/objects`, `node_modules`, `bin`, and `obj` contain thousands of ephemeral, hash-identical files that the user configured as excluded from sync. Scanning them causes disk thrashing and pollutes the duplicate report.
- **Architectural Solution:**
  - Leverage `CreateFilters()` inside `DetectDuplicatesAsync` to inherit all global and source-level `ExcludedFolders` and `ExcludedExtensions`.
  - Override `scanFilters.MaxFileSizeBytes = long.MaxValue` so duplicate analysis remains unbounded by the 25 MB single-file upload ceiling while respecting ignored directories.

### 1.4 Rigid Duplicate Selection Heuristics & Ergonomics (P1)
- **Current Behavior:** The duplicate inspector classifies the file with the oldest `LastModified` timestamp as `IsPrimary = true` (protected) and all other files as `IsSelected = true` (to delete).
- **Usability Gap:** Users frequently update files in clean project directories while keeping outdated copies in `backup/` or `downloads/`. Marking the oldest as the original forces manual inverse checking across hundreds of files.
- **Architectural Solution:**
  - Introduce `DuplicatePrimaryRule` enum:
    - `OldestModified` (default, backward-compatible).
    - `NewestModified` (preserves the most recent work).
    - `ShortestPath` (preserves the cleanest/shallowest file location).
    - `PreferCleanPath` (penalizes paths containing `copy`, `copia`, `backup`, `temp`, `old`, `test`).
  - Add quick action toolbar in `SyncDialogHelper.ShowDuplicatesInspectorDialogAsync`:
    - "Seleccionar todas las copias"
    - "Deseleccionar todo"
    - "Invertir selección"
    - Rule selector ComboBox.
  - Add "Abrir en Explorador" button per file row using `explorer.exe /select,"{path}"`.

### 1.5 Real-Time Search & Metrics in Orphan Dialog (P1)
- **Usability Gap:** When dozens of files are moved or deleted, users cannot filter by file extension or parent folder in `ShowCleanupSuggestionsDialogAsync`.
- **Architectural Solution:**
  - Add real-time `TextBox` filter that dynamically updates the visible list.
  - Add "Seleccionar todo" and "Deseleccionar todo" buttons.
  - Dynamically display the total reclaimable size of checked items.

### 1.6 Heuristic Rename / Move Detection (P2)
- **Context:** When a local file is renamed or moved from `folderA/file.txt` to `folderB/file.txt`:
  - `folderA/file.txt` becomes an orphan (`File.Exists == false`).
  - `folderB/file.txt` is discovered as an out-of-sync new file.
- **Mechanism:**
  - During orphan detection, compare the SHA-256 hash and file size of each missing file against newly discovered or active files in `_hashIndex`.
  - If a missing file's hash and size match an existing known file with a different path, flag it:
    - In `DriveOrphanItem`, add `string? RenamedToPath`.
    - In the UI, display an informative badge: `Renombrado a: {Path}`.
  - In `TrashOrphansAsync`, if a file was renamed and the new file was already uploaded to Google Drive, trashing the old remote file cleans up the previous location safely without data loss.

---

## 2. Invariant & Contract Protection

1. **Deterministic Index Purging:** No key is removed from `_hashIndex` unless explicitly trashed remotely or dismissed locally.
2. **WinUI 3 Thread Safety & Stability:** All UI dialog mutations, filtering collections, and event subscriptions must strictly follow WinUI 3 lifecycle rules (named handlers, `IsLoaded` guards, dispatcher dispatching).
3. **Zero Regressions:** All 147 existing tests in `GoogleDriveWorkSync.Tests` must continue to pass with 100% success.
