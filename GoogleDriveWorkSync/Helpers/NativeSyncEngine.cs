using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using GoogleDriveWorkSync.Models;

namespace GoogleDriveWorkSync.Helpers;

#region FFI DTOs

public class RustFilterOptionsDto
{
    [JsonPropertyName("included_extensions")]
    public List<string> IncludedExtensions { get; set; } = new();

    [JsonPropertyName("excluded_extensions")]
    public List<string> ExcludedExtensions { get; set; } = new();

    [JsonPropertyName("excluded_folders")]
    public List<string> ExcludedFolders { get; set; } = new();

    [JsonPropertyName("max_file_size_bytes")]
    public ulong MaxFileSizeBytes { get; set; }
}

public class RustSyncSourceDto
{
    [JsonPropertyName("local_folder_path")]
    public string LocalFolderPath { get; set; } = string.Empty;

    [JsonPropertyName("destination_prefix")]
    public string DestinationPrefix { get; set; } = string.Empty;
}

public class RustKnownHashEntryDto
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("last_write_time_utc_ticks")]
    public ulong LastWriteTimeUtcTicks { get; set; }

    [JsonPropertyName("file_size")]
    public ulong FileSize { get; set; }
}

public class RustScanAndDiffRequestDto
{
    [JsonPropertyName("sources")]
    public List<RustSyncSourceDto> Sources { get; set; } = new();

    [JsonPropertyName("filters")]
    public RustFilterOptionsDto Filters { get; set; } = new();

    [JsonPropertyName("only_modified_or_new")]
    public bool OnlyModifiedOrNew { get; set; }

    [JsonPropertyName("force_full_sync")]
    public bool ForceFullSync { get; set; }

    [JsonPropertyName("known_hashes")]
    public Dictionary<string, RustKnownHashEntryDto> KnownHashes { get; set; } = new();
}

public class RustScannedFileDto
{
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("relative_path")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("hash_key")]
    public string HashKey { get; set; } = string.Empty;

    [JsonPropertyName("file_size")]
    public ulong FileSize { get; set; }

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("classification")]
    public string Classification { get; set; } = string.Empty; // "Unchanged", "MetadataStale", "NeedsUpload", "Unreadable"

    [JsonPropertyName("is_new")]
    public bool IsNew { get; set; }

    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("error_message")]
    public string ErrorMessage { get; set; } = string.Empty;
}

public class RustScanAndDiffResponseDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }

    [JsonPropertyName("scanned_files")]
    public List<RustScannedFileDto> ScannedFiles { get; set; } = new();

    [JsonPropertyName("unreadable_directories")]
    public List<string> UnreadableDirectories { get; set; } = new();
}

public class RustBatchHashRequestDto
{
    [JsonPropertyName("file_paths")]
    public List<string> FilePaths { get; set; } = new();
}

public class RustBatchHashResultItemDto
{
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("file_size")]
    public ulong FileSize { get; set; }

    [JsonPropertyName("last_write_time_utc_ticks")]
    public ulong LastWriteTimeUtcTicks { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; }
}

public class RustBatchHashResponseDto
{
    [JsonPropertyName("results")]
    public List<RustBatchHashResultItemDto> Results { get; set; } = new();

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }
}

#endregion

/// <summary>
/// Managed bridge and P/Invoke interop with the high-performance native Rust sync engine (rust_sync_engine.dll).
/// Implements BLAKE3 SIMD / Rayon multithreaded hashing, parallel tree scanning, and state diffing with a managed fallback.
/// </summary>
public static partial class NativeSyncEngine
{
    private const string NativeLibName = "rust_sync_engine";
    private static readonly Lazy<bool> _isNativeAvailable = new(ProbeNativeEngine);

    public static bool IsNativeAvailable => _isNativeAvailable.Value;

