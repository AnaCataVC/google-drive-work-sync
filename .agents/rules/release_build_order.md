# Build Order, Packaging & Binary Enforcement in Release Workflows

## 1. Version Bump Prior to Build Compilation
In projects producing executable binaries or distribution packages (e.g., `.exe`, `.msi`, `.apk`, WinUI/InnoSetup/Tauri/PyInstaller installers):
1. The version bump in source files (e.g., `*.csproj`, `Package.appxmanifest`, `app.manifest`, `constants.py`, `package.json`, `Cargo.toml`, etc.) **MUST ALWAYS be completed and committed BEFORE** executing build or packaging scripts.
2. The final executable compilation MUST ONLY occur after confirming that the version across source configuration files has been updated.

## 2. Strict Directory Parity in Installer Scripts (Inno Setup / WiX / NSIS)
1. **Source Inspection:** Prior to running the build command (e.g., `dotnet publish`), inspect the `Source:` directive in the installer script (`installer.iss`, `setup.wxs`, etc.) to verify the exact folder from which binaries will be packaged.
2. **Publish Destination Match:** The output path parameter of the build command (e.g., `-o releases\AppName-win-x64`) MUST match the configured path in `Source:`.
3. **`FileVersion` Verification Prior to Packaging:** Before invoking the installer compiler (`ISCC.exe`), verify via terminal/PowerShell that the primary executable in the source folder reflects the new version (`(Get-Item <path>/<app.exe>).VersionInfo.FileVersion`).

## 3. GitHub CLI Account Management in Releases
1. **Active Account Verification:** Prior to executing any `gh release create`, `gh release upload`, or `gh api` commands, verify and switch the active account using `gh auth switch --user <Owner>` to prevent 404/403 permission errors.
2. **Mandatory Assets in Remote Releases:** NEVER declare a GitHub release complete without verifying via `gh release view <tag>` that the resulting compiled binaries (`.exe`, `.zip`, `.msi`, `.apk`) are physically uploaded and available in the cloud (`asset_count > 0`).
3. **Re-upload with Clobber:** If a release was created without assets or contains stale binaries, delete obsolete assets or execute `gh release upload <tag> <files> --clobber`.

## 4. In-Place Release Update Protocol (Hotfixing Existing Tags)
When patching issues and re-uploading binaries to an existing published tag:
1. **Force Git CLI Credentials:** To prevent 403 authorization mismatches across multi-account environments:
   - Run `gh auth setup-git` or inject the authentication token into the push:
     ```powershell
     $token = (gh auth token --user <Owner>).Trim()
     $authHeader = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("<Owner>:$token"))
     git -c http.extraheader="Authorization: Basic $authHeader" push origin <branch>
     ```
2. **Update the Remote Git Tag:**
   ```powershell
   git tag -fa <tag> -m "Release <tag>: Updated build with fixes"
   git push origin <tag> --force
   ```
3. **Prune and Re-upload Assets:**
   - Remove stale assets to prevent collisions:
     ```powershell
     gh release delete-asset <tag> <asset-name> --yes 2>$null
     ```
   - Re-upload updated binaries:
     ```powershell
     gh release upload <tag> <files> --clobber
     ```
4. **Remote Timestamp Verification:** Inspect remote GitHub API metadata to verify that uploaded binaries were physically replaced:
   ```powershell
   gh api repos/:owner/:repo/releases/tags/<tag> | ConvertFrom-Json | Select-Object -ExpandProperty assets | Select-Object name, size, updated_at
   ```

## 5. Post-Release Local Cleanup & Artifact Hygiene
Once the release publication is verified and physical assets are confirmed on GitHub (`gh release view <tag>` with `asset_count > 0`):
1. **Purge .gitignore Directories & Build Artifacts:** Immediately delete generated local distribution folders listed in `.gitignore` (e.g., `releases/`, `dist/`, packaging staging folders, intermediate binaries).
2. **Safe Removal of Staged Release Artifacts:**
   ```powershell
   Remove-Item -Recurse -Force releases -ErrorAction SilentlyContinue
   ```
   *(Adjust to the canonical distribution folder name for the project stack: `dist/`, `out/`, etc.)*
3. **Prune Heavy Incremental Build Caches (Toolchain Clean):**
   To reclaim disk space safely without corrupting the working tree or risking uncommitted source code, prefer native toolchain commands:
   - **Rust / Tauri:** `cargo clean` or explicit removal of `src-tauri\target` (after terminating file-locking processes).
   - **.NET / WinUI:** `dotnet clean` and targeted purging of `bin/` and `obj/`.
   - **Android:** `./gradlew clean` and purging of `app/build/`.
   - **Web / Frontend:** `Remove-Item -Recurse -Force dist -ErrorAction SilentlyContinue`.
   - *Invariant:* NEVER use blind glob patterns (such as `*build*` or `*bin*`) to avoid accidental deletion of uncommitted source files.

## 6. Automated CI/CD Release Pipelines & Tag Decoupling Invariant
Across all repositories, the automated release lifecycle MUST follow strict decoupling between continuous integration and release publishing:
1. **CI vs. Release Decoupling:**
   - **`ci.yml` (Day-to-day CI):** Triggers on `push` to `main` and `pull_request`. Runs tests, linters, and verification builds. Must NEVER assemble release packages, compile installers, or publish release assets.
   - **`release.yml` (Production Release):** Triggers strictly on `push: tags: ['v*']`. Executes the full test suite, compiles production binaries, builds installers, computes SHA-256 hashes, and publishes the official GitHub Release via `softprops/action-gh-release@v2`.
2. **Single Deliverable Policy (Zero Auxiliary Checksum Uploads):**
   - GitHub Releases natively computes and displays the SHA-256 cryptographic digest directly in the web UI next to each uploaded asset.
   - NEVER upload auxiliary `.sha256`, `SHA256SUMS.txt`, or checksum text files as release assets. Each release must contain strictly the official primary deliverable (the setup `.exe` or production `.apk`).
3. **Android Keystore Secrets Management via CLI:**
   - Android release keystores must NEVER be committed to Git.
   - When configuring or rotating signing credentials, use the GitHub CLI to inject the secrets directly:
     ```powershell
     $bytes = [System.IO.File]::ReadAllBytes("path/to/keystore.jks")
     [System.Convert]::ToBase64String($bytes) | gh secret set RELEASE_KEYSTORE_BASE64 -R <owner>/<repo>
     "<password>" | gh secret set RELEASE_KEYSTORE_PASSWORD -R <owner>/<repo>
     "<alias>"    | gh secret set RELEASE_KEY_ALIAS -R <owner>/<repo>
     "<password>" | gh secret set RELEASE_KEY_PASSWORD -R <owner>/<repo>
     ```
   - In `release.yml`, decode the keystore, build the APK, and immediately delete the temporary keystore with `if: always() run: rm -f ...`.

## 7. Inno Setup Dynamic Version Invariant (`#ifndef MyAppVersion`)
When scripting Windows installers with Inno Setup (`.iss` files):
1. **Mandatory Conditional Definition:** NEVER use unconditional `#define MyAppVersion "X.Y.Z"`. It overrides command-line arguments. ALWAYS wrap version macros in `#ifndef`:
   ```iss
   #ifndef MyAppVersion
   #define MyAppVersion "1.0.0"
   #endif
   ```
2. **Command-Line Override in CI:** GitHub Actions and build scripts must pass `/DMyAppVersion=$tagVersion` to `ISCC.exe` to guarantee that the generated installer metadata perfectly matches the semantic Git tag.
