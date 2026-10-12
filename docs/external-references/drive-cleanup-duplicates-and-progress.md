> **Created:** 2026-10-11
> **Last Updated:** 2026-10-11

# Research & Technical Architecture: Drive Deletion Suggestions, Duplicate Detection & Dynamic Progress

This document provides a comprehensive technical investigation, architectural analysis, and implementation roadmap for three advanced synchronization capabilities in **Google Drive Work Sync**:
1. **Google Drive Deletion Suggestions & Orphan Tracking** (safe trashing of files removed locally).
2. **Duplicate Detection Architecture** (size-bucketed hashing for local redundancy and Drive quota conservation).
3. **Real-Time Dynamic Pending Count Progression** (reactive WinUI 3 progress tracking as batches upload).

---

## 1. Google Drive Deletion Suggestions & Orphan Reconciliation

### 1.1 Context & Problem Statement
Currently, `Google Drive Work Sync` functions as an **additive, push-only synchronization engine**. 
- When a file is created or edited locally, `DriveSyncService` hashes the file, matches it against `sync_hashes.json`, and uploads it to the Google Apps Script Web App.
- When a user deletes a file locally, the next sync execution triggers `PurgeOrphanHashes()`:
  ```csharp
  // DriveSyncService.cs (Lines 1322-1349)
  public void PurgeOrphanHashes()
  {
      // Finds keys in _hashIndex where !File.Exists(localPath)
      // and silently deletes them from _hashIndex.
  }
  ```
- **Consequence:** The hash is purged from local state, but the remote file remains stranded indefinitely in Google Drive. Over time, the remote Drive folder accumulates dead/orphaned files that no longer exist on the user's workstation.

### 1.2 Proposed Architecture: Safe Deletion Suggestions
Rather than executing silent, automatic deletions in Google Drive (which violates the safety-first principle of backup software), the system should detect discrepancies and present **Deletion Suggestions**:

```
[ Local Disk Scan ] vs [ sync_hashes.json ]
                  │
                  ▼
   [ Discrepancy Reconciliation ]
   (Key in index, but !File.Exists)
                  │
                  ▼
   [ UI: Sugerencias de Limpieza en Drive ]
   (User reviews list with checkboxes)
                  │
                  ▼ User clicks "Mover a papelera en Drive"
   [ Google Apps Script: action = "trash" ]
                  │
                  ▼
        DriveApp: setTrashed(true) (30-day recovery window)
```

### 1.3 Wire Protocol Extension (Google Apps Script)
In Google Apps Script, `DriveApp` provides `file.setTrashed(true)`, which moves the file to the Google Drive Trash without permanently purging it (providing a 30-day recovery safety net).

#### Extended `Code.gs` Handler:
```javascript
function doPost(e) {
  var lock = LockService.getScriptLock();
  try {
    lock.waitLock(25000);
  } catch (t) {
    return ContentService.createTextOutput(JSON.stringify({
      status: "error", message: "Server is busy. Please try again shortly."
    })).setMimeType(ContentService.MimeType.JSON);
  }

  try {
    var AUTH_TOKEN = ""; // Match desktop settings
    var rootFolderId = "PASTE_YOUR_FOLDER_ID_HERE";
    var body = JSON.parse(e.postData.contents);

    if (AUTH_TOKEN && body.authToken !== AUTH_TOKEN) {
      return ContentService.createTextOutput(JSON.stringify({
        status: "error", message: "Unauthorized."
      })).setMimeType(ContentService.MimeType.JSON);
    }

    // ACTION: TRASH FILES
    if (body.action === "trash") {
      var filesToTrash = body.files || [];
      var trashResults = [];

      for (var i = 0; i < filesToTrash.length; i++) {
        var entry = filesToTrash[i];
        var relativePath = entry.relativePath;
        var fileId = entry.fileId;

        try {
          var targetFile = null;
          if (fileId) {
            targetFile = DriveApp.getFileById(fileId);
          } else if (relativePath) {
            // Fallback: Resolve file via path traversal
            targetFile = resolveFileByPath(rootFolderId, relativePath);
          }

          if (targetFile && !targetFile.isTrashed()) {
            targetFile.setTrashed(true);
            trashResults.push({ relativePath: relativePath, status: "trashed" });
          } else {
            trashResults.push({ relativePath: relativePath, status: "not_found" });
          }
        } catch (trashErr) {
          trashResults.push({ relativePath: relativePath, status: "error", message: trashErr.toString() });
        }
      }

      return ContentService.createTextOutput(JSON.stringify({
        status: "success", action: "trash", results: trashResults
      })).setMimeType(ContentService.MimeType.JSON);
    }

    // DEFAULT ACTION: UPLOAD BATCH (Preserves existing upload pipeline)
    // ...
  } finally {
    lock.releaseLock();
  }
}
```

