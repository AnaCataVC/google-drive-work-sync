using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Models;

namespace GoogleDriveWorkSync.Services.Interfaces;

public interface IDriveSyncService
{
    DriveSyncSettings Settings { get; }
    bool IsSyncing { get; }
    bool IsConfigured { get; }
    IReadOnlyList<SyncErrorItem> LastSyncErrors { get; }

    event EventHandler? SettingsChanged;
    event EventHandler<SyncProgressReport>? SyncProgressChanged;
    event EventHandler<SyncResultSummary>? SyncCompleted;
    event EventHandler<IReadOnlyList<SyncErrorItem>>? SyncErrorsChanged;

    void UpdateSettings(DriveSyncSettings settings);
    void ClearSyncErrors();
    void ClearHashIndex();
    void CancelSync();

    Task<SyncResultSummary> RunSyncAsync(
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceFullSync = false,
        SyncSource? onlySource = null);

    Task<SyncResultSummary> RetryFailedFilesAsync(
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutOfSyncFile>> PreviewOutOfSyncAsync(
        CancellationToken cancellationToken = default,
        SyncSource? onlySource = null);

    Task<string?> TestConnectionAsync(string webAppUrl, string? authToken = null, CancellationToken cancellationToken = default);

    // Helpers for checking if candidate or individual file is out of sync
    CandidateSyncStatus EvaluateFileStatus(string filePath, string destinationKey);
    void SaveKnownHash(string hashKey, string hash, FileInfo? fileInfo = null, string? fileId = null, string? driveRelativePath = null, string? localFilePath = null);
    string ComputeSha256(string filePath);

    // Upload support for individual or custom batches (used by Claude Sync)
    Task<bool> UploadSingleFileAsync(
        string localFilePath,
        string destinationRelativePath,
        string mimeType,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? FileId)> UploadSingleFileWithResultAsync(
        string localFilePath,
        string destinationRelativePath,
        string mimeType,
        CancellationToken cancellationToken = default);

    // Google Drive deletion suggestions & orphan reconciliation
    Task<IReadOnlyList<DriveOrphanItem>> DetectOrphansAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DriveOrphanItem>> DetectClaudeOrphansAsync(CancellationToken cancellationToken = default);

    Task<TrashResultSummary> TrashOrphansAsync(
        IEnumerable<DriveOrphanItem> orphans,
        CancellationToken cancellationToken = default);

    void DismissOrphans(IEnumerable<DriveOrphanItem> orphans);

    // Duplicate file detection (3-Tier pipeline)
    Task<DuplicateScanResult> DetectDuplicatesAsync(
        SyncSource? onlySource = null,
        CancellationToken cancellationToken = default);

    Task<DuplicateScanResult> DetectCandidateDuplicatesAsync(
        IEnumerable<ClaudeDiscoveryCandidate> candidates,
        CancellationToken cancellationToken = default);

    // Actionable duplicate file cleanup (Windows Recycle Bin + Google Drive Trash)
    Task<DuplicateCleanupResultSummary> DeleteDuplicateFilesAsync(
        IEnumerable<DuplicateFileItem> filesToPurge,
        bool trashFromDrive = true,
        CancellationToken cancellationToken = default);
}
