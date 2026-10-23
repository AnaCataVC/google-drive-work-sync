using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using GoogleDriveWorkSync.Services.Interfaces;
using Moq;
using Moq.Protected;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

/// <summary>
/// Unit and integration test suite covering advanced improvements for
/// Deduplication, Google Drive Orphan Trashing (Chunking), and Rename/Move Detection.
/// </summary>
public class AdvancedDeduplicationAndOrphanTests : IDisposable
{
    private readonly string _testDir;
    private readonly TempSettingsFileScope _settingsScope;

    public AdvancedDeduplicationAndOrphanTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GoogleDriveWorkSync_AdvancedTests_" + Guid.NewGuid().ToString("N"));
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

    private static DriveSyncService CreateService(HttpMessageHandler? handler = null)
    {
        var ctorWithClient = typeof(DriveSyncService).GetConstructor(new[] { typeof(HttpClient) });
        if (ctorWithClient != null && handler != null)
        {
            return (DriveSyncService)ctorWithClient.Invoke(new object[] { new HttpClient(handler) });
        }

        var service = new DriveSyncService();
        if (handler != null)
        {
            var clientField = typeof(DriveSyncService).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
            if (clientField != null)
            {
                clientField.SetValue(service, new HttpClient(handler));
            }
        }
        return service;
    }

