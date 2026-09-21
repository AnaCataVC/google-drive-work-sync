using System;
using System.IO;
using GoogleDriveWorkSync.Helpers;

namespace GoogleDriveWorkSync.Tests;

internal sealed class TempSettingsFileScope : IDisposable
{
    public string FilePath { get; }

    public TempSettingsFileScope(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid():N}.json");
        LocalSettingsHelper.SettingsFilePath = FilePath;
    }

    public void Dispose()
    {
        LocalSettingsHelper.ResetToDefaultPath();
        if (File.Exists(FilePath))
        {
            try { File.Delete(FilePath); } catch { }
        }
    }
}
