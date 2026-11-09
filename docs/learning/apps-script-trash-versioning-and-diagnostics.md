# Learning: Google Apps Script Web App Versioning, Fall-Through Pitfalls & Wire Protocol Validation

## Context
When performing remote orphan trashing or deleting local duplicates with remote trash enabled in **Google Drive Work Sync**, users encountered the error dialog:
```text
Resultado con advertencias
Se enviaron 0 archivos a la papelera de Drive.
Errores: <relativePath>: Exception: El argumento no puede ser nulo: name
```
The same exception occurred across all selected items, preventing remote files from being moved to Google Drive Trash.

---

## Root Causes Identified

### 1. Google Apps Script Web App Deployment Decoupling
- In Google Apps Script, saving code changes (`Ctrl+S`) in the web editor does **not** update active Web App (`/exec`) deployments.
- Deployed Web Apps remain bound to a static snapshot version until explicitly updated via **Deploy $\rightarrow$ Manage deployments $\rightarrow$ Edit $\rightarrow$ Version: New version $\rightarrow$ Deploy**.
- If a user deployed the Web App before remote trashing support was introduced (v1.4.0), the deployed endpoint continues running the legacy upload-only implementation.

### 2. Silent Fall-Through to Legacy Upload Logic
- The legacy `Code.gs` script lacked an `if (body.action === "trash")` guard and directly iterated `body.files` as an upload batch.
- When the desktop client issued a remote trash request, it transmitted only `{ fileId, relativePath }` without a `filename` field.
- The legacy upload handler attempted to clean-overwrite existing files:
  ```javascript
  var fileName = fileEntry.filename; // undefined in trash payloads
  var existingFiles = currentFolder.getFilesByName(fileName);
  ```
- Passing `undefined` to `Folder.getFilesByName()` throws Google Apps Script's native exception:
  `Exception: The argument cannot be null: name` (or localized `Exception: El argumento no puede ser nulo: name`).
- The script caught this exception and returned HTTP 200 with `{ status: "success", results: [{ status: "error", message: "..." }] }`, obscuring the fact that the entire trash action was unhandled.

### 3. Path Separator Variations
- Windows environments produce paths with backslashes (`\`), whereas Google Drive folder hierarchies require forward slashes (`/`).
- If relative paths containing backslashes were split using `relativePath.split('/')`, the entire path was treated as a single token, failing folder hierarchy traversal.

### 4. Unhandled `already_trashed` Server Status
- When a file was previously sent to Google Drive Trash (or removed via Drive UI), Apps Script returns `{ status: "already_trashed" }` without a `message` property.
- The desktop client only recognized `"trashed"` and `"not_found"`. Unrecognized statuses fell through to the error branch, where the absence of a `message` property defaulted to `"Error al procesar archivo."`.
- Consequently, files already in the trash were reported as errors, preventing their hash keys from being purged from `sync_hashes.json` and trapping the user in an unresolvable orphan loop.

---

## Solutions & Defenses Implemented

### 1. Defensive Server Action Validation in Desktop Client
In `DriveSyncService.cs` (`TrashOrphansChunkInternalAsync`), the client explicitly inspects the response for `"action": "trash"`:
```csharp
string returnedAction = root.TryGetProperty("action", out var actProp) ? actProp.GetString() ?? "" : "";
if (!string.Equals(returnedAction, "trash", StringComparison.OrdinalIgnoreCase))
{
    summary.ErrorCount += chunk.Count;
    summary.ErrorMessages.Add(
        "El Web App de Google Apps Script es una versión desactualizada que no soporta la acción de papelera ('trash'). " +
        "Actualiza Code.gs en script.google.com con el script de docs/google-setup-guide.md " +
        "y despliega una 'Nueva versión' en Administrar implementaciones.");
    return;
}
```
This converts opaque Drive API runtime exceptions into an immediate, actionable upgrade diagnostic.

### 2. Defensive Payload Enrichment
The client now includes `filename` in trash payloads alongside `fileId` and `relativePath`, ensuring backward compatibility and preventing null-name exceptions if an older or hybrid script inspects the property.

### 3. Resilient Path Normalization in `Code.gs`
Both the upload hierarchy builder and `resolveFileByPath` now normalize backslashes before tokenizing:
```javascript
var normalizedPath = relativePath.replace(/\\/g, '/');
var parts = normalizedPath.split('/').filter(function(p) { return p.length > 0; });
```

### 4. Comprehensive Deployment Documentation
Updated `docs/google-setup-guide.md` with an explicit "Updating an Existing Deployment" guide highlighting the mandatory "New version" step in Apps Script.

### 5. First-Class Support for `already_trashed` Status
The desktop client now explicitly handles `already_trashed` in `DriveSyncService.cs`, increments `AlreadyTrashedCount`, and immediately purges the corresponding hash key from `_hashIndex`. `SyncDialogHelper` reports files that were already in the trash as completed operations rather than errors.
