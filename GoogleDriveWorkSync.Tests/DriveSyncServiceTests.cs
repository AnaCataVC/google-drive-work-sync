using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using GoogleDriveWorkSync.Services.Interfaces;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class DriveSyncServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly TempSettingsFileScope _settingsScope;
    private readonly DriveSyncService _service;

    public DriveSyncServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GoogleDriveWorkSync_SyncTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);

        _settingsScope = new TempSettingsFileScope(Path.Combine(_testDir, "test_settings.json"));
        _service = new DriveSyncService();
    }

    public void Dispose()
    {
        _service.Dispose();
        _settingsScope.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("", "notes.md", "notes.md")]
    [InlineData(null, "sub\\notes.md", "sub/notes.md")]
    [InlineData("work-docs", "notes.md", "work-docs/notes.md")]
    [InlineData("/work-docs/", "sub\\notes.md", "work-docs/sub/notes.md")]
    public void CombineDestination_ShouldProduceForwardSlashPaths(string? prefix, string relativePath, string expected)
    {
        Assert.Equal(expected, DriveSyncService.CombineDestination(prefix, relativePath));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents\report.pdf", "work-docs/Documents/report.pdf", "report.pdf")]
    [InlineData(@"C:\docs\notes.md", "sub/notes.md", "notes.md")]
    [InlineData(@"C:\docs\notes.md", "", "notes.md")]
    public void ResolveUploadName_ShouldUseTheDestinationSegment(string filePath, string relativePath, string expected)
    {
        Assert.Equal(expected, DriveSyncService.ResolveUploadName(filePath, relativePath));
    }

    [Fact]
    public void HashKey_ShouldFallBackToTheAbsolutePath()
    {
        var file = new LocalFileMetadata { FilePath = @"C:\folder\file.md" };

        Assert.Equal(@"C:\folder\file.md", file.HashKey);

        file.HashKey = "prefix|C:\\folder\\file.md";
        Assert.Equal("prefix|C:\\folder\\file.md", file.HashKey);
    }

    [Fact]
    public void ScanFolder_ShouldExcludeBlacklistedExtensions()
    {
        File.WriteAllText(Path.Combine(_testDir, "document.pdf"), "PDF data");
        File.WriteAllText(Path.Combine(_testDir, "app.log"), "Log info");
        File.WriteAllText(Path.Combine(_testDir, "temp.tmp"), "Temp cache");

        var filters = SyncFilterOptions.Create(
            includedExtensions: "",
            excludedExtensions: ".log, .tmp",
            excludedFolders: "",
            maxFileSizeMb: 50
        );

        var results = _service.ScanFolder(_testDir, filters);

        Assert.Single(results);
        Assert.Equal("document.pdf", results[0].FileName);
    }

    [Fact]
    public void ScanFolder_ShouldOnlyIncludeWhitelistedExtensions_WhenConfigured()
    {
        File.WriteAllText(Path.Combine(_testDir, "report.docx"), "Docx content");
        File.WriteAllText(Path.Combine(_testDir, "sheet.xlsx"), "Excel content");
        File.WriteAllText(Path.Combine(_testDir, "notes.txt"), "Text content");

        var filters = SyncFilterOptions.Create(
            includedExtensions: ".docx, .xlsx",
            excludedExtensions: "",
            excludedFolders: "",
            maxFileSizeMb: 50
        );

        var results = _service.ScanFolder(_testDir, filters);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FileName == "report.docx");
        Assert.Contains(results, r => r.FileName == "sheet.xlsx");
        Assert.DoesNotContain(results, r => r.FileName == "notes.txt");
    }

    [Fact]
    public void ScanFolder_ShouldSkipIgnoredFoldersRecursively()
    {
        var workFolder = Path.Combine(_testDir, "Work");
        var gitFolder = Path.Combine(_testDir, ".git");
        var nodeFolder = Path.Combine(_testDir, "node_modules");

        Directory.CreateDirectory(workFolder);
        Directory.CreateDirectory(gitFolder);
        Directory.CreateDirectory(nodeFolder);

        File.WriteAllText(Path.Combine(workFolder, "presentation.pptx"), "Presentation data");
        File.WriteAllText(Path.Combine(gitFolder, "config"), "git config");
        File.WriteAllText(Path.Combine(nodeFolder, "package.json"), "{}");

        var filters = SyncFilterOptions.Create(
            includedExtensions: "",
            excludedExtensions: "",
            excludedFolders: "node_modules, .git",
            maxFileSizeMb: 50
        );

        var results = _service.ScanFolder(_testDir, filters);

        Assert.Single(results);
        Assert.Equal("presentation.pptx", results[0].FileName);
        Assert.Equal(Path.Combine("Work", "presentation.pptx"), results[0].RelativePath);
    }

    [Fact]
    public void ScanFolder_ShouldFilterOutFilesExceedingMaxFileSize()
    {
        var smallFile = Path.Combine(_testDir, "small.txt");
        var largeFile = Path.Combine(_testDir, "large.bin");

        File.WriteAllBytes(smallFile, new byte[1024]);
        File.WriteAllBytes(largeFile, new byte[3 * 1024 * 1024]); // 3 MB

        var filters = SyncFilterOptions.Create(
            includedExtensions: "",
            excludedExtensions: "",
            excludedFolders: "",
            maxFileSizeMb: 2 // 2 MB limit
        );

        var results = _service.ScanFolder(_testDir, filters);

        Assert.Single(results);
        Assert.Equal("small.txt", results[0].FileName);
    }

    [Fact]
    public void ComputeSha256_ShouldBeConsistentAndDeterministic()
    {
        var filePath = Path.Combine(_testDir, "test_sha.txt");
        File.WriteAllText(filePath, "Deterministic test content for GoogleDriveWorkSync");

        var hash1 = _service.ComputeSha256(filePath);
        var hash2 = _service.ComputeSha256(filePath);

        Assert.NotEmpty(hash1);
        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void UpdateSettings_ShouldUpdateServiceSettings()
    {
        var newSettings = new DriveSyncSettings
        {
            Sources = { new SyncSource { LocalFolderPath = _testDir, DestinationPrefix = "trabajo" } },
            WebAppUrl = "https://script.google.com/macros/s/test/exec",
            MaxFileSizeMb = 25
        };

        _service.UpdateSettings(newSettings);

        Assert.Equal(_testDir, Assert.Single(_service.Settings.Sources).LocalFolderPath);
        Assert.Equal("https://script.google.com/macros/s/test/exec", _service.Settings.WebAppUrl);
        Assert.Equal(25, _service.Settings.MaxFileSizeMb);
        Assert.True(_service.IsConfigured);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documentos\Trabajo", "", "Trabajo")]
    [InlineData(@"C:\Users\me\Documentos\Trabajo\", "", "Trabajo")]
    [InlineData(@"C:\Users\me\Documentos\Trabajo", "/respaldo/", "respaldo")]
    public void EffectiveDestinationPrefix_ShouldNeverBeEmpty(string localPath, string prefix, string expected)
    {
        var source = new SyncSource { LocalFolderPath = localPath, DestinationPrefix = prefix };
        Assert.Equal(expected, source.EffectiveDestinationPrefix);
    }

    private DriveSyncService.UploadCandidate MakeCandidate(string fileName, long sizeInBytes)
    {
        var path = Path.Combine(_testDir, fileName);
        using (var fs = File.Create(path))
        {
            fs.SetLength(sizeInBytes);
        }

        return new DriveSyncService.UploadCandidate(path, fileName, fileName, path, "hash", new FileInfo(path));
    }

    [Fact]
    public void BuildBatches_ShouldSplitWhenFileCountExceedsTheCap()
    {
        var candidates = Enumerable.Range(0, 9)
            .Select(i => MakeCandidate($"tiny{i}.txt", 1024))
            .ToList();

        var batches = DriveSyncService.BuildBatches(candidates);

        Assert.Equal(2, batches.Count);
        Assert.Equal(8, batches[0].Count);
        Assert.Single(batches[1]);
    }

    [Fact]
    public void BuildBatches_ShouldSplitByByteCap_AndKeepAnOversizedFileInItsOwnBatch()
    {
        var fileA = MakeCandidate("a.bin", 5L * 1024 * 1024);
        var fileB = MakeCandidate("b.bin", 5L * 1024 * 1024);
        var fileC = MakeCandidate("c.bin", 10L * 1024 * 1024);

        var batches = DriveSyncService.BuildBatches(new List<DriveSyncService.UploadCandidate> { fileA, fileB, fileC });

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Single(b));
    }

    [Fact]
    public void BuildBatches_ShouldReturnEmpty_WhenNoCandidates()
    {
        Assert.Empty(DriveSyncService.BuildBatches(new List<DriveSyncService.UploadCandidate>()));
    }

    [Fact]
    public async Task PreviewOutOfSyncAsync_ShouldListOnlyNewAndModifiedFiles_WithoutTouchingTheCache()
    {
        _service.ClearHashIndex();

        var syncFolder = Path.Combine(_testDir, "sync-source");
        Directory.CreateDirectory(syncFolder);

        var unchangedPath = Path.Combine(syncFolder, "unchanged.txt");
        var modifiedPath = Path.Combine(syncFolder, "modified.txt");
        var newPath = Path.Combine(syncFolder, "new.txt");

        File.WriteAllBytes(unchangedPath, new byte[2048]);
        File.WriteAllBytes(modifiedPath, new byte[2048]);
        File.WriteAllBytes(newPath, new byte[2048]);

        var unchangedHash = _service.ComputeSha256(unchangedPath);
        var modifiedOldHash = _service.ComputeSha256(modifiedPath);

        var source = new SyncSource { LocalFolderPath = syncFolder };
        var prefix = source.EffectiveDestinationPrefix;

        _service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/test",
            Sources = { source },
            OnlyModifiedOrNew = true
        });

        var hashIndexField = typeof(DriveSyncService).GetField("_hashIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var hashIndex = (Dictionary<string, HashCacheEntry>)hashIndexField.GetValue(_service)!;
        hashIndex[$"{prefix}|{unchangedPath}"] = new HashCacheEntry
        {
            Hash = unchangedHash,
            LastWriteTimeUtcTicks = new FileInfo(unchangedPath).LastWriteTimeUtc.Ticks,
            FileSize = 2048
        };
        hashIndex[$"{prefix}|{modifiedPath}"] = new HashCacheEntry
        {
            Hash = modifiedOldHash,
            LastWriteTimeUtcTicks = new FileInfo(modifiedPath).LastWriteTimeUtc.Ticks,
            FileSize = 2048
        };

        File.WriteAllBytes(modifiedPath, new byte[4096]);

        var indexCountBefore = hashIndex.Count;

        var outOfSync = await _service.PreviewOutOfSyncAsync();

        Assert.Equal(2, outOfSync.Count);
        Assert.Contains(outOfSync, f => f.FileName == "new.txt" && f.Reason == "Nuevo");
        Assert.Contains(outOfSync, f => f.FileName == "modified.txt" && f.Reason == "Modificado");
        Assert.DoesNotContain(outOfSync, f => f.FileName == "unchanged.txt");

        Assert.Equal(indexCountBefore, hashIndex.Count);
    }

    [Fact]
    public void PurgeOrphanHashes_ShouldKeepClaudeContextKeys_WhileDroppingMissingWorkFiles()
    {
        var existingFile = Path.Combine(_testDir, "claude.md");
        File.WriteAllText(existingFile, "context");

        _service.SaveKnownHash("claude/mi-repo/CLAUDE.md", "abc", new FileInfo(existingFile));
        _service.SaveKnownHash($"work|{Path.Combine(_testDir, "gone.txt")}", "def", new FileInfo(existingFile));

        _service.PurgeOrphanHashes();

        Assert.Equal(CandidateSyncStatus.UpToDate, _service.EvaluateFileStatus(existingFile, "claude/mi-repo/CLAUDE.md"));
        Assert.Equal(CandidateSyncStatus.New, _service.EvaluateFileStatus(existingFile, $"work|{Path.Combine(_testDir, "gone.txt")}"));
    }

    [Fact]
    public async Task RunSyncAsync_ShouldReportLockedFileAsError_InsteadOfUpToDate()
    {
        var syncFolder = Path.Combine(_testDir, "locked-source");
        Directory.CreateDirectory(syncFolder);
        var lockedPath = Path.Combine(syncFolder, "locked.txt");
        File.WriteAllText(lockedPath, "content");

        _service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/test",
            Sources = { new SyncSource { LocalFolderPath = syncFolder } },
            OnlyModifiedOrNew = true
        });

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var preview = await _service.PreviewOutOfSyncAsync();
            Assert.Contains(preview, f => f.FileName == "locked.txt" && f.Reason == "Sin acceso");

            var summary = await _service.RunSyncAsync();
            Assert.False(summary.Success);
            Assert.Equal(0, summary.Skipped);
            Assert.Contains(_service.LastSyncErrors, e => e.FilePath == lockedPath);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task TestConnectionAsync_ShouldThrow_WhenUrlIsNullOrEmpty(string? url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.TestConnectionAsync(url!));
    }
}
