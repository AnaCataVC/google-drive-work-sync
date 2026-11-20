using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;

namespace GoogleDriveWorkSync.Services;

/// <summary>
/// Restore from Drive: list the Web App's root folder, map each file to a local target, and
/// download selected files in batches that respect the same limits as uploads.
/// </summary>
public partial class DriveSyncService
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Sends a local file to the Recycle Bin and returns an error message, or null on success.
    /// Replaceable so tests never touch the real Recycle Bin.
    /// </summary>
    public Func<string, string?> RecycleFile { get; set; } = path =>
        WindowsRecycleBinHelper.SendToRecycleBin(path, out var error) ? null : (error ?? "Error desconocido.");

    private static string OutdatedScriptMessage(string action, string description) =>
        $"El Web App de Google Apps Script es una versión desactualizada que no soporta {description} ('{action}'). " +
        "Actualiza Code.gs en script.google.com con el script de docs/google-setup-guide.md " +
        "y despliega una 'Nueva versión' en Administrar implementaciones.";

    /// <summary>
    /// Posts an action to the Web App and returns the parsed response, or an error message when the
    /// request failed, the script reported an error, or the script did not handle the action.
    /// </summary>
    private async Task<(JsonDocument? Document, string? Error)> PostScriptActionAsync(
        string action,
        string description,
        object? files,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?> { ["action"] = action };
        if (files != null) payload["files"] = files;
        if (!string.IsNullOrWhiteSpace(_settings.AuthToken))
        {
            payload["authToken"] = _settings.AuthToken.Trim();
        }

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(_settings.WebAppUrl, content, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Error de red al contactar el Web App: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"El servidor respondió con código HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return (null, "El Web App devolvió una respuesta que no es JSON. Revisa que la URL y el despliegue sean correctos.");
            }

            var root = doc.RootElement;
            string status = root.TryGetProperty("status", out var sProp) ? sProp.GetString() ?? "" : "";
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                string message = root.TryGetProperty("message", out var mProp) ? mProp.GetString() ?? "Error desconocido en el servidor." : "Error desconocido en el servidor.";
                doc.Dispose();
                return (null, message);
            }

            // An outdated script falls through to the legacy upload handler and answers without this action.
            string returnedAction = root.TryGetProperty("action", out var aProp) ? aProp.GetString() ?? "" : "";
            if (!string.Equals(returnedAction, action, StringComparison.OrdinalIgnoreCase))
            {
                doc.Dispose();
                return (null, OutdatedScriptMessage(action, description));
            }

            return (doc, null);
        }
    }

    public async Task<RemoteListResult> ListRemoteFilesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new RemoteListResult { ErrorMessage = "La URL del Web App de Google Apps Script no está configurada." };
        }

        await _uploadSemaphore.WaitAsync(cancellationToken);
        try
        {
            var (doc, error) = await PostScriptActionAsync("list", "el listado de archivos", files: null, cancellationToken);
            if (doc == null)
            {
                return new RemoteListResult { ErrorMessage = error };
            }

            using (doc)
            {
                var result = new RemoteListResult();
                if (doc.RootElement.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in filesElement.EnumerateArray())
                    {
                        string relativePath = item.TryGetProperty("relativePath", out var rp) ? rp.GetString() ?? "" : "";
                        string fileId = item.TryGetProperty("fileId", out var fid) ? fid.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(fileId))
                            continue;

                        result.Items.Add(new RemoteDriveItem
                        {
                            RelativePath = relativePath.Replace('\\', '/').TrimStart('/'),
                            FileId = fileId,
                            Size = item.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0L,
                            ModifiedTime = item.TryGetProperty("modifiedTime", out var mt) &&
                                           DateTime.TryParse(mt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                                ? parsed
                                : null
                        });
                    }
                }
                return result;
            }
        }
        finally
        {
            _uploadSemaphore.Release();
        }
    }

    /// <summary>
    /// Splits a Drive-relative path into safe file-name segments. Rejects empty, rooted and
    /// <c>..</c>/<c>.</c> segments and characters Windows does not allow in names, since the
    /// path comes from a remote server and must never escape its mapping root.
    /// </summary>
    public static bool TryGetSafeSegments(string? relativePath, out string[] segments, out string? error)
    {
        segments = Array.Empty<string>();
        error = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "La ruta en Drive está vacía.";
            return false;
        }

        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\') || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            error = $"Ruta absoluta no permitida: {relativePath}";
            return false;
        }

        var parts = relativePath.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == "..")
            {
                error = $"Ruta con segmentos no permitidos: {relativePath}";
                return false;
            }

            if (part.IndexOfAny(InvalidFileNameChars) >= 0 || part != part.TrimEnd(' ', '.'))
            {
                error = $"Ruta con caracteres no válidos en Windows: {relativePath}";
                return false;
            }
        }

        segments = parts;
        return true;
    }

    /// <summary>True when <paramref name="path"/> resolves strictly inside <paramref name="root"/>.</summary>
    public static bool IsInsideRoot(string path, string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && fullPath.Length > fullRoot.Length;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<RestorePlanItem> BuildRestorePlan(IEnumerable<RemoteDriveItem> remoteItems, string? alternateRoot = null)
    {
        List<KeyValuePair<string, HashCacheEntry>> snapshot;
        lock (_hashLock)
        {
            snapshot = _hashIndex.ToList();
        }

        var byFileId = new Dictionary<string, KeyValuePair<string, HashCacheEntry>>(StringComparer.Ordinal);
        var byDrivePath = new Dictionary<string, KeyValuePair<string, HashCacheEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in snapshot)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value.FileId))
                byFileId.TryAdd(pair.Value.FileId, pair);
            if (!string.IsNullOrWhiteSpace(pair.Value.DriveRelativePath))
                byDrivePath.TryAdd(pair.Value.DriveRelativePath.Replace('\\', '/').TrimStart('/'), pair);
        }

        var sources = EnumerateSources().ToList();
        bool useAlternate = !string.IsNullOrWhiteSpace(alternateRoot);
        var plan = new List<RestorePlanItem>();

        foreach (var remote in remoteItems)
        {
            var item = new RestorePlanItem { Remote = remote };
            plan.Add(item);

            if (!TryGetSafeSegments(remote.RelativePath, out var segments, out var pathError))
            {
                item.Status = RestoreItemStatus.Unmapped;
                item.PlanError = pathError;
                continue;
            }

            byFileId.TryGetValue(remote.FileId, out var idEntry);

            if (useAlternate)
            {
                item.MappingRoot = alternateRoot;
                item.LocalTargetPath = Path.Combine(new[] { alternateRoot! }.Concat(segments).ToArray());
                item.HashKey = FindSourceHashKey(sources, item.LocalTargetPath);
            }
            else if (TryMapFromIndex(remote, byFileId, byDrivePath, out var indexed))
            {
                item.LocalTargetPath = indexed.Value.LocalFilePath;
                item.MappingRoot = Path.GetDirectoryName(indexed.Value.LocalFilePath);
                item.HashKey = indexed.Key;
            }
            else if (TryMapFromSources(sources, segments, out var source, out var target))
            {
                item.LocalTargetPath = target;
                item.MappingRoot = source.LocalFolderPath;
                item.HashKey = $"{source.EffectiveDestinationPrefix}|{target}";
            }
            else
            {
                item.Status = RestoreItemStatus.Unmapped;
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.MappingRoot) || !IsInsideRoot(item.LocalTargetPath!, item.MappingRoot))
            {
                item.PlanError = $"El destino local queda fuera de su carpeta raíz: {remote.RelativePath}";
                item.Status = RestoreItemStatus.Unmapped;
                item.LocalTargetPath = null;
                continue;
            }

            item.Status = ClassifyLocalTarget(item.LocalTargetPath!, idEntry.Value);
        }

        return plan;
    }

    private static bool TryMapFromIndex(
        RemoteDriveItem remote,
        Dictionary<string, KeyValuePair<string, HashCacheEntry>> byFileId,
        Dictionary<string, KeyValuePair<string, HashCacheEntry>> byDrivePath,
        out KeyValuePair<string, HashCacheEntry> match)
    {
        if (byFileId.TryGetValue(remote.FileId, out match) && !string.IsNullOrWhiteSpace(match.Value.LocalFilePath))
            return true;
        if (byDrivePath.TryGetValue(remote.RelativePath, out match) && !string.IsNullOrWhiteSpace(match.Value.LocalFilePath))
            return true;
        match = default;
        return false;
    }

    /// <summary>Longest destination prefix wins, so nested prefixes map to the most specific source.</summary>
    private static bool TryMapFromSources(List<SyncSource> sources, string[] segments, out SyncSource source, out string target)
    {
        source = null!;
        target = string.Empty;
        int bestLength = 0;

        foreach (var candidate in sources)
        {
            var prefixSegments = candidate.EffectiveDestinationPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (prefixSegments.Length == 0 || prefixSegments.Length >= segments.Length || prefixSegments.Length <= bestLength)
                continue;

            bool matches = true;
            for (int i = 0; i < prefixSegments.Length; i++)
            {
                if (!string.Equals(prefixSegments[i], segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches) continue;

            bestLength = prefixSegments.Length;
            source = candidate;
            target = Path.Combine(new[] { candidate.LocalFolderPath }.Concat(segments.Skip(prefixSegments.Length)).ToArray());
        }

        return bestLength > 0;
    }

    private static string? FindSourceHashKey(List<SyncSource> sources, string targetPath)
    {
        var source = sources.FirstOrDefault(s => IsInsideRoot(targetPath, s.LocalFolderPath));
        return source == null ? null : $"{source.EffectiveDestinationPrefix}|{targetPath}";
    }

    private RestoreItemStatus ClassifyLocalTarget(string targetPath, HashCacheEntry? entryForFileId)
    {
        if (!File.Exists(targetPath))
            return RestoreItemStatus.Missing;

        if (entryForFileId != null && !string.IsNullOrEmpty(entryForFileId.Hash))
        {
            try
            {
                if (string.Equals(ComputeSha256(targetPath), entryForFileId.Hash, StringComparison.OrdinalIgnoreCase))
                    return RestoreItemStatus.Identical;
            }
            catch (Exception ex)
            {
                // Unreadable means unknown, and unknown must never be reported as identical.
                DiagnosticLogger.LogCrash("DriveSyncService_ClassifyLocalTarget", ex, $"No se pudo calcular hash: {targetPath}");
            }
        }

        return RestoreItemStatus.WouldOverwrite;
    }

    /// <summary>
    /// Re-checks the trust boundary at write time: plan items are plain objects a caller can edit.
    /// </summary>
    private static string? ValidateRestoreTarget(RestorePlanItem item)
    {
        if (item.PlanError != null) return item.PlanError;
        if (!TryGetSafeSegments(item.Remote.RelativePath, out _, out var pathError)) return pathError;
        if (string.IsNullOrWhiteSpace(item.LocalTargetPath) || string.IsNullOrWhiteSpace(item.MappingRoot))
            return null;
        if (!IsInsideRoot(item.LocalTargetPath, item.MappingRoot))
            return $"El destino local queda fuera de su carpeta raíz: {item.Remote.RelativePath}";
        return null;
    }

    public static List<List<RestorePlanItem>> BuildDownloadBatches(IEnumerable<RestorePlanItem> items)
    {
        var batches = new List<List<RestorePlanItem>>();
        var current = new List<RestorePlanItem>();
        long currentBytes = 0;

        foreach (var item in items)
        {
            long size = Math.Max(0, item.Remote.Size);
            if (current.Count > 0 &&
                (current.Count >= MaxBatchFileCount || currentBytes + size > MaxBatchRawBytes))
            {
                batches.Add(current);
                current = new List<RestorePlanItem>();
                currentBytes = 0;
            }

            current.Add(item);
            currentBytes += size;
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }

    public async Task<RestoreResultSummary> RestoreFilesAsync(
        IEnumerable<RestorePlanItem> planItems,
        bool overwriteExisting,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = planItems?.ToList() ?? new List<RestorePlanItem>();
        var summary = new RestoreResultSummary { TotalRequested = items.Count };
        if (items.Count == 0) return summary;

        if (!IsConfigured)
        {
            summary.Errors = items.Count;
            summary.ErrorMessages.Add("La URL del Web App de Google Apps Script no está configurada.");
            return summary;
        }

        var toDownload = new List<RestorePlanItem>();
        foreach (var item in items)
        {
            var validationError = ValidateRestoreTarget(item);
            if (validationError != null)
            {
                summary.Errors++;
                summary.ErrorMessages.Add(validationError);
            }
            else if (string.IsNullOrWhiteSpace(item.LocalTargetPath))
            {
                summary.Skipped++;
                summary.SkippedMessages.Add($"{item.Remote.RelativePath}: sin destino local (elige otra carpeta para restaurarlo).");
            }
            else if (item.Status == RestoreItemStatus.Identical)
            {
                summary.Skipped++;
                summary.SkippedMessages.Add($"{item.Remote.RelativePath}: el archivo local ya es idéntico.");
            }
            else if (!overwriteExisting && File.Exists(item.LocalTargetPath))
            {
                summary.Skipped++;
                summary.SkippedMessages.Add($"{item.Remote.RelativePath}: ya existe localmente y no se permitió sobrescribir.");
            }
            else
            {
                toDownload.Add(item);
            }
        }

        if (toDownload.Count == 0) return summary;

        var batches = BuildDownloadBatches(toDownload);
        int processed = 0;

        await _uploadSemaphore.WaitAsync(cancellationToken);
        try
        {
            for (int b = 0; b < batches.Count; b++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    foreach (var pending in batches.Skip(b).SelectMany(x => x))
                    {
                        summary.Skipped++;
                        summary.SkippedMessages.Add($"{pending.Remote.RelativePath}: restauración cancelada por el usuario.");
                    }
                    break;
                }

                var batch = batches[b];
                ReportProgress(progress, new SyncProgressReport
                {
                    TotalFiles = toDownload.Count,
                    ProcessedFiles = processed,
                    UploadedCount = summary.Restored,
                    ErrorCount = summary.Errors,
                    CurrentFileName = Path.GetFileName(batch[0].Remote.RelativePath),
                    StatusMessage = $"Descargando lote {b + 1} de {batches.Count}..."
                });

                try
                {
                    await RestoreBatchAsync(batch, overwriteExisting, summary, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    foreach (var pending in batches.Skip(b).SelectMany(x => x))
                    {
                        summary.Skipped++;
                        summary.SkippedMessages.Add($"{pending.Remote.RelativePath}: restauración cancelada por el usuario.");
                    }
                    break;
                }
                finally
                {
                    PersistHashIndex();
                }

                processed += batch.Count;
            }
        }
        finally
        {
            _uploadSemaphore.Release();
        }

        ReportProgress(progress, new SyncProgressReport
        {
            TotalFiles = toDownload.Count,
            ProcessedFiles = processed,
            UploadedCount = summary.Restored,
            ErrorCount = summary.Errors,
            StatusMessage = "Restauración finalizada."
        });

        return summary;
    }

    private async Task RestoreBatchAsync(
        List<RestorePlanItem> batch,
        bool overwriteExisting,
        RestoreResultSummary summary,
        CancellationToken cancellationToken)
    {
        var requestFiles = batch.Select(i => new { fileId = i.Remote.FileId, relativePath = i.Remote.RelativePath }).ToList();
        var (doc, error) = await PostScriptActionAsync("download", "la descarga de archivos", requestFiles, cancellationToken);
        if (doc == null)
        {
            summary.Errors += batch.Count;
            summary.ErrorMessages.Add(error ?? "Error desconocido en el servidor.");
            return;
        }

        using (doc)
        {
            var resultsByFileId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in resultsElement.EnumerateArray())
                {
                    string id = result.TryGetProperty("fileId", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(id)) resultsByFileId.TryAdd(id, result);
                }
            }

            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!resultsByFileId.TryGetValue(item.Remote.FileId, out var result))
                {
                    summary.Errors++;
                    summary.ErrorMessages.Add($"{item.Remote.RelativePath}: el servidor no devolvió resultado para este archivo.");
                    continue;
                }

                string status = result.TryGetProperty("status", out var sProp) ? sProp.GetString() ?? "" : "";
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    string message = string.Equals(status, "not_found", StringComparison.OrdinalIgnoreCase)
                        ? "ya no existe en Drive o está en la papelera."
                        : (result.TryGetProperty("message", out var mProp) ? mProp.GetString() ?? "error al descargar." : "error al descargar.");
                    summary.Errors++;
                    summary.ErrorMessages.Add($"{item.Remote.RelativePath}: {message}");
                    continue;
                }

                byte[] data;
                try
                {
                    data = Convert.FromBase64String(result.TryGetProperty("data", out var dProp) ? dProp.GetString() ?? "" : "");
                }
                catch (FormatException)
                {
                    summary.Errors++;
                    summary.ErrorMessages.Add($"{item.Remote.RelativePath}: el servidor devolvió datos no válidos.");
                    continue;
                }

                var writeError = WriteRestoredFile(item, data, overwriteExisting);
                if (writeError != null)
                {
                    if (writeError.Skipped)
                    {
                        summary.Skipped++;
                        summary.SkippedMessages.Add($"{item.Remote.RelativePath}: {writeError.Message}");
                    }
                    else
                    {
                        summary.Errors++;
                        summary.ErrorMessages.Add($"{item.Remote.RelativePath}: {writeError.Message}");
                    }
                    continue;
                }

                summary.Restored++;
            }
        }
    }

    private sealed record RestoreWriteIssue(string Message, bool Skipped);

    /// <summary>
    /// Writes to a temp file in the target directory, then moves it into place, so a crash never
    /// leaves a truncated file at the target. An existing file goes to the Recycle Bin first.
    /// </summary>
    private RestoreWriteIssue? WriteRestoredFile(RestorePlanItem item, byte[] data, bool overwriteExisting)
    {
        var target = item.LocalTargetPath!;
        string? tempPath = null;
        try
        {
            var directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);

            tempPath = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.restoretmp");
            File.WriteAllBytes(tempPath, data);

            if (File.Exists(target))
            {
                // Re-checked here: the file may have appeared after the plan was built.
                if (!overwriteExisting)
                    return new RestoreWriteIssue("ya existe localmente y no se permitió sobrescribir.", Skipped: true);

                var recycleError = RecycleFile(target);
                if (recycleError != null)
                    return new RestoreWriteIssue($"no se pudo enviar el archivo actual a la Papelera de reciclaje: {recycleError}", Skipped: false);
            }

            File.Move(tempPath, target, overwrite: false);
            tempPath = null;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("DriveSyncService_WriteRestoredFile", ex, $"No se pudo escribir el archivo restaurado: {target}");
            return new RestoreWriteIssue($"no se pudo escribir el archivo: {ex.Message}", Skipped: false);
        }
        finally
        {
            if (tempPath != null)
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        if (item.HashKey != null)
        {
            try
            {
                var info = SnapshotFileInfo(target);
                SetKnownHash(item.HashKey, ComputeSha256(target), info, item.Remote.FileId, item.Remote.RelativePath, target);
            }
            catch (Exception ex)
            {
                // The file is restored; without an index entry the next sync only re-uploads it.
                DiagnosticLogger.LogCrash("DriveSyncService_WriteRestoredFile", ex, $"No se pudo actualizar el índice tras restaurar: {target}");
            }
        }

        return null;
    }
}