    private static Dictionary<string, HashCacheEntry> GetHashIndex(DriveSyncService service)
    {
        var field = typeof(DriveSyncService).GetField("_hashIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            return (Dictionary<string, HashCacheEntry>)field.GetValue(service)!;
        }

        var prop = typeof(DriveSyncService).GetProperty("_hashIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null)
        {
            return (Dictionary<string, HashCacheEntry>)prop.GetValue(service)!;
        }

        throw new InvalidOperationException("_hashIndex not found on DriveSyncService.");
    }

    #region P0: Orphan Lifecycle Preservation & Apps Script Chunking

    [Fact]
    public async Task RunSyncAsync_WhenFileDeletedLocally_DoesNotPurgeHashUntilTrashedOrDismissed()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"status\":\"success\",\"results\":[]}", Encoding.UTF8, "application/json")
            });

        using var service = CreateService(mockHandler.Object);
        var sourceDir = Path.Combine(_testDir, "sync-work");
        Directory.CreateDirectory(sourceDir);

        var existingFile = Path.Combine(sourceDir, "staying.txt");
        File.WriteAllText(existingFile, "content stay");

        var deletedFile = Path.Combine(sourceDir, "deleted_local.txt");
        File.WriteAllText(deletedFile, "content deleted");

        service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec",
            Sources = new List<SyncSource>
            {
                new() { LocalFolderPath = sourceDir, DestinationPrefix = "work" }
            }
        });

        // Seed both in hash index
        var index = GetHashIndex(service);
        var stayingKey = $"work|{existingFile}";
        var deletedKey = $"work|{deletedFile}";

        index[stayingKey] = new HashCacheEntry
        {
            Hash = service.ComputeSha256(existingFile),
            FileId = "drive-staying",
            DriveRelativePath = "work/staying.txt"
        };
        index[deletedKey] = new HashCacheEntry
        {
            Hash = service.ComputeSha256(deletedFile),
            FileId = "drive-deleted",
            DriveRelativePath = "work/deleted_local.txt"
        };

        // Physically delete the local file
        File.Delete(deletedFile);

        // Act: Run regular sync
        var summary = await service.RunSyncAsync();

        // Assert: The deleted file's hash MUST still exist in index so orphan suggestions can detect it!
        var indexAfterSync = GetHashIndex(service);
        Assert.True(indexAfterSync.ContainsKey(deletedKey), "Deleted file hash entry was erroneously purged by RunSyncAsync!");
        Assert.True(indexAfterSync.ContainsKey(stayingKey));

        // And DetectOrphansAsync should detect it
        var orphans = await service.DetectOrphansAsync();
        Assert.Contains(orphans, o => o.HashKey == deletedKey);
    }

    [Fact]
    public async Task TrashOrphansAsync_WithMoreThanChunkSize_SplitsRequestsIntoChunks()
    {
        // Arrange: 60 orphan items (which should produce 3 chunks: 25 + 25 + 10)
        int requestCount = 0;
        var capturedBatches = new List<int>();

        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
            {
                requestCount++;
                var json = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var filesArray = doc.RootElement.GetProperty("files");
                capturedBatches.Add(filesArray.GetArrayLength());

                var results = new List<object>();
                foreach (var el in filesArray.EnumerateArray())
                {
                    results.Add(new
                    {
                        relativePath = el.GetProperty("relativePath").GetString(),
                        status = "trashed"
                    });
                }

                var responseJson = JsonSerializer.Serialize(new
                {
                    status = "success",
                    action = "trash",
                    results
                });

                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

        using var service = CreateService(mockHandler.Object);
        service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test-app/exec"
        });

        var index = GetHashIndex(service);
        var orphans = new List<DriveOrphanItem>();

        for (int i = 0; i < 60; i++)
        {
            var key = $"work|C:\\test\\file_{i}.txt";
            index[key] = new HashCacheEntry
            {
                Hash = $"hash_{i}",
                FileId = $"id_{i}",
                DriveRelativePath = $"work/file_{i}.txt"
            };

            orphans.Add(new DriveOrphanItem
            {
                HashKey = key,
                LocalFilePath = $"C:\\test\\file_{i}.txt",
                FileName = $"file_{i}.txt",
                DriveRelativePath = $"work/file_{i}.txt",
                FileId = $"id_{i}",
                IsSelected = true
            });
        }

        // Act
        var summary = await service.TrashOrphansAsync(orphans);

        // Assert
        Assert.Equal(3, requestCount);
        Assert.Equal(new[] { 25, 25, 10 }, capturedBatches);
        Assert.Equal(60, summary.TotalRequested);
        Assert.Equal(60, summary.SuccessfullyTrashed);
        Assert.Equal(0, summary.ErrorCount);

        // Verify index was purged
        Assert.Empty(index);
    }

    #endregion

    #region P0 & P1: Duplicate Filtering and Primary Rules

    [Fact]
    public async Task DetectDuplicatesAsync_InheritsExcludedFolders()
    {
        // Arrange
        using var service = new DriveSyncService();
        var sourceDir = Path.Combine(_testDir, "dup-filter-root");
        Directory.CreateDirectory(sourceDir);

        // Main folder with 2 files sharing same content
        var fileA = Path.Combine(sourceDir, "doc1.txt");
        var fileB = Path.Combine(sourceDir, "doc2.txt");
        File.WriteAllText(fileA, "identical content here");
        File.WriteAllText(fileB, "identical content here");

        // Excluded folder (e.g. node_modules or .git) with a duplicate of the same content
        var excludedDir = Path.Combine(sourceDir, "node_modules");
        Directory.CreateDirectory(excludedDir);
        var excludedFile = Path.Combine(excludedDir, "pkg.txt");
        File.WriteAllText(excludedFile, "identical content here");

        service.UpdateSettings(new DriveSyncSettings
        {
            ExcludedFolders = "node_modules, .git",
            Sources = new List<SyncSource>
            {
                new() { LocalFolderPath = sourceDir, DestinationPrefix = "work" }
            }
        });

        // Act
        var result = await service.DetectDuplicatesAsync();

        // Assert
        Assert.Equal(1, result.TotalDuplicateGroups);
        var group = result.Groups[0];
        Assert.Equal(2, group.Files.Count); // fileA and fileB, NOT pkg.txt
        Assert.DoesNotContain(group.Files, f => f.FilePath.Contains("node_modules"));
    }

    [Fact]
    public void DuplicatePrimaryRule_EvaluatesCorrectOriginal_AllVariants()
    {
        var group = new DuplicateFileGroup
        {
            Hash = "abc123",
            FileSize = 100,
            Files = new List<DuplicateFileItem>
            {
                new() { FilePath = @"C:\work\project\deep\file.txt", LastModified = new DateTime(2026, 1, 1), IsPrimary = false },
                new() { FilePath = @"C:\work\file.txt", LastModified = new DateTime(2026, 5, 1), IsPrimary = false },
                new() { FilePath = @"C:\work\backup\file_copy.txt", LastModified = new DateTime(2026, 8, 1), IsPrimary = false }
            }
        };

        // 1. OldestModified: should pick 2026-01-01 (deep)
        group.ApplyPrimaryRule(DuplicatePrimaryRule.OldestModified);
        Assert.True(group.Files[0].IsPrimary);
        Assert.False(group.Files[1].IsPrimary);
        Assert.False(group.Files[2].IsPrimary);
        Assert.False(group.Files[0].IsSelected);
        Assert.True(group.Files[1].IsSelected);
        Assert.True(group.Files[2].IsSelected);

        // 2. NewestModified: should pick 2026-08-01 (backup)
        group.ApplyPrimaryRule(DuplicatePrimaryRule.NewestModified);
        Assert.True(group.Files[2].IsPrimary);
        Assert.False(group.Files[0].IsPrimary);
        Assert.False(group.Files[1].IsPrimary);

        // 3. ShortestPath: should pick C:\work\file.txt (shortest string length)
        group.ApplyPrimaryRule(DuplicatePrimaryRule.ShortestPath);
        Assert.True(group.Files[1].IsPrimary);
        Assert.False(group.Files[0].IsPrimary);
        Assert.False(group.Files[2].IsPrimary);

        // 4. PreferCleanPath: penalizes backup / copy, so picks C:\work\project\deep\file.txt or C:\work\file.txt
        group.ApplyPrimaryRule(DuplicatePrimaryRule.PreferCleanPath);
        Assert.False(group.Files[2].IsPrimary, "Backup path should not be marked primary in PreferCleanPath!");
    }

    #endregion

    #region P2: Heuristic Rename & Move Detection

    [Fact]
    public async Task DetectOrphansAsync_DetectsHeuristicRename_FromActiveFilesAndSiblings()
    {
        // Arrange
        using var service = new DriveSyncService();
        var sourceDir = Path.Combine(_testDir, "rename-work");
        Directory.CreateDirectory(sourceDir);

        var originalPath = Path.Combine(sourceDir, "old_name.md");
        var renamedPath = Path.Combine(sourceDir, "new_name.md");

        // Write content only to renamedPath (simulating rename from old_name to new_name)
        File.WriteAllText(renamedPath, "# Important Project Documentation");

        service.UpdateSettings(new DriveSyncSettings
        {
            Sources = new List<SyncSource>
            {
                new() { LocalFolderPath = sourceDir, DestinationPrefix = "work" }
            }
        });

        // Seed old file in index with the exact hash of the content
        var hash = service.ComputeSha256(renamedPath);
        var index = GetHashIndex(service);
        var oldKey = $"work|{originalPath}";
        index[oldKey] = new HashCacheEntry
        {
            Hash = hash,
            FileSize = new FileInfo(renamedPath).Length,
            FileId = "drive-old-id",
            DriveRelativePath = "work/old_name.md"
        };

        // Act
        var orphans = await service.DetectOrphansAsync();

        // Assert
        Assert.Single(orphans);
        var orphan = orphans[0];
        Assert.Equal(oldKey, orphan.HashKey);
        Assert.True(orphan.IsRenamed);
        Assert.Equal(renamedPath, orphan.RenamedToPath);
    }

    #endregion
}
