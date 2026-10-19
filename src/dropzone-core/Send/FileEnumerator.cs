using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dropzone.Core.Send;

/// <summary>Turns dropped paths into the list of files to send.</summary>
public static class FileEnumerator
{
    /// <summary>
    /// A file becomes its file name. A folder becomes every file inside it, recursively, named
    /// <c>&lt;folder name&gt;/&lt;sub&gt;/&lt;file&gt;</c> with <c>/</c> separators (findings F6), sorted ordinally. Paths that
    /// do not exist, and files or folders that cannot be enumerated, are skipped. Reparse points (junctions,
    /// symbolic links) are not followed, so a link cycle cannot make the walk endless. A file reached twice (same
    /// path dropped twice, or a file and its folder) is listed once, under the first name it was reached by.
    /// </summary>
    public static IReadOnlyList<(string FullPath, string RelativeName)> Expand(IEnumerable<string> paths)
    {
        var result = new List<(string FullPath, string RelativeName)>();
        // Windows paths are case-insensitive: the same file dropped twice, or a file plus its folder, is sent once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(full))
            {
                if (seen.Add(full))
                {
                    result.Add((full, Path.GetFileName(full)));
                }
            }
            else if (Directory.Exists(full))
            {
                result.AddRange(ExpandFolder(full).Where(f => seen.Add(f.FullPath)));
            }
        }

        return result;
    }

    private static IEnumerable<(string FullPath, string RelativeName)> ExpandFolder(string folder)
    {
        string root = Path.TrimEndingDirectorySeparator(folder);
        string name = Path.GetFileName(root);
        if (string.IsNullOrEmpty(name))
        {
            // A drive root such as "D:\": use the drive letter.
            name = root.TrimEnd(':', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var files = new List<(string FullPath, string RelativeName)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", options))
            {
                string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                files.Add((file, name + "/" + relative));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The folder itself became unreadable or vanished: send what was found so far.
        }

        return files.OrderBy(f => f.RelativeName, StringComparer.Ordinal);
    }
}
