using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using GoogleDriveWorkSync.Services.Interfaces;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class ClaudeDuplicateAndOrphanTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<string> _tempDirs = new();
    private readonly TempSettingsFileScope _settingsScope;
    private readonly DriveSyncService _service;

    public ClaudeDuplicateAndOrphanTests()
    {
        var settingsDir = CreateManagedTempDirectory("ClaudeDup_Settings");
        _settingsScope = new TempSettingsFileScope(Path.Combine(settingsDir, "test_settings.json"));
        _testDir = CreateManagedTempDirectory("ClaudeDup_Root");
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
    public async Task DetectCandidateDuplicatesAsync_IdentifiesIdenticalClaudeCandidates()
    {
        var content = "System instructions for Claude AI Pair Programming";
        var fileA = Path.Combine(_testDir, "RepoA_CLAUDE.md");
        var fileB = Path.Combine(_testDir, "RepoB_CLAUDE.md");
        var fileUnique = Path.Combine(_testDir, "Unique_CLAUDE.md");

        await File.WriteAllTextAsync(fileA, content, Encoding.UTF8);
        await File.WriteAllTextAsync(fileB, content, Encoding.UTF8);
        await File.WriteAllTextAsync(fileUnique, "Different prompt content here", Encoding.UTF8);

        var candidateA = new ClaudeDiscoveryCandidate
        {
            FilePath = fileA,
            RelativePath = "RepoA/CLAUDE.md",
            FileSizeBytes = new FileInfo(fileA).Length,
            Category = ClaudeDiscoveryCategory.Context
        };

        var candidateB = new ClaudeDiscoveryCandidate
        {
            FilePath = fileB,
            RelativePath = "RepoB/CLAUDE.md",
            FileSizeBytes = new FileInfo(fileB).Length,
            Category = ClaudeDiscoveryCategory.Context
        };

        var candidateUnique = new ClaudeDiscoveryCandidate
        {
            FilePath = fileUnique,
            RelativePath = "Unique/CLAUDE.md",
            FileSizeBytes = new FileInfo(fileUnique).Length,
            Category = ClaudeDiscoveryCategory.Context
        };

        var candidates = new List<ClaudeDiscoveryCandidate> { candidateA, candidateB, candidateUnique };

        var scanResult = await _service.DetectCandidateDuplicatesAsync(candidates);

        Assert.Equal(3, scanResult.TotalScannedFiles);
        Assert.Equal(1, scanResult.TotalDuplicateGroups);
        Assert.Equal(1, scanResult.TotalDuplicateFiles);
        Assert.True(scanResult.TotalWastedBytes > 0);

        var group = scanResult.Groups.Single();
        Assert.Equal(2, group.Files.Count);

        var primary = group.Files.Single(f => f.IsPrimary);
        var redundant = group.Files.Single(f => !f.IsPrimary);

        Assert.False(primary.IsSelected);
        Assert.True(redundant.IsSelected);
    }

    [Fact]
    public async Task DetectClaudeOrphansAsync_FlagsIndexedClaudeFilesDeletedLocally()
    {
        var localFile = Path.Combine(_testDir, "deleted_skill.md");
        await File.WriteAllTextAsync(localFile, "Temporary skill content", Encoding.UTF8);
        var fi = new FileInfo(localFile);
        string hash = _service.ComputeSha256(localFile);

        string destinationKey = "claude-md-unversioned/_claude-config/deleted_skill.md";
        _service.SaveKnownHash(destinationKey, hash, fi, fileId: "drive_file_id_123", driveRelativePath: destinationKey, localFilePath: localFile);

        // Before deletion: should have 0 orphans
        var orphansBefore = await _service.DetectClaudeOrphansAsync();
        Assert.Empty(orphansBefore);

        // Delete local file
        File.Delete(localFile);

        // After deletion: should detect as Claude orphan
        var orphansAfter = await _service.DetectClaudeOrphansAsync();
        Assert.Single(orphansAfter);

        var orphan = orphansAfter.First();
        Assert.Equal(destinationKey, orphan.HashKey);
        Assert.Equal(destinationKey, orphan.DriveRelativePath);
        Assert.Equal("drive_file_id_123", orphan.FileId);
        Assert.Equal(localFile, orphan.LocalFilePath);
        Assert.True(orphan.IsSelected);
    }

    [Fact]
    public async Task DismissOrphans_PurgesClaudeOrphanFromHashIndex()
    {
        var localFile = Path.Combine(_testDir, "to_dismiss.md");
        string destinationKey = "claude-md-unversioned/proj/to_dismiss.md";
        _service.SaveKnownHash(destinationKey, "hash_dummy", null, fileId: "id_456", driveRelativePath: destinationKey, localFilePath: localFile);

        var orphans = await _service.DetectClaudeOrphansAsync();
        Assert.Single(orphans);

        _service.DismissOrphans(orphans);

        var orphansAfter = await _service.DetectClaudeOrphansAsync();
        Assert.Empty(orphansAfter);
    }
}
