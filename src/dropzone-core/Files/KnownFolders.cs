using System;
using System.Runtime.InteropServices;

namespace Dropzone.Core.Files;

/// <summary>
/// Looks up Windows known folders at run time instead of hard-coding paths.
/// </summary>
public static class KnownFolders
{
    /// <summary>FOLDERID_Downloads.</summary>
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Returns the current user's Downloads folder (FOLDERID_Downloads).</summary>
    public static string GetDownloads()
    {
        int hresult = SHGetKnownFolderPath(in DownloadsFolderId, 0, IntPtr.Zero, out IntPtr pathPointer);
        try
        {
            Marshal.ThrowExceptionForHR(hresult);
            return Marshal.PtrToStringUni(pathPointer)
                ?? throw new InvalidOperationException("SHGetKnownFolderPath returned no path.");
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
