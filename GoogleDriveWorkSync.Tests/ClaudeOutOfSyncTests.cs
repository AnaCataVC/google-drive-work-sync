using System;
using System.IO;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class ClaudeOutOfSyncTests : IDisposable
{
    private readonly string _testDir;
    private readonly TempSettingsFileScope _settingsScope;
    private readonly DriveSyncService _driveSyncService;
    private readonly ClaudeDiscoveryService _discoveryService;

    public ClaudeOutOfSyncTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ClaudeOutOfSyncTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _settingsScope = new TempSettingsFileScope(Path.Combine(_testDir, "test_settings.json"));
        _driveSyncService = new DriveSyncService();
        _discoveryService = new ClaudeDiscoveryService(_driveSyncService, "git", _testDir);
    }

    public void Dispose()
    {
        _driveSyncService.Dispose();
        _settingsScope.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DiscoverAsync_ClassifiesNewCandidateAsOutOfSync_AndUpToDateOnceHashSaved()
    {
        // 1. Create a project with CLAUDE.md
        string projectDir = Path.Combine(_testDir, "my-claude-project");
        Directory.CreateDirectory(projectDir);
        string claudeFile = Path.Combine(projectDir, "CLAUDE.md");
        File.WriteAllText(claudeFile, "# System Instructions\nBuild things cleanly.");

        // 2. Initial discovery: should be New and OutOfSync
        var report1 = await _discoveryService.DiscoverAsync(_testDir, maxDepth: 2);
        var candidate = Assert.Single(report1.Candidates, c => c.FilePath == claudeFile);

        Assert.Equal(CandidateSyncStatus.New, candidate.SyncStatus);
        Assert.True(candidate.IsSelected); // Selected by default since it is out of sync
        Assert.Equal(1, report1.OutOfSyncCount);

        // 3. Simulate upload by saving hash to DriveSyncService
        string drivePath = _discoveryService.BuildDriveRelativePath(candidate);
        string fileHash = _driveSyncService.ComputeSha256(claudeFile);
        var fi = new FileInfo(claudeFile);
        _driveSyncService.SaveKnownHash(drivePath, fileHash, fi);

        // 4. Rediscover: now should be UpToDate and NOT selected by default
        var report2 = await _discoveryService.DiscoverAsync(_testDir, maxDepth: 2);
        var candidate2 = Assert.Single(report2.Candidates, c => c.FilePath == claudeFile);

        Assert.Equal(CandidateSyncStatus.UpToDate, candidate2.SyncStatus);
        Assert.False(candidate2.IsSelected); // Up-to-date items are not selected for sync by default
        Assert.Equal(0, report2.OutOfSyncCount);

        // 5. Modify the file
        await Task.Delay(50); // Ensure timestamp advances
        File.WriteAllText(claudeFile, "# System Instructions\nModified version.");

        // 6. Rediscover: now should be Modified and selected again
        var report3 = await _discoveryService.DiscoverAsync(_testDir, maxDepth: 2);
        var candidate3 = Assert.Single(report3.Candidates, c => c.FilePath == claudeFile);

        Assert.Equal(CandidateSyncStatus.Modified, candidate3.SyncStatus);
        Assert.True(candidate3.IsSelected);
        Assert.Equal(1, report3.OutOfSyncCount);
    }
}
