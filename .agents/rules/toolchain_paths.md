# Local Toolchain Paths

This file stores known installation paths for development tools on this machine.
Use these paths directly in scripts instead of searching each time.

## Inno Setup 6

- **ISCC.exe (command-line compiler):** `"$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"`
- **Compil32.exe (GUI IDE):** `"$env:LOCALAPPDATA\Programs\Inno Setup 6\Compil32.exe"`
- **Installation type:** User-level (installed under `AppData\Local`, not `Program Files`)
- **Note:** Because it is a user-level install, it does NOT appear under `C:\Program Files (x86)\Inno Setup 6\`. Always resolve via the `AppData\Local` path above.

### Usage in PowerShell

```powershell
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
& $iscc "installer.iss"
```
