using System.Text.Json;
using Dropzone.Core.Original;

namespace Dropzone.Core.Tests;

public class SeenHistoryStoreTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));

    public SeenHistoryStoreTests()
    {
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void Save_KeepsTheMostRecent500IdsInInsertionOrder()
    {
        string path = Path.Combine(_workDir, "seen-history.json");
        SeenHistoryStore store = new(path);
        for (int i = 0; i < 600; i++)
        {
            store.Add($"id-{i:0000}");
        }

        store.Save();

        string[] onDisk = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))!;
        Assert.Equal(Enumerable.Range(100, 500).Select(i => $"id-{i:0000}"), onDisk);
        Assert.False(store.Contains("id-0099"));
        Assert.True(store.Contains("id-0100"));
        Assert.False(new SeenHistoryStore(path).Contains("id-0099"));
        Assert.True(new SeenHistoryStore(path).Contains("id-0599"));
    }

    [Fact]
    public void Constructor_MissingFile_IsMissingAndEmpty()
    {
        SeenHistoryStore store = new(Path.Combine(_workDir, "absent.json"));

        Assert.Equal(SeenHistoryLoadState.Missing, store.LoadState);
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public void Constructor_EmptyArray_IsOk()
    {
        string path = Path.Combine(_workDir, "empty.json");
        File.WriteAllText(path, "[]");

        SeenHistoryStore store = new(path);

        Assert.Equal(SeenHistoryLoadState.Ok, store.LoadState);
        Assert.True(store.IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[\"id-1\", \"id-2\"")]
    [InlineData("{\"id\": \"id-1\"}")]
    [InlineData("null")]
    [InlineData("[\"id-1\", 7]")]
    [InlineData("[\"id-1\", null]")]
    public void Constructor_NotAJsonStringArray_IsCorrupt(string content)
    {
        string path = Path.Combine(_workDir, "corrupt.json");
        File.WriteAllText(path, content);

        SeenHistoryStore store = new(path);

        Assert.Equal(SeenHistoryLoadState.Corrupt, store.LoadState);
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public void Constructor_LockedFile_IsUnavailableUntilItLoads()
    {
        string path = Path.Combine(_workDir, "locked.json");
        SeenHistoryStore seed = new(path);
        seed.Add("id-1");
        seed.Save();

        SeenHistoryStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = new SeenHistoryStore(path);

            Assert.Equal(SeenHistoryLoadState.Unavailable, store.LoadState);
            Assert.True(store.IsEmpty);
            // A save now would replace the file with an empty list, so it is refused.
            Assert.Throws<InvalidOperationException>(() => store.Save());
        }

        store.EnsureLoaded();

        Assert.Equal(SeenHistoryLoadState.Ok, store.LoadState);
        Assert.True(store.Contains("id-1"));
    }
}
