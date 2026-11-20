using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GoogleDriveWorkSync.Models;

/// <summary>
/// A non-trashed file found under the Web App's root Drive folder.
/// </summary>
public class RemoteDriveItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime? ModifiedTime { get; set; }
}

/// <summary>
/// Result of listing Drive; <see cref="ErrorMessage"/> is set when the list could not be obtained.
/// </summary>
public class RemoteListResult
{
    public List<RemoteDriveItem> Items { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public bool Success => ErrorMessage == null;
}

public enum RestoreItemStatus
{
    /// <summary>No local file at the target path.</summary>
    Missing,
    /// <summary>The local file's SHA-256 equals the hash recorded for this Drive file.</summary>
    Identical,
    /// <summary>A local file exists and is not known to be identical.</summary>
    WouldOverwrite,
    /// <summary>No index entry or work source maps this Drive path to a local folder.</summary>
    Unmapped
}

/// <summary>
/// One Drive file in a restore plan, with its resolved local destination.
/// </summary>
public class RestorePlanItem : ObservableObject
{
    public RemoteDriveItem Remote { get; set; } = new();

    /// <summary>Resolved local target; null when unmapped or when the Drive path is unsafe.</summary>
    public string? LocalTargetPath { get; set; }

    /// <summary>Folder the target must stay inside (trust boundary for path traversal).</summary>
    public string? MappingRoot { get; set; }

    public RestoreItemStatus Status { get; set; }

    /// <summary>
    /// Hash index key to update after restoring; null when the target is outside every synced
    /// folder (nothing would re-upload it, and writing a key there would pollute the index).
    /// </summary>
    public string? HashKey { get; set; }

    /// <summary>Why the item cannot be restored (for example, an unsafe Drive path).</summary>
    public string? PlanError { get; set; }

    public string DriveRelativePath => Remote.RelativePath;
    public string FormattedSize => OutOfSyncFile.FormatBytes(Remote.Size);

    public string StatusText => PlanError != null
        ? "Ruta no válida"
        : Status switch
        {
            RestoreItemStatus.Missing => "Falta localmente",
            RestoreItemStatus.Identical => "Idéntico",
            RestoreItemStatus.WouldOverwrite => "Se sobrescribiría",
            _ => "Sin destino"
        };

    public string LocalTargetDisplay => LocalTargetPath ?? "(sin destino)";

    public bool CanSelect => PlanError == null && !string.IsNullOrWhiteSpace(LocalTargetPath);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// Outcome of a restore run. Every requested item ends up counted in exactly one bucket.
/// </summary>
public class RestoreResultSummary
{
    public int TotalRequested { get; set; }
    public int Restored { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public List<string> SkippedMessages { get; set; } = new();
    public List<string> ErrorMessages { get; set; } = new();
    public bool Success => Errors == 0;
}
