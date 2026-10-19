using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Dropzone.Core.Original;

namespace Dropzone.Core.Tests;

public class HistoryTimeFixerTests : IDisposable
{
    private static readonly DateTimeOffset OriginalWrite = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Completed = new(2026, 10, 18, 9, 15, 30, 250, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedLater = new(2026, 10, 19, 18, 40, 2, 123, TimeSpan.Zero);
    private static readonly FileSystemAccessRule AttributeWriteDenial = new(
        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        FileSystemRights.WriteAttributes,
        AccessControlType.Deny);

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));
    private readonly string _receivedDir;
    private readonly string _settingsPath;
    private readonly string _storePath;

    public HistoryTimeFixerTests()
    {
        _receivedDir = Path.Combine(_workDir, "Downloads");
        _settingsPath = Path.Combine(_workDir, "settings.json");
        _storePath = Path.Combine(_workDir, "seen-history.json");
        Directory.CreateDirectory(_receivedDir);
    }

    public void Dispose()
    {
        Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void ProcessOnce_FirstRun_MarksExistingWithoutTouching()
    {
        string existing = CreateReceivedFile("existing.txt");
        WriteSettings(FileEntry("existing-1", existing, Completed));
        SeenHistoryStore store = new(_storePath);
        using HistoryTimeFixer fixer = new(_settingsPath, store);

        Assert.Equal(0, fixer.ProcessOnce());

        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(existing));
        Assert.True(store.Contains("existing-1"));
    }

    [Fact]
    public void ProcessOnce_NewEntry_SetsMtimeToCompletion()
    {
        string file = CreateReceivedFile("new.txt");
        WriteSettings(FileEntry("new-1", file, Completed));
        using HistoryTimeFixer fixer = new(_settingsPath, SeededStore());
        List<string> fixedPaths = [];
        fixer.FileFixed += fixedPaths.Add;

        Assert.Equal(1, fixer.ProcessOnce());

        Assert.Equal(Completed.UtcDateTime, File.GetLastWriteTimeUtc(file));
        Assert.Equal(new[] { Path.GetFullPath(file) }, fixedPaths);
    }

    [Fact]
    public void ProcessOnce_NewEntry_KeepsCreationTime()
    {
        string file = CreateReceivedFile("created.txt");
        DateTime created = new(2020, 2, 2, 2, 2, 2, DateTimeKind.Utc);
        File.SetCreationTimeUtc(file, created);
        WriteSettings(FileEntry("created-1", file, Completed));
        using HistoryTimeFixer fixer = new(_settingsPath, SeededStore());

        Assert.Equal(1, fixer.ProcessOnce());

        Assert.Equal(created, File.GetCreationTimeUtc(file));
        Assert.Equal(Completed.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_MessageEntry_Ignored()
    {
        string file = CreateReceivedFile("not-a-message.txt");
        WriteSettings(
            new HistoryEntry("text-1", "Hello", string.Empty, true, 5, "Fixture Phone", Completed),
            new HistoryEntry("flagged-1", "not-a-message.txt", file, true, 8, "Fixture Phone", Completed),
            new HistoryEntry("nopath-1", "ghost.txt", string.Empty, false, 8, "Fixture Phone", Completed));
        SeenHistoryStore store = SeededStore();
        using HistoryTimeFixer fixer = new(_settingsPath, store);

        Assert.Equal(0, fixer.ProcessOnce());

        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(file));
        Assert.True(store.Contains("text-1"));
        Assert.True(store.Contains("flagged-1"));
        Assert.True(store.Contains("nopath-1"));
    }

    [Fact]
    public void ProcessOnce_MissingFile_MarkedSeen()
    {
        string missing = Path.Combine(_receivedDir, "gone.txt");
        WriteSettings(FileEntry("gone-1", missing, Completed));
        SeenHistoryStore store = SeededStore();
        using HistoryTimeFixer fixer = new(_settingsPath, store);

        Assert.Equal(0, fixer.ProcessOnce());
        Assert.True(store.Contains("gone-1"));

        // The entry is not revisited, so a file that later appears at that path is left alone.
        string reappeared = CreateReceivedFile("gone.txt");
        Assert.Equal(0, fixer.ProcessOnce());
        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(reappeared));
    }

    [Fact]
    public void ProcessOnce_SecondCall_DoesNotTouchAgain()
    {
        string file = CreateReceivedFile("twice.txt");
        WriteSettings(FileEntry("twice-1", file, Completed));
        using HistoryTimeFixer fixer = new(_settingsPath, SeededStore());
        Assert.Equal(1, fixer.ProcessOnce());

        DateTime userChange = new(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, userChange);

        Assert.Equal(0, fixer.ProcessOnce());
        Assert.Equal(userChange, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_SeenSurvivesNewInstance()
    {
        string first = CreateReceivedFile("first.txt");
        WriteSettings(FileEntry("first-1", first, Completed));
        using (HistoryTimeFixer fixer = new(_settingsPath, SeededStore()))
        {
            Assert.Equal(1, fixer.ProcessOnce());
        }

        DateTime userChange = new(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, userChange);
        string second = CreateReceivedFile("second.txt");
        WriteSettings(FileEntry("first-1", first, Completed), FileEntry("second-1", second, CompletedLater));

        // A new store and fixer read the saved ids. Only the entry that is new since then is fixed.
        using HistoryTimeFixer restarted = new(_settingsPath, new SeenHistoryStore(_storePath));

        Assert.Equal(1, restarted.ProcessOnce());
        Assert.Equal(userChange, File.GetLastWriteTimeUtc(first));
        Assert.Equal(CompletedLater.UtcDateTime, File.GetLastWriteTimeUtc(second));
    }

    [Fact]
    public void ProcessOnce_FileEditedAfterReceipt_LeftAloneAndMarkedSeen()
    {
        string file = CreateReceivedFile("edited.txt");
        DateTime edited = Completed.UtcDateTime.AddSeconds(6);
        File.SetLastWriteTimeUtc(file, edited);
        WriteSettings(FileEntry("edited-1", file, Completed));
        SeenHistoryStore store = SeededStore();
        using HistoryTimeFixer fixer = new(_settingsPath, store);

        Assert.Equal(0, fixer.ProcessOnce());

        Assert.Equal(edited, File.GetLastWriteTimeUtc(file));
        Assert.True(store.Contains("edited-1"));
    }

    [Fact]
    public void ProcessOnce_FileWrittenWithinFiveSecondsAfterCompletion_IsFixed()
    {
        // The official app may finish writing a moment after the time it records.
        string file = CreateReceivedFile("just-written.txt");
        File.SetLastWriteTimeUtc(file, Completed.UtcDateTime.AddSeconds(4));
        WriteSettings(FileEntry("just-1", file, Completed));
        using HistoryTimeFixer fixer = new(_settingsPath, SeededStore());

        Assert.Equal(1, fixer.ProcessOnce());

        Assert.Equal(Completed.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_UnreadableSettings_ChangesNothing()
    {
        string file = CreateReceivedFile("held.txt");
        WriteSettings(FileEntry("held-1", file, Completed));
        SeenHistoryStore store = SeededStore();
        using HistoryTimeFixer fixer = new(_settingsPath, store);

        using (new FileStream(_settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, fixer.ProcessOnce());
        }

        Assert.False(store.Contains("held-1"));
        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(file));

        Assert.Equal(1, fixer.ProcessOnce());
        Assert.Equal(Completed.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_LockedTarget_RetriedLater()
    {
        string file = CreateReceivedFile("busy.txt");
        WriteSettings(FileEntry("busy-1", file, Completed));
        SeenHistoryStore store = SeededStore();
        using HistoryTimeFixer fixer = new(_settingsPath, store);

        DenyAttributeWrites(file);
        try
        {
            Assert.Equal(0, fixer.ProcessOnce());
            Assert.False(store.Contains("busy-1"));
        }
        finally
        {
            AllowAttributeWrites(file);
        }

        Assert.Equal(1, fixer.ProcessOnce());
        Assert.Equal(Completed.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_LockedTarget_GivenUpAfterTenAttempts()
    {
        string file = CreateReceivedFile("stuck.txt");
        WriteSettings(FileEntry("stuck-1", file, Completed));
        SeenHistoryStore store = SeededStore();
        using HistoryTimeFixer fixer = new(_settingsPath, store);

        DenyAttributeWrites(file);
        try
        {
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                Assert.Equal(0, fixer.ProcessOnce());
                Assert.Equal(attempt == 10, store.Contains("stuck-1"));
            }
        }
        finally
        {
            AllowAttributeWrites(file);
        }

        Assert.Equal(0, fixer.ProcessOnce());
        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_EmptyHistoryAtFirstRun_FixesEntriesReceivedLater()
    {
        WriteSettings();
        using (HistoryTimeFixer firstRun = new(_settingsPath, new SeenHistoryStore(_storePath)))
        {
            Assert.Equal(0, firstRun.ProcessOnce());
        }

        string file = CreateReceivedFile("first.txt");
        WriteSettings(FileEntry("first-1", file, Completed));
        using HistoryTimeFixer later = new(_settingsPath, new SeenHistoryStore(_storePath));

        Assert.Equal(1, later.ProcessOnce());
        Assert.Equal(Completed.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_ExistingEmptyStore_IsNotAFirstRun()
    {
        File.WriteAllText(_storePath, "[]");
        string file = CreateReceivedFile("fresh.txt");
        WriteSettings(FileEntry("fresh-1", file, Completed));
        using HistoryTimeFixer fixer = new(_settingsPath, new SeenHistoryStore(_storePath));

        Assert.Equal(1, fixer.ProcessOnce());
        Assert.Equal(Completed.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_CorruptStore_TouchesNothingAlreadyInHistory()
    {
        File.WriteAllText(_storePath, "[\"half-written");
        string file = CreateReceivedFile("known.txt");
        WriteSettings(FileEntry("known-1", file, Completed));
        using HistoryTimeFixer fixer = new(_settingsPath, new SeenHistoryStore(_storePath));

        Assert.Equal(0, fixer.ProcessOnce());

        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(file));
        string[] saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_storePath))!;
        Assert.Equal(new[] { "known-1" }, saved);
    }

    [Fact]
    public void ProcessOnce_StoreLockedAtLoad_WaitsUntilItLoads()
    {
        SeededStore();
        string file = CreateReceivedFile("waiting.txt");
        WriteSettings(FileEntry("waiting-1", file, Completed));

        using FileStream locked = new(_storePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        SeenHistoryStore store = new(_storePath);
        using HistoryTimeFixer fixer = new(_settingsPath, store);
        Assert.Equal(SeenHistoryLoadState.Unavailable, store.LoadState);

        // Neither a first-run baseline nor processing happens while the store cannot be read.
        Assert.Equal(0, fixer.ProcessOnce());
        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(file));

        locked.Dispose();

        Assert.Equal(1, fixer.ProcessOnce());
        Assert.Equal(Completed.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_TimestampBeforeFileTimeRange_MarkedSeenWithoutTouching()
    {
        string file = CreateReceivedFile("ancient.txt");
        WriteSettings(FileEntry("ancient-1", file, DateTimeOffset.MinValue));
        SeenHistoryStore store = SeededStore();
        using HistoryTimeFixer fixer = new(_settingsPath, store);

        Assert.Equal(0, fixer.ProcessOnce());
        Assert.Equal(0, fixer.ProcessOnce());

        Assert.True(store.Contains("ancient-1"));
        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ProcessOnce_RelativePath_MarkedSeenWithoutTouching()
    {
        // A relative path resolves against the working directory, so a real file is put there for the test.
        string name = $"dropzone-relative-{Guid.NewGuid():N}.txt";
        string inWorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), name);
        File.WriteAllText(inWorkingDirectory, "relative");
        File.SetLastWriteTimeUtc(inWorkingDirectory, OriginalWrite.UtcDateTime);
        try
        {
            WriteSettings(new HistoryEntry("relative-1", name, name, false, 8, "Fixture Phone", Completed));
            SeenHistoryStore store = SeededStore();
            using HistoryTimeFixer fixer = new(_settingsPath, store);

            Assert.Equal(0, fixer.ProcessOnce());

            Assert.True(store.Contains("relative-1"));
            Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(inWorkingDirectory));
        }
        finally
        {
            File.Delete(inWorkingDirectory);
        }
    }

    [Fact]
    public async Task Start_SettingsChanged_RaisesFileFixedWithinFiveSeconds()
    {
        string existing = CreateReceivedFile("existing.txt");
        WriteSettings(FileEntry("existing-1", existing, Completed));
        using HistoryTimeFixer fixer = new(_settingsPath, new SeenHistoryStore(_storePath));
        TaskCompletionSource<string> raised = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixer.FileFixed += path => raised.TrySetResult(path);

        fixer.Start();
        string arrived = CreateReceivedFile("arrived.txt");
        WriteSettings(FileEntry("existing-1", existing, Completed), FileEntry("arrived-1", arrived, CompletedLater));

        string fixedPath = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Path.GetFullPath(arrived), fixedPath);
        Assert.Equal(CompletedLater.UtcDateTime, File.GetLastWriteTimeUtc(arrived));
        Assert.Equal(OriginalWrite.UtcDateTime, File.GetLastWriteTimeUtc(existing));
    }

    [Fact]
    public async Task Start_ContinuousChanges_FixesWithinOneAndAHalfSecondsOfTheFirstWrite()
    {
        string existing = CreateReceivedFile("existing.txt");
        WriteSettings(FileEntry("existing-1", existing, Completed));
        using HistoryTimeFixer fixer = new(_settingsPath, new SeenHistoryStore(_storePath));
        Stopwatch clock = Stopwatch.StartNew();
        TaskCompletionSource<TimeSpan> raisedAt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixer.FileFixed += _ => raisedAt.TrySetResult(clock.Elapsed);
        fixer.Start();

        string arrived = CreateReceivedFile("arrived.txt");
        HistoryEntry[] history = [FileEntry("existing-1", existing, Completed), FileEntry("arrived-1", arrived, CompletedLater)];
        TimeSpan firstWrite = clock.Elapsed;
        WriteSettingsAtomically(history);
        for (int i = 0; i < 29; i++)
        {
            await Task.Delay(100);
            WriteSettingsAtomically(history);
        }

        TimeSpan fixedAt = await raisedAt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(
            fixedAt - firstWrite < TimeSpan.FromSeconds(1.5),
            $"The file was fixed {fixedAt - firstWrite} after the first write, while the writes were still going on.");
        Assert.Equal(CompletedLater.UtcDateTime, File.GetLastWriteTimeUtc(arrived));
    }

    /// <summary>
    /// Makes <c>File.SetLastWriteTimeUtc</c> fail on the file. An exclusive handle does not do this on Windows,
    /// but a deny ACE on WriteAttributes does (UnauthorizedAccessException).
    /// </summary>
    private static void DenyAttributeWrites(string path)
    {
        FileSecurity security = new FileInfo(path).GetAccessControl();
        security.AddAccessRule(AttributeWriteDenial);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AllowAttributeWrites(string path)
    {
        FileSecurity security = new FileInfo(path).GetAccessControl();
        security.RemoveAccessRuleSpecific(AttributeWriteDenial);
        new FileInfo(path).SetAccessControl(security);
    }

    private SeenHistoryStore SeededStore()
    {
        // Any saved id makes the store Ok, so the next pass is not a first run.
        SeenHistoryStore store = new(_storePath);
        store.Add("seed");
        store.Save();
        return store;
    }

    private string CreateReceivedFile(string name)
    {
        string path = Path.Combine(_receivedDir, name);
        File.WriteAllText(path, "received");
        File.SetLastWriteTimeUtc(path, OriginalWrite.UtcDateTime);
        return path;
    }

    private static HistoryEntry FileEntry(string id, string path, DateTimeOffset timestamp) =>
        new(id, Path.GetFileName(path), path, false, 8, "Fixture Phone", timestamp);

    private void WriteSettings(params HistoryEntry[] history)
    {
        File.WriteAllText(_settingsPath, SettingsJson(history));
    }

    /// <summary>Replaces settings.json in one step, so that a reader never sees a half-written file.</summary>
    private void WriteSettingsAtomically(params HistoryEntry[] history)
    {
        string temp = _settingsPath + ".tmp";
        File.WriteAllText(temp, SettingsJson(history));
        File.Move(temp, _settingsPath, overwrite: true);
    }

    private static string SettingsJson(HistoryEntry[] history)
    {
        Dictionary<string, object?> values = new()
        {
            ["flutter.ls_alias"] = "Fixture Laptop",
            ["flutter.ls_port"] = 53317,
            ["flutter.ls_receive_history"] = history.Select(ToHistoryItem).ToArray(),
        };
        return JsonSerializer.Serialize(values);
    }

    private static string ToHistoryItem(HistoryEntry entry) =>
        JsonSerializer.Serialize(new
        {
            id = entry.Id,
            fileName = entry.FileName,
            path = entry.Path,
            isMessage = entry.IsMessage,
            fileSize = entry.FileSize,
            senderAlias = entry.SenderAlias,
            timestamp = entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        });
}
