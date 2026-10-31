# UX Specification: Duplicate & Orphan Scan Indicators and Action Feedback

> **Status:** Approved  
> **Target Interface:** Desktop (WinUI 3 / Windows 11)  
> **Related Plan:** [implementation_plan.md](file:///C:/Users/anaca/.gemini/antigravity/brain/a45330a1-346d-4f76-9a72-cf86b989e610/implementation_plan.md)  

---

## 1. Scope & Cognitive Intent Decoding

### 1.1 Primary User Goals & Mental Models
- **Goal:** The user wants to scan for duplicate files across their work sources or inspect orphaned files remaining in Google Drive after local deletions, and optionally remove/trash them to save space.
- **Mental Model (Doherty Threshold & Visibility of System Status):** When clicking an action from a menu flyout (e.g., *"Inspector de archivos duplicados"* or *"Sugerencias de limpieza en Drive"*), the user expects immediate visual acknowledgment (< 400ms) that the system is processing their request.
- **Cognitive Friction Diagnosed:** Previously, the menu closed silently and nothing on screen indicated work was occurring. If directory traversal or SHA-256 hash calculation took 3 to 15 seconds, the user perceived the app as unresponsive, leading to duplicate clicks or confusion. Similarly, confirming trashing in Google Drive returned to an idle view while network requests ran.

### 1.2 Progressive Disclosure & Decision Hierarchy
1. **Trigger Phase:** User opens the secondary options menu from `WorkSyncView` or `ContextDiscoveryView`. Dropdown button is disabled while background scans or syncs are in flight (`CanOpenTools`).
2. **In-Flight Scan Phase:** Immediate full-page translucent overlay (`SmokeFillColorDefaultBrush`) with a centered card, active `ProgressRing`, and explicit state title (*"Escaneando archivos duplicados..."* / *"Buscando archivos huérfanos..."*).
3. **Inspection / Selection Phase:** Dialog opens with progressive controls: search filter box, bulk selection buttons (*"Seleccionar todo"*, *"Deseleccionar todo"*), and real-time metric counter (*"X de Y seleccionados · Z MB a liberar"*).
4. **Action Confirmation Phase:** Explicit modal asking for confirmation before moving files to Recycle Bin / Drive Trash.
5. **In-Flight Action Phase:** The overlay reactivates with action-specific status (*"Moviendo archivos huérfanos a la papelera de Google Drive..."* / *"Moviendo copias duplicadas a la papelera..."*) while network/file operations complete.
6. **Outcome Phase:** Summary dialog with clear metrics, freed space, and warnings (if any).

---

## 2. The 5 Essential UX States (State Matrix)

| State | Screen / Component Behavior | Actionable Elements & Microcopy |
| :--- | :--- | :--- |
| **1. Ideal / Populated** | Results dialog displays categorized list of items (orphan files with rename badges, or duplicate groups with primary file pinned). | CheckBoxes for individual files, *"Mover seleccionados a papelera"*, *"Cerrar"*. |
| **2. Empty State** | Zero orphans or zero duplicate groups found. Informative success card with green checkmark icon (`\uE73E`). | Dialog Title: *"Inspector de archivos duplicados"* / *"Sugerencias de limpieza"*.<br>Body: *"Escaneo completado (X archivos analizados). No se encontraron copias duplicadas."* / *"No se encontraron archivos huérfanos."*<br>CTA: *"Cerrar"*. |
| **3. Loading & Transition** | Background search or cleanup action in flight. Page controls are shielded from duplicate pointer input by a modal smoke backdrop. | Centered Card with `ProgressRing` (36x36).<br>Title: *"Escaneando archivos duplicados..."* / *"Buscando archivos huérfanos en Google Drive..."*.<br>Subtext: *"Por favor espera mientras se procesa la solicitud..."*. |
| **4. Error & Recovery** | Network failure communicating with Google Apps Script Web App or locked local files. | Outcome Dialog with Title: *"Resultado con advertencias"*.<br>Body: Lists failed items and explicit remediation message.<br>CTA: *"Aceptar"*. |
| **5. Partial / Degraded** | Some files trashed successfully in Drive, others failed or already missing (404). | Summary displays exact counts: *"Se enviaron X archivos a la papelera. Y archivos ya no existían en Drive. Z errores."* |

---

## 3. Microcopy & Button Directory

| UI Element | Label / Text | Semantic Purpose & UX Feedback |
| :--- | :--- | :--- |
| Scan Duplicates (WorkSync) | *"Inspector de archivos duplicados"* | Opens tool; triggers loading overlay: *"Escaneando archivos duplicados..."* |
| Scan Duplicates (Claude) | *"Inspector de archivos duplicados"* | Opens tool; triggers loading overlay: *"Escaneando archivos de contexto IA duplicados..."* |
| Scan Orphans (WorkSync) | *"Sugerencias de limpieza en Drive (Archivos huérfanos)"* | Opens tool; triggers loading overlay: *"Buscando archivos huérfanos en Google Drive..."* |
| Scan Orphans (Claude) | *"Sugerencias de limpieza en Drive (Archivos huérfanos)"* | Opens tool; triggers loading overlay: *"Buscando archivos huérfanos de Claude en Google Drive..."* |
| Action Trash Orphans | *"Mover a la papelera en Drive"* | Prompts confirmation, then triggers overlay: *"Moviendo archivos huérfanos a la papelera de Drive..."* |
| Action Dismiss Orphans | *"Dejar de seguir localmente"* | Removes hash from index; displays confirmation: *"Seguimiento actualizado: Se dejaron de seguir X archivos localmente."* |
| Action Trash Duplicates | *"Mover seleccionados a papelera"* | Prompts confirmation, then triggers overlay: *"Moviendo copias duplicadas a la papelera..."* |
| Secondary Dismiss / Close | *"Cerrar"* | Dismisses dialog without modifying files or Drive storage. |

---

## 4. Usability Heuristics & Guardrails

1. **Visibility of System Status (Nielsen #1):**
   - No silent async operations. Every phase (scan start, scan completion, batch deletion, batch result) transitions visibly with animated progress rings and unambiguous microcopy.
2. **Error Prevention & Concurrency Guard (Nielsen #5):**
   - The dropdown button and commands declare `CanExecute = nameof(CanOpenTools)`, disabling the flyout when `IsBusy == true` or when a synchronization is already executing (`IsDriveSyncing` / `IsScanningOutOfSync`).
   - The busy overlay sits at `Canvas.ZIndex="1000"` and intercepts all pointer hit tests, preventing race conditions.
3. **Destructive Action Friction (Nielsen #5):**
   - Moving files to trash requires explicit confirmation dialog detailing what will happen (Windows Recycle Bin + 30-day Google Drive trash).
4. **Token Consistency (ami-audit-ui-consistency compliance):**
   - Use `{ThemeResource SmokeFillColorDefaultBrush}` for the backdrop.
   - Use `{ThemeResource CardBackgroundFillColorDefaultBrush}` and `{ThemeResource CardStrokeColorDefaultBrush}` for cards.
   - Use typography styles `{StaticResource SubtitleTextBlockStyle}` and `{StaticResource CaptionTextBlockStyle}`.
   - Zero raw hex values in XAML.
