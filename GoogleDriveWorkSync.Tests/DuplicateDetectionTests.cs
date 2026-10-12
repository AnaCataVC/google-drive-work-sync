using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using GoogleDriveWorkSync.Services.Interfaces;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

/// <summary>
/// Exhaustive Cleanroom Black-Box test suite for Feature 2: Fast Multi-Tier Duplicate Detection.
/// Evaluates IDriveSyncService.DetectDuplicatesAsync and duplicate models strictly against
/// the formal contract specification in docs/contracts/fast-duplicate-detection.contract.md.
/// </summary>
public class DuplicateDetectionTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<string> _tempDirs = new();
    private readonly TempSettingsFileScope _settingsScope;
    private readonly DriveSyncService _service;
    private readonly SyncSource _source;

    public DuplicateDetectionTests()
    {
        var settingsDir = CreateManagedTempDirectory("DupTests_Settings");
        _settingsScope = new TempSettingsFileScope(Path.Combine(settingsDir, "test_settings.json"));
        _testDir = CreateManagedTempDirectory("DupTests_Main");
        _service = new DriveSyncService();

        _source = new SyncSource
        {
            LocalFolderPath = _testDir,
            DestinationPrefix = "dup-work"
        };

        _service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec",
            Sources = { _source }
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

    private string CreateManagedTempDirectory(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"GoogleDriveWorkSync_{suffix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string CreateTestFile(string baseDirectory, string relativePath, byte[] content, DateTime? lastModified = null)
    {
        var fullPath = Path.Combine(baseDirectory, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(fullPath, content);

        if (lastModified.HasValue)
        {
            File.SetLastWriteTimeUtc(fullPath, lastModified.Value);
        }

        return fullPath;
    }

    private string CreateTestFileOfSize(string baseDirectory, string relativePath, int sizeInBytes, byte fillByte, DateTime? lastModified = null)
    {
        var data = new byte[sizeInBytes];
        Array.Fill(data, fillByte);
        return CreateTestFile(baseDirectory, relativePath, data, lastModified);
    }

    private void PopulateHashCache(string filePath, string hash, SyncSource source)
    {
        var fi = new FileInfo(filePath);
        // Save using canonical prefix|path key format
        _service.SaveKnownHash($"{source.EffectiveDestinationPrefix}|{filePath}", hash, fi);
        // Also save with raw path to guarantee compatibility
        _service.SaveKnownHash(filePath, hash, fi);
    }

    #region Acceptance Criterion 1: Tier 1 Filtering (Unique Sizes Discarded)

    [Fact]
    public async Task DetectDuplicatesAsync_WhenAllFilesHaveUniqueSizes_ShouldDiscardAllAndReturnZeroGroups()
    {
        // Given a set of local files where all files have unique sizes > 0
        CreateTestFileOfSize(_testDir, "unique_small.txt", 128, 0x01);
        CreateTestFileOfSize(_testDir, "unique_medium.txt", 256, 0x02);
        CreateTestFileOfSize(_testDir, "unique_large.txt", 512, 0x03);

        // When DetectDuplicatesAsync() is called
        var result = await _service.DetectDuplicatesAsync();

        // Then CandidateSizeBuckets MUST equal 0
        Assert.Equal(0, result.CandidateSizeBuckets);

        // And Groups MUST be empty
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalDuplicateGroups);
        Assert.Equal(0, result.TotalDuplicateFiles);
        Assert.Equal(0, result.TotalWastedBytes);
        Assert.Equal(3, result.TotalScannedFiles);
    }

    #endregion

    #region Acceptance Criterion 2: Zero-Length File Handling

    [Fact]
    public async Task DetectDuplicatesAsync_WhenZeroLengthFilesExist_ShouldExcludeThemFromDuplicateGroups()
    {
        // Given multiple files with Length == 0
        CreateTestFile(_testDir, "empty1.txt", Array.Empty<byte>());
        CreateTestFile(_testDir, "empty2.txt", Array.Empty<byte>());
        CreateTestFile(_testDir, "empty3.txt", Array.Empty<byte>());
        CreateTestFileOfSize(_testDir, "non_empty.txt", 100, 0xAA);

        // When DetectDuplicatesAsync() is called
        var result = await _service.DetectDuplicatesAsync();

        // Then zero-byte files MUST be excluded from duplicate groups
        Assert.Equal(0, result.CandidateSizeBuckets);
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalDuplicateGroups);
        Assert.Equal(0, result.TotalDuplicateFiles);

        // And TotalWastedBytes MUST NOT count zero-byte files
        Assert.Equal(0, result.TotalWastedBytes);
        Assert.Equal(4, result.TotalScannedFiles);
    }

    #endregion

    #region Acceptance Criterion 3: Tier 2 Hash Index Cache Reuse

    [Fact]
    public async Task DetectDuplicatesAsync_WhenFilesHaveMatchingCachedHashesInHashIndex_ShouldReuseHashWithoutReadingDiskAndGroupDuplicates()
    {
        // Given two local files sharing identical size
        var size = 2048;
        var f1 = CreateTestFileOfSize(_testDir, "cached_f1.bin", size, 0x77);
        var f2 = CreateTestFileOfSize(_testDir, "cached_f2.bin", size, 0x77);

        var knownHash = _service.ComputeSha256(f1);

        // And both files have matching entries in _hashIndex
        PopulateHashCache(f1, knownHash, _source);
        PopulateHashCache(f2, knownHash, _source);

        // Lock both files with FileShare.None. If DetectDuplicatesAsync attempts to open the file stream,
        // it would throw an IOException. A successful run proves Tier 2 cached hash reuse without disk stream access.
        using var lock1 = new FileStream(f1, FileMode.Open, FileAccess.Read, FileShare.None);
        using var lock2 = new FileStream(f2, FileMode.Open, FileAccess.Read, FileShare.None);

        // When DetectDuplicatesAsync() is called
        var result = await _service.DetectDuplicatesAsync();

        // Then both files MUST be grouped into a single DuplicateFileGroup
        Assert.Single(result.Groups);
        var group = result.Groups[0];
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(knownHash, group.Hash);
        Assert.Equal(size, group.FileSize);
        Assert.Equal(size, group.WastedBytes);
    }

    [Fact]
    public async Task DetectDuplicatesAsync_WhenFilesHaveDifferingCachedHashes_ShouldReuseHashAndNotGroupThem()
    {
        // Given two files of identical size but different cached hashes
        var size = 2048;
        var f1 = CreateTestFileOfSize(_testDir, "diff_cache1.bin", size, 0x11);
        var f2 = CreateTestFileOfSize(_testDir, "diff_cache2.bin", size, 0x22);

        var hash1 = _service.ComputeSha256(f1);
        var hash2 = _service.ComputeSha256(f2);
        Assert.NotEqual(hash1, hash2);

        PopulateHashCache(f1, hash1, _source);
        PopulateHashCache(f2, hash2, _source);

        // Lock files exclusively
        using var lock1 = new FileStream(f1, FileMode.Open, FileAccess.Read, FileShare.None);
        using var lock2 = new FileStream(f2, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await _service.DetectDuplicatesAsync();

        // Then no duplicate group is formed
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalDuplicateGroups);
    }

    #endregion

    #region Acceptance Criterion 4: Tier 3A Header Hashing Filtering

    [Fact]
    public async Task DetectDuplicatesAsync_WhenFilesHaveSameSizeButDifferent16KBHeaders_ShouldDiscardWithoutFullRead()
    {
        // Given two files sharing identical file size (32 KB > 16 KB header),
        // but with different content in the first 16 KB (16,384 bytes).
        var totalSize = 32768;
        var headerSize = 16384;

        var bytesA = new byte[totalSize];
        Array.Fill(bytesA, (byte)0xAA, 0, headerSize);
        Array.Fill(bytesA, (byte)0x00, headerSize, totalSize - headerSize);

        var bytesB = new byte[totalSize];
        Array.Fill(bytesB, (byte)0xBB, 0, headerSize);
        Array.Fill(bytesB, (byte)0x00, headerSize, totalSize - headerSize);

        CreateTestFile(_testDir, "diff_header_a.bin", bytesA);
        CreateTestFile(_testDir, "diff_header_b.bin", bytesB);

        // When DetectDuplicatesAsync() is called
        var result = await _service.DetectDuplicatesAsync();

        // Then candidate size bucket was formed because size matched
        Assert.Equal(1, result.CandidateSizeBuckets);

        // But detects header hash mismatch and Groups MUST be empty
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalDuplicateGroups);
        Assert.Equal(0, result.TotalDuplicateFiles);
        Assert.Equal(0, result.TotalWastedBytes);
    }

    #endregion

    #region Acceptance Criterion 5: Tier 3B Full Stream Verification & Duplicate Grouping

    [Fact]
    public async Task DetectDuplicatesAsync_WhenFilesHaveIdenticalHeadersButDifferingTail_ShouldExcludeDifferingFileAndGroupExactMatches()
    {
        // Given three files A, B, C of size 32 KB (> 16 KB):
        // - A and B have identical content throughout.
        // - C has identical 16 KB header to A and B, but different bytes near the end.
        var totalSize = 32768;
        var headerSize = 16384;

        var bytesCommon = new byte[totalSize];
        Array.Fill(bytesCommon, (byte)0xCC, 0, headerSize);
        Array.Fill(bytesCommon, (byte)0x11, headerSize, totalSize - headerSize);

        var bytesDifferingTail = new byte[totalSize];
        Array.Fill(bytesDifferingTail, (byte)0xCC, 0, headerSize);
        Array.Fill(bytesDifferingTail, (byte)0x99, headerSize, totalSize - headerSize);

        var pathA = CreateTestFile(_testDir, "file_a.dat", bytesCommon);
        var pathB = CreateTestFile(_testDir, "file_b.dat", bytesCommon);
        var pathC = CreateTestFile(_testDir, "file_c.dat", bytesDifferingTail);

        // When DetectDuplicatesAsync() is called
        var result = await _service.DetectDuplicatesAsync();

        // Then Groups MUST contain exactly 1 group with A and B
        Assert.Equal(1, result.CandidateSizeBuckets);
        Assert.Single(result.Groups);
        var group = result.Groups[0];

        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, f => f.FilePath == pathA);
        Assert.Contains(group.Files, f => f.FilePath == pathB);

        // And C MUST NOT be included in the group
        Assert.DoesNotContain(group.Files, f => f.FilePath == pathC);

        // And WastedBytes for the group MUST equal (2 - 1) * 32,768 = 32,768 bytes
        Assert.Equal(totalSize, group.WastedBytes);
        Assert.Equal(totalSize, result.TotalWastedBytes);
        Assert.Equal(1, result.TotalDuplicateFiles);
    }

    [Fact]
    public async Task DetectDuplicatesAsync_WhenFilesAreSmallerThan16KBAndIdentical_ShouldGroupDuplicatesCorrectly()
    {
        // Given two small files (< 16 KB, e.g. 512 bytes) with identical content
        var size = 512;
        var f1 = CreateTestFileOfSize(_testDir, "small1.txt", size, 0x42);
        var f2 = CreateTestFileOfSize(_testDir, "small2.txt", size, 0x42);

        var result = await _service.DetectDuplicatesAsync();

        Assert.Single(result.Groups);
        var group = result.Groups[0];
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(size, group.FileSize);
        Assert.Equal(size, group.WastedBytes);
    }

    #endregion

    #region Acceptance Criterion 6: Primary File Attribution

    [Fact]
    public async Task DetectDuplicatesAsync_ShouldMarkEarliestLastModifiedAsPrimary_AndOthersAsNonPrimary()
    {
        // Given three identical files with distinct LastModified timestamps:
        // fOld (2026-01-01), fMid (2026-03-01), fNew (2026-06-01)
        var content = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var tOld = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var tMid = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var tNew = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        var pMid = CreateTestFile(_testDir, "file_mid.bin", content, tMid);
        var pNew = CreateTestFile(_testDir, "file_new.bin", content, tNew);
        var pOld = CreateTestFile(_testDir, "file_old.bin", content, tOld);

        // When DetectDuplicatesAsync() produces the group
        var result = await _service.DetectDuplicatesAsync();

        // Then the group contains all 3 files
        Assert.Single(result.Groups);
        var group = result.Groups[0];
        Assert.Equal(3, group.Files.Count);

        // Then the file with the earliest LastModified timestamp (pOld) MUST have IsPrimary == true
        var primaryFile = Assert.Single(group.Files, f => f.IsPrimary);
        Assert.Equal(pOld, primaryFile.FilePath);

        // And subsequent files MUST have IsPrimary == false
        var nonPrimaryFiles = group.Files.Where(f => !f.IsPrimary).ToList();
        Assert.Equal(2, nonPrimaryFiles.Count);
        Assert.Contains(nonPrimaryFiles, f => f.FilePath == pMid);
        Assert.Contains(nonPrimaryFiles, f => f.FilePath == pNew);
    }

    #endregion

    #region Acceptance Criterion 7: Error Resilience & Cancellation

    [Fact]
    public async Task DetectDuplicatesAsync_WhenEncounteringLockedFile_ShouldSkipGracefullyWithoutThrowing()
    {
        // Given valid duplicate files and an unreadable locked file with the same size
        var size = 1024;
        var p1 = CreateTestFileOfSize(_testDir, "valid1.txt", size, 0x33);
        var p2 = CreateTestFileOfSize(_testDir, "valid2.txt", size, 0x33);
        var pLocked = CreateTestFileOfSize(_testDir, "locked.txt", size, 0x99);

        // Lock pLocked with exclusive read-write lock, no share
        using var lockedStream = new FileStream(pLocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // When DetectDuplicatesAsync encounters the unreadable file
        var result = await _service.DetectDuplicatesAsync();

        // Then the service MUST gracefully skip that file without throwing an unhandled exception
        // and still group the readable duplicates
        Assert.Single(result.Groups);
        var group = result.Groups[0];
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, f => f.FilePath == p1);
        Assert.Contains(group.Files, f => f.FilePath == p2);
        Assert.DoesNotContain(group.Files, f => f.FilePath == pLocked);
    }

    [Fact]
    public async Task DetectDuplicatesAsync_WhenCancellationRequested_ShouldThrowOperationCanceledException()
    {
        // Given a pre-cancelled CancellationToken
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // When DetectDuplicatesAsync is called with the cancelled token
        // Then it MUST honor the token and throw OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.DetectDuplicatesAsync(cancellationToken: cts.Token));
    }

    #endregion

    #region Multi-Source & Scoping Invariants

    [Fact]
    public async Task DetectDuplicatesAsync_WhenOnlySourceProvided_ShouldRestrictScanToThatSource()
    {
        var testDir2 = CreateManagedTempDirectory("DupTests_SecondSource");
        var source2 = new SyncSource
        {
            LocalFolderPath = testDir2,
            DestinationPrefix = "second-source"
        };

        _service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec",
            Sources = { _source, source2 }
        });

        // Create duplicates only in source 2
        CreateTestFileOfSize(testDir2, "dup1.bin", 1024, 0x55);
        CreateTestFileOfSize(testDir2, "dup2.bin", 1024, 0x55);

        // Create unique file in source 1
        CreateTestFileOfSize(_testDir, "unique.bin", 1024, 0x77);

        // When scanning only source 1
        var resultSource1 = await _service.DetectDuplicatesAsync(onlySource: _source);
        Assert.Empty(resultSource1.Groups);
        Assert.Equal(1, resultSource1.TotalScannedFiles);

        // When scanning only source 2
        var resultSource2 = await _service.DetectDuplicatesAsync(onlySource: source2);
        Assert.Single(resultSource2.Groups);
        Assert.Equal(2, resultSource2.TotalScannedFiles);
    }

    [Fact]
    public async Task DetectDuplicatesAsync_WhenOnlySourceIsNull_ShouldScanAllSourcesAndDetectCrossSourceDuplicates()
    {
        var testDir2 = CreateManagedTempDirectory("DupTests_CrossSource");
        var source2 = new SyncSource
        {
            LocalFolderPath = testDir2,
            DestinationPrefix = "cross-source"
        };

        _service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec",
            Sources = { _source, source2 }
        });

        // Create duplicate copy across two different sync sources
        var content = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var p1 = CreateTestFile(_testDir, "file_in_source1.dat", content);
        var p2 = CreateTestFile(testDir2, "file_in_source2.dat", content);

        var result = await _service.DetectDuplicatesAsync(onlySource: null);

        Assert.Single(result.Groups);
        var group = result.Groups[0];
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, f => f.FilePath == p1);
        Assert.Contains(group.Files, f => f.FilePath == p2);
    }

    [Fact]
    public async Task DetectDuplicatesAsync_WhenMultipleDistinctDuplicateGroupsExist_ShouldGroupEachIndependentlyAndAggregateStats()
    {
        // Group A: 3 copies of 1000-byte file
        CreateTestFileOfSize(_testDir, "a1.bin", 1000, 0xAA);
        CreateTestFileOfSize(_testDir, "a2.bin", 1000, 0xAA);
        CreateTestFileOfSize(_testDir, "a3.bin", 1000, 0xAA);

        // Group B: 2 copies of 2500-byte file
        CreateTestFileOfSize(_testDir, "b1.bin", 2500, 0xBB);
        CreateTestFileOfSize(_testDir, "b2.bin", 2500, 0xBB);

        // Unique file: 5000 bytes
        CreateTestFileOfSize(_testDir, "unique.bin", 5000, 0xCC);

        var result = await _service.DetectDuplicatesAsync();

        Assert.Equal(2, result.CandidateSizeBuckets);
        Assert.Equal(2, result.TotalDuplicateGroups);

        // Total duplicate files: (3 - 1) + (2 - 1) = 3
        Assert.Equal(3, result.TotalDuplicateFiles);

        // Total wasted bytes: (2 * 1000) + (1 * 2500) = 4500
        Assert.Equal(4500, result.TotalWastedBytes);
        Assert.Equal(6, result.TotalScannedFiles);
    }

    [Fact]
    public async Task DetectDuplicatesAsync_ShouldDiscoverDuplicatesInNestedSubdirectories()
    {
        var content = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        var p1 = CreateTestFile(_testDir, "root.txt", content);
        var p2 = CreateTestFile(_testDir, Path.Combine("sub1", "nested", "copy1.txt"), content);
        var p3 = CreateTestFile(_testDir, Path.Combine("sub2", "copy2.txt"), content);

        var result = await _service.DetectDuplicatesAsync();

        Assert.Single(result.Groups);
        var group = result.Groups[0];
        Assert.Equal(3, group.Files.Count);
        Assert.Contains(group.Files, f => f.FilePath == p1);
        Assert.Contains(group.Files, f => f.FilePath == p2);
        Assert.Contains(group.Files, f => f.FilePath == p3);
    }

    #endregion

    #region Model Contracts & Invariant Verifications

    [Fact]
    public void DuplicateFileItem_PropertiesAndObservableNotification_ShouldBehaveAccordingToContract()
    {
        var item = new DuplicateFileItem
        {
            FilePath = @"C:\work\report.pdf",
            FileName = "report.pdf",
            RelativePath = "report.pdf",
            FileSize = 1048576,
            LastModified = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            IsPrimary = true
        };

        Assert.Equal(@"C:\work\report.pdf", item.FilePath);
        Assert.Equal("report.pdf", item.FileName);
        Assert.Equal("report.pdf", item.RelativePath);
        Assert.Equal(1048576, item.FileSize);
        Assert.Equal(OutOfSyncFile.FormatBytes(1048576), item.FormattedFileSize);
        Assert.True(item.IsPrimary);
        Assert.False(item.IsSelected);

        // Verify INotifyPropertyChanged on IsSelected
        var propertyChangedFired = false;
        string? changedPropertyName = null;

        item.PropertyChanged += (sender, args) =>
        {
            propertyChangedFired = true;
            changedPropertyName = args.PropertyName;
        };

        item.IsSelected = true;

        Assert.True(propertyChangedFired);
        Assert.Equal(nameof(DuplicateFileItem.IsSelected), changedPropertyName);
        Assert.True(item.IsSelected);
    }

    [Fact]
    public void DuplicateFileGroup_WastedBytesAndProperties_ShouldCalculateCorrectly()
    {
        var group = new DuplicateFileGroup
        {
            Hash = "abc123hash",
            FileSize = 2000
        };

        // Empty files collection: Math.Max(0, (0 - 1) * 2000) = 0
        Assert.Equal(0, group.WastedBytes);

        // 1 file in group: (1 - 1) * 2000 = 0
        group.Files.Add(new DuplicateFileItem { FileSize = 2000 });
        Assert.Equal(0, group.WastedBytes);

        // 2 files in group: (2 - 1) * 2000 = 2000
        group.Files.Add(new DuplicateFileItem { FileSize = 2000 });
        Assert.Equal(2000, group.WastedBytes);

        // 4 files in group: (4 - 1) * 2000 = 6000
        group.Files.Add(new DuplicateFileItem { FileSize = 2000 });
        group.Files.Add(new DuplicateFileItem { FileSize = 2000 });
        Assert.Equal(6000, group.WastedBytes);
        Assert.Equal(OutOfSyncFile.FormatBytes(6000), group.FormattedWastedBytes);
        Assert.Equal(OutOfSyncFile.FormatBytes(2000), group.FormattedFileSize);
    }

    [Fact]
    public void DuplicateScanResult_Aggregates_ShouldReflectGroupsAccurately()
    {
        var emptyResult = new DuplicateScanResult();
        Assert.Equal(0, emptyResult.TotalDuplicateGroups);
        Assert.Equal(0, emptyResult.TotalDuplicateFiles);
        Assert.Equal(0, emptyResult.TotalWastedBytes);
        Assert.Equal(OutOfSyncFile.FormatBytes(0), emptyResult.FormattedTotalWastedBytes);

        var populatedResult = new DuplicateScanResult
        {
            TotalScannedFiles = 10,
            CandidateSizeBuckets = 2,
            Groups = new List<DuplicateFileGroup>
            {
                new()
                {
                    FileSize = 1000,
                    Files = new List<DuplicateFileItem>
                    {
                        new() { FileSize = 1000 },
                        new() { FileSize = 1000 },
                        new() { FileSize = 1000 }
                    } // Wasted = 2000, Duplicates = 2
                },
                new()
                {
                    FileSize = 5000,
                    Files = new List<DuplicateFileItem>
                    {
                        new() { FileSize = 5000 },
                        new() { FileSize = 5000 }
                    } // Wasted = 5000, Duplicates = 1
                }
            }
        };

        Assert.Equal(2, populatedResult.TotalDuplicateGroups);
        Assert.Equal(3, populatedResult.TotalDuplicateFiles);
        Assert.Equal(7000, populatedResult.TotalWastedBytes);
        Assert.Equal(OutOfSyncFile.FormatBytes(7000), populatedResult.FormattedTotalWastedBytes);
    }

    #endregion
}