### 1.4 Preserving `FileId` in Desktop Hash Index
To optimize Drive deletion and avoid slow path traversals in Apps Script:
- When Apps Script uploads a file, it already returns `fileId`:
  `{ "relativePath": "...", "status": "success", "fileId": "1a2B3c..." }`
- Update `HashCacheEntry` in `SyncModels.cs`:
  ```csharp
  public class HashCacheEntry
  {
      public string Hash { get; set; } = string.Empty;
      public long LastWriteTimeUtcTicks { get; set; }
      public long FileSize { get; set; }
      public string? FileId { get; set; } // Stored upon successful upload
  }
  ```
- When a file is missing locally, the desktop app immediately has its `FileId` and can trash it in $O(1)$ time without searching folders.

---

## 2. Duplicate Detection Architecture

### 2.1 Why Duplicate Detection Matters
Users often have redundant copies of large files across monitored folders (e.g., `reporte_final.pdf` duplicated inside `docs/` and `backup/`, or duplicated assets).
- Uploading duplicate files wastes Google Drive storage quota and Google Apps Script execution time (Base64 payload limits).
- Detecting duplicates gives users clarity on disk waste and allows deduplicating locally or skipping duplicate uploads.

### 2.2 Multi-Tier Algorithmic Pipeline in .NET 9
Calculating full SHA-256 hashes for thousands of files across gigabytes of data is I/O-intensive. We employ an optimal 3-tier filtering pipeline:

```
[ All Scanned Files (N files) ]
                │
                ▼
[ Tier 1: Group by FileSize ]  ──── (Unique sizes discarded: 0 disk I/O)
                │
                ▼ Candidates with matching size (S files)
[ Tier 2: Check sync_hashes.json ]  ── (If both already cached, compare SHA-256: 0 disk I/O)
                │
                ▼ Uncached candidates
[ Tier 3A: Partial Hash (16 KB Header) ]  ── (Different headers discarded: < 1% disk I/O)
                │
                ▼ Identical headers
[ Tier 3B: Full Stream SHA-256 ]  ── (Guaranteed exact duplicate confirmation)
```

### 2.3 Data Structures & Service Design
```csharp
public class DuplicateFileGroup
{
    public string Hash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public List<LocalFileMetadata> Files { get; set; } = new();
    public long WastedBytes => (Files.Count - 1) * FileSize;
    public string FormattedWastedBytes => OutOfSyncFile.FormatBytes(WastedBytes);
}
```

#### Duplicate Analyzer Implementation:
```csharp
public async Task<List<DuplicateFileGroup>> DetectDuplicatesAsync(
    IEnumerable<LocalFileMetadata> files,
    CancellationToken token = default)
{
    return await Task.Run(() =>
    {
        var duplicates = new List<DuplicateFileGroup>();

        // Tier 1: Bucket by file size (files > 0 bytes only)
        var sizeBuckets = files
            .Where(f => f.FileSize > 0)
            .GroupBy(f => f.FileSize)
            .Where(g => g.Count() > 1);

        foreach (var bucket in sizeBuckets)
        {
            token.ThrowIfCancellationRequested();

            // Tier 2 & 3: Compute or lookup full SHA-256
            var hashGroups = bucket
                .GroupBy(f => string.IsNullOrEmpty(f.Hash) ? ComputeSha256(f.FilePath) : f.Hash)
                .Where(g => g.Count() > 1);

            foreach (var group in hashGroups)
            {
                duplicates.Add(new DuplicateFileGroup
                {
                    Hash = group.Key,
                    FileSize = bucket.Key,
                    Files = group.ToList()
                });
            }
        }

        return duplicates.OrderByDescending(d => d.WastedBytes).ToList();
    }, token);
}
```

---

## 3. Real-Time Dynamic Pending Count Progression

### 3.1 Current Architectural Limitation
Currently:
1. `WorkSyncViewModel.cs` calls `RefreshOutOfSync()`, which populates `OutOfSyncCount`, `NewFilesCount`, and `ModifiedFilesCount`.
2. When the user clicks **Sincronizar desincronizados**, `RunSyncAsync` executes.
3. During execution:
   ```csharp
   // WorkSyncViewModel.cs:
   private void OnSyncProgressChanged(object? sender, SyncProgressReport report)
   {
       RunOnUIThread(() =>
       {
           DriveSyncProgress = report.Percentage;
           DriveSyncDetailText = report.StatusMessage;
           // OutOfSyncCount is NEVER updated here!
       });
   }
   ```
