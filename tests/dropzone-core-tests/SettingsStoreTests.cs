using Dropzone.Core.Settings;

namespace Dropzone.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        AppSettings settings = new SettingsStore(Path.Combine(_workDir, "absent", "settings.json")).Load();

        Assert.Null(settings.Password);
        Assert.Equal(53319, settings.PrivatePort);
        Assert.Equal(Corner.BottomRight, settings.Corner);
        Assert.True(settings.AutoStart);
        Assert.False(settings.FirstRunDone);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        string path = Path.Combine(_workDir, "nested", "settings.json");
        AppSettings saved = new("hunter2-but-longer", 54000, Corner.TopLeft, AutoStart: false, FirstRunDone: true);

        new SettingsStore(path).Save(saved);
        AppSettings loaded = new SettingsStore(path).Load();

        Assert.Equal(saved, loaded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveThenLoad_OriginalAutoStart_RoundTrips(bool startsWithWindows)
    {
        string path = Path.Combine(_workDir, "settings.json");
        AppSettings saved = new(null, OriginalAutoStart: startsWithWindows);

        new SettingsStore(path).Save(saved);

        Assert.Equal(startsWithWindows, new SettingsStore(path).Load().OriginalAutoStart);
    }

    [Fact]
    public void Load_NoOriginalAutoStart_GivesNull()
    {
        string path = Path.Combine(_workDir, "settings.json");
        File.WriteAllText(path, """{"privatePort": 54000, "originalAutoStart": "yes"}""");

        Assert.Null(new SettingsStore(path).Load().OriginalAutoStart);
        Assert.Null(new SettingsStore(Path.Combine(_workDir, "absent.json")).Load().OriginalAutoStart);
    }

    [Fact]
    public void SaveThenLoad_NoPassword_RoundTripsAsNull()
    {
        string path = Path.Combine(_workDir, "settings.json");
        SettingsStore store = new(path);

        store.Save(new AppSettings(null, Corner: Corner.BottomLeft));

        Assert.Equal(new AppSettings(null, Corner: Corner.BottomLeft), store.Load());
    }

    [Fact]
    public void Save_DoesNotWritePlaintextPassword()
    {
        string path = Path.Combine(_workDir, "settings.json");

        new SettingsStore(path).Save(new AppSettings("hunter2"));

        string text = File.ReadAllText(path);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("passwordProtected", text);
    }

    [Theory]
    [InlineData(53317)] // the official app's port
    [InlineData(0)]
    [InlineData(65536)]
    public void Load_InvalidPort_GivesDefault53319(int port)
    {
        string path = Path.Combine(_workDir, "settings.json");
        File.WriteAllText(path, $"{{\"privatePort\": {port}, \"corner\": \"TopLeft\"}}");

        AppSettings settings = new SettingsStore(path).Load();

        Assert.Equal(53319, settings.PrivatePort);
        Assert.Equal(Corner.TopLeft, settings.Corner);
    }

    [Fact]
    public void Load_PortEqualToOriginalAppsConfiguredPort_GivesDefault53319()
    {
        // The official app was moved off 53317 to 54100; a saved private port of 54100 would fight it for the port.
        string path = Path.Combine(_workDir, "settings.json");
        File.WriteAllText(path, """{"privatePort": 54100, "corner": "TopLeft"}""");

        AppSettings settings = new SettingsStore(path, originalPort: () => 54100).Load();

        Assert.Equal(53319, settings.PrivatePort);
        Assert.Equal(Corner.TopLeft, settings.Corner);
    }

    [Theory]
    [InlineData(54101)] // another port
    [InlineData(null)] // the official app's settings could not be read
    public void Load_PortDifferentFromOriginalApps_IsKept(int? originalPort)
    {
        string path = Path.Combine(_workDir, "settings.json");
        File.WriteAllText(path, """{"privatePort": 54100}""");

        AppSettings settings = new SettingsStore(path, originalPort: () => originalPort).Load();

        Assert.Equal(54100, settings.PrivatePort);
    }

    [Fact]
    public void Current_BeforeLoad_AlsoRejectsTheOriginalAppsPort()
    {
        string path = Path.Combine(_workDir, "settings.json");
        File.WriteAllText(path, """{"privatePort": 54100}""");

        Assert.Equal(53319, new SettingsStore(path, originalPort: () => 54100).Current.PrivatePort);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        string path = Path.Combine(_workDir, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Equal(new AppSettings(null), new SettingsStore(path).Load());
    }

    [Fact]
    public void ToString_DoesNotPrintPassword()
    {
        string text = new AppSettings("hunter2-secret", 54000).ToString();

        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("54000", text);
    }

    [Fact]
    public void Update_AppliesChangeToCurrentSettingsAndSaves()
    {
        string path = Path.Combine(_workDir, "settings.json");
        SettingsStore store = new(path);
        store.Load();

        store.Update(s => s with { Password = "a-long-shared-password" });
        AppSettings result = store.Update(s => s with { FirstRunDone = true });

        AppSettings expected = new("a-long-shared-password", FirstRunDone: true);
        Assert.Equal(expected, result);
        Assert.Equal(expected, store.Current);
        Assert.Equal(expected, new SettingsStore(path).Load());
    }

    [Fact]
    public void FirstRunSequence_PasswordSavedWhileChecksRun_IsKeptWhenFirstRunDoneIsSet()
    {
        // The first-run bug: the settings window saves a password while the first-run checks are still running;
        // then FirstRunDone is set. Setting it must not write back the settings as they were before the save.
        string path = Path.Combine(_workDir, "settings.json");
        SettingsStore store = new(path);
        AppSettings atStartup = store.Load();
        Assert.False(atStartup.FirstRunDone);

        // Settings window: only the edited fields, applied to the current settings.
        store.Update(s => s with { Password = "a-long-shared-password", Corner = Corner.TopLeft });

        // First-run checks finish.
        store.Update(s => s with { FirstRunDone = true });

        AppSettings onDisk = new SettingsStore(path).Load();
        Assert.Equal("a-long-shared-password", onDisk.Password);
        Assert.Equal(Corner.TopLeft, onDisk.Corner);
        Assert.True(onDisk.FirstRunDone);
        Assert.Contains("passwordProtected", File.ReadAllText(path));
    }

    [Fact]
    public void Update_WriteFails_CurrentIsUnchanged()
    {
        string path = Path.Combine(_workDir, "settings.json");
        SettingsStore store = new(path);
        store.Save(new AppSettings("a-long-shared-password"));
        Directory.CreateDirectory(path + ".tmp"); // the temp file cannot be created

        Exception error = Assert.ThrowsAny<Exception>(() => store.Update(s => s with { PrivatePort = 54003 }));
        Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
        Assert.Equal(53319, store.Current.PrivatePort);
    }

    [Fact]
    public void Update_ConcurrentChanges_NoneIsLost()
    {
        string path = Path.Combine(_workDir, "settings.json");
        SettingsStore store = new(path);
        store.Load();

        Parallel.For(0, 50, i =>
        {
            if (i == 7)
            {
                store.Update(s => s with { Password = "a-long-shared-password" });
            }
            else if (i == 31)
            {
                store.Update(s => s with { FirstRunDone = true });
            }
            else
            {
                store.Update(s => s with { AutoStart = !s.AutoStart });
            }
        });

        AppSettings final = new SettingsStore(path).Load();
        Assert.Equal("a-long-shared-password", final.Password);
        Assert.True(final.FirstRunDone);
        Assert.Equal(store.Current, final);
    }

    [Fact]
    public void Current_BeforeLoad_ReadsTheFile()
    {
        string path = Path.Combine(_workDir, "settings.json");
        new SettingsStore(path).Save(new AppSettings(null, 54002));

        Assert.Equal(54002, new SettingsStore(path).Current.PrivatePort);
    }

    [Fact]
    public void Load_UndecryptablePassword_KeepsOtherFieldsAndDropsPassword()
    {
        string path = Path.Combine(_workDir, "settings.json");
        File.WriteAllText(path, """{"passwordProtected":"bm90LWRwYXBp","privatePort":54001,"firstRunDone":true}""");

        AppSettings loaded = new SettingsStore(path).Load();

        Assert.Null(loaded.Password);
        Assert.Equal(54001, loaded.PrivatePort);
        Assert.True(loaded.FirstRunDone);
    }
}
