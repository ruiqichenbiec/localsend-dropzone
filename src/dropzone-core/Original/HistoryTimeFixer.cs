using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Dropzone.Core.Original;

/// <summary>
/// Sets the modification time of each file that the official LocalSend app has just received to the moment the
/// receive finished (the history entry's <c>timestamp</c>). Only <c>LastWriteTimeUtc</c> is changed; the official
/// app's <c>settings.json</c> is only read.
/// </summary>
public sealed class HistoryTimeFixer(string settingsPath, SeenHistoryStore store) : IDisposable
{
    private const int MaxAttemptsPerEntry = 10;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(10);

    /// <summary>A file last written more than this after its entry's time was edited after it arrived.</summary>
    private static readonly TimeSpan EditedAfterReceiptMargin = TimeSpan.FromSeconds(5);

    // NTFS cannot store an earlier time.
    private static readonly DateTime EarliestFileTimeUtc = DateTime.FromFileTimeUtc(0);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _failedAttempts = new();
    private bool _passScheduled;
    private bool _started;
    private bool _disposed;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private Timer? _fallbackTimer;

    /// <summary>Raised after a file's modification time has been set. The argument is the file's full path.</summary>
    public event Action<string>? FileFixed;

    /// <summary>
    /// Checks the history once and sets the modification time of each new file entry. Messages, entries without a
    /// fully qualified path, entries whose file is missing, entries whose file was written more than 5 seconds after
    /// the entry's time (edited since it arrived), and entries with a time NTFS cannot store are marked as handled
    /// without a change. Thread-safe.
    /// </summary>
    /// <returns>The number of files whose modification time was changed.</returns>
    public int ProcessOnce()
    {
        List<string> fixedPaths;
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            fixedPaths = Pass();
        }

        foreach (string path in fixedPaths)
        {
            FileFixed?.Invoke(path);
        }

        return fixedPaths.Count;
    }

    /// <summary>
    /// Starts watching <c>settings.json</c>. A change schedules one pass 500 ms later, and a pass also runs every
    /// 10 seconds. One pass runs at once: on a first run it marks the existing history as handled.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started)
            {
                return;
            }

            _started = true;
            string? directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
            if (directory is not null && Directory.Exists(directory))
            {
                _watcher = new FileSystemWatcher(directory, Path.GetFileName(settingsPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                _watcher.Changed += OnSettingsChanged;
                _watcher.Created += OnSettingsChanged;
                _watcher.Renamed += OnSettingsChanged;
                _watcher.EnableRaisingEvents = true;
            }

            _debounceTimer = new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);
            _fallbackTimer = new Timer(_ => RunPass(), null, FallbackInterval, FallbackInterval);
        }

        RunPass();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _fallbackTimer?.Dispose();
    }

    private void OnSettingsChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            // Only the first change of a burst arms the timer, so a steady stream of changes cannot postpone the pass.
            if (_disposed || _passScheduled)
            {
                return;
            }

            _passScheduled = true;
            _debounceTimer?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed()
    {
        lock (_gate)
        {
            // Cleared before the pass runs, so a change that arrives during the pass schedules another pass.
            _passScheduled = false;
        }

        RunPass();
    }

    /// <summary>
    /// Runs one pass from a timer or from <see cref="Start"/>. Nothing may escape from here, because an exception on
    /// a timer thread would end the process. A pass that failed is retried by the next change or tick.
    /// </summary>
    private void RunPass()
    {
        try
        {
            ProcessOnce();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>One pass. The caller holds <see cref="_gate"/>. Returns the full paths of the files it changed.</summary>
    private List<string> Pass()
    {
        List<string> changed = new();

        // Until the seen store has loaded, there is no baseline and no processing: nothing is marked and no file is touched.
        store.EnsureLoaded();
        if (store.LoadState == SeenHistoryLoadState.Unavailable)
        {
            return changed;
        }

        OriginalSettings? settings = OriginalSettingsReader.TryRead(settingsPath);
        if (settings is null)
        {
            // Unreadable right now, for example while the official app is writing it. Nothing is decided.
            return changed;
        }

        if (store.LoadState is SeenHistoryLoadState.Missing or SeenHistoryLoadState.Corrupt)
        {
            // A first run for this store: the history that already exists is marked as handled, and no file is touched.
            // The store is saved even when the history is empty, so the next pass reads it as Ok rather than as a first run.
            foreach (HistoryEntry entry in settings.ReceiveHistory)
            {
                if (!string.IsNullOrEmpty(entry.Id))
                {
                    store.Add(entry.Id);
                }
            }

            store.Save();
            return changed;
        }

        bool storeChanged = false;
        void MarkSeen(string id)
        {
            store.Add(id);
            storeChanged = true;
        }

        foreach (HistoryEntry entry in settings.ReceiveHistory)
        {
            if (string.IsNullOrEmpty(entry.Id) || store.Contains(entry.Id))
            {
                continue;
            }

            if (!CanBeFixed(entry))
            {
                MarkSeen(entry.Id);
                continue;
            }

            string fullPath = Path.GetFullPath(entry.Path);
            try
            {
                if (File.GetLastWriteTimeUtc(fullPath) > entry.Timestamp.UtcDateTime + EditedAfterReceiptMargin)
                {
                    // Written well after the receive finished: the user has changed the file since. Leave it alone.
                    _failedAttempts.Remove(entry.Id);
                    MarkSeen(entry.Id);
                    continue;
                }

                File.SetLastWriteTimeUtc(fullPath, entry.Timestamp.UtcDateTime);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked or not writable right now. Try again on a later pass, and give up after the tenth failure.
                int failures = _failedAttempts.GetValueOrDefault(entry.Id) + 1;
                if (failures < MaxAttemptsPerEntry)
                {
                    _failedAttempts[entry.Id] = failures;
                    continue;
                }

                _failedAttempts.Remove(entry.Id);
                MarkSeen(entry.Id);
                continue;
            }

            _failedAttempts.Remove(entry.Id);
            MarkSeen(entry.Id);
            changed.Add(fullPath);
        }

        if (storeChanged)
        {
            store.Save();
        }

        return changed;
    }

    /// <summary>
    /// True when the entry names a file that can be given the completion time: not a message, a fully qualified path
    /// to a file that still exists, and a time that NTFS can store.
    /// </summary>
    private static bool CanBeFixed(HistoryEntry entry) =>
        !entry.IsMessage
        && !string.IsNullOrEmpty(entry.Path)
        && Path.IsPathFullyQualified(entry.Path)
        && File.Exists(entry.Path)
        && entry.Timestamp.UtcDateTime >= EarliestFileTimeUtc;
}