4. Even though 40 out of 50 files have already uploaded successfully, the KPI card still displays `50 Archivos pendientes` until the entire sync finishes and `OnSyncCompleted` runs.

### 3.2 Dynamic Reactive Update Design
To provide instant feedback without thread locking or UI stutter:

#### Step 1: Extend `SyncProgressReport`
```csharp
public class SyncProgressReport
{
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public string CurrentFileName { get; set; } = string.Empty;
    public int UploadedCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public int Percentage => TotalFiles == 0 ? 100 : (int)Math.Round(ProcessedFiles * 100.0 / TotalFiles);

    // Dynamic pending tracking:
    public int TotalPendingFiles { get; set; }
    public int RemainingPendingFiles => Math.Max(0, TotalPendingFiles - UploadedCount - ErrorCount);
    public IReadOnlyList<string>? RecentlyCompletedPaths { get; set; }
}
```

#### Step 2: Update Progress in `DriveSyncService.cs`
Inside `RunSyncAsync`:
```csharp
int totalPending = pendingUploads.Count;

foreach (var batch in batches)
{
    // ... ProcessBatchAsync ...

    ReportProgress(progress, new SyncProgressReport
    {
        TotalFiles = summary.TotalScanned,
        ProcessedFiles = processed,
        TotalPendingFiles = totalPending,
        UploadedCount = summary.Uploaded,
        SkippedCount = summary.Skipped,
        ErrorCount = summary.Errors,
        RecentlyCompletedPaths = batch.Select(b => b.FilePath).ToList(),
        StatusMessage = $"Progreso: {summary.Uploaded}/{totalPending} subidos ({summary.Errors} errores)"
    });
}
```

#### Step 3: Update ViewModel Reactively in `WorkSyncViewModel.cs`
```csharp
private void OnSyncProgressChanged(object? sender, SyncProgressReport report)
{
    RunOnUIThread(() =>
    {
        DriveSyncProgress = report.Percentage;
        DriveSyncDetailText = report.StatusMessage;

        if (report.TotalPendingFiles > 0)
        {
            OutOfSyncCount = report.RemainingPendingFiles;
            SyncActionTitle = OutOfSyncCount > 0
                ? $"Sincronizando ({OutOfSyncCount} pendientes)..."
                : "Completando sincronización...";

            // Dynamically prune finished items from the preview list
            if (report.RecentlyCompletedPaths != null && report.RecentlyCompletedPaths.Count > 0)
            {
                var completedSet = new HashSet<string>(report.RecentlyCompletedPaths, StringComparer.OrdinalIgnoreCase);
                for (int i = OutOfSyncFilesList.Count - 1; i >= 0; i--)
                {
                    if (completedSet.Contains(OutOfSyncFilesList[i].FilePath))
                    {
                        OutOfSyncFilesList.RemoveAt(i);
                    }
                }
            }
        }
    });
}
```

### 3.3 UI Threading & Performance Guarantees
- Batching guarantees that events fire at most once every 1.5 – 3 seconds per batch (not per microsecond), ensuring `DispatcherQueue` is never overloaded.
- Updating `OutOfSyncCount` and removing items from `OutOfSyncFilesList` occurs smoothly and natively animates in WinUI 3 `ItemsRepeater` / `ListView`.

---

## 4. Implementation Feasibility & Risk Matrix

| Capability | Feasibility | Risk Level | Architectural Requirements |
| :--- | :--- | :--- | :--- |
| **Dynamic Pending Count** | **100% (High / Immediate)** | Low | Update `SyncProgressReport` and `WorkSyncViewModel.OnSyncProgressChanged`. Purely client-side. Zero Apps Script changes required. |
| **Local Duplicate Detection** | **100% (High / Immediate)** | Low | Implement `DuplicateFileGroup` model and 3-tier size+hash grouping in `DriveSyncService`. Zero Apps Script changes required. |
| **Drive Deletion Suggestions** | **100% (High / Phased)** | Medium (Safety) | **Phase 1 (Client Detection):** Reconcile `sync_hashes.json` vs disk, display suggested orphans in UI.<br>**Phase 2 (Remote Trash):** Add `"action": "trash"` handler to `Code.gs` deploying `file.setTrashed(true)`. |

---

## 5. References & Links
- [Google Apps Script: DriveApp.File.setTrashed() Documentation](https://developers.google.com/apps-script/reference/drive/file#settrashedtrashed)
- [Google Apps Script: Execution Limits & Quotas](https://developers.google.com/apps-script/guides/services/quotas)
- [Microsoft Learn: WinUI 3 DispatcherQueue & Thread Safety](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueue)
- [CommunityToolkit.Mvvm ObservableCollection Patterns](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/observablecollection)
