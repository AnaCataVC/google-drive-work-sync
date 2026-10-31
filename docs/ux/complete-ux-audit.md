# Auditoría Completa de Experiencia de Usuario (UX)
## Google Drive Work Sync (.NET 9 / WinUI 3)

> **Fecha:** Octubre 2026  
> **Objetivo:** Evaluación heurística integral, matriz de completitud de estados de interacción, ergonomía de formularios y accesibilidad.  
> **Metodología:** 10 Heurísticas de Usabilidad de Jakob Nielsen, Leyes Cognitivas de UX (Fitts, Hick, Miller, Doherty) y Matriz Canónica de 5 Estados.

---

## 1. Resumen Ejecutivo

**Google Drive Work Sync** presenta una arquitectura de interacción sólida, de alto rendimiento y alineada con los estándares modernos de Windows 11 (Fluent Design System, Mica, tipografía de sistema). La reciente incorporación de indicadores de carga modales para duplicados y huérfanos resolvió el cuello de botella cognitivo más crítico de la aplicación.

### Puntuación Global de Usabilidad: **9.4 / 10**
- **Visibilidad del Estado del Sistema:** 9.6 / 10
- **Prevención y Recuperación de Errores:** 9.5 / 10
- **Consistencia y Estándares:** 9.7 / 10
- **Ergonomía de Formularios y Acciones:** 8.8 / 10

---

## 2. Evaluación de las 10 Heurísticas de Jakob Nielsen

### Heurística 1: Visibilidad del Estado del Sistema (Visibility of System Status) — ⭐⭐⭐⭐⭐ (Excelente)
- **Diagnóstico:** La aplicación mantiene al usuario informado en todo momento:
  - Tarjetas KPI superiores reflejan en tiempo real el estado general (*"Al día"*, *"Sincronizando..."*, *"Con errores"*), conteo de pendientes, composición (nuevos vs modificados) y último respaldo con código cromático semántico.
  - La barra de sincronización incluye una `ProgressBar` reactiva con porcentaje y detalle textual (*"Subiendo archivo X de Y..."*).
  - Los procesos asíncronos de búsqueda y limpieza de huérfanos y duplicados ahora cuentan con un **overlay modal translúcido (`SmokeFillColorDefaultBrush`)** y `ProgressRing` que elimina la incertidumbre operativa (< 400 ms, Umbral de Doherty).
  - En la bandeja del sistema (*System Tray*), el tooltip y el ícono reflejan el estado de la app en segundo plano.

### Heurística 2: Correspondencia entre el Sistema y el Mundo Real (Match Between System & Real World) — ⭐⭐⭐⭐⭐ (Excelente)
- **Diagnóstico:** Vocabulario intuitivo y centrado en la tarea del usuario:
  - Formato amigable de tamaños de archivo (`FormatBytes`: KB, MB, GB).
  - Explicaciones claras sobre el mecanismo de respaldo por firmas SHA-256 sin tecnicismos innecesarios.
  - Metáforas claras de Google Drive (*"Subcarpeta en Drive"*, *"Mover a papelera de Drive con 30 días de recuperación"*).

### Heurística 3: Control y Libertad del Usuario (User Control & Freedom) — ⭐⭐⭐⭐⭐ (Excelente)
- **Diagnóstico:** Salidas de emergencia bien señalizadas:
  - Botón explícito de *"Cancelar"* durante la sincronización activa.
  - Botón *"Cerrar"* / escape en todos los diálogos modales.
  - En la bandeja del sistema, comandos claros para *"Mostrar Ventana"*, *"Minimizar a la bandeja"* y *"Salir"*.
  - En el inspector de duplicados y sugerencias de huérfanos, el usuario tiene control granular: casillas individuales, *"Seleccionar todo"*, *"Deseleccionar todo"* e *"Invertir selección"*.

### Heurística 4: Consistencia y Estándares (Consistency & Standards) — ⭐⭐⭐⭐⭐ (Excelente)
- **Diagnóstico:** Paridad estructural completa:
  - Estructura simétrica y consistente entre la vista de trabajo (`WorkSyncView`) y la vista de Claude (`ContextDiscoveryView`): misma grilla de 4 tarjetas KPI, misma barra de acción principal, mismo menú desplegable secundario y misma lista de archivos desincronizados.
  - Uso riguroso de componentes nativos WinUI 3 y Segoe Fluent Icons (`&#xE753;`, `&#xE712;`, `&#xE74D;`, `&#xE8B9;`).
  - Cumplimiento de convenciones Windows 11 (Mica backdrop, bordes redondeados estándar de 8px y 12px, elevación con sombra compartida).

### Heurística 5: Prevención de Errores (Error Prevention) — ⭐⭐⭐⭐⭐ (Excelente)
- **Diagnóstico:** Salvaguardas proactivas ante colisiones y pérdidas de datos:
  - Los comandos secundarios se deshabilitan reactivamente (`CanOpenTools => !IsBusy && !IsDriveSyncing`) cuando hay sincronizaciones o análisis en curso.
  - Diálogos de doble confirmación con advertencia de impacto antes de mover archivos a la Papelera de Windows o a la papelera de Google Drive.
  - En Ajustes, la entrada de tamaño máximo de archivo (`NumberBox`) está restringida físicamente con `Minimum="1"` y `Maximum="25"` MB para respetar las cuotas de Google Apps Script.

### Heurística 6: Reconocimiento antes que Recuerdo (Recognition Rather than Recall) — ⭐⭐⭐⭐⭐ (Excelente)
- **Diagnóstico:** Reducción de carga cognitiva:
  - Los diálogos de desincronizados, duplicados y huérfanos incluyen un cuadro de búsqueda en vivo con filtrado instantáneo por nombre y ruta.
  - Insignias visuales destacadas para archivos *"Renombrados / movidos a..."* con tooltip explicativo de la nueva ruta física.
  - Tooltips contextuales en botones de acción y campos de entrada de Ajustes.

