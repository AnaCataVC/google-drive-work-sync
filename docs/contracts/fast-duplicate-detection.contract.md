# Formal Contract: Fast Multi-Tier Duplicate Detection

**Feature Slug:** `fast-duplicate-detection`  
**Status:** Frozen Contract (Cleanroom Specification)  
**Date:** 2026-10-11  

---

## 1. Scope & Objective

This contract specifies the interface and algorithmic behavior for detecting duplicate files across monitored source folders in **Google Drive Work Sync**.

Users often retain redundant copies of files across local directories. These duplicate files consume unnecessary disk space, inflate upload queues, and waste Google Drive cloud storage quotas.

To avoid heavy disk I/O when scanning large folder trees, the system implements a **3-tier algorithmic pipeline**:
1. **Tier 1 (Size Partitioning):** Cluster files by `FileInfo.Length > 0`. Discard unique file sizes with zero additional disk I/O.
2. **Tier 2 (Hash Index Cache Reuse):** For size-colliding candidates, check if valid metadata (`LastWriteTimeUtcTicks` and `FileSize`) exists in `_hashIndex`. If valid, reuse the pre-calculated SHA-256 hash without reading the file.
3. **Tier 3A (16 KB Header Hashing):** For candidates requiring hash calculation, read the initial 16 KB (16,384 bytes) and compute a header SHA-256 hash. Discard candidates with unique header hashes before reading the full file.
4. **Tier 3B (Full Stream Verification):** For candidates matching both file size and 16 KB header, compute the complete SHA-256 hash across the entire file stream to confirm exact duplicates.

---

## 2. Public Models & Service Interface

### 2.1 Model: `DuplicateFileItem`
Represents an individual file that is part of a duplicate group.

```csharp
namespace GoogleDriveWorkSync.Models;

public class DuplicateFileItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FormattedFileSize => OutOfSyncFile.FormatBytes(FileSize);
    public DateTime LastModified { get; set; }
    public bool IsPrimary { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
```

### 2.2 Model: `DuplicateFileGroup`
Represents a collection of files sharing identical size and full SHA-256 content hash.

```csharp
namespace GoogleDriveWorkSync.Models;

public class DuplicateFileGroup
{
    public string Hash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FormattedFileSize => OutOfSyncFile.FormatBytes(FileSize);
    public List<DuplicateFileItem> Files { get; set; } = new();

    /// <summary>
    /// Redundant bytes consumed by this group: (Count - 1) * FileSize.
    /// Exactly 1 copy is considered the primary retain copy; N - 1 are redundant.
    /// </summary>
    public long WastedBytes => Math.Max(0, (Files.Count - 1) * FileSize);
    public string FormattedWastedBytes => OutOfSyncFile.FormatBytes(WastedBytes);
}
```

### 2.3 Model: `DuplicateScanResult`
Summary container returned by `DetectDuplicatesAsync`.

```csharp
namespace GoogleDriveWorkSync.Models;

public class DuplicateScanResult
{
    public int TotalScannedFiles { get; set; }
    public int CandidateSizeBuckets { get; set; }
    public List<DuplicateFileGroup> Groups { get; set; } = new();
    public int TotalDuplicateGroups => Groups.Count;
    public int TotalDuplicateFiles => Groups.Sum(g => Math.Max(0, g.Files.Count - 1));
    public long TotalWastedBytes => Groups.Sum(g => g.WastedBytes);
    public string FormattedTotalWastedBytes => OutOfSyncFile.FormatBytes(TotalWastedBytes);
}
```

### 2.4 Service Contract: `IDriveSyncService` Extension
The service interface `GoogleDriveWorkSync.Services.Interfaces.IDriveSyncService` must expose:

```csharp
namespace GoogleDriveWorkSync.Services.Interfaces;

public interface IDriveSyncService
{
    // ... existing members ...

    Task<DuplicateScanResult> DetectDuplicatesAsync(
        SyncSource? onlySource = null,
        CancellationToken cancellationToken = default);
}
```

---

## 3. Behavioral Acceptance Criteria

### Acceptance Criterion 1: Tier 1 Filtering (Unique Sizes Discarded)
- **Given** a set of local files across scanned sources where all files have unique sizes > 0.
- **When** `DetectDuplicatesAsync()` is called.
- **Then** `CandidateSizeBuckets` MUST equal 0.
- **And** `Groups` MUST be empty.
- **And** no file streams beyond initial file metadata scanning shall be opened.

### Acceptance Criterion 2: Zero-Length File Handling
- **Given** multiple files with `Length == 0`.
- **When** `DetectDuplicatesAsync()` is called.
- **Then** zero-byte files MUST be excluded from duplicate groups.
- **And** `TotalWastedBytes` MUST NOT count zero-byte files.

### Acceptance Criterion 3: Tier 2 Hash Index Cache Reuse
- **Given** two local files $F_1$ and $F_2$ sharing identical size.
- **And** both $F_1$ and $F_2$ have matching entries in `_hashIndex` where `LastWriteTimeUtcTicks` and `FileSize` match local filesystem timestamps and lengths.
- **When** `DetectDuplicatesAsync()` is called.
- **Then** the service MUST retrieve the cached SHA-256 hashes without reading the file contents from disk.
- **And** if the cached hashes match, $F_1$ and $F_2$ MUST be grouped into a single `DuplicateFileGroup`.

### Acceptance Criterion 4: Tier 3A Header Hashing Filtering
- **Given** two files $F_1$ and $F_2$ sharing identical file sizes (e.g., 50 MB), but with different content in the first 16 KB.
- **And** neither file is present in `_hashIndex`.
- **When** `DetectDuplicatesAsync()` is called.
- **Then** the service reads only the first 16 KB of each file.
- **And** detects the header hash mismatch.
- **And** MUST NOT read the remainder of either file stream.
- **And** `Groups` MUST be empty.

### Acceptance Criterion 5: Tier 3B Full Stream Verification & Duplicate Grouping
- **Given** three files $A$, $B$, $C$ of size 1 MB:
  - $A$ and $B$ have identical content throughout.
  - $C$ has identical 16 KB header to $A$ and $B$, but different bytes near the end.
- **When** `DetectDuplicatesAsync()` is called.
- **Then** `Groups` MUST contain exactly 1 group with $A$ and $B$.
- **And** $C$ MUST NOT be included in the group.
- **And** `WastedBytes` for the group MUST equal 1 MB (`(2 - 1) * 1 MB`).

### Acceptance Criterion 6: Primary File Attribution
- **Given** a `DuplicateFileGroup` with files $F_{old}$ (modified 2026-01-01) and $F_{new}$ (modified 2026-06-01).
- **When** `DetectDuplicatesAsync()` produces the group.
- **Then** the file with the earliest `LastModified` timestamp ($F_{old}$) MUST have `IsPrimary == true`.
- **And** subsequent files MUST have `IsPrimary == false`.

### Acceptance Criterion 7: Error Resilience & Cancellation
- **Given** a file in a scanned folder that is locked or inaccessible (`UnauthorizedAccessException` or `IOException`).
- **When** `DetectDuplicatesAsync()` encounters the unreadable file.
- **Then** the service MUST gracefully skip that file without throwing an unhandled exception or aborting the remainder of the scan.
- **When** `cancellationToken` is cancelled.
- **Then** `DetectDuplicatesAsync()` MUST honor the token and throw `OperationCanceledException`.
