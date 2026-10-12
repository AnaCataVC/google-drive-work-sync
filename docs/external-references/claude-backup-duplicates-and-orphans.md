> **Created:** 2026-10-12
> **Last Updated:** 2026-10-12

# Technical Architecture: Claude Context Duplicates, Actionable Deduplication & Drive Orphan Reconciliation

This document specifies the architecture, data models, and behavioral design for extending multi-tier duplicate detection and Google Drive orphan reconciliation to **AI Context Backup (Claude)**, alongside making duplicate inspection fully **actionable** across both Work Sync and Claude Backup modules.

---

## 1. Context & Objectives

In version 1.4.0, Google Drive Work Sync introduced:
- **3-Tier Duplicate Detection**: Fast size-partitioning, hash cache reuse, 16 KB header hashing, and full SHA-256 verification.
- **Drive Orphan Deletion Suggestions**: Detection of locally deleted files that linger in Google Drive, offering explicit user-driven soft-trashing (`action: "trash"`).

However, these capabilities were initially scoped only to monitored project source folders (`WorkSyncView`). This architecture addresses two key operational gaps:
1. **Claude Backup Parity**: AI context files (`CLAUDE.md`, mods, hooks, skills, MCP configurations) frequently suffer from cross-repository duplication and orphaned remote backups when local repositories or skills are deleted or refactored.
2. **Actionable Duplicate Inspection**: Previously, duplicate inspection was read-only. Users need the ability to securely move redundant copies to the Windows Recycle Bin and Google Drive Trash while preserving the primary/original copy.

---

## 2. Architecture & Data Contracts

### 2.1 Preserving Source & Identity Metadata for Claude Files
In earlier versions, Claude files uploaded via `UploadSingleFileAsync` were indexed in `sync_hashes.json` using only their remote destination path as the key, without persisting the local file origin or the remote Google Drive `FileId`.

#### `HashCacheEntry` Extension:
```csharp
public class HashCacheEntry
{
    public string Hash { get; set; } = string.Empty;
    public long LastWriteTimeUtcTicks { get; set; }
    public long FileSize { get; set; }
    public string? FileId { get; set; }
    public string? DriveRelativePath { get; set; }
    public string? LocalFilePath { get; set; } // Tracks original local path for orphan detection
}
```

When a Claude candidate is uploaded:
1. `UploadSingleFileInternalAsync` extracts and returns the Google Apps Script `FileId`.
2. `SaveKnownHash` records `LocalFilePath = candidate.FilePath`, `DriveRelativePath = destinationPath`, and `FileId = result.FileId`.

### 2.2 Claude Drive Orphan Reconciliation
A Claude orphan file is defined as any indexed file under the Claude destination prefix (`ClaudeDestinationPrefix`, default `claude-md-unversioned`) whose recorded `LocalFilePath` no longer exists on the local filesystem (`!File.Exists(LocalFilePath)`).

Orphan detection:
```text
[ _hashIndex Entries ]
       │
       ▼ Matches ClaudeDestinationPrefix or has LocalFilePath
[ Check File.Exists(entry.LocalFilePath) ]
       │
       ├─► True  -> Retain in index
       └─► False -> Yield DriveOrphanItem for cleanup dialog
```

### 2.3 Multi-Tier Duplicate Detection on Claude Candidates
Rather than running an unconstrained filesystem search, Claude duplicate detection operates directly on the discovered candidates (`ClaudeDiscoveryCandidate`), leveraging their pre-filtered, security-sanitized context:
1. **Tier 1 (Size Partitioning):** Cluster discovered candidates by `FileSizeBytes > 0`. Discard unique sizes.
2. **Tier 2 (Hash Cache Reuse):** For candidates in colliding size buckets, consult `_hashIndex` if write timestamp matches.
3. **Tier 3A (16 KB Header Hash):** Read first 16,384 bytes to filter out superficial matches.
4. **Tier 3B (Full SHA-256 Stream):** Complete stream hash for exact binary verification.
5. **Primary Retention Invariant:** Exactly 1 file per group is marked as `IsPrimary = true` (earliest modified or canonical path); remaining copies are marked `IsPrimary = false` and pre-selected for cleanup.

### 2.4 Actionable Deduplication Pipeline (Windows Recycle Bin + Cloud Trash)
Deleting duplicate files must NEVER result in irreversible data loss (`Shift+Delete`).
- **Local Cleanup:** Selected redundant copies are sent to the **Windows Recycle Bin** using native Win32 `SHFileOperation` (`FO_DELETE` with `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT`). If a user realizes they needed that specific copy, it can be restored directly from the Windows desktop Recycle Bin.
- **Drive Cloud Cleanup:** For redundant copies that were already synchronized to Google Drive (present in `_hashIndex`), a batch `trash` command is dispatched to the Google Apps Script Web App, moving them to the 30-day Google Drive Trash and purging their entries from `_hashIndex`.

---

## 3. UI/UX Workflow & Dialog Contracts

### 3.1 Claude Discovery View (`ContextDiscoveryView.xaml`)
In the secondary options dropdown (`...`) next to the primary sync button:
- `Ver qué falta por sincronizar` (Existing)
- `Forzar sincronización total a Drive` (Existing)
- `Sugerencias de limpieza en Drive (Archivos huérfanos)` (New, FontIcon `&#xE74D;`)
- `Inspector de archivos duplicados` (New, FontIcon `&#xE8B9;`)

### 3.2 Actionable Duplicates Dialog (`WorkSyncView` & `ContextDiscoveryView`)
- Displays KPIs: Duplicate Groups, Redundant Copies, Recoverable Space.
- Lists each group:
  - **Original File:** Green badge, disabled checkbox (protected from deletion).
  - **Redundant Copies:** Normal badge, checked by default, individual checkbox to toggle.
- Primary Action: `Mover copias seleccionadas a papelera`.
- Secondary Action: `Cerrar`.
- Confirmation Dialog: Outlines exact count of local files moving to Windows Recycle Bin and count of files moving to Google Drive Trash.
