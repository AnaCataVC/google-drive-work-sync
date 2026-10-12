# Formal Contract: Google Drive Deletion Suggestions & Orphan Reconciliation

**Feature Slug:** `drive-deletion-suggestions`  
**Status:** Frozen Contract (Cleanroom Specification)  
**Date:** 2026-10-11  

---

## 1. Scope & Objective

This contract specifies the behavioral and interface requirements for detecting and resolving orphaned files in Google Drive within **Google Drive Work Sync**.

An **orphan file** is a file that was previously synchronized to Google Drive (recorded in `sync_hashes.json`), but is no longer present on the local filesystem (`File.Exists == false`).

The system must:
1. Preserve `FileId` and `DriveRelativePath` in `HashCacheEntry` when files are uploaded.
2. Detect orphan files across active monitored sources without altering Claude unversioned context entries.
3. Transmit remote trash requests (`action: "trash"`) to the Google Apps Script Web App for selected orphans, moving them to the Google Drive Trash.
4. Atomically purge confirmed trashed keys from `sync_hashes.json`.
5. Support local dismissal of orphans (dropping them from the local index without sending remote trash commands).

---

## 2. Public Interfaces & Data Contracts

### 2.1 Model: `HashCacheEntry` (Extensions)
`GoogleDriveWorkSync.Models.HashCacheEntry` must include `FileId` and `DriveRelativePath` while retaining full backward compatibility with existing serialized JSON.

```csharp
namespace GoogleDriveWorkSync.Models;

public class HashCacheEntry
{
    public string Hash { get; set; } = string.Empty;
    public long LastWriteTimeUtcTicks { get; set; }
    public long FileSize { get; set; }
    public string? FileId { get; set; }
    public string? DriveRelativePath { get; set; }
}
```

### 2.2 Model: `DriveOrphanItem`
Represents an individual orphan file detected during reconciliation.

```csharp
namespace GoogleDriveWorkSync.Models;

public class DriveOrphanItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string HashKey { get; set; } = string.Empty;
    public string LocalFilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DriveRelativePath { get; set; } = string.Empty;
    public string? FileId { get; set; }
    public long FileSize { get; set; }
    public string FormattedFileSize => OutOfSyncFile.FormatBytes(FileSize);
    public DateTime? LastWriteTimeUtc { get; set; }

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
```

### 2.3 Model: `TrashResultSummary`
Summary of batch trash execution.

```csharp
namespace GoogleDriveWorkSync.Models;

public class TrashResultSummary
{
    public int TotalRequested { get; set; }
    public int SuccessfullyTrashed { get; set; }
    public int NotFoundCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> ErrorMessages { get; set; } = new();
    public bool Success => ErrorCount == 0;
}
```

### 2.4 Service Contract: `IDriveSyncService` Extensions
`GoogleDriveWorkSync.Services.Interfaces.IDriveSyncService` must expose:

```csharp
namespace GoogleDriveWorkSync.Services.Interfaces;

public interface IDriveSyncService
{
    // ... Existing members ...

    Task<IReadOnlyList<DriveOrphanItem>> DetectOrphansAsync(CancellationToken cancellationToken = default);

    Task<TrashResultSummary> TrashOrphansAsync(
        IEnumerable<DriveOrphanItem> orphans,
        CancellationToken cancellationToken = default);

    void DismissOrphans(IEnumerable<DriveOrphanItem> orphans);
}
```

---

## 3. Behavioral Acceptance Criteria

### Acceptance Criterion 1: Orphan Detection
- **Given** `_hashIndex` contains entries:
  - $E_1$: Key `"work|C:\docs\report.docx"` where `File.Exists(@"C:\docs\report.docx") == false`.
  - $E_2$: Key `"work|C:\docs\budget.xlsx"` where `File.Exists(@"C:\docs\budget.xlsx") == true`.
  - $E_3$: Key `"claude-config/settings.json"` (unversioned Claude context key without `|`).
  - Active source prefix in settings includes `"work"`.
