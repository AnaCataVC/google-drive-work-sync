using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;

namespace GoogleDriveWorkSync.Services;

/// <summary>
/// Implementation of Google Drive synchronization service using Google Apps Script Web App bridge.
/// Supports batching up to 8 files / 9 MB, SHA-256 caching with fast-path metadata, retry with backoff,
/// read-only preview of out-of-sync files, and shared hash index for work files and Claude context.
/// </summary>
public class DriveSyncService : IDriveSyncService, IDisposable
{
    private const string SettingsKey = "DriveSyncSettings";
    public static readonly string DefaultDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoogleDriveWorkSync",
        "Data");

    /// <summary>
    /// Directory holding the hash index and error log. Settable so tests never touch the user's real index.
    /// </summary>
    public static string DataDirectory { get; set; } = DefaultDataDirectory;
    private static string HashIndexFile => Path.Combine(DataDirectory, "sync_hashes.json");
    private static string ErrorsFile => Path.Combine(DataDirectory, "sync_errors.json");

    private const string UnreadableDirectoryCategory = "Carpeta sin acceso";
    private const int MaxBatchFileCount = 8;
    private const long MaxBatchRawBytes = 9L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _uploadSemaphore = new(1, 1);
    private readonly object _hashLock = new();
    private readonly object _errorLock = new();
    private readonly List<SyncErrorItem> _lastSyncErrors = new();
    private Dictionary<string, HashCacheEntry> _hashIndex = new(StringComparer.OrdinalIgnoreCase);

    private DriveSyncSettings _settings;
    private CancellationTokenSource? _activeCts;
    private readonly object _ctsLock = new();
    private int _isSyncing; // 0 = idle, 1 = syncing

    public DriveSyncSettings Settings => _settings;
    public bool IsSyncing => Volatile.Read(ref _isSyncing) == 1;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.WebAppUrl);

    public IReadOnlyList<SyncErrorItem> LastSyncErrors
    {
        get
        {
            lock (_errorLock)
            {
                return _lastSyncErrors.ToList().AsReadOnly();
            }
        }
    }

    public event EventHandler? SettingsChanged;
    public event EventHandler<SyncProgressReport>? SyncProgressChanged;
    public event EventHandler<SyncResultSummary>? SyncCompleted;
    public event EventHandler<IReadOnlyList<SyncErrorItem>>? SyncErrorsChanged;

    public DriveSyncService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _settings = LoadSettings();
        LoadHashIndex();
        LoadSyncErrors();
    }

    public void UpdateSettings(DriveSyncSettings settings)
    {
        // Ordinal: Apps Script deployment IDs are case-sensitive, so a case change is a different target.
        if (!string.Equals(_settings.WebAppUrl, settings.WebAppUrl, StringComparison.Ordinal))
        {
            ClearHashIndex();
        }

        _settings = settings;
        SaveSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHashIndex()
    {
        lock (_hashLock)
        {
            _hashIndex.Clear();
            SaveHashIndex();
        }
    }

    public void ClearSyncErrors()
    {
        lock (_errorLock)
        {
            _lastSyncErrors.Clear();
            SaveSyncErrors();
        }
        SyncErrorsChanged?.Invoke(this, LastSyncErrors);
    }

    public void CancelSync()
    {
        lock (_ctsLock)
        {
            try
            {
                if (_activeCts != null && !_activeCts.IsCancellationRequested)
                {
                    _activeCts.Cancel();
                }
            }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task<SyncResultSummary> RunSyncAsync(
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceFullSync = false,
        SyncSource? onlySource = null)
    {
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) != 0)
        {
            return new SyncResultSummary
            {
                Message = "Ya hay una sincronización en curso."
            };
        }

        if (!IsConfigured || !EnumerateSources().Any())
        {
            Interlocked.Exchange(ref _isSyncing, 0);
            return new SyncResultSummary
            {
                Message = "Configuración incompleta: Verifica la URL del Web App y las carpetas locales."
            };
        }

        CancellationToken token;
        lock (_ctsLock)
        {
            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _activeCts.Token;
        }

        var summary = new SyncResultSummary();
        var currentRunErrors = new List<SyncErrorItem>();

        try
        {
            ReportProgress(progress, new SyncProgressReport
            {
                StatusMessage = "Escaneando archivos locales..."
            });

            var (localFiles, unreadableDirectories) = await CollectFilesAsync(progress, token, onlySource);
            summary.TotalScanned = localFiles.Count;

            foreach (var directory in unreadableDirectories)
            {
                RecordError(summary, currentRunErrors, new SyncErrorItem
                {
                    FileName = Path.GetFileName(directory),
                    FilePath = directory,
                    ErrorCategory = UnreadableDirectoryCategory,
                    ErrorMessage = "No se pudo leer la carpeta; su contenido no se sincronizó.",
                    Timestamp = DateTime.Now
                });
            }

            int processed = 0;
            var pendingUploads = new List<UploadCandidate>();

            foreach (var file in localFiles)
            {
                if (token.IsCancellationRequested)
                {
                    summary.Message = "Sincronización cancelada por el usuario.";
                    break;
                }

                processed++;

                var classification = ClassifyFile(file, forceFullSync);

                if (classification.Outcome == FileClassificationOutcome.MetadataStale)
                {
                    SetKnownHash(file.HashKey, classification.Hash!, classification.StatFileInfo);
                }

                if (classification.Outcome == FileClassificationOutcome.Unreadable)
                {
                    var (category, friendlyMsg) = CategorizeError(classification.ReadError!, file.FilePath);
                    RecordError(summary, currentRunErrors, new SyncErrorItem
                    {
                        FileName = file.FileName,
                        FilePath = file.FilePath,
                        RelativePath = file.RelativePath,
                        HashKey = file.HashKey,
                        ErrorCategory = category,
                        ErrorMessage = friendlyMsg,
                        Timestamp = DateTime.Now
                    });
                    continue;
                }

                if (classification.Outcome != FileClassificationOutcome.NeedsUpload)
                {
                    summary.Skipped++;
                    ReportProgress(progress, new SyncProgressReport
                    {
                        TotalFiles = summary.TotalScanned,
                        ProcessedFiles = processed,
                        CurrentFileName = file.FileName,
                        UploadedCount = summary.Uploaded,
                        SkippedCount = summary.Skipped,
                        ErrorCount = summary.Errors,
                        StatusMessage = $"Sin cambios: {file.FileName}"
                    });
                    continue;
                }

                file.Hash = classification.Hash!;
                pendingUploads.Add(new UploadCandidate(
                    file.FilePath, file.FileName, file.RelativePath, file.HashKey, file.Hash, classification.StatFileInfo!));
            }

            PersistHashIndex();

            if (!token.IsCancellationRequested)
            {
                var batches = BuildBatches(pendingUploads);
                foreach (var batch in batches)
                {
                    if (token.IsCancellationRequested)
                    {
                        summary.Message = "Sincronización cancelada por el usuario.";
                        break;
                    }

                    ReportProgress(progress, new SyncProgressReport
                    {
                        TotalFiles = summary.TotalScanned,
                        ProcessedFiles = processed,
                        CurrentFileName = batch.Count == 1 ? batch[0].FileName : $"{batch.Count} archivos",
                        UploadedCount = summary.Uploaded,
                        SkippedCount = summary.Skipped,
                        ErrorCount = summary.Errors,
                        StatusMessage = batch.Count == 1
                            ? $"Subiendo: {batch[0].FileName}..."
                            : $"Subiendo lote de {batch.Count} archivos..."
                    });

                    await ProcessBatchAsync(batch, summary, currentRunErrors, token);

                    ReportProgress(progress, new SyncProgressReport
                    {
                        TotalFiles = summary.TotalScanned,
                        ProcessedFiles = processed,
                        CurrentFileName = batch[^1].FileName,
                        UploadedCount = summary.Uploaded,
                        SkippedCount = summary.Skipped,
                        ErrorCount = summary.Errors,
                        StatusMessage = $"Progreso: {summary.Uploaded} subidos, {summary.Errors} errores."
                    });
                }
            }

            lock (_errorLock)
            {
                _lastSyncErrors.Clear();
                _lastSyncErrors.AddRange(currentRunErrors);
                SaveSyncErrors();
            }
            SyncErrorsChanged?.Invoke(this, LastSyncErrors);

            if (!token.IsCancellationRequested)
            {
                PurgeOrphanHashes();

                summary.Message = summary.Success
                    ? $"Sincronización completada: {summary.Uploaded} subidos, {summary.Skipped} sin cambios."
                    : $"Sincronización completada con {summary.Errors} errores ({summary.Uploaded} subidos, {summary.Skipped} sin cambios).";

                _settings.LastSyncTime = DateTime.Now;
                _settings.LastSyncStatus = summary.Success
                    ? $"Al día ({DateTime.Now:HH:mm})"
                    : $"Completado con {summary.Errors} errores ({DateTime.Now:HH:mm})";
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("DriveSyncService_RunSyncAsync", ex, "Error durante la sincronización");
            summary.Message = $"Error durante la sincronización: {ex.Message}";
        }
        finally
        {
            lock (_ctsLock)
            {
                _activeCts?.Dispose();
                _activeCts = null;
            }

            Interlocked.Exchange(ref _isSyncing, 0);
            SyncCompleted?.Invoke(this, summary);
        }

        return summary;
    }

    public async Task<SyncResultSummary> RetryFailedFilesAsync(
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) != 0)
        {
            return new SyncResultSummary
            {
                Message = "Ya hay una sincronización en curso."
            };
        }

        if (!IsConfigured)
        {
            Interlocked.Exchange(ref _isSyncing, 0);
            return new SyncResultSummary
            {
                Message = "Configuración incompleta: Verifica la URL del Web App."
            };
        }

        List<SyncErrorItem> filesToRetry;
        lock (_errorLock)
        {
            filesToRetry = _lastSyncErrors.ToList();
        }

        if (filesToRetry.Count == 0)
        {
            Interlocked.Exchange(ref _isSyncing, 0);
            return new SyncResultSummary
            {
                Message = "No hay archivos con error pendientes de reintentar."
            };
        }

        CancellationToken token;
        lock (_ctsLock)
        {
            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _activeCts.Token;
        }

        var summary = new SyncResultSummary
        {
            TotalScanned = filesToRetry.Count
        };

        var remainingErrors = new List<SyncErrorItem>();
        int processed = 0;

        try
        {
            var pendingRetries = new List<UploadCandidate>();
            var filters = CreateFilters();
            var activePrefixes = new HashSet<string>(
                EnumerateSources().Select(s => s.EffectiveDestinationPrefix), StringComparer.OrdinalIgnoreCase);

            foreach (var item in filesToRetry)
            {
                processed++;

                // A directory cannot be retried on its own; the next full sync re-scans it.
                if (item.ErrorCategory == UnreadableDirectoryCategory)
                {
                    remainingErrors.Add(item);
                    continue;
                }

                // Drop errors whose source was removed or whose file is now filtered out:
                // a regular sync would no longer upload them either.
                var pipeIndex = item.HashKey.IndexOf('|');
                if (pipeIndex < 0 || !activePrefixes.Contains(item.HashKey[..pipeIndex]) ||
                    (File.Exists(item.FilePath) && !filters.ShouldIncludeFile(new FileInfo(item.FilePath), out _)))
                {
                    continue;
                }

                if (!File.Exists(item.FilePath))
                {
                    summary.Errors++;
                    var (category, friendlyMsg) = CategorizeError(
                        new FileNotFoundException("El archivo local ya no existe.", item.FilePath), item.FilePath);
                    item.ErrorMessage = friendlyMsg;
                    item.ErrorCategory = category;
                    item.Timestamp = DateTime.Now;
                    remainingErrors.Add(item);
                    summary.FailedFiles.Add(item);
                    continue;
                }

                // Snapshot metadata before hashing, and hash the current bytes rather than reusing
                // the failed run's hash, so the index describes what is actually uploaded now.
                var info = SnapshotFileInfo(item.FilePath);
                string currentHash;
                try
                {
                    currentHash = ComputeSha256(item.FilePath);
                }
                catch (Exception ex)
                {
                    summary.Errors++;
                    var (category, friendlyMsg) = CategorizeError(ex, item.FilePath);
                    item.ErrorMessage = friendlyMsg;
                    item.ErrorCategory = category;
                    item.Timestamp = DateTime.Now;
                    remainingErrors.Add(item);
                    summary.FailedFiles.Add(item);
                    continue;
                }

                pendingRetries.Add(new UploadCandidate(
                    item.FilePath, item.FileName, item.RelativePath, item.HashKey, currentHash, info));
            }

            var batches = BuildBatches(pendingRetries);
            for (int bi = 0; bi < batches.Count; bi++)
            {
                if (token.IsCancellationRequested)
                {
                    summary.Message = "Reintento cancelado por el usuario.";
                    for (int rem = bi; rem < batches.Count; rem++)
                    {
                        foreach (var candidate in batches[rem])
                        {
                            remainingErrors.Add(BuildErrorItem(candidate, "Cancelado", "Reintento cancelado por el usuario."));
                        }
                    }
                    break;
                }

                var batch = batches[bi];

                ReportProgress(progress, new SyncProgressReport
                {
                    TotalFiles = summary.TotalScanned,
                    ProcessedFiles = processed,
                    CurrentFileName = batch.Count == 1 ? batch[0].FileName : $"{batch.Count} archivos",
                    UploadedCount = summary.Uploaded,
                    SkippedCount = summary.Skipped,
                    ErrorCount = summary.Errors,
                    StatusMessage = batch.Count == 1
                        ? $"Reintentando: {batch[0].FileName}..."
                        : $"Reintentando lote de {batch.Count} archivos..."
                });

                await ProcessBatchAsync(batch, summary, remainingErrors, token);
            }

            lock (_errorLock)
            {
                _lastSyncErrors.Clear();
                _lastSyncErrors.AddRange(remainingErrors);
                SaveSyncErrors();
            }
            SyncErrorsChanged?.Invoke(this, LastSyncErrors);

            summary.FailedFiles = remainingErrors;
            if (!token.IsCancellationRequested)
            {
                summary.Message = summary.Success
                    ? $"Reintento exitoso: Todos los {summary.Uploaded} archivos se subieron correctamente."
                    : $"Reintento completado: {summary.Uploaded} subidos, {summary.Errors} aún con error.";

                _settings.LastSyncTime = DateTime.Now;
                _settings.LastSyncStatus = summary.Success
                    ? $"Al día ({DateTime.Now:HH:mm})"
                    : $"Completado con {summary.Errors} errores ({DateTime.Now:HH:mm})";
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            summary.Message = $"Error durante el reintento: {ex.Message}";
        }
        finally
        {
            lock (_ctsLock)
            {
                _activeCts?.Dispose();
                _activeCts = null;
            }

            Interlocked.Exchange(ref _isSyncing, 0);
            SyncCompleted?.Invoke(this, summary);
        }

        return summary;
    }

    public async Task<IReadOnlyList<OutOfSyncFile>> PreviewOutOfSyncAsync(
        CancellationToken cancellationToken = default,
        SyncSource? onlySource = null)
    {
        if (!IsConfigured)
        {
            return Array.Empty<OutOfSyncFile>();
        }

        var (localFiles, _) = await CollectFilesAsync(progress: null, cancellationToken, onlySource);
        var result = new List<OutOfSyncFile>();

        foreach (var file in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var classification = ClassifyFile(file, forceFullSync: false);
            if (classification.Outcome is not (FileClassificationOutcome.NeedsUpload or FileClassificationOutcome.Unreadable))
                continue;

            result.Add(new OutOfSyncFile
            {
                FileName = file.FileName,
                FilePath = file.FilePath,
                RelativePath = file.RelativePath,
                FileSize = file.FileSize,
                Reason = classification.Outcome == FileClassificationOutcome.Unreadable
                    ? "Sin acceso"
                    : classification.IsNew ? "Nuevo" : "Modificado"
            });
        }

        return result;
    }

    public async Task<string?> TestConnectionAsync(string webAppUrl, string? authToken = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webAppUrl))
            throw new ArgumentException("La URL del Web App no puede estar vacía.");

        string tempFile = Path.Combine(Path.GetTempPath(), "test_drive_sync.txt");
        try
        {
            await File.WriteAllTextAsync(tempFile, $"Google Drive Work Sync - Prueba de conexión realizada el {DateTime.Now}", cancellationToken);
            bool success = await UploadSingleFileInternalAsync(tempFile, "_healthcheck/connection-test.txt", "text/plain", webAppUrl, authToken, cancellationToken);
            return success ? "OK" : null;
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    public async Task<bool> UploadSingleFileAsync(
        string localFilePath,
        string destinationRelativePath,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return false;
        return await UploadSingleFileInternalAsync(localFilePath, destinationRelativePath, mimeType, _settings.WebAppUrl, null, cancellationToken);
    }

    private async Task<bool> UploadSingleFileInternalAsync(
        string localFilePath,
        string destinationRelativePath,
        string mimeType,
        string webAppUrl,
        string? overrideAuthToken,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(localFilePath);
        string hash = ComputeSha256(localFilePath);
        string fileName = Path.GetFileName(localFilePath);

        var candidate = new UploadCandidate(localFilePath, fileName, destinationRelativePath, destinationRelativePath, hash, fileInfo);
        var results = await UploadBatchAsync(new List<UploadCandidate> { candidate }, webAppUrl, cancellationToken, mimeType, overrideAuthToken);
        return results.Count > 0 && results[0].Success;
    }

    public CandidateSyncStatus EvaluateFileStatus(string filePath, string destinationKey)
    {
        if (!File.Exists(filePath)) return CandidateSyncStatus.New;

        var fileInfo = new FileInfo(filePath);
        string? cachedHash = GetKnownHash(destinationKey);

        if (cachedHash != null && IsMetadataConfirmed(destinationKey, filePath, fileInfo.Length))
        {
            return CandidateSyncStatus.UpToDate;
        }

        if (cachedHash == null)
        {
            return CandidateSyncStatus.New;
        }

        string currentHash = ComputeSha256(filePath);
        return string.Equals(cachedHash, currentHash, StringComparison.OrdinalIgnoreCase)
            ? CandidateSyncStatus.UpToDate
            : CandidateSyncStatus.Modified;
    }

    public sealed record UploadCandidate(string FilePath, string FileName, string RelativePath, string HashKey, string Hash, FileInfo Info);
    private sealed record BatchUploadResult(bool Success, string? FileId, string? ErrorMessage);

    public static List<List<UploadCandidate>> BuildBatches(List<UploadCandidate> candidates)
    {
        var batches = new List<List<UploadCandidate>>();
        var current = new List<UploadCandidate>();
        long currentBytes = 0;

        foreach (var candidate in candidates)
        {
            if (current.Count > 0 &&
                (current.Count >= MaxBatchFileCount || currentBytes + candidate.Info.Length > MaxBatchRawBytes))
            {
                batches.Add(current);
                current = new List<UploadCandidate>();
                currentBytes = 0;
            }

            current.Add(candidate);
            currentBytes += candidate.Info.Length;
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }

    private static SyncErrorItem BuildErrorItem(UploadCandidate candidate, string category, string message) => new()
    {
        FileName = candidate.FileName,
        FilePath = candidate.FilePath,
        RelativePath = candidate.RelativePath,
        HashKey = candidate.HashKey,
        Hash = candidate.Hash,
        ErrorMessage = message,
        ErrorCategory = category,
        Timestamp = DateTime.Now
    };

    private static void RecordError(SyncResultSummary summary, List<SyncErrorItem> errorSink, SyncErrorItem errorItem)
    {
        summary.Errors++;
        errorSink.Add(errorItem);
        summary.FailedFiles.Add(errorItem);
    }

    /// <summary>
    /// Returns a FileInfo whose length and timestamps are read now. FileInfo otherwise caches them
    /// lazily on first access, which could record metadata from after the hash was computed.
    /// </summary>
    private static FileInfo SnapshotFileInfo(string filePath)
    {
        var info = new FileInfo(filePath);
        info.Refresh();
        return info;
    }

    private SyncFilterOptions CreateFilters() => SyncFilterOptions.Create(
        _settings.IncludedExtensions,
        _settings.ExcludedExtensions,
        _settings.ExcludedFolders,
        _settings.MaxFileSizeMb);

    private async Task ProcessBatchAsync(
        List<UploadCandidate> batch,
        SyncResultSummary summary,
        List<SyncErrorItem> errorSink,
        CancellationToken token)
    {
        try
        {
            var results = await UploadBatchAsync(batch, _settings.WebAppUrl, token);
            for (int i = 0; i < batch.Count; i++)
            {
                var candidate = batch[i];
                var result = results[i];

                if (result.Success)
                {
                    SetKnownHash(candidate.HashKey, candidate.Hash, candidate.Info);
                    summary.Uploaded++;
                }
                else
                {
                    summary.Errors++;
                    var (category, friendlyMsg) = CategorizeError(new Exception(result.ErrorMessage ?? "Error desconocido"), candidate.FilePath);
                    var errorItem = BuildErrorItem(candidate, category, friendlyMsg);
                    errorSink.Add(errorItem);
                    summary.FailedFiles.Add(errorItem);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Not a failure: a regular sync re-detects these files, and a retry keeps them pending.
            foreach (var candidate in batch)
            {
                errorSink.Add(BuildErrorItem(candidate, "Cancelado", "Subida cancelada por el usuario."));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("DriveSyncService_ProcessBatchAsync", ex, $"Error al procesar lote de {batch.Count} archivos");
            var (category, friendlyMsg) = CategorizeError(ex, batch.Count == 1 ? batch[0].FilePath : $"{batch.Count} archivos en lote");
            foreach (var candidate in batch)
            {
                summary.Errors++;
                var errorItem = BuildErrorItem(candidate, category, friendlyMsg);
                errorSink.Add(errorItem);
                summary.FailedFiles.Add(errorItem);
            }
        }
        finally
        {
            PersistHashIndex();
        }
    }

    private async Task<List<BatchUploadResult>> UploadBatchAsync(
        List<UploadCandidate> batch,
        string webAppUrl,
        CancellationToken token,
        string? overrideMimeType = null,
        string? overrideAuthToken = null)
    {
        await _uploadSemaphore.WaitAsync(token);
        try
        {
            var fileEntries = new List<object>(batch.Count);
            foreach (var candidate in batch)
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(candidate.FilePath, token);
                string fileName = ResolveUploadName(candidate.FilePath, candidate.RelativePath);

                fileEntries.Add(new
                {
                    filename = fileName,
                    relativePath = candidate.RelativePath,
                    mimeType = overrideMimeType ?? GetMimeType(fileName),
                    data = Convert.ToBase64String(fileBytes)
                });
            }

            var payload = new Dictionary<string, object?> { ["files"] = fileEntries };
            string? tokenToUse = !string.IsNullOrWhiteSpace(overrideAuthToken)
                ? overrideAuthToken.Trim()
                : _settings.AuthToken;

            if (!string.IsNullOrWhiteSpace(tokenToUse))
            {
                payload["authToken"] = tokenToUse.Trim();
            }

            string requestJson = JsonSerializer.Serialize(payload);

            int maxRetries = 2;
            int delayMs = 1500;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(webAppUrl, content, token);

                    if ((response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                         response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                         response.StatusCode == System.Net.HttpStatusCode.InternalServerError) && attempt < maxRetries)
                    {
                        await Task.Delay(delayMs + Random.Shared.Next(100, 500), token);
                        delayMs *= 2;
                        continue;
                    }

                    response.EnsureSuccessStatusCode();

                    var responseString = await response.Content.ReadAsStringAsync(token);

                    JsonElement result;
                    try
                    {
                        result = JsonSerializer.Deserialize<JsonElement>(responseString);
                    }
                    catch (JsonException)
                    {
                        if (attempt < maxRetries)
                        {
                            await Task.Delay(delayMs + Random.Shared.Next(100, 500), token);
                            delayMs *= 2;
                            continue;
                        }

                        string rawPreview = responseString.Length > 200 ? responseString[..200] + "..." : responseString;
                        throw new Exception($"Respuesta inválida de Apps Script (no es JSON):\n{rawPreview}");
                    }

                    if (result.TryGetProperty("status", out var status) && status.GetString() == "error")
                    {
                        string msg = result.TryGetProperty("message", out var m) ? m.GetString() ?? "Error desconocido" : "Error desconocido";

                        if ((msg.Contains("Service invoked too many times", StringComparison.OrdinalIgnoreCase) ||
                             msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) && attempt < maxRetries)
                        {
                            await Task.Delay(delayMs + Random.Shared.Next(100, 500), token);
                            delayMs *= 2;
                            continue;
                        }

                        throw new Exception($"Apps Script Error: {msg}");
                    }

                    var perFileResults = new List<BatchUploadResult>(batch.Count);
                    if (result.TryGetProperty("results", out var resultsArray) && resultsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in resultsArray.EnumerateArray())
                        {
                            bool itemSuccess = item.TryGetProperty("status", out var itemStatus) && itemStatus.GetString() == "success";
                            string? fileId = item.TryGetProperty("fileId", out var fid) ? fid.GetString() : null;
                            string? errMsg = item.TryGetProperty("message", out var im) ? im.GetString() : null;
                            perFileResults.Add(new BatchUploadResult(itemSuccess, fileId, errMsg));
                        }
                    }

                    if (perFileResults.Count != batch.Count)
                    {
                        throw new Exception("Apps Script devolvió una cantidad de resultados distinta a la cantidad de archivos enviados.");
                    }

                    return perFileResults;
                }
                catch (Exception ex) when (attempt < maxRetries && !token.IsCancellationRequested &&
                                           (ex is TaskCanceledException || ex is HttpRequestException))
                {
                    await Task.Delay(delayMs + Random.Shared.Next(100, 500), token);
                    delayMs *= 2;
                }
            }

            throw new Exception("Se agotaron los intentos de subida.");
        }
        finally
        {
            _uploadSemaphore.Release();
        }
    }

    public static (string Category, string FriendlyMessage) CategorizeError(Exception ex, string filePath)
    {
        if (ex is IOException ioEx && (ioEx.HResult == unchecked((int)0x80070020) || ioEx.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)))
        {
            return ("Archivo en uso / Bloqueado", "El archivo está abierto en otra aplicación o bloqueado por Windows.");
        }

        if (ex is UnauthorizedAccessException)
        {
            return ("Permiso denegado", "Sin permisos de lectura para acceder a este archivo local.");
        }

        if (ex is TaskCanceledException || ex is TimeoutException)
        {
            return ("Tiempo de espera agotado", "La subida superó el límite de tiempo de Google Apps Script.");
        }

        var message = ex.Message;
        if (message.Contains("429") || message.Contains("Service invoked too many times", StringComparison.OrdinalIgnoreCase) || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return ("Límite de Google Apps Script", "Google Apps Script ha superado la cuota de peticiones por minuto o día.");
        }

        if (message.Contains("503") || message.Contains("500") || message.Contains("504") || message.Contains("Respuesta inválida de Apps Script", StringComparison.OrdinalIgnoreCase))
        {
            return ("Error de servidor en Google", "El servidor de Google Apps Script falló o devolvió una respuesta no válida.");
        }

        if (message.Contains("Payload too large", StringComparison.OrdinalIgnoreCase) || message.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase))
        {
            return ("Archivo demasiado grande", "El archivo excede el tamaño máximo permitido para subir vía Base64.");
        }

        return ("Error de subida", message);
    }

    public static string ResolveUploadName(string filePath, string normalizedRelativePath)
    {
        var lastSegment = normalizedRelativePath.Split('/')[^1];
        return string.IsNullOrWhiteSpace(lastSegment) ? Path.GetFileName(filePath) : lastSegment;
    }

    /// <summary>
    /// Scans a folder breadth-first. Directory links (junctions, symlinks) are not followed, since they
    /// can form cycles. Directories that cannot be read are added to <paramref name="unreadableDirectories"/>
    /// so the caller can report them instead of treating their contents as synced.
    /// </summary>
    public List<LocalFileMetadata> ScanFolder(
        string rootFolderPath,
        SyncFilterOptions? filters = null,
        List<string>? unreadableDirectories = null)
    {
        var results = new List<LocalFileMetadata>();
        if (string.IsNullOrWhiteSpace(rootFolderPath) || !Directory.Exists(rootFolderPath))
            return results;

        filters ??= new SyncFilterOptions();
        var directoriesQueue = new Queue<string>();
        directoriesQueue.Enqueue(rootFolderPath);

        while (directoriesQueue.Count > 0)
        {
            var currentDir = directoriesQueue.Dequeue();

            try
            {
                var subDirs = Directory.GetDirectories(currentDir);
                foreach (var subDir in subDirs)
                {
                    var dirInfo = new DirectoryInfo(subDir);
                    if (!filters.IsFolderExcluded(dirInfo.Name) &&
                        !dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        directoriesQueue.Enqueue(subDir);
                    }
                }
            }
            catch (Exception)
            {
                unreadableDirectories?.Add(currentDir);
                continue;
            }

            try
            {
                var files = Directory.GetFiles(currentDir);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (filters.ShouldIncludeFile(fileInfo, out _))
                        {
                            results.Add(new LocalFileMetadata
                            {
                                FilePath = file,
                                FileName = fileInfo.Name,
                                RelativePath = Path.GetRelativePath(rootFolderPath, file),
                                FileSize = fileInfo.Length,
                                Hash = string.Empty
                            });
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
                unreadableDirectories?.Add(currentDir);
            }
        }

        return results;
    }

    private IEnumerable<SyncSource> EnumerateSources(SyncSource? onlySource = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _settings.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.LocalFolderPath))
                continue;

            if (onlySource != null &&
                !string.Equals(source.LocalFolderPath, onlySource.LocalFolderPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (seen.Add($"{source.EffectiveDestinationPrefix}|{source.LocalFolderPath}"))
                yield return source;
        }
    }

    private async Task<(List<LocalFileMetadata> Files, List<string> UnreadableDirectories)> CollectFilesAsync(
        IProgress<SyncProgressReport>? progress,
        CancellationToken token,
        SyncSource? onlySource = null)
    {
        return await Task.Run(() =>
        {
            var collected = new List<LocalFileMetadata>();
            var unreadableDirectories = new List<string>();
            var filters = CreateFilters();

            foreach (var source in EnumerateSources(onlySource))
            {
                token.ThrowIfCancellationRequested();

                var prefix = source.EffectiveDestinationPrefix;

                ReportProgress(progress, new SyncProgressReport
                {
                    StatusMessage = $"Escaneando {prefix}..."
                });

                foreach (var file in ScanFolder(source.LocalFolderPath, filters, unreadableDirectories))
                {
                    file.RelativePath = CombineDestination(prefix, file.RelativePath);
                    file.HashKey = $"{prefix}|{file.FilePath}";
                    collected.Add(file);
                }
            }

            return (collected, unreadableDirectories);
        }, token);
    }

    public static string CombineDestination(string? prefix, string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(prefix))
            return normalized;

        return $"{prefix.Replace('\\', '/').Trim('/')}/{normalized}";
    }

    private enum FileClassificationOutcome
    {
        Unchanged,
        MetadataStale,
        NeedsUpload,
        Unreadable
    }

    private readonly record struct FileClassification(
        FileClassificationOutcome Outcome, string? Hash, FileInfo? StatFileInfo, bool IsNew, Exception? ReadError = null);

    private FileClassification ClassifyFile(LocalFileMetadata file, bool forceFullSync)
    {
        // Metadata is captured before hashing: if the file changes in between, the stored timestamp
        // is older than the file's, so the next run re-hashes instead of trusting a stale entry.
        var statFileInfo = SnapshotFileInfo(file.FilePath);

        if (!forceFullSync && _settings.OnlyModifiedOrNew)
        {
            string? cachedHash = GetKnownHash(file.HashKey);
            bool metadataConfirmed = cachedHash != null && IsMetadataConfirmed(file.HashKey, file.FilePath, file.FileSize);

            if (metadataConfirmed)
            {
                return new FileClassification(FileClassificationOutcome.Unchanged, cachedHash, null, IsNew: false);
            }

            string hash;
            try
            {
                hash = ComputeSha256(file.FilePath);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogCrash("DriveSyncService_ClassifyFile", ex, $"No se pudo calcular hash (archivo bloqueado o sin permiso): {file.FilePath}");
                return new FileClassification(FileClassificationOutcome.Unreadable, null, statFileInfo, IsNew: false, ex);
            }

            if (cachedHash != null && string.Equals(cachedHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                return new FileClassification(FileClassificationOutcome.MetadataStale, hash, statFileInfo, IsNew: false);
            }

            return new FileClassification(FileClassificationOutcome.NeedsUpload, hash, statFileInfo, IsNew: cachedHash == null);
        }
        else
        {
            string hash;
            try
            {
                hash = ComputeSha256(file.FilePath);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogCrash("DriveSyncService_ClassifyFile", ex, $"No se pudo calcular hash (archivo bloqueado o sin permiso): {file.FilePath}");
                return new FileClassification(FileClassificationOutcome.Unreadable, null, statFileInfo, IsNew: false, ex);
            }
            return new FileClassification(FileClassificationOutcome.NeedsUpload, hash, statFileInfo, IsNew: GetKnownHash(file.HashKey) == null);
        }
    }

    public string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hashBytes = sha256.ComputeHash(stream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private void ReportProgress(IProgress<SyncProgressReport>? progress, SyncProgressReport report)
    {
        progress?.Report(report);
        SyncProgressChanged?.Invoke(this, report);
    }

    private string? GetKnownHash(string hashKey)
    {
        lock (_hashLock)
        {
            return _hashIndex.TryGetValue(hashKey, out var entry) ? entry.Hash : null;
        }
    }

    private bool IsMetadataConfirmed(string hashKey, string filePath, long scannedFileSize)
    {
        lock (_hashLock)
        {
            if (!_hashIndex.TryGetValue(hashKey, out var entry))
                return false;

            // Skip entries that were saved without metadata (legacy hash-only format).
            if (entry.LastWriteTimeUtcTicks == 0L)
                return false;

            try
            {
                var fi = new FileInfo(filePath);
                return fi.Exists &&
                       fi.LastWriteTimeUtc.Ticks == entry.LastWriteTimeUtcTicks &&
                       fi.Length == entry.FileSize &&
                       scannedFileSize == entry.FileSize;
            }
            catch
            {
                return false;
            }
        }
    }

    public void SaveKnownHash(string hashKey, string hash, FileInfo? fileInfo = null)
    {
        SetKnownHash(hashKey, hash, fileInfo);
        PersistHashIndex();
    }

    /// <summary>
    /// Updates the in-memory index only; bulk callers persist once per batch with <see cref="PersistHashIndex"/>.
    /// </summary>
    private void SetKnownHash(string hashKey, string hash, FileInfo? fileInfo)
    {
        lock (_hashLock)
        {
            _hashIndex[hashKey] = new HashCacheEntry
            {
                Hash = hash,
                LastWriteTimeUtcTicks = fileInfo != null ? fileInfo.LastWriteTimeUtc.Ticks : 0L,
                FileSize = fileInfo?.Length ?? 0L
            };
        }
    }

    private void PersistHashIndex()
    {
        lock (_hashLock)
        {
            SaveHashIndex();
        }
    }

    private static string? TryReadFileText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveJsonFile<T>(string path, T value)
    {
        // Write-then-replace: a crash mid-write must not leave a truncated index, which would
        // load as empty and silently trigger a full re-upload.
        string tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("DriveSyncService_SaveJsonFile", ex, $"No se pudo guardar {path}");
        }
    }

    private void LoadHashIndex()
    {
        lock (_hashLock)
        {
            var newIndex = new Dictionary<string, HashCacheEntry>(StringComparer.OrdinalIgnoreCase);
            var json = TryReadFileText(HashIndexFile);

            if (json != null)
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);

                    if (doc.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                            {
                                newIndex[prop.Name] = new HashCacheEntry
                                {
                                    Hash = prop.Value.GetString() ?? string.Empty,
                                    LastWriteTimeUtcTicks = 0L,
                                    FileSize = 0L
                                };
                            }
                            else if (prop.Value.ValueKind == JsonValueKind.Object)
                            {
                                var entry = JsonSerializer.Deserialize<HashCacheEntry>(prop.Value.GetRawText());
                                if (entry != null)
                                    newIndex[prop.Name] = entry;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogCrash("DriveSyncService_LoadHashIndex", ex, "Índice de hashes corrupto; se reinicia vacío");
                    newIndex.Clear();
                }
            }

            _hashIndex = newIndex;
        }
    }

    private void SaveHashIndex()
    {
        SaveJsonFile(HashIndexFile, _hashIndex);
    }

    private void LoadSyncErrors()
    {
        lock (_errorLock)
        {
            var json = TryReadFileText(ErrorsFile);
            if (json == null) return;

            try
            {
                var list = JsonSerializer.Deserialize<List<SyncErrorItem>>(json);
                if (list != null)
                {
                    _lastSyncErrors.Clear();
                    _lastSyncErrors.AddRange(list);
                }
            }
            catch
            {
                _lastSyncErrors.Clear();
            }
        }
    }

    private void SaveSyncErrors()
    {
        SaveJsonFile(ErrorsFile, _lastSyncErrors);
    }

    private static DriveSyncSettings LoadSettings()
    {
        return LocalSettingsHelper.LoadJson<DriveSyncSettings>(SettingsKey);
    }

    private void SaveSettings()
    {
        LocalSettingsHelper.SaveJson(SettingsKey, _settings);
    }

    private static string GetMimeType(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".txt" => "text/plain",
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".zip" => "application/zip",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".csv" => "text/csv",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
    }

    public void PurgeOrphanHashes()
    {
        lock (_hashLock)
        {
            var orphanKeys = new List<string>();
            foreach (var (key, _) in _hashIndex)
            {
                // Only work-file keys ("prefix|localPath") carry a local path. Claude context keys are
                // Drive-relative paths with no local counterpart, so File.Exists would always drop them.
                var pipeIndex = key.IndexOf('|');
                if (pipeIndex < 0)
                    continue;

                if (!File.Exists(key[(pipeIndex + 1)..]))
                {
                    orphanKeys.Add(key);
                }
            }

            foreach (var k in orphanKeys)
            {
                _hashIndex.Remove(k);
            }

            if (orphanKeys.Count > 0)
            {
                SaveHashIndex();
            }
        }
    }

    public void Dispose()
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _httpClient.Dispose();
        _uploadSemaphore.Dispose();
    }
}
