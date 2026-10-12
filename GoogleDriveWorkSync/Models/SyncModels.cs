using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GoogleDriveWorkSync.Models;

/// <summary>
/// Metadata for a local file discovered during folder scanning.
/// </summary>
public class LocalFileMetadata
{
    private string? _hashKey;

    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long FileSize { get; set; }

    /// <summary>
    /// Key identifying this file in the incremental-sync hash index.
    /// Prefixed with the destination prefix to support multi-destination mapping.
    /// </summary>
    public string HashKey
    {
        get => string.IsNullOrEmpty(_hashKey) ? FilePath : _hashKey;
        set => _hashKey = value;
    }
}

/// <summary>
/// Rules and criteria for filtering files during folder scanning.
/// </summary>
public class SyncFilterOptions
{
    public HashSet<string> IncludedExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludedExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludedFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long MaxFileSizeBytes { get; set; } = 25L * 1024 * 1024; // 25 MB max ceiling

    public static SyncFilterOptions Create(
        string? includedExtensions,
        string? excludedExtensions,
        string? excludedFolders,
        long maxFileSizeMb)
    {
        // Enforce hard cap of 25MB due to Google Apps Script payload limit
        long effectiveMb = Math.Clamp(maxFileSizeMb, 1, 25);
        var options = new SyncFilterOptions
        {
            MaxFileSizeBytes = effectiveMb * 1024L * 1024L
        };

        if (!string.IsNullOrWhiteSpace(includedExtensions))
        {
            foreach (var ext in includedExtensions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = ext.Trim();
                if (!normalized.StartsWith('.')) normalized = "." + normalized;
                options.IncludedExtensions.Add(normalized);
            }
        }

        if (!string.IsNullOrWhiteSpace(excludedExtensions))
        {
            foreach (var ext in excludedExtensions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = ext.Trim();
                if (!normalized.StartsWith('.')) normalized = "." + normalized;
                options.ExcludedExtensions.Add(normalized);
            }
        }

        if (!string.IsNullOrWhiteSpace(excludedFolders))
        {
            foreach (var folder in excludedFolders.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                options.ExcludedFolders.Add(folder.Trim());
            }
        }

        return options;
    }

    public bool IsFolderExcluded(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return false;
        return ExcludedFolders.Contains(folderName);
    }

    public bool ShouldIncludeFile(FileInfo fileInfo, out string? skipReason)
    {
        skipReason = null;
        var ext = fileInfo.Extension;

        // 1. Excluded extensions
        if (ExcludedExtensions.Contains(ext))
        {
            skipReason = $"Excluded extension ({ext})";
            return false;
        }

        // 2. Included extensions whitelist (if configured)
        if (IncludedExtensions.Count > 0 && !IncludedExtensions.Contains(ext))
        {
            skipReason = $"Extension not in whitelist ({ext})";
            return false;
        }

        // 3. Max file size check
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            skipReason = $"Exceeds max size limit ({fileInfo.Length / (1024 * 1024)} MB > {MaxFileSizeBytes / (1024 * 1024)} MB)";
            return false;
        }

        return true;
    }
}

/// <summary>
/// Represents a file that has changed or is new, returned by read-only preview.
/// </summary>
public class OutOfSyncFile
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Reason { get; set; } = "Nuevo"; // "Nuevo" | "Modificado"

    public string FormattedFileSize => FormatBytes(FileSize);

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int i = 0;
        double dBytes = bytes;
        while (dBytes >= 1024 && i < suffixes.Length - 1)
        {
            dBytes /= 1024;
            i++;
        }
        return $"{dBytes:0.##} {suffixes[i]}";
    }
}

/// <summary>
/// Entry stored in sync_hashes.json for metadata fast-path and SHA-256 caching.
/// </summary>
public class HashCacheEntry
{
    public string Hash { get; set; } = string.Empty;
    public long LastWriteTimeUtcTicks { get; set; }
    public long FileSize { get; set; }
    public string? FileId { get; set; }
    public string? DriveRelativePath { get; set; }
}

/// <summary>
/// Represents an individual orphan file detected during reconciliation.
/// </summary>
public class DriveOrphanItem : ObservableObject
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

/// <summary>
/// Summary of batch trash execution.
/// </summary>
public class TrashResultSummary
{
    public int TotalRequested { get; set; }
    public int SuccessfullyTrashed { get; set; }
    public int NotFoundCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> ErrorMessages { get; set; } = new();
    public bool Success => ErrorCount == 0;
}

/// <summary>
/// Record of a file that failed during a sync run.
/// </summary>
public class SyncErrorItem
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string HashKey { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string ErrorCategory { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Represents an individual file completed during an incremental batch report.
/// </summary>
public class CompletedSyncItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool IsNew { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Real-time progress report during synchronization.
/// </summary>
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

    // Dynamic tracking extensions
    public int TotalPendingFiles { get; set; }
    public int RemainingPendingFiles => Math.Max(0, TotalPendingFiles - UploadedCount - ErrorCount);
    public IReadOnlyList<CompletedSyncItem> CompletedItems { get; set; } = Array.Empty<CompletedSyncItem>();
}


/// <summary>
/// Final summary of a sync operation.
/// </summary>
public class SyncResultSummary
{
    public int TotalScanned { get; set; }
    public int Uploaded { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool Success => Errors == 0;
    public List<SyncErrorItem> FailedFiles { get; set; } = new();
}

/// <summary>
/// Represents an individual file that is part of a duplicate group.
/// </summary>
public class DuplicateFileItem : ObservableObject
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

/// <summary>
/// Represents a collection of files sharing identical size and full SHA-256 content hash.
/// </summary>
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

/// <summary>
/// Summary container returned by DetectDuplicatesAsync.
/// </summary>
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

