using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GoogleDriveWorkSync.Helpers;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class NativeSyncEngineTests
{
    [Fact]
    public void IsNativeAvailable_ReturnsTrue_WhenDllIsPresent()
    {
        Assert.True(NativeSyncEngine.IsNativeAvailable, "rust_sync_engine.dll should be loaded and available.");
    }

    [Fact]
    public void ComputeFileHash_ComputesConsistentBlake3Hash()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"native_test_{Guid.NewGuid()}.txt");
        try
        {
            File.WriteAllText(tempFile, "Hello Google Drive Work Sync Rust SIMD!");
            string hash1 = NativeSyncEngine.ComputeFileHash(tempFile);
            string hash2 = NativeSyncEngine.ComputeFileHash(tempFile);

            Assert.False(string.IsNullOrWhiteSpace(hash1));
            Assert.Equal(64, hash1.Length);
            Assert.Equal(hash1, hash2);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ComputeHeaderHash_ReadsUpToMaxBytesCorrectly()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"native_header_test_{Guid.NewGuid()}.bin");
        try
        {
            byte[] bytes = new byte[32768];
            Array.Fill(bytes, (byte)42);
            File.WriteAllBytes(tempFile, bytes);

            var headerRes = NativeSyncEngine.ComputeHeaderHash(tempFile, 16384);
            Assert.NotNull(headerRes);
            Assert.Equal(16384, headerRes.Value.BytesRead);
            Assert.Equal(64, headerRes.Value.HeaderHash.Length);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void BatchHashFiles_HashesMultipleFilesConcurrentlyWithRayon()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"native_batch_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var files = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                string f = Path.Combine(tempDir, $"file_{i}.txt");
                File.WriteAllText(f, $"Content batch index {i}");
                files.Add(f);
            }

            var batchRes = NativeSyncEngine.BatchHashFiles(files);
            Assert.NotNull(batchRes);
            Assert.Equal(10, batchRes.Results.Count);
            foreach (var r in batchRes.Results)
            {
                Assert.True(r.Success);
                Assert.Equal(64, r.Hash.Length);
                Assert.True(r.FileSize > 0);
                Assert.True(r.LastWriteTimeUtcTicks > 0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanAndDiff_ScansDirectoryAndClassifiesFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"native_scan_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string f1 = Path.Combine(tempDir, "doc1.txt");
            string f2 = Path.Combine(tempDir, "temp.tmp"); // Should be excluded by filter
            File.WriteAllText(f1, "Documentation text");
            File.WriteAllText(f2, "Temporary text");

            var req = new RustScanAndDiffRequestDto
            {
                Sources = new List<RustSyncSourceDto>
                {
                    new() { LocalFolderPath = tempDir, DestinationPrefix = "Work" }
                },
                Filters = new RustFilterOptionsDto
                {
                    ExcludedExtensions = new List<string> { ".tmp" },
                    ExcludedFolders = new List<string> { ".git" },
                    MaxFileSizeBytes = 100 * 1024 * 1024
                },
                OnlyModifiedOrNew = true,
                ForceFullSync = false
            };

            var res = NativeSyncEngine.ScanAndDiff(req);
            Assert.NotNull(res);
            Assert.True(res.Success);
            Assert.Single(res.ScannedFiles);
            Assert.Equal("doc1.txt", res.ScannedFiles[0].FileName);
            Assert.Equal("NeedsUpload", res.ScannedFiles[0].Classification);
            Assert.True(res.ScannedFiles[0].IsNew);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
