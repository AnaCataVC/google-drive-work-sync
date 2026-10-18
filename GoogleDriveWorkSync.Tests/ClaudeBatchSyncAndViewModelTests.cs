using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using GoogleDriveWorkSync.Services.Interfaces;
using GoogleDriveWorkSync.ViewModels;
using Moq;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class ClaudeBatchSyncAndViewModelTests : IDisposable
{
    private readonly string _testDir;
    private readonly TempSettingsFileScope _settingsScope;

    public ClaudeBatchSyncAndViewModelTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GoogleDriveWorkSync_ClaudeBatchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _settingsScope = new TempSettingsFileScope(Path.Combine(_testDir, "test_settings.json"));
    }

    public void Dispose()
    {
        _settingsScope.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public void ClaudeDiscoveryCandidate_SyncStatusChange_RaisesPropertyChangedForSyncStatusAndStatusBadge()
    {
        var candidate = new ClaudeDiscoveryCandidate
        {
            FilePath = Path.Combine(_testDir, "CLAUDE.md"),
            SyncStatus = CandidateSyncStatus.New
        };

        var changedProps = new List<string>();
        candidate.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        Assert.Equal("Nuevo", candidate.StatusBadge);

        candidate.SyncStatus = CandidateSyncStatus.UpToDate;

        Assert.Contains(nameof(ClaudeDiscoveryCandidate.SyncStatus), changedProps);
        Assert.Contains(nameof(ClaudeDiscoveryCandidate.StatusBadge), changedProps);
        Assert.Equal("Al día", candidate.StatusBadge);
    }

    [Fact]
    public async Task SyncClaudeCandidatesAsync_GroupsIntoBatchesAndUpdatesStatusesAndHashes()
    {
        int postCallCount = 0;
        var handler = new FakeBatchHttpMessageHandler(async req =>
        {
            postCallCount++;
            var body = await req.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var files = doc.RootElement.GetProperty("files");
            var results = new List<object>();
            int idx = 0;
            foreach (var f in files.EnumerateArray())
            {
                results.Add(new { status = "success", fileId = $"fid_{postCallCount}_{idx++}" });
            }
            var responseJson = JsonSerializer.Serialize(new { status = "success", results });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var service = new DriveSyncService(httpClient);
        service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/fake/exec",
            OnlyModifiedOrNew = true
        });

        // Create 10 small Claude candidate files -> should be grouped into 2 batches (8 + 2)
        var candidates = new List<ClaudeDiscoveryCandidate>();
        for (int i = 0; i < 10; i++)
        {
            string path = Path.Combine(_testDir, $"skill_{i}.md");
            File.WriteAllText(path, $"# Skill {i} content");
            var fi = new FileInfo(path);
            candidates.Add(new ClaudeDiscoveryCandidate
            {
                FilePath = path,
                RelativePath = $"skills/skill_{i}.md",
                Category = ClaudeDiscoveryCategory.Skill,
                FileSizeBytes = fi.Length,
                SyncStatus = CandidateSyncStatus.New
            });
        }

        var reports = new List<SyncProgressReport>();
        var progress = new SynchronousProgress<SyncProgressReport>(r => reports.Add(r));

        var summary = await service.SyncClaudeCandidatesAsync(
            candidates,
            c => $"claude-md-unversioned/_claude-config/{c.RelativePath}",
            progress,
            CancellationToken.None,
            forceFullSync: false);

        Assert.True(summary.Success);
        Assert.Equal(10, summary.Uploaded);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(2, postCallCount); // 8 files in batch 1 + 2 files in batch 2
        Assert.All(candidates, c => Assert.Equal(CandidateSyncStatus.UpToDate, c.SyncStatus));
        Assert.NotNull(service.Settings.LastClaudeSyncAt);
        Assert.Equal(10, service.Settings.LastClaudeSyncCount);

        // Verify localFilePath was stored in hash index so orphan detection works
        File.Delete(candidates[0].FilePath);
        var orphans = await service.DetectClaudeOrphansAsync();
        Assert.Single(orphans);
        Assert.Equal(Path.GetFileName(candidates[0].FilePath), orphans[0].FileName);
    }

    [Fact]
    public async Task ContextDiscoveryViewModel_OnSyncProgressReported_DecrementsPendingCountsAndRemovesCompletedItemsLive()
    {
        var discoveryMock = new Mock<IClaudeDiscoveryService>();
        var driveSyncMock = new Mock<IDriveSyncService>();

        var settings = new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec",
            ClaudeTargetDirectory = _testDir,
            ClaudeMaxDepth = 3
        };

        driveSyncMock.Setup(s => s.Settings).Returns(settings);
        driveSyncMock.Setup(s => s.IsConfigured).Returns(true);
        driveSyncMock.Setup(s => s.LastClaudeSyncErrors).Returns(Array.Empty<SyncErrorItem>());

        string file1 = Path.Combine(_testDir, "CLAUDE.md");
        string file2 = Path.Combine(_testDir, "SKILL.md");

        discoveryMock.Setup(d => d.BuildDriveRelativePath(It.IsAny<ClaudeDiscoveryCandidate>()))
            .Returns((ClaudeDiscoveryCandidate c) => $"claude-md-unversioned/_claude-config/{c.RelativePath}");

        discoveryMock.Setup(d => d.DiscoverAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClaudeDiscoveryReport
            {
                RepositoriesScanned = 2,
                Candidates = new List<ClaudeDiscoveryCandidate>
                {
                    new()
                    {
                        FilePath = file1,
                        RelativePath = "CLAUDE.md",
                        Category = ClaudeDiscoveryCategory.Context,
                        FileSizeBytes = 128,
                        SyncStatus = CandidateSyncStatus.New,
                        IsSelected = true
                    },
                    new()
                    {
                        FilePath = file2,
                        RelativePath = "skills/SKILL.md",
                        Category = ClaudeDiscoveryCategory.Skill,
                        FileSizeBytes = 256,
                        SyncStatus = CandidateSyncStatus.Modified,
                        IsSelected = true
                    }
                }
            });

        var vm = new ContextDiscoveryViewModel(discoveryMock.Object, driveSyncMock.Object);
        await vm.DiscoverAsync();

        Assert.Equal(2, vm.OutOfSyncCandidatesCount);
        Assert.Equal(1, vm.NewCandidatesCount);
        Assert.Equal(1, vm.ModifiedCandidatesCount);
        Assert.Equal(2, vm.OutOfSyncCandidatesList.Count);
        Assert.True(vm.HasOutOfSyncCandidates);
        Assert.Equal("Por sincronizar", vm.ClaudeSyncStatusText);

        // Simulate batch completion of the first file
        vm.OnSyncProgressReported(new SyncProgressReport
        {
            TotalFiles = 2,
            ProcessedFiles = 1,
            TotalPendingFiles = 2,
            UploadedCount = 1,
            ErrorCount = 0,
            StatusMessage = "Progreso: 1 subidos, 0 errores.",
            CompletedItems = new List<CompletedSyncItem>
            {
                new()
                {
                    FilePath = file1,
                    FileName = "CLAUDE.md",
                    IsNew = true,
                    Success = true
                }
            }
        });

        Assert.Equal(1, vm.OutOfSyncCandidatesCount);
        Assert.Equal(0, vm.NewCandidatesCount);
        Assert.Equal(1, vm.ModifiedCandidatesCount);
        Assert.Single(vm.OutOfSyncCandidatesList);
        Assert.Equal("SKILL.md", vm.OutOfSyncCandidatesList[0].FileName);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class FakeBatchHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
