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

    Task<string?> TestConnectionAsync(string webAppUrl, CancellationToken cancellationToken = default);

    // Helpers for checking if candidate or individual file is out of sync
    CandidateSyncStatus EvaluateFileStatus(string filePath, string destinationKey);
    void SaveKnownHash(string hashKey, string hash, FileInfo fileInfo);
    string ComputeSha256(string filePath);

    // Upload support for individual or custom batches (used by Claude Sync)
    Task<bool> UploadSingleFileAsync(
        string localFilePath,
        string destinationRelativePath,
        string mimeType,
        CancellationToken cancellationToken = default);
}