    private static bool ProbeNativeEngine()
    {
        try
        {
            // Probe by attempting to call FreeString on IntPtr.Zero (safe no-op in Rust)
            rust_free_string(IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("NativeSyncEngine_ProbeNativeEngine", ex, "Native Rust engine not loaded; fallback to managed execution");
            return false;
        }
    }

    #region P/Invoke Declarations

    [LibraryImport(NativeLibName, EntryPoint = "rust_compute_blake3_file", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int rust_compute_blake3_file(
        string path,
        byte* outHash32,
        byte* cancelFlag);

    [LibraryImport(NativeLibName, EntryPoint = "rust_compute_blake3_header", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int rust_compute_blake3_header(
        string path,
        nuint maxBytes,
        byte* outHash32,
        nuint* outBytesRead,
        byte* cancelFlag);

    [LibraryImport(NativeLibName, EntryPoint = "rust_scan_and_diff_json", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr rust_scan_and_diff_json(
        string requestJson,
        byte* cancelFlag);

    [LibraryImport(NativeLibName, EntryPoint = "rust_batch_hash_files_json", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr rust_batch_hash_files_json(
        string requestJson,
        byte* cancelFlag);

    [LibraryImport(NativeLibName, EntryPoint = "rust_free_string")]
    private static partial void rust_free_string(IntPtr ptr);

    #endregion

    /// <summary>
    /// Helper box for cancellation flag to avoid CS1686 (taking address of a captured local variable).
    /// </summary>
    private sealed class CancelBox
    {
        public byte Value;
    }

    /// <summary>
    /// Computes BLAKE3 hash for a file. Falls back to SHA-256 if the native engine is unavailable.
    /// </summary>
    public static string ComputeFileHash(string filePath, CancellationToken cancellationToken = default)
    {
        if (IsNativeAvailable)
        {
            Span<byte> hashBytes = stackalloc byte[32];
            var box = new CancelBox();
            using var reg = cancellationToken.Register(() => box.Value = 1);

            int result;
            unsafe
            {
                fixed (byte* pHash = hashBytes)
                fixed (byte* pCancel = &box.Value)
                {
                    result = rust_compute_blake3_file(filePath, pHash, pCancel);
                }
            }

            if (result == 0)
            {
                return Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            if (cancellationToken.IsCancellationRequested || result == (int)System.IO.FileAttributes.ReparsePoint)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new IOException($"Native engine hash failed for '{filePath}' (Error code {result})");
        }

        // Managed fallback (SHA-256)
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes BLAKE3 header hash up to maxBytes. Falls back to managed SHA-256 if unavailable.
    /// </summary>
    public static (string HeaderHash, int BytesRead)? ComputeHeaderHash(string filePath, int maxBytes = 16384, CancellationToken cancellationToken = default)
    {
        if (IsNativeAvailable)
        {
            Span<byte> hashBytes = stackalloc byte[32];
            nuint bytesRead = 0;
            var box = new CancelBox();
            using var reg = cancellationToken.Register(() => box.Value = 1);

            int result;
            unsafe
            {
                fixed (byte* pHash = hashBytes)
                fixed (byte* pCancel = &box.Value)
                {
                    result = rust_compute_blake3_header(filePath, (nuint)maxBytes, pHash, &bytesRead, pCancel);
                }
            }

            if (result == 0)
            {
                return (Convert.ToHexString(hashBytes).ToLowerInvariant(), (int)bytesRead);
            }

            return null;
        }

        // Managed fallback
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buffer = new byte[maxBytes];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(buffer, 0, totalRead);
            return (Convert.ToHexString(hashBytes).ToLowerInvariant(), totalRead);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Executes parallel scanning and diffing using the Rust engine. Returns null if native engine is unavailable.
    /// </summary>
    public static RustScanAndDiffResponseDto? ScanAndDiff(
        RustScanAndDiffRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!IsNativeAvailable)
            return null;

        string requestJson = JsonSerializer.Serialize(request);
        var box = new CancelBox();
        using var reg = cancellationToken.Register(() => box.Value = 1);

        IntPtr responsePtr;
        unsafe
        {
            fixed (byte* pCancel = &box.Value)
            {
                responsePtr = rust_scan_and_diff_json(requestJson, pCancel);
            }
        }

        if (responsePtr == IntPtr.Zero)
            return null;

        try
        {
            string jsonResponse = Marshal.PtrToStringUTF8(responsePtr)!;
            return JsonSerializer.Deserialize<RustScanAndDiffResponseDto>(jsonResponse);
        }
        finally
        {
            rust_free_string(responsePtr);
        }
    }

    /// <summary>
    /// Computes hashes in parallel for a list of file paths using Rayon in Rust.
    /// </summary>
    public static RustBatchHashResponseDto? BatchHashFiles(
        List<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        if (!IsNativeAvailable || filePaths.Count == 0)
            return null;

        var request = new RustBatchHashRequestDto { FilePaths = filePaths };
        string requestJson = JsonSerializer.Serialize(request);
        var box = new CancelBox();
        using var reg = cancellationToken.Register(() => box.Value = 1);

        IntPtr responsePtr;
        unsafe
        {
            fixed (byte* pCancel = &box.Value)
            {
                responsePtr = rust_batch_hash_files_json(requestJson, pCancel);
            }
        }

        if (responsePtr == IntPtr.Zero)
            return null;

        try
        {
            string jsonResponse = Marshal.PtrToStringUTF8(responsePtr)!;
            return JsonSerializer.Deserialize<RustBatchHashResponseDto>(jsonResponse);
        }
        finally
        {
            rust_free_string(responsePtr);
        }
    }
}
