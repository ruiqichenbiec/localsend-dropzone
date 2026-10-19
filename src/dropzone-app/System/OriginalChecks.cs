using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Dropzone.App.SystemIntegration;

/// <summary>Read-only checks on the official LocalSend app.</summary>
public static class OriginalChecks
{
    private const string ProcessName = "localsend_app";
    private const string ExeName = "localsend_app.exe";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>True when a <c>localsend_app</c> process is running.</summary>
    public static bool IsRunning()
    {
        Process[] processes = Process.GetProcessesByName(ProcessName);
        foreach (Process process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    /// <summary>True when a value under <c>HKCU\…\Run</c> points at <c>localsend_app.exe</c>.</summary>
    public static bool StartsWithWindows()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key is null)
        {
            return false;
        }

        foreach (string name in key.GetValueNames())
        {
            if (key.GetValue(name) is string command && command.Contains(ExeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
