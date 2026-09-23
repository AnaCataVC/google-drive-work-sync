# Google Drive Work Sync

[English](README.md) • [Español](README.es.md)

[![Plataforma](https://img.shields.io/badge/plataforma-Windows%2011%20%7C%20Windows%2010%20(1809%2B)-0078D6?style=flat-square&logo=windows)](https://microsoft.com)
[![Framework](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Interfaz](https://img.shields.io/badge/UI-WinUI%203%20%2F%20Windows%20App%20SDK%202.4-0078D6?style=flat-square)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![Arquitectura](https://img.shields.io/badge/Arquitectura-x64-blue?style=flat-square)]()
[![Licencia](https://img.shields.io/badge/Licencia-MIT-green?style=flat-square)](LICENSE)

Aplicación de escritorio moderna y de alto rendimiento para Windows 11, diseñada para sincronizar carpetas de trabajo y archivos de contexto para IA (Claude) directamente hacia Google Drive mediante una Web App de Google Apps Script. Desarrollada con Fluent Design, material translúcido Mica nativo, ejecución minimizada en la bandeja del sistema y hashing incremental de alta precisión.

---

## Características Principales

### 1. Sincronización Incremental de Archivos de Trabajo
- **Vía Rápida por Metadatos y Verificación SHA-256:** Evita subir archivos duplicados o sin cambios comprobando primero la fecha de modificación y el tamaño en bytes, calculando el hash criptográfico únicamente cuando los metadatos difieren.
- **Índice Persistente de Hashes:** Almacena el estado de los archivos en `%LOCALAPPDATA%\GoogleDriveWorkSync\Data\sync_hashes.json`.
- **Procesamiento por Lotes Adaptativo:** Agrupa archivos en lotes de hasta 8 elementos o 9 MB sin comprimir (~12 MB en base64) para respetar los límites de carga y tiempo de ejecución de Google Apps Script.
- **Recuperación Resiliente ante Errores:** Registra fallos transitorios de red (429, 500, 503) en `sync_errors.json` y permite reintentos directos con retroceso exponencial (*exponential backoff*).
- **Flujo de Trabajo por Defecto Orientado a Desincronizados:** Sincroniza de forma predeterminada solo los archivos nuevos o modificados, ofreciendo diálogos de previsualización antes de iniciar la subida.

### 2. Detección y Respaldo de Contexto IA (Claude)
- **Exploración Jerárquica de Proyectos:** Búsqueda en anchura (BFS niveles 1 al 6) por repositorios y carpetas de trabajo, identificando archivos de directrices de proyecto (`CLAUDE.md`), habilidades de agentes, prompts de subagentes, memorias y hooks.
- **Exclusión de Repositorios Anidados y Worktrees:** El traversal BFS detecta si un subdirectorio es raíz de un repositorio git —tanto repos estándar (carpeta `.git`) como worktrees vinculados (archivo `.git` generado por `git worktree add`)— y los omite por completo, evitando que los `CLAUDE.md` versionados se clasifiquen erróneamente como archivos no sincronizados.
- **Protección Multicapa contra Fugas de Secretos:** Defensa en tres fases (lista negra de nombres de archivo, escaneo por expresiones regulares en los primeros 64 KB de contenido para tokens PAT/SSH/OAuth, y saneamiento estricto de configuraciones de servidores MCP).
- **Detección de Seguimiento en Git:** Consultas en lotes de `git ls-files` para distinguir entre documentación versionada y notas locales o scratchpads sin seguimiento.
- **Clasificación de Estado:** Cruza los archivos descubiertos contra el índice de hashes para etiquetarlos en tiempo real como *Nuevo*, *Modificado* o *Al día*.

### 3. Programador Automático Flexible y Bandeja del Sistema
- **Reglas de Programación Personalizadas:** Configura respaldos automáticos en segundo plano seleccionando días específicos de la semana y la hora exacta de ejecución.
- **Minimización a la Bandeja del Sistema:** Se ejecuta silenciosamente en segundo plano utilizando `H.NotifyIcon.WinUI`.
- **Inicio con Windows:** Integración nativa a través del parámetro `--autostart` para ejecutarse al iniciar sesión en Windows.

---

## Tecnologías y Arquitectura

- **Plataforma y Lenguaje:** C# 13, .NET 9.0 (`net9.0-windows10.0.26100.0`, WinUI 3 no empaquetado)
- **Framework de UI:** Windows App SDK 2.4 / WinUI 3 con material Mica y Fluent Design System
- **Patrón MVVM:** `CommunityToolkit.Mvvm` 8.4.0 (Generadores de código fuente para propiedades observables y comandos)
- **Inyección de Dependencias:** `Microsoft.Extensions.Hosting` 9.0.2 (Servicios desacoplados, ViewModels y ciclo de vida de la ventana)
- **Integración de Bandeja de Notificaciones:** `H.NotifyIcon.WinUI` 2.1.4
- **Pruebas Unitarias:** xUnit 2.9.2 + Moq 4.20.72 (100% de éxito en 66 pruebas automatizadas)
- **Instalador:** Inno Setup 6.7 con compresión ultra LZMA2 y registro de inicio automático en Windows

---

## Decisiones de Arquitectura y Aprendizajes Clave

### Invariante de Subprocesos para DispatcherQueue
Los ViewModels y controles de WinUI 3 dependen de `Microsoft.UI.Dispatching.DispatcherQueue` para actualizar la interfaz desde hilos secundarios. Para prevenir condiciones de carrera o excepciones de referencia nula, `App.DispatcherQueue` se inicializa explícitamente en el hilo principal antes de instanciar `MainWindow`.

### Protocolo de Comunicación con Google Apps Script
Las Web Apps de Google Apps Script reciben cargas útiles JSON vía HTTP POST. En lugar de transmitir archivos de forma masiva o individual que agotarían el límite de tiempo de ejecución (6 minutos), los archivos se transmiten en lotes óptimos con control de tamaño.

### Filtro de Secretos en Tres Capas
1. **Filtro por Nombre de Archivo:** Excluye archivos críticos reconocidos (`.env`, `.npmrc`, `id_rsa`, `credentials.json`).
2. **Escaneo Profundo por Expresiones Regulares:** Examina los primeros 64 KB en busca de claves privadas, tokens de GitHub (`ghp_`) y credenciales de nube.
3. **Saneador de Configuraciones JSON:** Analiza configuraciones MCP, eliminando variables de entorno sensibles antes del respaldo.

---

## Instalación y Uso

### Requisitos
- Windows 11 (compilación 22000+) o Windows 10 (versión 1809+)
- [.NET 9.0 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (incluido en el instalador autónomo)

### Instalación Rápida (Instalador Único)
1. Descarga `GoogleDriveWorkSync-Setup-v1.2.0.exe` desde la sección de [GitHub Releases](https://github.com/AnaCataVC/google-drive-work-sync/releases).
2. Ejecuta el instalador. Puedes marcar la opción de inicio automático con Windows si lo deseas.
3. Abre la aplicación desde el Menú Inicio o el Escritorio.

### Compilación desde el Código Fuente

```powershell
# 1. Clonar el repositorio
git clone https://github.com/AnaCataVC/google-drive-work-sync.git
cd google-drive-work-sync

# 2. Restaurar dependencias y ejecutar pruebas unitarias
dotnet test GoogleDriveWorkSync.Tests/GoogleDriveWorkSync.Tests.csproj

# 3. Compilar en configuración Release
dotnet build GoogleDriveWorkSync/GoogleDriveWorkSync.csproj -c Release

# 4. Publicar versión autónoma para x64
dotnet publish GoogleDriveWorkSync/GoogleDriveWorkSync.csproj -c Release -r win-x64 --self-contained true -o releases/GoogleDriveWorkSync-win-x64

# 5. Compilar instalador con Inno Setup
& "C:\Users\anaca\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer/GoogleDriveWorkSync.iss
```

---

## Configuración

Dirígete a la pestaña de **Ajustes** en la aplicación:
1. **URL del Web App de Google Apps Script:** Introduce la URL del script web desplegado (`https://script.google.com/macros/s/.../exec`).
2. **Token de Autenticación:** Introduce el token secreto compartido con el script.
3. **Carpetas de Origen:** Agrega las rutas de las carpetas locales a sincronizar y su prefijo de destino en Drive.
4. **Programación Automática:** Activa el respaldo desatendido, elige los días deseados y define la hora de ejecución.

---

## Licencia

Este proyecto está bajo la Licencia MIT. Consulta el archivo [LICENSE](LICENSE) para más información.