- **When** `DetectOrphansAsync()` is executed.
- **Then** only $E_1$ MUST be returned in the orphan list.
- **And** $E_2$ MUST NOT be returned (file exists).
- **And** $E_3$ MUST NOT be returned (Claude context key without local disk counterpart).
- **And** the returned item MUST have `IsSelected == true`, `HashKey`, `LocalFilePath`, `FileName`, `FileSize`, and any cached `FileId` and `DriveRelativePath`.

### Acceptance Criterion 2: Remote Trashing Wire Protocol
- **Given** selected orphans $O_1$ and $O_2$.
- **When** `TrashOrphansAsync([O_1, O_2])` is executed with valid `WebAppUrl`.
- **Then** the service MUST send an HTTP POST request to `WebAppUrl` containing:
  ```json
  {
    "action": "trash",
    "authToken": "<authToken-if-configured>",
    "files": [
      { "fileId": "<O1.FileId>", "relativePath": "<O1.DriveRelativePath>" },
      { "fileId": "<O2.FileId>", "relativePath": "<O2.DriveRelativePath>" }
    ]
  }
  ```
- **When** Apps Script responds with:
  ```json
  {
    "status": "success",
    "action": "trash",
    "results": [
      { "relativePath": "...", "status": "trashed" },
      { "relativePath": "...", "status": "not_found" }
    ]
  }
  ```
- **Then** `SuccessfullyTrashed` MUST equal 1, `NotFoundCount` MUST equal 1, and `ErrorCount` MUST equal 0.
- **And** the keys for both `trashed` and `not_found` items MUST be purged from `_hashIndex`.
- **And** the updated hash index MUST be persisted atomically to disk.

### Acceptance Criterion 3: Trashing Error Resilience
- **Given** Apps Script returns `{ "status": "error", "message": "Access Denied" }` or HTTP failure:
- **Then** `TrashResultSummary.Success` MUST be `false`, `ErrorCount` MUST reflect the failed items, and `ErrorMessages` MUST contain the error message.
- **And** the corresponding keys MUST NOT be purged from `_hashIndex`.

### Acceptance Criterion 4: Local Dismissal
- **Given** selected orphans $O_1, O_2$.
- **When** `DismissOrphans([O_1, O_2])` is executed.
- **Then** no network requests shall be made.
- **And** $O_1.HashKey$ and $O_2.HashKey$ MUST be removed from `_hashIndex` and persisted to disk.

### Acceptance Criterion 5: Capture `FileId` on Upload
- **Given** a batch upload completes successfully and Apps Script returns `fileId: "1abc..."`.
- **When** `DriveSyncService` records the hash into `_hashIndex`.
- **Then** the entry in `_hashIndex` MUST record `FileId == "1abc..."` and `DriveRelativePath == candidate.RelativePath`.

### Acceptance Criterion 6: Backward Compatibility
- **Given** a `sync_hashes.json` generated by previous versions containing `{ "Hash": "abc", "LastWriteTimeUtcTicks": 100, "FileSize": 50 }` (or legacy string format).
- **When** `LoadHashIndex()` loads the file.
- **Then** it must deserialize without exception, with `FileId == null` and `DriveRelativePath == null`.

---

## 4. Boundary Values & Edge Cases

1. **Empty Orphans List:** Calling `TrashOrphansAsync` with empty collection returns immediately with `TotalRequested = 0`, without issuing an HTTP request.
2. **Missing WebAppUrl:** Calling `TrashOrphansAsync` when `IsConfigured == false` returns `Success = false` with an explanatory error message without throwing unhandled exceptions.
3. **Cancellation:** Honoring `CancellationToken` throughout `DetectOrphansAsync` and `TrashOrphansAsync`.
4. **Thread Safety:** Invocations that mutate `_hashIndex` must synchronize across the internal `_hashLock`.
