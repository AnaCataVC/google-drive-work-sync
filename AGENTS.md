# Repository Agent Guidelines: Google Drive Work Sync

This document defines architecture invariants, development workflows, quality gates, and release packaging rules for **Google Drive Work Sync**. All AI agents and automated workflows interacting with this repository must strictly adhere to these directives.

---

## 1. Project Overview & Architecture

**Google Drive Work Sync** is a lightweight, high-performance Windows desktop application engineered in C# 13 and unpackaged WinUI 3 (.NET 9). It automates bidirectional-safe synchronization of local work folders to Google Drive via Google Apps Script Web Apps without requiring full Google Drive client installations or OAuth2 client secrets. It also features automated Claude AI context discovery, three-tiered secret redaction, and an automated scheduler.

### Directory Structure
```text
google-drive-work-sync/
├── GoogleDriveWorkSync/          # WinUI 3 Desktop Application (.NET 9)
│   ├── Assets/                  # Application icons (AppIcon.ico, AppIcon.png)
│   ├── Helpers/                 # Autostart, converters, logging, settings helpers
│   ├── Models/                  # Sync settings, schedule models, update models
│   ├── Services/                # DriveSyncService, ClaudeDiscoveryService, SyncScheduleService
│   ├── ViewModels/              # WorkSyncViewModel, ContextDiscoveryViewModel, SettingsViewModel
│   ├── Views/                   # Mica-styled WinUI 3 pages and navigation
│   ├── App.xaml / MainWindow    # App lifecycle and main window shell
│   └── GoogleDriveWorkSync.csproj
├── GoogleDriveWorkSync.Tests/    # Unit & Integration Tests (xUnit + Moq)
├── installer/                   # Inno Setup packaging script
│   └── GoogleDriveWorkSync.iss  # Compilation script generating Setup EXE
├── docs/                        # Architecture decisions, benchmarks & learning notes
├── releases/                    # Compiled installers & release outputs (gitignored)
├── README.md                    # English portfolio documentation
├── README.es.md                 # Spanish portfolio documentation
└── GoogleDriveWorkSync.sln      # Visual Studio solution file
```

---

## 2. Release Distribution Invariant (Single Deliverable Policy)

> [!IMPORTANT]
> **Single Official Deliverable for GitHub Releases:**
> Every release published on GitHub **MUST ALWAYS** publish **ONLY** the official Inno Setup installer executable:
> ```text
> GoogleDriveWorkSync-Setup-vX.Y.Z.exe
> ```
> - **Strict Prohibition on Extra Assets**: NEVER publish standalone portable executables (`.exe`), unpackaged binaries, or compressed archives (`.zip`) as GitHub Release assets. Production binaries compiled during `dotnet publish` exist strictly to be packaged into the Setup installer by Inno Setup, never as independent downloads.
> - **Verification Gate**: Any release workflow or release agent (`ami-release-manager`) must verify with `gh release view <tag> --json assets` that only the Setup installer executable is uploaded. Releases containing portable/zip files or lacking the Setup executable are strictly non-compliant.

### Packaging Pipeline Specification
1. **Version Bump Order**: Bump the version in:
   - `installer/GoogleDriveWorkSync.iss` (`#define MyAppVersion "X.Y.Z"`)
   - `GoogleDriveWorkSync/GoogleDriveWorkSync.csproj` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`)
2. **Publish Self-Contained Deployment**:
   ```powershell
   dotnet publish GoogleDriveWorkSync/GoogleDriveWorkSync.csproj -c Release -r win-x64 --self-contained true -o releases/GoogleDriveWorkSync-win-x64
   ```
3. **Compile Inno Setup Installer**:
   ```powershell
   & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer/GoogleDriveWorkSync.iss
   ```
   *Target output: `releases/GoogleDriveWorkSync-Setup-vX.Y.Z.exe`.*
4. **Release Asset Upload**:
   ```powershell
   gh release upload vX.Y.Z "releases/GoogleDriveWorkSync-Setup-vX.Y.Z.exe" --clobber
   ```
5. **Local Cleanliness**: Purge local `releases/` directory post-upload to prevent disk bloat.

---

## 3. Mandatory Agent Rules & Directives

### 🌐 Language & Communication
- **Source Code**: All C# code (classes, methods, variables, docstrings) MUST be in **English**.
- **User Chat**: Communicate with the user in **Spanish** unless requested otherwise.
- **Git Commits**: Use **Conventional Commits** in **English** (`feat: ...`, `fix: ...`, `docs: ...`, `chore: ...`).
- **README Files**: Maintain symmetric bilingual documentation (`README.md` and `README.es.md`).
- **Zero Flags Rule**: Never use country flag emojis (`🇺🇸`, `🇪🇸`, etc.) in documentation or UI.

### 🔒 Security & Privacy
- **Absolute Paths**: NEVER leak absolute computer paths (e.g., `C:\Users\...`) into code, documentation, or commits. Always use relative paths or generic placeholders.
- **Three-Tiered Secret Filter**: Maintain the fail-closed defense: blacklisted secret filenames (`.env`, `credentials.json`), 64 KB header regex scans for PAT/SSH tokens, and recursive sanitization of MCP configuration files before sync staging.

### 💻 PowerShell Environment
- **Command Chaining**: NEVER use `&&` or `||` in terminal commands. Use `;` or separate sequential commands.
- **GitHub CLI Context**: Switch to personal account `AnaCataVC` (`gh auth switch -u AnaCataVC --hostname github.com 2>$null`).

---

## 4. WinUI 3 Desktop Stability Invariants

1. **DispatcherQueue Initialization**: Always capture and initialize `App.DispatcherQueue` on the UI thread before instantiating `MainWindow` to prevent null references during early ViewModel resolution.
2. **Crash Logging**: Global exception handlers (`App.UnhandledException`, `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) must persist crash details to `%LOCALAPPDATA%\GoogleDriveWorkSync\Logs\`.
3. **Crash-Safe Hash Index**: Sync state index (`sync_hashes.json`) must be written to a temporary file and atomically swapped into place, ensuring unexpected process aborts never truncate the index.
4. **No Silent Skips**: Unreadable locked files or permission-denied directories must be reported as errors, never classified as "unchanged".

---

## 5. Build & Test Commands (PowerShell)

```powershell
# Restore & build solution
dotnet build GoogleDriveWorkSync.sln -c Release

# Run automated unit & integration test suite
dotnet test GoogleDriveWorkSync.Tests/GoogleDriveWorkSync.Tests.csproj
```