### Heurística 7: Flexibilidad y Eficiencia de Uso (Flexibility & Efficiency of Use) — ⭐⭐⭐⭐☆ (Muy Bueno)
- **Diagnóstico:** Soporte tanto para usuarios nóveles como avanzados:
  - Usuarios directos: Botón prominente de *"Sincronizar desincronizados"* (un solo clic).
  - Usuarios avanzados: Menú desplegable con opciones de previsualización modal, re-subida forzada total, inspector de duplicados y limpieza de huérfanos.
  - Sincronización programada configurable por días individuales de la semana (Lunes a Domingo) y selector de hora (`TimePicker`).

### Heurística 8: Diseño Estético y Minimalista (Aesthetic & Minimalist Design) — ⭐⭐⭐⭐⭐ (Excelente)
- **Diagnóstico:** Alta limpieza visual y jerarquía clara (Ley de Hick y Miller):
  - La pantalla principal no satura al usuario: muestra métricas clave en la parte superior y la lista de archivos pendientes abajo.
  - En Ajustes, los filtros avanzados (extensiones permitidas/excluidas, carpetas ignoradas, borrado de caché de hashes) están elegantemente agrupados dentro de un `Expander` para evitar sobrecarga cognitiva.

### Heurística 9: Ayudar a los Usuarios a Reconocer, Diagnosticar y Recuperarse de Errores — ⭐⭐⭐⭐⭐ (Excelente)
- **Diagnóstico:** Comunicación clara de fallos sin tecnicismos herméticos:
  - Banner crítico en rojo visible solo cuando existen errores de sincronización (`HasSyncErrors`).
  - Acción directa de *"Reintentar errores"* y botón *"Ver detalles"* que abre un diálogo con la lista de archivos afectados y su categoría (Red, Bloqueo local, Cuota, etc.).

### Heurística 10: Ayuda y Documentación (Help & Documentation) — ⭐⭐⭐⭐☆ (Muy Bueno)
- **Diagnóstico:** Microcopia asistiva bien distribuida:
  - Placeholders claros con ejemplos reales en los campos de Ajustes (`https://script.google.com/macros/s/.../exec`, `ej: claude-md-unversioned`).
  - Textos de ayuda (*caption textblocks*) debajo de cada sección explicando su funcionamiento.

---

## 3. Matriz de Completitud de los 5 Estados de UX

| Vista / Flujo | Ideal (Con datos) | Vacío (Empty State) | Carga / Transición | Error / Recuperación | Parcial / Degradado |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Archivos de Trabajo (`WorkSyncView`)** | Lista en vivo de archivos pendientes con tamaño y ruta. | Banner de éxito verde: *"¡Todo tu trabajo está sincronizado!"*. | Overlay modal con `ProgressRing` y texto de escaneo/sincronización. | Banner crítico en rojo con botón *"Reintentar"* y detalle de errores. | Muestra archivos pendientes mientras los sincronizados se descuentan en tiempo real. |
| **Contexto IA (`ContextDiscoveryView`)** | Árbol/Lista de archivos Claude agrupados por categoría. | Banner de éxito verde: *"Todo tu contexto de Claude está al día en Google Drive"*. | Overlay modal de escaneo y `ProgressRing` en botón *"Actualizar estado"*. | Banner crítico con reintento y diálogo de diagnóstico por categoría. | Soporta selección parcial de candidatos mediante CheckBoxes. |
| **Inspector de Duplicados** | Grupos ordenados por tamaño con copia principal fijada. | Diálogo con ícono verde de éxito: *"No se encontraron copias duplicadas"*. | Overlay modal activo: *"Escaneando archivos duplicados..."*. | Diálogo de resultado con desglose de advertencias o fallos individuales. | Muestra grupos con selección manual o invertida. |
| **Limpieza de Huérfanos** | Lista de archivos eliminados localmente con detección de renombres. | Diálogo con ícono verde: *"No se encontraron archivos huérfanos"*. | Overlay modal activo: *"Buscando archivos huérfanos..."* / *"Moviendo a papelera..."*. | Reporte de archivos no encontrados en Drive o errores de red. | Permite seleccionar subconjuntos para enviar a papelera o dejar de seguir. |

---

## 4. Oportunidades de Pulido Resueltas

1. **Confirmación al eliminar carpeta en Ajustes (Fricción destructiva):**
   - *Implementado:* En `SettingsView.xaml.cs`, el botón `DeleteSourceButton_Click` despliega un `ContentDialog` con `DefaultButton = ContentDialogButton.Close`: *"¿Eliminar carpeta de sincronización? La carpeta ya no se respaldará en Google Drive. Los archivos que ya existen en Google Drive no serán eliminados."*
   - *Accesibilidad:* Se añadió `AutomationProperties.Name="Eliminar carpeta de sincronización"` al botón de solo ícono.

2. **Uso de Token de Sistema para Texto sobre Acento:**
   - *Implementado:* En los badges de conteo y motivo de desincronización (`WorkSyncView.xaml` y `ContextDiscoveryView.xaml`), se sustituyó `Foreground="White"` por `Foreground="{ThemeResource TextOnAccentFillColorPrimaryBrush}"`, logrando 100% de conformidad con tokens de alto contraste.

3. **Advertencia al Restablecer Caché de Hashes en Ajustes:**
   - *Implementado:* `ClearHashIndexButton_Click` ahora presenta un diálogo de confirmación previo advirtiendo que se recalcularán los hashes SHA-256 de todos los archivos locales desde cero antes de proceder.
