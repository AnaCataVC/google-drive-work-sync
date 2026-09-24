using System;
using System.IO;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Services;
using Xunit;

// The settings path and data directory are process-wide statics; parallel test classes would
// redirect them under each other and could reach the user's real sync state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GoogleDriveWorkSync.Tests;

/// <summary>
/// Redirects both the settings file and the hash index / error log to temp locations,
/// so tests never read or wipe the user's real sync state.
/// </summary>
internal sealed class TempSettingsFileScope : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"test_sync_data_{Guid.NewGuid():N}");

    public string FilePath { get; }

    public TempSettingsFileScope(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid():N}.json");
        LocalSettingsHelper.SettingsFilePath = FilePath;
        DriveSyncService.DataDirectory = _dataDirectory;
    }

    public void Dispose()
    {
        LocalSettingsHelper.ResetToDefaultPath();
        DriveSyncService.DataDirectory = DriveSyncService.DefaultDataDirectory;
        if (File.Exists(FilePath))
        {
            try { File.Delete(FilePath); } catch { }
        }
        if (Directory.Exists(_dataDirectory))
        {
            try { Directory.Delete(_dataDirectory, true); } catch { }
        }
    }
}
