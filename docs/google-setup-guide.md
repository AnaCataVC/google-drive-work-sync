# Google Apps Script Setup Guide

This guide explains how to set up and deploy the Google Apps Script Web App that acts as the secure bridge between **Google Drive Work Sync** and your Google Drive account.

---

## Architecture Overview

```
[ Google Drive Work Sync (.NET 9 / WinUI 3) ]
                    │
                    │  HTTPS POST (JSON Batch: max 8 files / 9 MB)
                    ▼
       [ Google Apps Script Web App ]
                    │
                    │  DriveApp API (Folder ID Cache + Clean Overwrite)
                    ▼
          [ Google Drive Folder ]
```

The wire protocol uses a standard JSON POST body:
```json
{
  "authToken": "optional-shared-secret",
  "files": [
    {
      "filename": "document.docx",
      "relativePath": "work/reports/document.docx",
      "mimeType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      "data": "<base64-encoded-payload>"
    }
  ]
}
```

---

## Step-by-Step Setup

### Step 1: Create a Google Drive Target Folder
1. Go to [Google Drive](https://drive.google.com).
2. Create a new folder (e.g., `Work Sync Backup`).
3. Open the folder and copy the **Folder ID** from the address bar:
   - URL: `https://drive.google.com/drive/folders/1a2B3c4D5e6F7g8H9i0J`
   - Folder ID: `1a2B3c4D5e6F7g8H9i0J`

---

### Step 2: Create the Apps Script Project
1. Open [Google Apps Script](https://script.google.com).
2. Click **New project**.
3. Name your project (e.g., `Google Drive Work Sync Bridge`).
4. Replace the contents of `Code.gs` with the following:

```javascript
function doPost(e) {
  // 1. Concurrency lock to prevent simultaneous duplicate folder/file creation
  var lock = LockService.getScriptLock();
  try {
    lock.waitLock(25000);
  } catch (t) {
    return ContentService.createTextOutput(JSON.stringify({
      status: "error",
      message: "Server is busy. Please try again shortly."
    })).setMimeType(ContentService.MimeType.JSON);
  }

  try {
    // 2. Optional: Set a shared secret token to protect your endpoint (leave empty if not needed)
    var AUTH_TOKEN = ""; // e.g. "my-super-secret-token"

    // 3. Paste your Google Drive Folder ID here
    var rootFolderId = "PASTE_YOUR_FOLDER_ID_HERE";

    // 4. Parse JSON request body
    var body = JSON.parse(e.postData.contents);

    if (AUTH_TOKEN && body.authToken !== AUTH_TOKEN) {
      return ContentService.createTextOutput(JSON.stringify({
        status: "error",
        message: "Unauthorized: Invalid or missing authentication token."
      })).setMimeType(ContentService.MimeType.JSON);
    }

    var files = body.files || [];
    var folderCache = PropertiesService.getScriptProperties();
    var results = [];

    for (var f = 0; f < files.length; f++) {
      var fileEntry = files[f];
      var relativePath = fileEntry.relativePath || fileEntry.filename;

      try {
        var currentFolder = DriveApp.getFolderById(rootFolderId);
        var fileName = fileEntry.filename;
        var mimeType = fileEntry.mimeType || "application/octet-stream";

        // 5. Recreate subfolder hierarchy using cached folder IDs
        var pathParts = relativePath.split("/");
        if (pathParts.length > 1) {
          var cacheKeyParts = [];
          for (var i = 0; i < pathParts.length - 1; i++) {
            var subfolderName = pathParts[i].trim();
            if (subfolderName.length === 0) continue;
            cacheKeyParts.push(subfolderName);

            var cacheKey = "folderId:" + cacheKeyParts.join("/");
            var cachedId = folderCache.getProperty(cacheKey);
            var resolvedFolder = null;

            if (cachedId) {
              try {
                resolvedFolder = DriveApp.getFolderById(cachedId);
                if (resolvedFolder.isTrashed()) resolvedFolder = null;
              } catch (staleIdErr) {
                resolvedFolder = null;
              }
            }

            if (!resolvedFolder) {
              var matchingFolders = currentFolder.getFoldersByName(subfolderName);
              resolvedFolder = matchingFolders.hasNext() ? matchingFolders.next() : currentFolder.createFolder(subfolderName);
              folderCache.setProperty(cacheKey, resolvedFolder.getId());
            }

            currentFolder = resolvedFolder;
          }
        }

        // 6. Clean overwrite: trash existing versions of the same file in this folder
        var existingFiles = currentFolder.getFilesByName(fileName);
        while (existingFiles.hasNext()) {
          existingFiles.next().setTrashed(true);
        }

        // 7. Decode Base64 and write new file
        var data = Utilities.base64Decode(fileEntry.data);
        var blob = Utilities.newBlob(data, mimeType, fileName);
        var file = currentFolder.createFile(blob);

        results.push({ relativePath: relativePath, status: "success", fileId: file.getId(), url: file.getUrl() });
      } catch (fileErr) {
        results.push({ relativePath: relativePath, status: "error", message: fileErr.toString() });
      }
    }

    return ContentService.createTextOutput(JSON.stringify({
      status: "success",
      results: results
    })).setMimeType(ContentService.MimeType.JSON);

  } catch (err) {
    return ContentService.createTextOutput(JSON.stringify({
      status: "error",
      message: err.toString()
    })).setMimeType(ContentService.MimeType.JSON);
  } finally {
    lock.releaseLock();
  }
}
```

5. Replace `"PASTE_YOUR_FOLDER_ID_HERE"` with the Folder ID from Step 1.
6. (Optional) Set `AUTH_TOKEN` if you want a shared secret token.
7. Save the script (`Ctrl+S`).

---

### Step 3: Deploy as a Web App
1. Click **Deploy** (blue button) $\rightarrow$ **New deployment**.
2. Click the gear icon ⚙️ $\rightarrow$ **Web app**.
3. Settings:
   - **Execute as:** `Me (<your-email>)`
   - **Who has access:** `Anyone`
4. Click **Deploy** and grant permissions if prompted.
5. Copy the generated **Web App URL** (`https://script.google.com/macros/s/.../exec`).

---

### Step 4: Configure the Desktop App
1. Open **Google Drive Work Sync**.
2. Go to **Ajustes** (Settings).
3. Paste the **URL del Web App**.
4. If you configured `AUTH_TOKEN`, enter it in the **Token de autenticación** field.
5. Click **Probar Conexión** to verify communication.
