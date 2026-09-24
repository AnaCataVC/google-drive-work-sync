> **Created:** 2026-09-21
> **Last Updated:** 2026-09-23

# Google Drive Work Sync: Architecture & Technology Research

## 1. Overview & Objective
`Google Drive Work Sync` is a Windows 11 desktop application consolidating:
1. **Work Files Sync (`work-activity-panel` heritage):** Incremental backup with SHA-256 caching (`sync_hashes.json`), metadata fast-path (`LastWriteTimeUtc` + `FileSize`), batching (max 8 files or 9 MB payload per POST), multi-source local folders mapped to Drive subfolders, retry with exponential backoff for 429/500/503, error tracking (`sync_errors.json`, which also records unreadable files and folders), crash-safe index writes (temp file + replace, once per batch), and probe-file connection testing. Index keys: work files use `<prefix>|<localPath>`; Claude context uses the Drive-relative path. Only the former is purged when the local file disappears.
2. **AI Context Backup (`claude-desktop-tools` heritage):** Multi-level BFS discovery of `CLAUDE.md`, `references/`, `skills/`, `agents/`, `hooks/`, `agent-memory/`, `project-memory/`, `settings.json`, `keybindings.json`, `mcpServers`. Multi-layer secret redaction (filename keywords, 64KB content regex for AWS/GitHub/SSH/Slack keys, fail-closed MCP sanitization). Git untracked detection via `git ls-files` in batches of 50 (`core.quotePath=false`; fail-open to "untracked" when git errors). Granular category and item selection.
3. **Flexible Synchronization Scheduler:** Decoupled precision background timer allowing users to select days of the week (Pills Mon–Sun) and a target time (`TimePicker`), running automated backups silently in the background without UI hangs.
4. **App Quality, Diagnostics & Single Setup Installer:** Global exception handling dumping to `%LOCALAPPDATA%\SmartSync\Logs\crash.log` and `startup_diagnostic.log`. Native Mica background, Fluent Design, System Tray via `H.NotifyIcon.WinUI`, GitHub Releases auto-updater, and a single Inno Setup `.exe` installer with `--autostart` support.

---

## 2. Technology Stack Evaluation & Versions

### Framework & Target Runtime
- **Runtime:** `.NET 9.0` (`net9.0-windows10.0.26100.0`, Min: `10.0.17763.0`)
- **UI Framework:** WinUI 3 via `Microsoft.WindowsAppSDK` 2.4.0 (Unpackaged, `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`).
- **Build Tools:** `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2526 + `Microsoft.Windows.SDK.BuildTools.WinApp` 0.6.0.
- **MVVM Framework:** `CommunityToolkit.Mvvm` 8.4.0 (Source generators for ObservableProperty, RelayCommand).
- **Dependency Injection & Hosting:** `Microsoft.Extensions.Hosting` 9.0.2 (`Microsoft.Extensions.DependencyInjection`).
- **System Tray:** `H.NotifyIcon.WinUI` 2.4.1 (or compatible stable 2.1.4+ matching WAP/CDT proven patterns).
- **Testing:** xUnit 2.5.3 + Moq 4.20.72 + `Microsoft.NET.Test.Sdk` 17.8.0.
- **Installer:** Inno Setup 6 (`ISCC.exe`) generating a single setup installer executable (`GoogleDriveWorkSync-Setup-vX.X.X.exe`).

---

## 3. Key Architectural Decisions & Protocols

### Google Apps Script Web App Wire Protocol
- Endpoint: Single `/exec` URL deploying Google Apps Script.
- Payload: JSON POST `{ "authToken": "...", "files": [ { "filename": "...", "relativePath": "...", "mimeType": "...", "data": "base64..." } ] }`.
- Concurrency & Batching: Batched up to 8 files or 9 MB uncompressed bytes to amortize cold starts and `LockService.waitLock` overhead while respecting Google's ~25 MB payload limit.
- Path Routing:
  - Work Files: `<DestinationPrefix>/<relativePath>`
  - Claude Context with Repo: `<DestinationPrefix>/<repoName>/<relativePath>`
  - Claude Context without Repo: `<DestinationPrefix>/_sin-repo/<relativePath>`
  - Claude Global Configurations: `<DestinationPrefix>/_claude-config/<relativePath>`

### Secret Scanning Multi-Layer Defense
1. **Filename Filter:** Exclude names containing `secret`, `credential`, `password`, `token`, `private_key`, `id_rsa`, `id_ed25519`.
2. **Content Scanning:** Inspect initial 64 KB using compiled regex patterns for Private Keys (`BEGIN RSA/OPENSSH PRIVATE KEY`), AWS Access Keys (`AKIA...`), GitHub Tokens (`ghp_...`, `github_pat_...`), Slack Tokens (`xoxb-...`), and HTTP Bearer tokens.
3. **MCP Config Sanitization:** Strip secret fragments (`token`, `key`, `secret`, `auth`, `header`, `env`) with fail-closed guarantee (if any secret pattern remains post-sanitization, file upload is completely prohibited).

### Scheduler Architecture
- Decoupled from work shifts: A lightweight background timer (`System.Threading.Timer` or `PeriodicTimer`) checking every 30-60 seconds if the current `DateTime.Now` matches the scheduled days (`List<DayOfWeek>`) and scheduled target time (`TimeSpan`), triggering `RunScheduledSyncAsync()` if not already synced today.

---

## 4. References & Documentation Links
- [Windows App SDK 2.4 Documentation](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)
- [CommunityToolkit.Mvvm Documentation](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)
- [H.NotifyIcon Documentation](https://github.com/HavenDV/H.NotifyIcon)
- Local reference implementations:
  - `c:\Users\anaca\Repos\work-activity-panel` (`DriveSyncService.cs`, `UpdateService.cs`, `AutostartHelper.cs`)
  - `c:\Users\anaca\Repos\claude-desktop-tools` (`ClaudeConfigDiscoveryService.cs`, `DriveSyncService.cs`, `ContextDiscoveryViewModel.cs`)
