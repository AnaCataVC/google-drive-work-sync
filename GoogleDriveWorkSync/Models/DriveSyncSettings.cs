using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace GoogleDriveWorkSync.Models;

/// <summary>
/// One local folder to synchronize and the name of the Drive subfolder it lands in.
/// </summary>
public class SyncSource
{
    public string LocalFolderPath { get; set; } = string.Empty;

    /// <summary>Name of the destination subfolder in Drive.</summary>
    public string DestinationPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Destination actually used when syncing.
    /// </summary>
    [JsonIgnore]
    public string EffectiveDestinationPrefix =>
        string.IsNullOrWhiteSpace(DestinationPrefix)
            ? (string.IsNullOrWhiteSpace(LocalFolderPath) ? "Work" : new DirectoryInfo(LocalFolderPath.TrimEnd('\\', '/')).Name)
            : DestinationPrefix.Replace('\\', '/').Trim('/');
}

/// <summary>
/// Settings for Google Drive synchronization across work folders and Claude context.
/// </summary>
public class DriveSyncSettings
{
    public string WebAppUrl { get; set; } = string.Empty;

    /// <summary>
    /// Drive folder the Web App writes into (user-facing clickable link).
    /// </summary>
    public string DriveFolderUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional shared secret authentication token for Google Apps Script Web App.
    /// </summary>
    public string AuthToken { get; set; } = string.Empty;

    public string IncludedExtensions { get; set; } = string.Empty;
    public string ExcludedExtensions { get; set; } = ".tmp, .log, .exe, .bak, .zip";
    public string ExcludedFolders { get; set; } = "node_modules, .git, bin, obj, .vs, temp";
    public long MaxFileSizeMb { get; set; } = 20;
    public bool OnlyModifiedOrNew { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastSyncTime { get; set; }
    public string LastSyncStatus { get; set; } = "Nunca sincronizado";

    /// <summary>Every folder to synchronize for Work Files.</summary>
    public List<SyncSource> Sources { get; set; } = new();

    /// <summary>Drive destination prefix for Claude AI Context.</summary>
    public string ClaudeDestinationPrefix { get; set; } = "claude-md-unversioned";

    /// <summary>Bucket for Claude files with no owning Git repository.</summary>
    public string ClaudeNoRepoBucketName { get; set; } = "_sin-repo";

    /// <summary>Bucket for Claude global configuration (skills, hooks, memories with no repo).</summary>
    public string ClaudeConfigBucketName { get; set; } = "_claude-config";

    /// <summary>Timestamp of last successful Claude context sync.</summary>
    public DateTime? LastClaudeSyncAt { get; set; }

    /// <summary>Count of files uploaded in last Claude context sync.</summary>
    public int LastClaudeSyncCount { get; set; }
}
