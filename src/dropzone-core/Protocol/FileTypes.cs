using System;
using System.Collections.Generic;
using System.IO;

namespace Dropzone.Core.Protocol;

/// <summary>
/// Maps a file name to the MIME type sent as <c>FileDto.FileType</c>. Extensions match case-insensitively.
/// </summary>
public static class FileTypes
{
    private const string FallbackMimeType = "application/octet-stream";

    private static readonly Dictionary<string, string> MimeTypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".webm"] = "video/webm",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".log"] = "text/plain",
        [".pdf"] = "application/pdf",
        [".apk"] = "application/vnd.android.package-archive",
        [".zip"] = "application/zip",
    };

    /// <summary>
    /// Returns the MIME type for the extension of <paramref name="path"/>, for example "image/jpeg" for "a.JPG".
    /// Returns "application/octet-stream" when there is no extension or the extension is not in the table.
    /// </summary>
    public static string FromPath(string path)
    {
        string? extension = Path.GetExtension(path);
        if (extension is not null && MimeTypeByExtension.TryGetValue(extension, out string? mimeType))
        {
            return mimeType;
        }

        return FallbackMimeType;
    }
}
