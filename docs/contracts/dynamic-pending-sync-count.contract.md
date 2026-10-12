# Formal Contract: Dynamic Pending Sync Count & Real-Time Pruning

**Feature Slug:** `dynamic-pending-sync-count`  
**Status:** Frozen Contract (Cleanroom Specification)  
**Date:** 2026-10-11  

---

## 1. Scope & Objective

This contract specifies the behavioral and interface requirements for real-time reactive updating of pending synchronization counts and items during active sync operations in **Google Drive Work Sync**.

As files are processed and uploaded in batches:
1. The pending count (`OutOfSyncCount`) decrements reactively.
2. The secondary counters for new (`NewFilesCount`) and modified (`ModifiedFilesCount`) files decrement in accordance with each completed file's classification.
3. Successfully synced files are pruned in real time from the in-memory observable collection (`OutOfSyncFilesList`).
4. The action button label (`SyncActionTitle`) reflects the active remaining pending count.

---

## 2. Public Interfaces & Data Contracts

### 2.1 Model: `CompletedSyncItem`
Represents an individual file completed during an incremental batch report.

```csharp
namespace GoogleDriveWorkSync.Models;

public class CompletedSyncItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool IsNew { get; set; }
    public bool Success { get; set; }
}
```

### 2.2 Model Extension: `SyncProgressReport`
`GoogleDriveWorkSync.Models.SyncProgressReport` must expose the following additional public properties while preserving all existing properties:

```csharp
namespace GoogleDriveWorkSync.Models;

public class SyncProgressReport
{
    // Existing properties:
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public string CurrentFileName { get; set; } = string.Empty;
    public int UploadedCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public int Percentage => TotalFiles == 0 ? 100 : (int)Math.Round(ProcessedFiles * 100.0 / TotalFiles);

    // Dynamic tracking extensions:
    public int TotalPendingFiles { get; set; }
    public int RemainingPendingFiles => Math.Max(0, TotalPendingFiles - UploadedCount - ErrorCount);
    public IReadOnlyList<CompletedSyncItem> CompletedItems { get; set; } = Array.Empty<CompletedSyncItem>();
}
```

### 2.3 ViewModel Behavioral Surface: `WorkSyncViewModel`
`GoogleDriveWorkSync.ViewModels.WorkSyncViewModel` handles `SyncProgressChanged` events:

```csharp
namespace GoogleDriveWorkSync.ViewModels;

public partial class WorkSyncViewModel : ObservableObject
{
    // Observable properties impacted:
    public int OutOfSyncCount { get; set; }
    public int NewFilesCount { get; set; }
    public int ModifiedFilesCount { get; set; }
    public bool HasOutOfSyncFiles { get; set; }
    public string SyncActionTitle { get; set; }
    public ObservableCollection<OutOfSyncFile> OutOfSyncFilesList { get; }
    
    // Method/Handler reacting to progress reports:
    // When a report with TotalPendingFiles > 0 or CompletedItems is received,
    // updates state on the UI thread.
}
```

---

## 3. Behavioral Acceptance Criteria

### Acceptance Criterion 1: Regressive Pending Count Decrement
- **Given** a synchronization session starts with $N > 0$ pending files (`TotalPendingFiles = N`).
- **When** a batch progress report is published indicating $K$ files have been uploaded (`UploadedCount = K`, `RemainingPendingFiles = N - K`).
- **Then** `OutOfSyncCount` MUST update to equal `RemainingPendingFiles`.
- **And** `HasOutOfSyncFiles` MUST be `true` while `RemainingPendingFiles > 0`, and `false` when `RemainingPendingFiles == 0`.
- **And** `SyncActionTitle` MUST display `"Sincronizando ({RemainingPendingFiles} pendientes)..."` when `RemainingPendingFiles > 0`, and `"Completando sincronización..."` when `RemainingPendingFiles == 0`.

### Acceptance Criterion 2: Granular Decrement of New vs Modified Files
- **Given** `NewFilesCount = N_new` and `ModifiedFilesCount = N_mod`.
- **When** `CompletedItems` contains a successfully uploaded item (`Success == true`):
  - If `item.IsNew == true`: `NewFilesCount` MUST decrement by 1 (bounded at $\ge 0$).
  - If `item.IsNew == false`: `ModifiedFilesCount` MUST decrement by 1 (bounded at $\ge 0$).
- **When** `CompletedItems` contains an item where `Success == false`:
  - Neither `NewFilesCount` nor `ModifiedFilesCount` are decremented for that failed item.

### Acceptance Criterion 3: Real-Time In-Memory List Pruning
- **Given** `OutOfSyncFilesList` contains files $F_1, F_2, \dots, F_m$.
- **When** a progress report includes `CompletedItems` containing $F_1$ with `Success == true`.
- **Then** $F_1$ MUST be removed from `OutOfSyncFilesList`.
- **And** if an item in `CompletedItems` is not present in `OutOfSyncFilesList` (e.g. truncated preview beyond 200 items), no exception shall occur.
- **And** items with `Success == false` MUST NOT be removed from `OutOfSyncFilesList`.

### Acceptance Criterion 4: Service Batch Progress Reporting
- **Given** `DriveSyncService.RunSyncAsync` executes with $P$ pending upload candidates.
- **When** a batch is processed:
  - `ReportProgress` MUST be called with `TotalPendingFiles = P`.
  - `CompletedItems` MUST include the candidates processed in that batch, specifying their `FilePath`, `FileName`, whether they were `IsNew`, and whether the upload was `Success`.

---

## 4. Boundary Values, Invariants & Edge Cases

1. **Zero Pending Files:** If a sync runs with 0 pending files (`TotalPendingFiles = 0`), `OutOfSyncCount` remains 0, and no removals occur.
2. **Non-Negative Invariant:** `OutOfSyncCount`, `NewFilesCount`, and `ModifiedFilesCount` MUST never become negative under any circumstance (`Math.Max(0, value)`).
3. **Thread Safety:** All modifications to observable properties and collections bound to WinUI 3 controls MUST be executed on the UI thread via `RunOnUIThread`.
4. **Final Synchronization Completion:** When `OnSyncCompleted` fires, `RefreshOutOfSync()` is executed as a source-of-truth reconciliation against the physical disk.

---

## 5. Prohibited Details

- The contract does NOT dictate internal private helper method names in `DriveSyncService` or `WorkSyncViewModel`.
- The contract does NOT restrict whether `CompletedItems` is populated per batch or per individual item within a batch, provided each processed candidate is reported with its outcome.
