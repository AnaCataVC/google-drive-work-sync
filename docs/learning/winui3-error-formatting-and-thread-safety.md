# Learning: WinUI 3 Single-Line Truncation and Thread-Safe Observable Collections

## Context
In `GoogleDriveWorkSync`, when attempting to synchronize large folder trees (~6,300 files) or during background API failures, the UI displayed an uninformative message:
```text
Error al sincronizar:
```
with an empty body, leaving the user without any actionable error details or visibility into the underlying cause.

---

## Root Causes Identified

1. **XAML Single-Line TextBlock Truncation (`\r\n` Cutoff):**
   - WinUI 3 `TextBlock` elements without `TextWrapping="Wrap"` or explicit height expansion render only the first visual line.
   - When an exception message contains leading newlines or is formatted across multiple lines (e.g. from nested `AggregateException`, WinRT COM errors, or multi-line error strings), the `TextBlock` terminates rendering at the first `\r\n`.
   - Because the initial portion (`"Error al sincronizar: "`) fits within the container width, no ellipsis (`...`) was rendered, giving the visual illusion of a completely blank error.

2. **WinUI 3 Thread Affinity Violations (`RPC_E_WRONG_THREAD` / `0x8001010E`):**
   - Background service tasks running on `Task.Run` (such as `IDriveSyncService.RunSyncAsync`) trigger events (`SyncProgressChanged`, `SyncCompleted`, `SyncErrorsChanged`).
   - Modifying `ObservableCollection` items (`Clear()`, `Add()`) or UI-bound properties directly from a background thread raises `INotifyCollectionChanged` and `PropertyChanged` outside the dispatcher, throwing `COMException (0x8001010E)` in WinUI 3.

3. **Empty / Generic Exception Messages in Wrapped Exceptions:**
   - In .NET, wrapper exceptions (`AggregateException`, `TargetInvocationException`) often have top-level messages like `"One or more errors occurred."` while the true message resides in `InnerException`.
   - Unhandled COM/WinRT stowed exceptions may have empty string messages, which when interpolated as `$"Error: {ex.Message}"` evaluates to `"Error: "`.

4. **UI Freeze from Massive Individual Collection Notifications:**
   - Calling `ObservableCollection.Add()` in a tight loop for 6,000+ items triggers thousands of individual layout passes on the UI thread, causing perceptible freezes.

---

## Architectural Patterns & Solutions Implemented

### 1. Robust Exception Normalization (`GetDisplayErrorMessage`)
A centralized exception sanitizer inspects the inner exception hierarchy, falls back to the exception type name if messages are empty or whitespace, and normalizes `\r\n` and `\n` to spaces:
```csharp
public static string GetDisplayErrorMessage(Exception ex, string fallback = "Error inesperado durante la operación.")
{
    if (ex == null) return fallback;

    var current = ex;
    string candidate = string.Empty;

    while (current != null)
    {
        var msg = current.Message?.Trim();
        if (!string.IsNullOrWhiteSpace(msg) &&
            !msg.Equals("Exception of type 'System.Exception' was thrown.", StringComparison.OrdinalIgnoreCase) &&
            !msg.Equals("One or more errors occurred.", StringComparison.OrdinalIgnoreCase))
        {
            candidate = msg;
        }
        current = current.InnerException;
    }

    if (string.IsNullOrWhiteSpace(candidate))
    {
        candidate = !string.IsNullOrWhiteSpace(ex.Message)
            ? ex.Message.Trim()
            : ex.GetType().Name;
    }

    candidate = candidate.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
    while (candidate.Contains("  "))
    {
        candidate = candidate.Replace("  ", " ");
    }

    return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
}
```

### 2. Standalone Thread Dispatcher & Crash Logging (`DiagnosticLogger`)
Decoupled UI thread checking and dispatching into a shared helper that works seamlessly in both desktop WinUI 3 applications and plain `net9.0` unit test suites:
```csharp
public static class DiagnosticLogger
{
    public static Func<bool>? IsUIThreadCheck { get; set; }
    public static Action<Action>? UIThreadDispatcher { get; set; }

    public static void RunOnUIThread(Action action)
    {
        if (UIThreadDispatcher != null && (IsUIThreadCheck == null || !IsUIThreadCheck()))
        {
            UIThreadDispatcher(action);
        }
        else
        {
            action();
        }
    }
}
```

### 3. Visual Tooltip Affordance (`ToolTipService.ToolTip`)
Added `ToolTipService.ToolTip="{x:Bind ViewModel.DriveSyncDetailText, Mode=OneWay}"` on the status `TextBlock` so users can hover to inspect lengthy or trimmed messages.

### 4. UI Item Throttling for Massive Datasets
When populating the preview list of out-of-sync files, only the first 200 items are added to `ObservableCollection<OutOfSyncFile>`, while keeping `OutOfSyncCount` fully accurate (e.g. 6,295), preventing thread freeze while maintaining live counts.
