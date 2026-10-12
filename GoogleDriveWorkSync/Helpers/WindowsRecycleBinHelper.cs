using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GoogleDriveWorkSync.Helpers;

/// <summary>
/// Provides safe file deletion by moving items to the Windows Recycle Bin
/// via Win32 Shell API (SHFileOperation with FOF_ALLOWUNDO).
/// </summary>
public static class WindowsRecycleBinHelper
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// Safely sends a file to the Windows Recycle Bin.
    /// Returns true if the file was recycled or already deleted; false on error.
    /// </summary>
    public static bool SendToRecycleBin(string filePath, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            errorMessage = "Path cannot be null or empty.";
            return false;
        }

        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            // Already gone - treat as success with no error
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                else if (Directory.Exists(filePath)) Directory.Delete(filePath, true);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);

            // Win32 SHFileOperation requires double null-terminated string
            var fileOp = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = fullPath + "\0\0",
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null
            };

            int result = SHFileOperation(ref fileOp);

            if (result != 0 || fileOp.fAnyOperationsAborted)
            {
                int lastWin32Error = Marshal.GetLastWin32Error();
                errorMessage = $"Shell operation failed with error code 0x{result:X} (Win32: {lastWin32Error}).";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
