using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Dropzone.Core.Original;

/// <summary>What the seen-history file looked like the last time it was read.</summary>
public enum SeenHistoryLoadState
{
    /// <summary>The file does not exist. This is a first run.</summary>
    Missing,

    /// <summary>The file is a JSON array of strings, possibly empty.</summary>
    Ok,

    /// <summary>The file exists but is not a JSON array of strings. It is handled like a first run.</summary>
    Corrupt,

    /// <summary>
    /// The file could not be read, for example because it is locked. Its contents are unknown, so the store is
    /// read again later.
    /// </summary>
    Unavailable,
}

/// <summary>
/// The ids of receive-history entries already handled, kept as a JSON array of strings. <see cref="Save"/> keeps
/// only the most recent 500 ids, in the order they were added. Not thread-safe: <see cref="HistoryTimeFixer"/>
/// serializes access.
/// </summary>
public sealed class SeenHistoryStore
{
    private const int MaxIds = 500;

    private readonly string _path;
    private readonly List<string> _order = new();
    private readonly HashSet<string> _ids = new();

    public SeenHistoryStore(string path)
    {
        _path = path;
        Read();
    }

    /// <summary>How the file looked when it was last read.</summary>
    public SeenHistoryLoadState LoadState { get; private set; }

    /// <summary>True when no id is held. This is also the case while <see cref="LoadState"/> is Unavailable.</summary>
    public bool IsEmpty => _order.Count == 0;

    public bool Contains(string id) => _ids.Contains(id);

    /// <summary>Marks <paramref name="id"/> as handled. An id that is already present is left where it is.</summary>
    public void Add(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (_ids.Add(id))
        {
            _order.Add(id);
        }
    }

    /// <summary>
    /// Reads the file again unless it is already Ok, so that the caller's decision follows the file as it is now.
    /// </summary>
    public void EnsureLoaded()
    {
        if (LoadState != SeenHistoryLoadState.Ok)
        {
            Read();
        }
    }

    /// <summary>
    /// Writes the ids through a temp file, dropping the oldest ones beyond 500. Refused while the store is
    /// Unavailable, because the write would replace a file whose contents were never read.
    /// </summary>
    public void Save()
    {
        if (LoadState == SeenHistoryLoadState.Unavailable)
        {
            throw new InvalidOperationException("The seen-history file could not be read, so it must not be replaced.");
        }

        int excess = _order.Count - MaxIds;
        if (excess > 0)
        {
            foreach (string id in _order.GetRange(0, excess))
            {
                _ids.Remove(id);
            }

            _order.RemoveRange(0, excess);
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_order));
        File.Move(temp, _path, overwrite: true);
        LoadState = SeenHistoryLoadState.Ok;
    }

    private void Read()
    {
        _order.Clear();
        _ids.Clear();

        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            LoadState = SeenHistoryLoadState.Missing;
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoadState = SeenHistoryLoadState.Unavailable;
            return;
        }

        List<string> ids = new();
        if (!TryParseIds(text, ids))
        {
            LoadState = SeenHistoryLoadState.Corrupt;
            return;
        }

        foreach (string id in ids)
        {
            _order.Add(id);
            _ids.Add(id);
        }

        LoadState = SeenHistoryLoadState.Ok;
    }

    /// <summary>
    /// True when the text is a JSON array of strings. Empty strings are dropped, and a repeated id is kept once.
    /// </summary>
    private static bool TryParseIds(string text, List<string> ids)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            HashSet<string> distinct = new();
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string? id = item.GetString();
                if (!string.IsNullOrEmpty(id) && distinct.Add(id))
                {
                    ids.Add(id);
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
