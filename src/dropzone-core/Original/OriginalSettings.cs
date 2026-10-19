using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dropzone.Core.Original;

/// <summary>One receive-history entry of the official app. <c>Path</c> is empty for text messages.</summary>
public record HistoryEntry(
    string Id,
    string FileName,
    string Path,
    bool IsMessage,
    long FileSize,
    string SenderAlias,
    DateTimeOffset Timestamp);

/// <summary>
/// The parts of the official app's <c>settings.json</c> that Dropzone uses. <c>FileLastWriteUtc</c> is the
/// file's last-write time, taken while the file is being read. <c>SaveToHistory</c> is the official app's
/// "save to history" switch; while it is off, no history entries are written and no modification time is fixed.
/// </summary>
public record OriginalSettings(
    string Alias,
    int Port,
    IReadOnlyList<HistoryEntry> ReceiveHistory,
    DateTime FileLastWriteUtc,
    bool SaveToHistory = true);

/// <summary>Reads the official LocalSend app's <c>settings.json</c> without changing it.</summary>
public static class OriginalSettingsReader
{
    private const string AliasKey = "flutter.ls_alias";
    private const string PortKey = "flutter.ls_port";
    private const string HistoryKey = "flutter.ls_receive_history";

    // persistence_provider.dart (v1.18.2): _saveToHistory = 'ls_save_to_history', default true.
    private const string SaveToHistoryKey = "flutter.ls_save_to_history";
    private const int MaxFileBytes = 64 * 1024 * 1024;
    private const int DefaultPort = 53317;

    /// <summary>%APPDATA%\LocalSend\settings.json.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalSend", "settings.json");

    /// <summary>
    /// Reads the settings at <paramref name="path"/>. Returns null when the file cannot be opened or read, is not
    /// JSON, has no non-blank alias, or has a port that is present but is not an integer from 1 to 65535.
    /// A missing port means 53317, a missing history key means no history, and a save-to-history flag that is missing
    /// or not a boolean means true (the official app's default). Malformed history entries are skipped. The file is
    /// opened shared so a running official app is not blocked. Only the keys above are read;
    /// <c>flutter.ls_security_context</c> is never looked at, and the bytes read (which contain it) are zeroed as soon
    /// as parsing is done.
    /// </summary>
    public static OriginalSettings? TryRead(string path)
    {
        try
        {
            byte[] content;
            DateTime lastWriteUtc;
            using (FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
                long length = file.Length;
                if (length > MaxFileBytes)
                {
                    return null;
                }

                // Exactly one buffer of the file's size, so no copy of the private key is left behind by a growing
                // stream. A file that shrinks meanwhile ends the read early (EndOfStreamException); one that grows is
                // cut off, fails to parse, and is read again next time.
                content = new byte[length];
                try
                {
                    file.ReadExactly(content);
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(content);
                    throw;
                }
            }

            return ParseAndClear(content, lastWriteUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses <paramref name="content"/> (see <see cref="TryRead"/>) and then zeroes it, whether or not it parsed:
    /// the official app's settings hold its private key, which must not linger in memory.
    /// </summary>
    internal static OriginalSettings? ParseAndClear(byte[] content, DateTime lastWriteUtc)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            return Parse(document.RootElement, lastWriteUtc);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            // After the document is disposed: JsonDocument parses this array in place rather than copying it.
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static OriginalSettings? Parse(JsonElement root, DateTime lastWriteUtc)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? alias = GetString(root, AliasKey);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        int port = DefaultPort;
        if (root.TryGetProperty(PortKey, out JsonElement portElement))
        {
            if (portElement.ValueKind != JsonValueKind.Number
                || !portElement.TryGetInt32(out port)
                || port is < 1 or > 65535)
            {
                return null;
            }
        }

        List<HistoryEntry> history = [];
        if (root.TryGetProperty(HistoryKey, out JsonElement historyElement)
            && historyElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in historyElement.EnumerateArray())
            {
                HistoryEntry? entry = ParseHistoryEntry(item);
                if (entry is not null)
                {
                    history.Add(entry);
                }
            }
        }

        bool saveToHistory = GetBool(root, SaveToHistoryKey) ?? true;
        return new OriginalSettings(alias, port, history, lastWriteUtc, saveToHistory);
    }

    /// <summary>Parses one history item, which is a string holding a JSON object. Anything else gives null.</summary>
    private static HistoryEntry? ParseHistoryEntry(JsonElement item)
    {
        string? json = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        if (json is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement entry = document.RootElement;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? id = GetString(entry, "id");
            string? fileName = GetString(entry, "fileName");
            string? senderAlias = GetString(entry, "senderAlias");
            bool? isMessage = GetBool(entry, "isMessage");
            long? fileSize = GetInt64(entry, "fileSize");
            DateTimeOffset? timestamp = GetTimestamp(entry, "timestamp");
            if (id is null || fileName is null || senderAlias is null
                || isMessage is null || fileSize is null || timestamp is null)
            {
                return null;
            }

            string path = GetString(entry, "path") ?? string.Empty;
            return new HistoryEntry(id, fileName, path, isMessage.Value, fileSize.Value, senderAlias, timestamp.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
        {
            return value.GetBoolean();
        }

        return null;
    }

    private static long? GetInt64(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number))
        {
            return number;
        }

        return null;
    }

    private static DateTimeOffset? GetTimestamp(JsonElement obj, string name)
    {
        string? text = GetString(obj, name);
        if (text is not null
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp))
        {
            return timestamp;
        }

        return null;
    }
}
