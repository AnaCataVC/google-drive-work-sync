# Google Drive Work Sync

[English](README.md) • [Español](README.es.md)

[![Platform](https://img.shields.io/badge/platform-Windows%2011%20%7C%20Windows%2010%20(1809%2B)-0078D6?style=flat-square&logo=windows)](https://microsoft.com)
[![Framework](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-WinUI%203%20%2F%20Windows%20App%20SDK%202.4-0078D6?style=flat-square)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![Architecture](https://img.shields.io/badge/Architecture-x64-blue?style=flat-square)]()
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)

A modern, high-performance Windows 11 desktop application designed to synchronize work directories and Claude AI contextual knowledge directly to Google Drive via Google Apps Script Web Apps. Built with native Fluent Design, Mica backdrop material, system tray background execution, and precision incremental hashing.

---

## Key Features

### 1. Incremental Work File Synchronization
- **Metadata Fast-Path & SHA-256 Verification:** Avoids re-uploading unmodified files by checking file modification timestamps and byte sizes first, computing full cryptographic hashes only when metadata indicates changes.
- **Persistent Hash Index:** Stores file states in `%LOCALAPPDATA%\GoogleDriveWorkSync\Data\sync_hashes.json`.
- **Adaptive Batching:** Packages files into payloads of up to 8 files or 9 MB uncompressed (~12 MB base64) to conform to Google Apps Script execution quotas and payload ceilings.
- **Fail-Soft Error Recovery:** Persists transient HTTP failures (429, 500, 503) to `sync_errors.json` and supports single-click batch retries with exponential backoff.
- **Out-of-Sync Default Workflow:** Syncs only new or modified files by default, providing explicit pre-sync difference inspection dialogues.

### 2. Claude AI Context Discovery & Backup
- **Multi-Level Project Traversal:** Performs breadth-first scans (levels 1–6) across developer workspaces, detecting Claude project guidelines (`CLAUDE.md`), agent skills, subagent prompts, memory files, and hooks.
- **Nested Repository & Worktree Exclusion:** The BFS traversal detects directories that are git repository roots — both standard repos (`.git` directory) and linked worktrees (`.git` file written by `git worktree add`) — and skips them entirely, preventing versioned `CLAUDE.md` files from being misclassified as untracked or out-of-sync.
- **Multi-Layer Secret Redaction:** Employs a three-tiered defense (blacklisted filenames, 64 KB header regex scans for PAT/SSH/OAuth tokens, and fail-closed MCP server configuration parsing) to prevent accidental data leakage.
- **Git Tracking Awareness:** Uses batched `git ls-files` queries to distinguish tracked documentation from uncommitted local scratchpad notes.
- **Status Classification:** Automatically cross-references candidates against the persistent hash index to classify items as *New*, *Modified*, or *Up to date*.

### 3. Automated Flexible Scheduler & System Tray
- **Custom Schedule Rules:** Configure background synchronization by selecting days of the week and exact run times.
- **System Tray Minimization:** Runs silently in the background with `H.NotifyIcon.WinUI`, minimizing to the notification area.
- **Windows Autostart:** Seamless integration via `--autostart` command-line switch to launch silently on Windows logon.

---

## Tech Stack & Architecture

- **Runtime & Language:** C# 13, .NET 9.0 (`net9.0-windows10.0.26100.0`, unpackaged WinUI 3)
- **UI Framework:** Windows App SDK 2.4 / WinUI 3 with native Mica backdrop material and Fluent Design System
- **MVVM Pattern:** `CommunityToolkit.Mvvm` 8.4.0 (Source Generators for Observable Properties & Relay Commands)
- **Dependency Injection:** `Microsoft.Extensions.Hosting` 9.0.2 (Decoupled Services, ViewModels, and Window lifecycle)
- **Tray & Shell Integration:** `H.NotifyIcon.WinUI` 2.1.4
- **Testing:** xUnit 2.9.2 + Moq 4.20.72 (100% test pass rate across 66 unit tests)
- **Installer:** Inno Setup 6.7 with LZMA2 ultra compression and automated registry autostart registration

---

## Architecture & Design Decisions

### DispatcherQueue Threading Invariant
WinUI 3 ViewModels and UI controls rely on `Microsoft.UI.Dispatching.DispatcherQueue` for cross-thread synchronization. To prevent race conditions or null references during early ViewModel resolution, `App.DispatcherQueue` is explicitly captured on the main thread prior to instantiating `MainWindow`.

### Google Apps Script Web App Wire Protocol
Google Apps Script Web Apps receive JSON payloads via HTTP POST. Rather than sending massive individual file uploads or streaming uploads that trigger Google Apps Script execution time limits (6 minutes), files are dispatched in pre-batched chunks. If a batch contains large binary files exceeding 9 MB, it is split immediately into smaller sub-batches.

### Three-Tiered Secret Filter
1. **Filename Filter:** Excludes known secret files (`.env`, `.npmrc`, `id_rsa`, `credentials.json`).
2. **Regex Deep Content Scan:** Scans the first 64 KB of text for GitHub tokens (`ghp_`), AWS Access Keys (`AKIA...`), and generic private key blocks.
3. **Fail-Closed JSON Sanitizer:** Parses MCP configuration files, strips sensitive environment variables, and redacts inline tokens before staging for backup.

---

## Installation & Setup

### Requirements
- Windows 11 (build 22000+) or Windows 10 (version 1809+)
- [.NET 9.0 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (included in self-contained installer)

### Quick Install (Setup Executable)
1. Download `GoogleDriveWorkSync-Setup-v1.0.1.exe` from the latest [GitHub Releases](https://github.com/AnaCataVC/google-drive-work-sync/releases).
2. Run the installer. You can optionally check "Iniciar Google Drive Work Sync automáticamente al iniciar sesión en Windows".
3. Launch the application from the Start Menu or Desktop.

### Building from Source

```powershell
# 1. Clone repository
git clone https://github.com/AnaCataVC/google-drive-work-sync.git
cd google-drive-work-sync

# 2. Restore and run unit tests
dotnet test GoogleDriveWorkSync.Tests/GoogleDriveWorkSync.Tests.csproj

# 3. Build Release binary
dotnet build GoogleDriveWorkSync/GoogleDriveWorkSync.csproj -c Release

# 4. Publish self-contained x64 deployment
dotnet publish GoogleDriveWorkSync/GoogleDriveWorkSync.csproj -c Release -r win-x64 --self-contained true -o releases/GoogleDriveWorkSync-win-x64

# 5. Compile Inno Setup installer
& "C:\Users\anaca\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer/GoogleDriveWorkSync.iss
```

---

## Configuration

Navigate to **Ajustes** (Settings) in the application:
1. **URL de Google Apps Script:** Enter the deployment URL of your Google Apps Script Web App (`https://script.google.com/macros/s/.../exec`).
2. **Token de autenticación:** Enter your shared secret token.
3. **Carpetas de origen:** Add local folders to synchronize and specify their destination subfolder prefix in Google Drive.
4. **Programación automática:** Toggle automated synchronization, choose target weekdays, and set execution time.

---

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
