---
description: Global requirements for desktop application stability, crash logging, WinUI 3/XAML safety, and robust smoke testing.
trigger: always_on
---

# Directrices Globales de Estabilidad en Aplicaciones Desktop (WinUI 3 / WPF / MAUI / Desktop)

## 1. Manejo Global de Excepciones y Logging de Arranque
En cualquier aplicación de escritorio, es OBLIGATORIO configurar desde el inicio del proyecto:
1. **Manejadores Globales de Excepciones**:
   - `App.UnhandledException` (excepciones de UI y XAML parser)
   - `AppDomain.CurrentDomain.UnhandledException` (excepciones de hilos de fondo / CLR)
   - `TaskScheduler.UnobservedTaskException` (tareas asíncronas no observadas)
2. **Volcado a Disco Inmediato**: Todo crash debe serializar la excepción, mensaje, inner exception y stack trace completo a un archivo local persistente (ej. `%LOCALAPPDATA%\<AppName>\Logs\crash.log` y `startup_diagnostic.log`). Esto garantiza que los fallos en arranque no queden en silencio.

## 2. Invariantes de Seguridad en XAML y Concurrencia (WinUI 3)
1. **TargetType Mismatch**: NUNCA aplicar estilos específicos de `Button` (como `AccentButtonStyle`) a controles compuestos o derivados de `Control` (`SplitButton`, `DropDownButton`, `ToggleButton`) a menos que el estilo esté explícitamente definido para ese `TargetType`. En WinUI 3, esto genera `STATUS_STOWED_EXCEPTION` (`0xc000027b`).
2. **Recursos Inexistentes**: Verificar que cualquier clave referenciada en `{StaticResource ...}` exista físicamente en `XamlControlsResources` de la versión del SDK en uso o en los diccionarios locales (`MergedDictionaries`).
3. **PasswordBox Binding**: `PasswordBox.Password` NO es una `DependencyProperty` y no admite Data Binding `x:Bind Mode=TwoWay`. Usar `TextBox` o capturar el evento `PasswordChanged` en code-behind.
4. **ComboBox Dual Binding**: Al usar `ComboBox` con `IsEditable="True"`, no enlazar simultáneamente `SelectedItem` y `Text` en modo `TwoWay` a la misma propiedad para evitar que `SelectedItem = null` borre el texto del usuario.
5. **DispatcherQueue Ordering**: En WinUI 3, obtener e inicializar `App.DispatcherQueue` ANTES de instanciar la ventana principal (`new MainWindow()`) para evitar referencias nulas durante la resolución temprana de ViewModels en la navegación inicial.
6. **Cross-Thread PropertyChanged en Enlaces x:Bind**: En WinUI 3, `{x:Bind}` no auto-sincroniza eventos de `INotifyPropertyChanged` procedentes de hilos secundarios. Toda mutación de propiedades en modelos o ViewModels enlazados a la UI debe ejecutarse en el hilo de UI (`DispatcherQueue.TryEnqueue`) para evitar `COMException (0x8001010E): RPC_E_WRONG_THREAD`.
7. **Ciclo de Vida de Eventos entre Page y ViewModels Singleton**: Si una `Page` XAML se navega dinámicamente y se enlaza a un ViewModel `Singleton`, NUNCA suscribirse con lambdas anónimas en el constructor. Debe suscribirse en `Loaded` y desuscribirse en `Unloaded` usando métodos con nombre y resguardo `if (!this.IsLoaded) return;`. De lo contrario, instancias fantasma permanecerán vivas e invocarán múltiples `ContentDialog.ShowAsync()` concurrentes sobre el mismo `XamlRoot`, provocando crashes fatales.
8. **Despacho Asíncrono Esperable (`RunOnUIThreadAsync`)**: Cuando un proceso de fondo o tarea programada invoca operaciones en un ViewModel que modifique colecciones u observables, debe utilizar un patrón esperable (`TaskCompletionSource` sobre `DispatcherQueue`) para garantizar que la UI complete la actualización de estado antes de que el proceso secundario continúe.

## 3. Protocolo de Smoke Testing Automatizado
Al realizar pruebas de humo (*smoke tests*) sobre binarios compilados:
1. **Ventana de Tiempo Mínima**: Mantener el proceso en ejecución durante al menos 3 a 5 segundos para permitir que la navegación inicial e inflación de páginas XAML completen.
2. **Validación de Logs y Visor de Eventos**:
   - Verificar que el proceso continúe vivo y respondiendo (`Responding: True`).
   - Comprobar que el archivo `crash.log` no exista o esté libre de excepciones.
   - En caso de cierre repentino, consultar inmediatamente el Visor de Eventos de Windows:
     ```powershell
     Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-5); Level=1,2}
     ```
