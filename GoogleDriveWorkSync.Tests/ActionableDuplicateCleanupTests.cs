using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using GoogleDriveWorkSync.Services.Interfaces;
using GoogleDriveWorkSync.ViewModels;
using Moq;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class ActionableDuplicateCleanupTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<string> _tempDirs = new();
    private readonly TempSettingsFileScope _settingsScope;
    private readonly DriveSyncService _service;

    public ActionableDuplicateCleanupTests()
    {
        var settingsDir = CreateManagedTempDirectory("ActionableDup_Settings");
        _settingsScope = new TempSettingsFileScope(Path.Combine(settingsDir, "test_settings.json"));
        _testDir = CreateManagedTempDirectory("ActionableDup_Root");
        _service = new DriveSyncService();

        _service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec",
            ClaudeDestinationPrefix = "claude-md-unversioned"
        });
    }

    public void Dispose()
    {
        _service.Dispose();
        _settingsScope.Dispose();

        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    private string CreateManagedTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    [Fact]
    public void WindowsRecycleBinHelper_HandlesNonExistentFileGracefully()
    {
        string fakePath = Path.Combine(_testDir, "non_existent_file_xyz.txt");
        bool success = WindowsRecycleBinHelper.SendToRecycleBin(fakePath, out var error);

        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public void WindowsRecycleBinHelper_RecyclesExistingFile()
    {
        string file = Path.Combine(_testDir, "test_to_recycle.txt");
        File.WriteAllText(file, "Sample content to be recycled");
        Assert.True(File.Exists(file));

        bool success = WindowsRecycleBinHelper.SendToRecycleBin(file, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task DeleteDuplicateFilesAsync_ProtectsPrimaryAndRecyclesRedundantCopies()
    {
        string primaryFile = Path.Combine(_testDir, "Original.txt");
        string copyFile1 = Path.Combine(_testDir, "Copy1.txt");
        string copyFile2 = Path.Combine(_testDir, "Copy2.txt");

        await File.WriteAllTextAsync(primaryFile, "Identical duplicate payload", Encoding.UTF8);
        await File.WriteAllTextAsync(copyFile1, "Identical duplicate payload", Encoding.UTF8);
        await File.WriteAllTextAsync(copyFile2, "Identical duplicate payload", Encoding.UTF8);

        long fileSize = new FileInfo(primaryFile).Length;

        var items = new List<DuplicateFileItem>
        {
            new DuplicateFileItem { FilePath = primaryFile, FileName = "Original.txt", FileSize = fileSize, IsPrimary = true, IsSelected = true },
            new DuplicateFileItem { FilePath = copyFile1, FileName = "Copy1.txt", FileSize = fileSize, IsPrimary = false, IsSelected = true },
            new DuplicateFileItem { FilePath = copyFile2, FileName = "Copy2.txt", FileSize = fileSize, IsPrimary = false, IsSelected = true }
        };

        var summary = await _service.DeleteDuplicateFilesAsync(items, trashFromDrive: false);

        // Primary should NOT be deleted, even if IsSelected was true
        Assert.True(File.Exists(primaryFile));
        Assert.False(File.Exists(copyFile1));
        Assert.False(File.Exists(copyFile2));

        Assert.Equal(2, summary.LocalFilesRecycled);
        Assert.Equal(fileSize * 2, summary.BytesFreed);
        Assert.True(summary.Success);
    }

    [Fact]
    public void ContextDiscoveryViewModel_ExposesCleanupAndDuplicateCommands()
    {
        var mockDiscovery = new Mock<IClaudeDiscoveryService>();
        var mockDrive = new Mock<IDriveSyncService>();
        mockDrive.Setup(d => d.Settings).Returns(new DriveSyncSettings());

        var vm = new ContextDiscoveryViewModel(mockDiscovery.Object, mockDrive.Object);

        bool cleanupFired = false;
        bool duplicateFired = false;

        vm.CleanupSuggestionsRequested += (s, e) => cleanupFired = true;
        vm.DuplicatesInspectorRequested += (s, e) => duplicateFired = true;

        vm.OpenCleanupSuggestionsCommand.Execute(null);
        vm.OpenDuplicatesInspectorCommand.Execute(null);

        Assert.True(cleanupFired);
        Assert.True(duplicateFired);
    }

    [Fact]
    public void WorkSyncViewModel_ExposesActionableDuplicateDelete()
    {
        var mockDrive = new Mock<IDriveSyncService>();
        mockDrive.Setup(d => d.Settings).Returns(new DriveSyncSettings());

        var vm = new WorkSyncViewModel(mockDrive.Object);

        bool duplicateFired = false;
        vm.DuplicatesInspectorRequested += (s, e) => duplicateFired = true;

        vm.OpenDuplicatesInspectorCommand.Execute(null);
        Assert.True(duplicateFired);
    }
}
