using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GoogleDriveWorkSync.Helpers;

/// <summary>
/// Helper class for managing application autostart on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutostartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "GoogleDriveWorkSync";

    public const string AutostartArgument = "--autostart";

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildAutostartCommandLine(string executablePath) => $"\"{executablePath}\" {AutostartArgument}";

    public static bool HasAutostartArgument(IEnumerable<string>? args) =>
        args?.Any(a => string.Equals(a, AutostartArgument, StringComparison.OrdinalIgnoreCase)) ?? false;

    public static void EnsureAutostartSynced()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            var existingValue = key?.GetValue(AppName) as string;
            if (!string.IsNullOrEmpty(existingValue))
            {
                var expectedValue = BuildAutostartCommandLine(processPath);
                if (!string.Equals(existingValue.Trim(), expectedValue.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    key?.SetValue(AppName, expectedValue);
                }
            }
        }
        catch
        {
            // Ignore registry errors
        }
    }

    public static void EnableAutostart()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.SetValue(AppName, BuildAutostartCommandLine(processPath));
        }
        catch
        {
            // Ignore registry errors
        }
    }

    public static void DisableAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(AppName, false);
        }
        catch
        {
            // Ignore registry errors
        }
    }

    public static void SetAutostart(bool enabled)
    {
        if (enabled) EnableAutostart();
        else DisableAutostart();
    }
}
