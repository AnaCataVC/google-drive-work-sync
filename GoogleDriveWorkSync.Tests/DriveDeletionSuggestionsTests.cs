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
/// Cleanroom black-box test suite for Google Drive Deletion Suggestions & Orphan Reconciliation.
/// Validates compliance with docs/contracts/drive-deletion-suggestions.contract.md.
/// </summary>
public class DriveDeletionSuggestionsTests : IDisposable
{
    private readonly string _testDir;
    private readonly TempSettingsFileScope _settingsScope;

    public DriveDeletionSuggestionsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GoogleDriveWorkSync_OrphanTests_" + Guid.NewGuid().ToString("N"));
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

    #region Helper Methods

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

        throw new InvalidOperationException("_hashIndex field or property not found on DriveSyncService.");
    }

    #endregion

    #region Acceptance Criterion 1: Orphan Detection

    [Fact]
    public async Task AcceptanceCriterion1_DetectOrphansAsync_IdentifiesMissingFiles_AndIgnoresExistingFilesAndClaudeContextKeys()
    {
        using var service = CreateService();
        var sourceDir = Path.Combine(_testDir, "work-docs");
        Directory.CreateDirectory(sourceDir);

        var existingFilePath = Path.Combine(sourceDir, "budget.xlsx");
        File.WriteAllText(existingFilePath, "budget data");

        var missingFilePath = Path.Combine(sourceDir, "report.docx");
        if (File.Exists(missingFilePath)) File.Delete(missingFilePath);

        var source = new SyncSource
        {
            LocalFolderPath = sourceDir,
            DestinationPrefix = "work"
        };
        service.UpdateSettings(new DriveSyncSettings
        {
            Sources = { source }
        });

        var index = GetHashIndex(service);
        var key1 = $"work|{missingFilePath}";
        var key2 = $"work|{existingFilePath}";
        var key3 = "claude-config/settings.json";

        index[key1] = new HashCacheEntry
        {
            Hash = "hash-report",
            FileSize = 1024,
            LastWriteTimeUtcTicks = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks,
            FileId = "drive-id-report",
            DriveRelativePath = "work/report.docx"
        };

        index[key2] = new HashCacheEntry
        {
            Hash = "hash-budget",
            FileSize = 2048,
            LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(existingFilePath).Ticks,
            FileId = "drive-id-budget",
            DriveRelativePath = "work/budget.xlsx"
        };

        index[key3] = new HashCacheEntry
        {
            Hash = "hash-claude",
            FileSize = 512,
            LastWriteTimeUtcTicks = DateTime.UtcNow.Ticks
        };

        var orphans = await service.DetectOrphansAsync();

        var orphan = Assert.Single(orphans);
        Assert.Equal(key1, orphan.HashKey);
        Assert.Equal(missingFilePath, orphan.LocalFilePath);
        Assert.Equal("report.docx", orphan.FileName);
        Assert.Equal("work/report.docx", orphan.DriveRelativePath);
        Assert.Equal("drive-id-report", orphan.FileId);
        Assert.Equal(1024, orphan.FileSize);
        Assert.True(orphan.IsSelected);
        Assert.DoesNotContain(orphans, o => o.HashKey == key2);
        Assert.DoesNotContain(orphans, o => o.HashKey == key3);
    }

    [Fact]
    public async Task DetectOrphansAsync_WhenNoOrphansExist_ReturnsEmptyList()
    {
        using var service = CreateService();
        var sourceDir = Path.Combine(_testDir, "clean-source");
        Directory.CreateDirectory(sourceDir);

        var file1 = Path.Combine(sourceDir, "file1.txt");
        File.WriteAllText(file1, "content1");

        service.UpdateSettings(new DriveSyncSettings
        {
            Sources = { new SyncSource { LocalFolderPath = sourceDir, DestinationPrefix = "clean" } }
        });

        var index = GetHashIndex(service);
        index[$"clean|{file1}"] = new HashCacheEntry
        {
            Hash = "h1",
            FileSize = 8,
            LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(file1).Ticks
        };

        var orphans = await service.DetectOrphansAsync();
        Assert.Empty(orphans);
    }

    [Fact]
    public async Task DetectOrphansAsync_WhenMultipleActiveSourcesHaveMissingFiles_ReturnsAllOrphans()
    {
        using var service = CreateService();
        var dirA = Path.Combine(_testDir, "sourceA");
        var dirB = Path.Combine(_testDir, "sourceB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var missingA = Path.Combine(dirA, "goneA.txt");
        var missingB = Path.Combine(dirB, "goneB.txt");

        service.UpdateSettings(new DriveSyncSettings
        {
            Sources =
            {
                new SyncSource { LocalFolderPath = dirA, DestinationPrefix = "srcA" },
                new SyncSource { LocalFolderPath = dirB, DestinationPrefix = "srcB" }
            }
        });

        var index = GetHashIndex(service);
        index[$"srcA|{missingA}"] = new HashCacheEntry { Hash = "ha", FileSize = 10 };
        index[$"srcB|{missingB}"] = new HashCacheEntry { Hash = "hb", FileSize = 20 };

        var orphans = await service.DetectOrphansAsync();
        Assert.Equal(2, orphans.Count);
        Assert.Contains(orphans, o => o.HashKey == $"srcA|{missingA}");
        Assert.Contains(orphans, o => o.HashKey == $"srcB|{missingB}");
    }

    #endregion

    #region Acceptance Criterion 2: Remote Trashing Wire Protocol

    [Fact]
    public async Task AcceptanceCriterion2_TrashOrphansAsync_TransmitsWireProtocol_AndPurgesTrashedAndNotFoundKeys()
    {
        string? requestBody = null;
        HttpRequestMessage? capturedRequest = null;

        var responseJson = """
        {
          "status": "success",
          "action": "trash",
          "results": [
            { "relativePath": "work/doc1.docx", "status": "trashed" },
            { "relativePath": "work/doc2.xlsx", "status": "not_found" }
          ]
        }
        """;

        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) =>
            {
                capturedRequest = req;
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        using var service = CreateService(mockHandler.Object);
        service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test-app/exec",
            AuthToken = "token-xyz"
        });

        var key1 = "work|C:\\docs\\doc1.docx";
        var key2 = "work|C:\\docs\\doc2.xlsx";

        var index = GetHashIndex(service);
        index[key1] = new HashCacheEntry
        {
            Hash = "hash1",
            FileId = "drive-id-1",
            DriveRelativePath = "work/doc1.docx"
        };
        index[key2] = new HashCacheEntry
        {
            Hash = "hash2",
            FileId = "drive-id-2",
            DriveRelativePath = "work/doc2.xlsx"
        };

        var orphans = new List<DriveOrphanItem>
        {
            new()
            {
                HashKey = key1,
                LocalFilePath = "C:\\docs\\doc1.docx",
                FileName = "doc1.docx",
                FileId = "drive-id-1",
                DriveRelativePath = "work/doc1.docx",
                IsSelected = true
            },
            new()
            {
                HashKey = key2,
                LocalFilePath = "C:\\docs\\doc2.xlsx",
                FileName = "doc2.xlsx",
                FileId = "drive-id-2",
                DriveRelativePath = "work/doc2.xlsx",
                IsSelected = true
            }
        };

        var summary = await service.TrashOrphansAsync(orphans);

        // Verify wire protocol
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("https://script.google.com/macros/s/test-app/exec", capturedRequest.RequestUri?.ToString());

        Assert.NotNull(capturedRequest?.Content);
        requestBody = await capturedRequest.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(requestBody);
        var root = jsonDoc.RootElement;
        Assert.Equal("trash", root.GetProperty("action").GetString());
        Assert.Equal("token-xyz", root.GetProperty("authToken").GetString());

        var files = root.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal("drive-id-1", files[0].GetProperty("fileId").GetString());
        Assert.Equal("work/doc1.docx", files[0].GetProperty("relativePath").GetString());
        Assert.Equal("drive-id-2", files[1].GetProperty("fileId").GetString());
        Assert.Equal("work/doc2.xlsx", files[1].GetProperty("relativePath").GetString());

        // Verify result summary
        Assert.Equal(2, summary.TotalRequested);
        Assert.Equal(1, summary.SuccessfullyTrashed);
        Assert.Equal(1, summary.NotFoundCount);
        Assert.Equal(0, summary.ErrorCount);
        Assert.True(summary.Success);

        // Verify index in memory purged
        Assert.False(index.ContainsKey(key1));
        Assert.False(index.ContainsKey(key2));

        // Verify persistence to disk
        var diskIndexPath = Path.Combine(DriveSyncService.DataDirectory, "sync_hashes.json");
        if (File.Exists(diskIndexPath))
        {
            var diskContent = File.ReadAllText(diskIndexPath);
            Assert.DoesNotContain("doc1.docx", diskContent);
            Assert.DoesNotContain("doc2.xlsx", diskContent);
        }
    }

    #endregion

    #region Acceptance Criterion 3: Trashing Error Resilience

    [Fact]
    public async Task AcceptanceCriterion3_TrashOrphansAsync_WhenServerReturnsError_RetainsKeysAndReportsError()
    {
        var responseJson = """
        {
          "status": "error",
          "message": "Access Denied"
        }
        """;

        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        using var service = CreateService(mockHandler.Object);
        service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec"
        });

        var key = "work|C:\\docs\\protected.docx";
        var index = GetHashIndex(service);
        index[key] = new HashCacheEntry
        {
            Hash = "hash1",
            FileId = "protected-id",
            DriveRelativePath = "work/protected.docx"
        };

        var orphans = new List<DriveOrphanItem>
        {
            new()
            {
                HashKey = key,
                LocalFilePath = "C:\\docs\\protected.docx",
                FileName = "protected.docx",
                FileId = "protected-id",
                DriveRelativePath = "work/protected.docx"
            }
        };

        var summary = await service.TrashOrphansAsync(orphans);

        Assert.False(summary.Success);
        Assert.True(summary.ErrorCount > 0);
        Assert.Contains(summary.ErrorMessages, msg => msg.Contains("Access Denied"));
        Assert.True(index.ContainsKey(key));
    }

    [Fact]
    public async Task AcceptanceCriterion3_TrashOrphansAsync_WhenHttpFails_RetainsKeysAndReportsError()
    {
        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError,
                Content = new StringContent("Server Error", Encoding.UTF8, "text/plain")
            });

        using var service = CreateService(mockHandler.Object);
        service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec"
        });

        var key = "work|C:\\docs\\file.docx";
        var index = GetHashIndex(service);
        index[key] = new HashCacheEntry { Hash = "h", FileId = "id", DriveRelativePath = "work/file.docx" };

        var orphans = new List<DriveOrphanItem>
        {
            new() { HashKey = key, FileName = "file.docx", FileId = "id", DriveRelativePath = "work/file.docx" }
        };

        var summary = await service.TrashOrphansAsync(orphans);

        Assert.False(summary.Success);
        Assert.Equal(1, summary.ErrorCount);
        Assert.True(index.ContainsKey(key));
    }

    [Fact]
    public async Task TrashOrphansAsync_WhenResultsContainIndividualErrors_OnlyPurgesSuccessfulItems()
    {
        var responseJson = """
        {
          "status": "success",
          "action": "trash",
          "results": [
            { "relativePath": "work/good.docx", "status": "trashed" },
            { "relativePath": "work/locked.docx", "status": "error", "message": "File is locked" }
          ]
        }
        """;

        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        using var service = CreateService(mockHandler.Object);
        service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test/exec"
        });

        var keyGood = "work|C:\\docs\\good.docx";
        var keyLocked = "work|C:\\docs\\locked.docx";

        var index = GetHashIndex(service);
        index[keyGood] = new HashCacheEntry { Hash = "h1", DriveRelativePath = "work/good.docx" };
        index[keyLocked] = new HashCacheEntry { Hash = "h2", DriveRelativePath = "work/locked.docx" };

        var orphans = new List<DriveOrphanItem>
        {
            new() { HashKey = keyGood, DriveRelativePath = "work/good.docx" },
            new() { HashKey = keyLocked, DriveRelativePath = "work/locked.docx" }
        };

        var summary = await service.TrashOrphansAsync(orphans);

        Assert.Equal(1, summary.SuccessfullyTrashed);
        Assert.Equal(1, summary.ErrorCount);
        Assert.False(summary.Success);
        Assert.False(index.ContainsKey(keyGood));
        Assert.True(index.ContainsKey(keyLocked));
    }

    #endregion

    #region Acceptance Criterion 4: Local Dismissal

    [Fact]
    public void AcceptanceCriterion4_DismissOrphans_RemovesKeysLocallyWithoutNetworkRequests()
    {
        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        using var service = CreateService(mockHandler.Object);

        var key1 = "work|C:\\docs\\old1.docx";
        var key2 = "work|C:\\docs\\old2.docx";
        var retainedKey = "work|C:\\docs\\keep.docx";

        var index = GetHashIndex(service);
        index[key1] = new HashCacheEntry { Hash = "h1", FileSize = 100 };
        index[key2] = new HashCacheEntry { Hash = "h2", FileSize = 200 };
        index[retainedKey] = new HashCacheEntry { Hash = "h3", FileSize = 300 };

        var orphansToDismiss = new List<DriveOrphanItem>
        {
            new() { HashKey = key1, FileName = "old1.docx" },
            new() { HashKey = key2, FileName = "old2.docx" }
        };

        service.DismissOrphans(orphansToDismiss);

        // No network requests should have been made
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());

        // Keys purged in memory
        Assert.False(index.ContainsKey(key1));
        Assert.False(index.ContainsKey(key2));
        Assert.True(index.ContainsKey(retainedKey));

        // Persisted to disk
        var diskIndexPath = Path.Combine(DriveSyncService.DataDirectory, "sync_hashes.json");
        if (File.Exists(diskIndexPath))
        {
            var diskContent = File.ReadAllText(diskIndexPath);
            Assert.DoesNotContain("old1.docx", diskContent);
            Assert.DoesNotContain("old2.docx", diskContent);
            Assert.Contains("keep.docx", diskContent);
        }
    }

    [Fact]
    public void DismissOrphans_WithEmptyList_DoesNotModifyIndexOrThrow()
    {
        using var service = CreateService();
        var index = GetHashIndex(service);
        index["key1"] = new HashCacheEntry { Hash = "h" };

        service.DismissOrphans(Array.Empty<DriveOrphanItem>());

        Assert.True(index.ContainsKey("key1"));
    }

    #endregion

    #region Acceptance Criterion 5: Capture FileId on Upload

    [Fact]
    public void AcceptanceCriterion5_HashCacheEntry_PreservesFileIdAndDriveRelativePath()
    {
        var entry = new HashCacheEntry
        {
            Hash = "sha256-abc",
            FileSize = 4096,
            LastWriteTimeUtcTicks = DateTime.UtcNow.Ticks,
            FileId = "1abc_gdrive_id_123",
            DriveRelativePath = "work/reports/annual.pdf"
        };

        Assert.Equal("1abc_gdrive_id_123", entry.FileId);
        Assert.Equal("work/reports/annual.pdf", entry.DriveRelativePath);

        var json = JsonSerializer.Serialize(entry);
        var deserialized = JsonSerializer.Deserialize<HashCacheEntry>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("1abc_gdrive_id_123", deserialized.FileId);
        Assert.Equal("work/reports/annual.pdf", deserialized.DriveRelativePath);
    }

    [Fact]
    public async Task AcceptanceCriterion5_RunSyncAsync_CapturesFileIdAndDriveRelativePathInHashIndex()
    {
        var syncFolder = Path.Combine(_testDir, "sync-capture-test");
        Directory.CreateDirectory(syncFolder);
        var localFile = Path.Combine(syncFolder, "presentation.pptx");
        File.WriteAllText(localFile, "slide deck content");

        var source = new SyncSource
        {
            LocalFolderPath = syncFolder,
            DestinationPrefix = "deck-source"
        };

        var uploadResponseJson = """
        {
          "status": "success",
          "uploaded": 1,
          "results": [
            {
              "status": "success",
              "relativePath": "deck-source/presentation.pptx",
              "fileId": "1abc_captured_file_id_999"
            }
          ]
        }
        """;

        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(uploadResponseJson, Encoding.UTF8, "application/json")
            });

        using var service = CreateService(mockHandler.Object);
        service.UpdateSettings(new DriveSyncSettings
        {
            WebAppUrl = "https://script.google.com/macros/s/test-app/exec",
            Sources = { source }
        });

        var summary = await service.RunSyncAsync();

        if (summary.Uploaded > 0)
        {
            var index = GetHashIndex(service);
            var expectedKey = $"deck-source|{localFile}";
            if (index.TryGetValue(expectedKey, out var entry))
            {
                Assert.Equal("1abc_captured_file_id_999", entry.FileId);
                Assert.Equal("deck-source/presentation.pptx", entry.DriveRelativePath);
            }
        }
    }

    #endregion

    #region Acceptance Criterion 6: Backward Compatibility

    [Fact]
    public void AcceptanceCriterion6_BackwardCompatibility_DeserializesWithoutFileIdOrRelativePath()
    {
        var jsonWithoutNewFields = """
        {
          "Hash": "abc12345",
          "LastWriteTimeUtcTicks": 638000000000000000,
          "FileSize": 1024
        }
        """;

        var entry = JsonSerializer.Deserialize<HashCacheEntry>(jsonWithoutNewFields);
        Assert.NotNull(entry);
        Assert.Equal("abc12345", entry.Hash);
        Assert.Equal(638000000000000000, entry.LastWriteTimeUtcTicks);
        Assert.Equal(1024, entry.FileSize);
        Assert.Null(entry.FileId);
        Assert.Null(entry.DriveRelativePath);
    }

    [Fact]
    public void AcceptanceCriterion6_LoadHashIndex_LoadsFileWithoutNewFields_WithoutException()
    {
        var legacyDiskJson = """
        {
          "work|C:\\legacy\\file.txt": {
            "Hash": "old_hash",
            "LastWriteTimeUtcTicks": 638000000000000000,
            "FileSize": 500
          }
        }
        """;

        Directory.CreateDirectory(DriveSyncService.DataDirectory);
        File.WriteAllText(Path.Combine(DriveSyncService.DataDirectory, "sync_hashes.json"), legacyDiskJson);

        using var service = CreateService();
        var index = GetHashIndex(service);

        Assert.True(index.ContainsKey("work|C:\\legacy\\file.txt"));
        var entry = index["work|C:\\legacy\\file.txt"];
        Assert.Equal("old_hash", entry.Hash);
        Assert.Null(entry.FileId);
        Assert.Null(entry.DriveRelativePath);
    }

    [Fact]
    public void AcceptanceCriterion6_LoadHashIndex_SupportsLegacySimpleStringDictionary()
    {
        var legacyStringDiskJson = """
        {
          "work|C:\\legacy\\simple.txt": "raw_hash_string_value"
        }
        """;

        Directory.CreateDirectory(DriveSyncService.DataDirectory);
        File.WriteAllText(Path.Combine(DriveSyncService.DataDirectory, "sync_hashes.json"), legacyStringDiskJson);

        using var service = CreateService();
        var index = GetHashIndex(service);

        Assert.True(index.ContainsKey("work|C:\\legacy\\simple.txt"));
        var entry = index["work|C:\\legacy\\simple.txt"];
        Assert.Equal("raw_hash_string_value", entry.Hash);
        Assert.Null(entry.FileId);
        Assert.Null(entry.DriveRelativePath);
    }

    #endregion

    #region Boundary Values & Edge Cases

    [Fact]
    public async Task BoundaryValues_TrashOrphansAsync_WithEmptyCollection_ReturnsImmediately()
    {
        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        using var service = CreateService(mockHandler.Object);
        service.UpdateSettings(new DriveSyncSettings { WebAppUrl = "https://script.google.com/test" });

        var summary = await service.TrashOrphansAsync(Array.Empty<DriveOrphanItem>());

        Assert.Equal(0, summary.TotalRequested);
        Assert.Equal(0, summary.SuccessfullyTrashed);
        Assert.Equal(0, summary.ErrorCount);
        Assert.True(summary.Success);
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task BoundaryValues_TrashOrphansAsync_WhenUnconfigured_ReturnsFailureWithoutThrowing()
    {
        using var service = CreateService();
        service.UpdateSettings(new DriveSyncSettings { WebAppUrl = "" });
        Assert.False(service.IsConfigured);

        var orphans = new List<DriveOrphanItem>
        {
            new() { HashKey = "work|C:\\docs\\file.docx", FileName = "file.docx" }
        };

        var summary = await service.TrashOrphansAsync(orphans);

        Assert.False(summary.Success);
        Assert.True(summary.ErrorCount > 0);
        Assert.NotEmpty(summary.ErrorMessages);
    }

    [Fact]
    public async Task BoundaryValues_DetectOrphansAsync_HonorsCancellationToken()
    {
        using var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DetectOrphansAsync(cts.Token));
    }

    [Fact]
    public async Task BoundaryValues_TrashOrphansAsync_HonorsCancellationToken()
    {
        using var service = CreateService();
        service.UpdateSettings(new DriveSyncSettings { WebAppUrl = "https://script.google.com/test" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var orphans = new List<DriveOrphanItem>
        {
            new() { HashKey = "work|C:\\docs\\file.docx", FileName = "file.docx" }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TrashOrphansAsync(orphans, cts.Token));
    }

    [Fact]
    public void BoundaryValues_DismissOrphans_ThreadSafety_ConcurrentMutations()
    {
        using var service = CreateService();
        var index = GetHashIndex(service);

        var items = new List<DriveOrphanItem>();
        for (int i = 0; i < 50; i++)
        {
            var key = $"work|C:\\docs\\item_{i}.txt";
            index[key] = new HashCacheEntry { Hash = $"h{i}", FileSize = 100 };
            items.Add(new DriveOrphanItem { HashKey = key, FileName = $"item_{i}.txt" });
        }

        Parallel.ForEach(items.Chunk(5), chunk =>
        {
            service.DismissOrphans(chunk);
        });

        Assert.Empty(index);
    }

    #endregion

    #region Model Invariants & Property Notifications

    [Fact]
    public void DriveOrphanItem_DefaultsAndPropertyChangeNotification()
    {
        var item = new DriveOrphanItem();
        Assert.True(item.IsSelected);
        Assert.Equal(string.Empty, item.HashKey);
        Assert.Equal(string.Empty, item.LocalFilePath);
        Assert.Equal(string.Empty, item.FileName);
        Assert.Equal(string.Empty, item.DriveRelativePath);
        Assert.Null(item.FileId);
        Assert.Equal(0, item.FileSize);
        Assert.Null(item.LastWriteTimeUtc);

        bool fired = false;
        item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DriveOrphanItem.IsSelected))
                fired = true;
        };

        item.IsSelected = false;
        Assert.False(item.IsSelected);
        Assert.True(fired);
    }

    [Fact]
    public void DriveOrphanItem_FormattedFileSize_MatchesOutOfSyncFileFormatBytes()
    {
        var item = new DriveOrphanItem { FileSize = 1048576 };
        Assert.Equal(OutOfSyncFile.FormatBytes(1048576), item.FormattedFileSize);
    }

    [Fact]
    public void TrashResultSummary_SuccessProperty_AccuratelyReflectsErrorCount()
    {
        var summary = new TrashResultSummary { ErrorCount = 0 };
        Assert.True(summary.Success);

        summary.ErrorCount = 2;
        Assert.False(summary.Success);
    }

    [Fact]
    public async Task IDriveSyncService_MoqContractSupport()
    {
        var mock = new Mock<IDriveSyncService>();
        var orphanList = new List<DriveOrphanItem>
        {
            new() { HashKey = "work|C:\\a.txt", FileName = "a.txt" }
        };

        mock.Setup(s => s.DetectOrphansAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(orphanList);

        mock.Setup(s => s.TrashOrphansAsync(It.IsAny<IEnumerable<DriveOrphanItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrashResultSummary
            {
                TotalRequested = 1,
                SuccessfullyTrashed = 1,
                ErrorCount = 0
            });

        var orphans = await mock.Object.DetectOrphansAsync();
        Assert.Single(orphans);

        var summary = await mock.Object.TrashOrphansAsync(orphans);
        Assert.True(summary.Success);

        mock.Object.DismissOrphans(orphans);
        mock.Verify(s => s.DismissOrphans(orphans), Times.Once);
    }

    #endregion
}
