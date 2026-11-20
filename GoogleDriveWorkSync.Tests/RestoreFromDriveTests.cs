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
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class RestoreFromDriveTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _sourceDir;
    private readonly TempSettingsFileScope _settingsScope;
    private readonly FakeScriptHandler _handler = new();
    private readonly DriveSyncService _service;
    private readonly List<string> _recycled = new();

    public RestoreFromDriveTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GoogleDriveWorkSync_Restore_" + Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_testDir, "source");
        Directory.CreateDirectory(_sourceDir);
        _settingsScope = new TempSettingsFileScope(Path.Combine(_testDir, "test_settings.json"));

        _service = new DriveSyncService(new HttpClient(_handler));
        var settings = _service.Settings;
        settings.WebAppUrl = "https://script.example.test/exec";
        settings.Sources = new() { new SyncSource { LocalFolderPath = _sourceDir, DestinationPrefix = "work" } };
        _service.UpdateSettings(settings);

        // Never touch the real Recycle Bin: record the call and delete the file instead.
        _service.RecycleFile = path =>
        {
            _recycled.Add(path);
            File.Delete(path);
            return null;
        };
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

    private Dictionary<string, HashCacheEntry> HashIndex =>
        (Dictionary<string, HashCacheEntry>)typeof(DriveSyncService)
            .GetField("_hashIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_service)!;

    private static RemoteDriveItem Remote(string path, string id, long size = 10) =>
        new() { RelativePath = path, FileId = id, Size = size };

    private string WriteLocal(string relativePath, string content)
    {
        var full = Path.Combine(_testDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    // ---------------- Plan mapping ----------------

    [Fact]
    public void BuildRestorePlan_MapsByIndexFileIdFirst()
    {
        var elsewhere = Path.Combine(_testDir, "elsewhere", "notes.md");
        _service.SaveKnownHash("claude/notes.md", "h", fileId: "id-1", driveRelativePath: "claude/notes.md", localFilePath: elsewhere);

        var item = _service.BuildRestorePlan(new[] { Remote("work/renamed/notes.md", "id-1") }).Single();

        Assert.Equal(elsewhere, item.LocalTargetPath);
        Assert.Equal("claude/notes.md", item.HashKey);
        Assert.Equal(RestoreItemStatus.Missing, item.Status);
    }

    [Fact]
    public void BuildRestorePlan_MapsByIndexDrivePathWhenFileIdUnknown()
    {
        var local = Path.Combine(_testDir, "mapped", "a.txt");
        _service.SaveKnownHash("k", "h", fileId: "old-id", driveRelativePath: "other/a.txt", localFilePath: local);

        var item = _service.BuildRestorePlan(new[] { Remote("other/a.txt", "new-id") }).Single();

        Assert.Equal(local, item.LocalTargetPath);
        Assert.Equal("k", item.HashKey);
    }

    [Fact]
    public void BuildRestorePlan_MapsBySourcePrefix()
    {
        var item = _service.BuildRestorePlan(new[] { Remote("work/sub/b.txt", "id-b") }).Single();

        var expected = Path.Combine(_sourceDir, "sub", "b.txt");
        Assert.Equal(expected, item.LocalTargetPath);
        Assert.Equal($"work|{expected}", item.HashKey);
        Assert.Equal(RestoreItemStatus.Missing, item.Status);
    }

    [Fact]
    public void BuildRestorePlan_UnknownPrefix_IsUnmapped()
    {
        var item = _service.BuildRestorePlan(new[] { Remote("unknown/c.txt", "id-c") }).Single();

        Assert.Equal(RestoreItemStatus.Unmapped, item.Status);
        Assert.Null(item.LocalTargetPath);
        Assert.Null(item.PlanError);
    }

    [Fact]
    public void BuildRestorePlan_AlternateRoot_SendsEveryItemThere()
    {
        var alt = Path.Combine(_testDir, "alt");
        var plan = _service.BuildRestorePlan(new[] { Remote("work/a.txt", "1"), Remote("unknown/b.txt", "2") }, alt);

        Assert.Equal(Path.Combine(alt, "work", "a.txt"), plan[0].LocalTargetPath);
        Assert.Equal(Path.Combine(alt, "unknown", "b.txt"), plan[1].LocalTargetPath);
        Assert.All(plan, i => Assert.Equal(RestoreItemStatus.Missing, i.Status));
        // Outside every synced folder: no index key, so nothing pollutes the hash index.
        Assert.All(plan, i => Assert.Null(i.HashKey));
    }

    [Fact]
    public void BuildRestorePlan_ClassifiesIdenticalAndWouldOverwrite()
    {
        var same = WriteLocal(Path.Combine("source", "same.txt"), "same");
        var changed = WriteLocal(Path.Combine("source", "changed.txt"), "local edit");
        _service.SaveKnownHash($"work|{same}", _service.ComputeSha256(same), fileId: "id-same", driveRelativePath: "work/same.txt", localFilePath: same);
        _service.SaveKnownHash($"work|{changed}", "not-the-local-hash", fileId: "id-changed", driveRelativePath: "work/changed.txt", localFilePath: changed);
        WriteLocal(Path.Combine("source", "unknown.txt"), "exists without index entry");

        var plan = _service.BuildRestorePlan(new[]
        {
            Remote("work/same.txt", "id-same"),
            Remote("work/changed.txt", "id-changed"),
            Remote("work/unknown.txt", "id-unknown"),
            Remote("work/missing.txt", "id-missing")
        });

        Assert.Equal(RestoreItemStatus.Identical, plan[0].Status);
        Assert.Equal(RestoreItemStatus.WouldOverwrite, plan[1].Status);
        Assert.Equal(RestoreItemStatus.WouldOverwrite, plan[2].Status);
        Assert.Equal(RestoreItemStatus.Missing, plan[3].Status);
    }

    // ---------------- Trust boundary ----------------

    [Theory]
    [InlineData("../x.txt")]
    [InlineData("work/../../x.txt")]
    [InlineData("work/./x.txt")]
    [InlineData("/abs/x.txt")]
    [InlineData("C:/Windows/x.txt")]
    [InlineData("work/a<b.txt")]
    [InlineData("work//x.txt")]
    public void BuildRestorePlan_RejectsUnsafeDrivePaths(string unsafePath)
    {
        var alt = Path.Combine(_testDir, "alt");
        foreach (var root in new[] { null, alt })
        {
            var item = _service.BuildRestorePlan(new[] { Remote(unsafePath, "id") }, root).Single();
            Assert.NotNull(item.PlanError);
            Assert.Null(item.LocalTargetPath);
        }
    }

    [Fact]
    public async Task RestoreFilesAsync_RejectsTamperedTargetOutsideMappingRoot()
    {
        var outside = Path.Combine(_testDir, "outside.txt");
        var tampered = new RestorePlanItem
        {
            Remote = Remote("work/x.txt", "id-x"),
            LocalTargetPath = outside,
            MappingRoot = _sourceDir,
            Status = RestoreItemStatus.Missing
        };
        var traversal = new RestorePlanItem
        {
            Remote = Remote("../x.txt", "id-y"),
            LocalTargetPath = Path.Combine(_sourceDir, "x.txt"),
            MappingRoot = _sourceDir,
            Status = RestoreItemStatus.Missing
        };

        var summary = await _service.RestoreFilesAsync(new[] { tampered, traversal }, overwriteExisting: true);

        Assert.Equal(2, summary.Errors);
        Assert.Equal(0, summary.Restored);
        Assert.False(File.Exists(outside));
        Assert.Empty(_handler.Requests);
    }

    // ---------------- End to end ----------------

    [Fact]
    public async Task ListThenRestore_DownloadsInChunksWritesFilesAndUpdatesIndex()
    {
        for (int i = 0; i < 10; i++)
        {
            _handler.Files[$"id-{i}"] = ($"work/dir/file{i}.txt", Encoding.UTF8.GetBytes($"content {i}"));
        }

        var listed = await _service.ListRemoteFilesAsync();
        Assert.True(listed.Success, listed.ErrorMessage);
        Assert.Equal(10, listed.Items.Count);

        var plan = _service.BuildRestorePlan(listed.Items);
        var summary = await _service.RestoreFilesAsync(plan, overwriteExisting: false);

        Assert.Equal(10, summary.Restored);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(new[] { 8, 2 }, _handler.Requests.Where(r => r.Action == "download").Select(r => r.FileCount));

        for (int i = 0; i < 10; i++)
        {
            var path = Path.Combine(_sourceDir, "dir", $"file{i}.txt");
            Assert.Equal($"content {i}", File.ReadAllText(path));

            var entry = HashIndex[$"work|{path}"];
            Assert.Equal($"id-{i}", entry.FileId);
            Assert.Equal($"work/dir/file{i}.txt", entry.DriveRelativePath);
            Assert.Equal(path, entry.LocalFilePath);
            Assert.Equal(_service.ComputeSha256(path), entry.Hash);
        }

        Assert.Empty(Directory.GetFiles(_sourceDir, "*.restoretmp", SearchOption.AllDirectories));

        // The next sync must see restored files as already uploaded.
        var pending = await _service.PreviewOutOfSyncAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public void BuildDownloadBatches_RespectsCountAndSizeLimits()
    {
        const long mb = 1024 * 1024;
        var items = new[] { 5 * mb, 5 * mb, 20 * mb, 1 * mb, 1 * mb }
            .Select((size, i) => new RestorePlanItem { Remote = Remote($"work/{i}.bin", $"id{i}", size) })
            .ToList();

        var batches = DriveSyncService.BuildDownloadBatches(items);

        Assert.Equal(new[] { 1, 1, 1, 2 }, batches.Select(b => b.Count));
        Assert.Equal(20 * mb, batches[2].Single().Remote.Size);
    }

    [Fact]
    public async Task RestoreFilesAsync_OverwriteOff_SkipsExistingFile()
    {
        var existing = WriteLocal(Path.Combine("source", "a.txt"), "local");
        _handler.Files["id-a"] = ("work/a.txt", Encoding.UTF8.GetBytes("remote"));

        var plan = _service.BuildRestorePlan(new[] { Remote("work/a.txt", "id-a") });
        Assert.Equal(RestoreItemStatus.WouldOverwrite, plan[0].Status);

        var summary = await _service.RestoreFilesAsync(plan, overwriteExisting: false);

        Assert.Equal(1, summary.Skipped);
        Assert.Single(summary.SkippedMessages);
        Assert.Equal(0, summary.Restored);
        Assert.Equal("local", File.ReadAllText(existing));
        Assert.Empty(_recycled);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task RestoreFilesAsync_OverwriteOn_RecyclesOldFileThenWrites()
    {
        var existing = WriteLocal(Path.Combine("source", "a.txt"), "local");
        _handler.Files["id-a"] = ("work/a.txt", Encoding.UTF8.GetBytes("remote"));

        var plan = _service.BuildRestorePlan(new[] { Remote("work/a.txt", "id-a") });
        var summary = await _service.RestoreFilesAsync(plan, overwriteExisting: true);

        Assert.Equal(1, summary.Restored);
        Assert.Equal(new[] { existing }, _recycled);
        Assert.Equal("remote", File.ReadAllText(existing));
    }

    [Fact]
    public async Task RestoreFilesAsync_ReportsNotFoundAsError()
    {
        var plan = _service.BuildRestorePlan(new[] { Remote("work/gone.txt", "id-gone") });

        var summary = await _service.RestoreFilesAsync(plan, overwriteExisting: false);

        Assert.Equal(1, summary.Errors);
        Assert.Contains("work/gone.txt", summary.ErrorMessages.Single());
    }

    [Fact]
    public async Task OutdatedScript_ListAndDownload_ReturnDiagnostic()
    {
        _handler.Outdated = true;

        var listed = await _service.ListRemoteFilesAsync();
        Assert.False(listed.Success);
        Assert.Contains("desactualizada", listed.ErrorMessage);
        Assert.Contains("Nueva versión", listed.ErrorMessage);

        var plan = _service.BuildRestorePlan(new[] { Remote("work/a.txt", "id-a") });
        var summary = await _service.RestoreFilesAsync(plan, overwriteExisting: false);
        Assert.Equal(1, summary.Errors);
        Assert.Contains("desactualizada", summary.ErrorMessages.Single());
        Assert.False(File.Exists(Path.Combine(_sourceDir, "a.txt")));
    }

    /// <summary>Stands in for the Apps Script Web App's list and download actions.</summary>
    private sealed class FakeScriptHandler : HttpMessageHandler
    {
        public Dictionary<string, (string Path, byte[] Data)> Files { get; } = new();
        public List<(string Action, int FileCount)> Requests { get; } = new();
        public bool Outdated { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            var files = doc.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().ToList()
                : new List<JsonElement>();
            Requests.Add((action, files.Count));

            object response;
            if (Outdated)
            {
                // An old script ignores the action and answers like the legacy upload branch.
                response = new { status = "success", results = Array.Empty<object>() };
            }
            else if (action == "list")
            {
                response = new
                {
                    status = "success",
                    action = "list",
                    files = Files.Select(kv => new { relativePath = kv.Value.Path, fileId = kv.Key, size = (long)kv.Value.Data.Length, modifiedTime = "2026-01-02T03:04:05.000Z" })
                };
            }
            else
            {
                response = new
                {
                    status = "success",
                    action = "download",
                    results = files.Select(e =>
                    {
                        var id = e.GetProperty("fileId").GetString()!;
                        var path = e.GetProperty("relativePath").GetString();
                        return Files.TryGetValue(id, out var file)
                            ? (object)new { fileId = id, relativePath = path, status = "ok", data = Convert.ToBase64String(file.Data) }
                            : new { fileId = id, relativePath = path, status = "not_found", message = "missing" };
                    }).ToList()
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            };
        }
    }
}
