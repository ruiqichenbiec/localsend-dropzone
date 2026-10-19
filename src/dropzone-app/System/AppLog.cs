using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Dropzone.Core.Logging;

namespace Dropzone.App.SystemIntegration;

/// <summary>
/// Plain-text log at <c>%LOCALAPPDATA%\localsend-dropzone\logs\app.log</c>. The file is rolled over once a day
/// (the previous day's becomes <c>app-yyyy-MM-dd.log</c>) and 14 days are kept. Never pass a password or token in:
/// as a second line of defence, <c>pin=</c> and <c>token=</c> query values are masked. Line breaks and other control
/// characters in a message are escaped (<see cref="LogText.SingleLine"/>).
/// </summary>
public static class AppLog
{
    private const int DaysKept = 14;

    private static readonly object Gate = new();
    private static readonly Regex SecretQuery = new(
        @"(?<key>[?&](pin|token)=)[^&\s""']*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "localsend-dropzone", "logs");

    private static string CurrentPath => Path.Combine(LogDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? error = null) => Write("WARN", message, error);

    public static void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    private static void Write(string level, string message, Exception? error)
    {
        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        // One message, one line: names that peers choose (device and sender aliases, file names) cannot forge entries.
        string line = $"{time} {level} {LogText.SingleLine(message)}";
        if (error is not null)
        {
            line += Environment.NewLine + error;
        }

        line = SecretQuery.Replace(line, "${key}***");
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                RollIfNeeded();
                File.AppendAllText(CurrentPath, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the program down.
            }
        }
    }

    /// <summary>Moves a previous day's app.log aside and deletes rolled files older than 14 days. Caller holds the lock.</summary>
    private static void RollIfNeeded()
    {
        string current = CurrentPath;
        if (!File.Exists(current))
        {
            return;
        }

        DateTime day = File.GetLastWriteTime(current).Date;
        if (day >= DateTime.Today)
        {
            return;
        }

        string rolled = Path.Combine(LogDirectory, $"app-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");
        if (File.Exists(rolled))
        {
            File.AppendAllText(rolled, File.ReadAllText(current));
            File.Delete(current);
        }
        else
        {
            File.Move(current, rolled);
        }

        foreach (string old in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
        {
            if (File.GetLastWriteTime(old) < DateTime.Today.AddDays(-DaysKept))
            {
                File.Delete(old);
            }
        }
    }
}
