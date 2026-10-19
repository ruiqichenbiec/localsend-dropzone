using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using Dropzone.App.SystemIntegration;
using Microsoft.Win32;

namespace Dropzone.App.Drag;

/// <summary>Hands dropped files to the official LocalSend app.</summary>
public static class OriginalLauncher
{
    private const string ExeName = "localsend_app.exe";
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string DefaultExePath = @"C:\Program Files\LocalSend\localsend_app.exe";

    /// <summary>
    /// Starts <c>localsend_app.exe</c> with every path as its own quoted argument; a running official app takes them
    /// over and opens its Send page with them (findings F5). When that cannot be started, the exe is started without
    /// arguments and <paramref name="notify"/> says to drag the files in; when the exe is not found, it says so.
    /// </summary>
    /// <param name="notify">Shows a one-line notification; safe from any thread.</param>
    public static void OpenWithFiles(IReadOnlyList<string> paths, Action<string> notify)
    {
        string? exe = FindExe();
        if (exe is null)
        {
            AppLog.Warn("The official LocalSend app was not found.");
            notify("没有找到 LocalSend");
            return;
        }

        if (TryStart(exe, QuoteAll(paths)))
        {
            AppLog.Info($"Handed {paths.Count} path(s) to {exe}.");
            return;
        }

        if (TryStart(exe, string.Empty))
        {
            notify("已打开 LocalSend，请把文件拖进窗口");
        }
        else
        {
            notify("没有找到 LocalSend");
        }
    }

    /// <summary>
    /// <c>InstallLocation</c> of LocalSend's uninstall entry (machine-wide, 32-bit view, then per-user) plus
    /// <c>localsend_app.exe</c>, else the default install path; null when neither exists.
    /// </summary>
    private static string? FindExe()
    {
        RegistryKey[] roots =
        [
            Registry.LocalMachine,
            Registry.LocalMachine,
            Registry.CurrentUser,
        ];
        string[] keyPaths =
        [
            UninstallKeyPath,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            UninstallKeyPath,
        ];

        for (int i = 0; i < roots.Length; i++)
        {
            try
            {
                if (FindInUninstallKey(roots[i], keyPaths[i]) is { } found)
                {
                    return found;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                AppLog.Warn($"Reading {keyPaths[i]} failed.", error);
            }
        }

        return File.Exists(DefaultExePath) ? DefaultExePath : null;
    }

    private static string? FindInUninstallKey(RegistryKey root, string keyPath)
    {
        using RegistryKey? uninstall = root.OpenSubKey(keyPath, writable: false);
        if (uninstall is null)
        {
            return null;
        }

        foreach (string name in uninstall.GetSubKeyNames())
        {
            using RegistryKey? entry = uninstall.OpenSubKey(name, writable: false);
            // The installer names it e.g. "LocalSend 版本 1.18.2".
            if (entry?.GetValue("DisplayName") is not string displayName
                || !displayName.StartsWith("LocalSend", StringComparison.OrdinalIgnoreCase)
                || displayName.Contains("Dropzone", StringComparison.OrdinalIgnoreCase)
                || entry.GetValue("InstallLocation") is not string location
                || string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            string exe = Path.Combine(location.Trim().Trim('"'), ExeName);
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    /// <summary>Each path in double quotes; a trailing backslash (a drive root) is doubled so it does not escape the quote.</summary>
    private static string QuoteAll(IReadOnlyList<string> paths)
    {
        StringBuilder arguments = new();
        foreach (string path in paths.Where(p => !string.IsNullOrEmpty(p)))
        {
            if (arguments.Length > 0)
            {
                arguments.Append(' ');
            }

            arguments.Append('"').Append(path);
            if (path.EndsWith('\\'))
            {
                arguments.Append('\\');
            }

            arguments.Append('"');
        }

        return arguments.ToString();
    }

    private static bool TryStart(string exe, string arguments)
    {
        try
        {
            ProcessStartInfo start = new(exe, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            };
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn($"Starting {exe} failed.", error);
            return false;
        }
    }
}
