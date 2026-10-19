using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Dropzone.Core.Files;

/// <summary>
/// Keeps received file names inside the receive folder and picks names that do not collide with existing files.
/// </summary>
public static class SafePaths
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Turns a sender-supplied name into a relative path joined with '\'. Both '/' and '\' separate segments.
    /// Empty and "." segments are dropped. Invalid file-name characters become '_', trailing dots and spaces
    /// are removed, and a reserved device name gets a '_' prefix. Returns null when the name is empty or any
    /// segment is "..".
    /// </summary>
    public static string? ToSafeRelativePath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (string raw in fileName.Split('/', '\\'))
        {
            if (raw.Trim(' ', '.').Length == 0)
            {
                // Only dots and spaces: "." and blanks are dropped; "..", "..." and ". ." could climb out.
                if (raw.Count(c => c == '.') >= 2)
                {
                    return null;
                }

                continue;
            }

            segments.Add(SanitizeSegment(raw));
        }

        return segments.Count == 0 ? null : string.Join('\\', segments);
    }

    /// <summary>
    /// Returns the path of <paramref name="relativePath"/> under <paramref name="directory"/>, or the first
    /// "name (n).ext" (n from 1) that does not exist yet. Only computes the path: it creates neither the file
    /// nor any directory. <paramref name="relativePath"/> must be the output of ToSafeRelativePath: it is not
    /// re-validated here.
    /// </summary>
    public static string GetUniquePath(string directory, string relativePath)
    {
        string candidate = Path.Combine(directory, relativePath);
        if (!Exists(candidate))
        {
            return candidate;
        }

        string folder = Path.GetDirectoryName(candidate) ?? directory;
        string fileName = Path.GetFileName(candidate);

        // A leading dot belongs to the name (".gitignore" has no extension); only a later dot starts one.
        int dot = fileName.LastIndexOf('.');
        string stem = dot > 0 ? fileName[..dot] : fileName;
        string extension = dot > 0 ? fileName[dot..] : string.Empty;

        for (int n = 1; ; n++)
        {
            string numbered = stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + extension;
            string path = Path.Combine(folder, numbered);
            if (!Exists(path))
            {
                return path;
            }
        }
    }

    private static string SanitizeSegment(string segment)
    {
        char[] chars = segment.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidNameChars, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        string name = new string(chars).TrimEnd(' ', '.');

        int dot = name.IndexOf('.');
        string stem = (dot < 0 ? name : name[..dot]).Trim(' ');
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
