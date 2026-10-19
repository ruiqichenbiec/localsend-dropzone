using Dropzone.Core.Files;

namespace Dropzone.Core.Tests;

public class SafePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dropzone-tests-" + Guid.NewGuid().ToString("N"));

    public SafePathsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("报告.pdf", "报告.pdf")]
    [InlineData("a/b.txt", @"a\b.txt")]
    [InlineData(@"a\b.txt", @"a\b.txt")]
    [InlineData("a/./b.txt", @"a\b.txt")]
    [InlineData("/etc/passwd", @"etc\passwd")]
    [InlineData(@"C:\Windows\x.dll", @"C_\Windows\x.dll")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a/lpt1", @"a\_lpt1")]
    [InlineData("name. ", "name")]
    [InlineData("x<y>.txt", "x_y_.txt")]
    [InlineData("../evil.txt", null)]
    [InlineData("a/../../evil", null)]
    [InlineData("", null)]
    [InlineData("///", null)]
    public void ToSafeRelativePath_Cases(string input, string? expected)
    {
        Assert.Equal(expected, SafePaths.ToSafeRelativePath(input));
    }

    [Fact]
    public void GetUniquePath_NoConflict_ReturnsSame()
    {
        Assert.Equal(Path.Combine(_root, "报告.pdf"), SafePaths.GetUniquePath(_root, "报告.pdf"));
    }

    [Fact]
    public void GetUniquePath_OneConflict_AppendsOne()
    {
        File.WriteAllText(Path.Combine(_root, "报告.pdf"), "x");

        Assert.Equal(Path.Combine(_root, "报告 (1).pdf"), SafePaths.GetUniquePath(_root, "报告.pdf"));
    }

    [Fact]
    public void GetUniquePath_TwoConflicts_AppendsTwo()
    {
        File.WriteAllText(Path.Combine(_root, "报告.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "报告 (1).pdf"), "x");

        Assert.Equal(Path.Combine(_root, "报告 (2).pdf"), SafePaths.GetUniquePath(_root, "报告.pdf"));
    }

    [Fact]
    public void GetUniquePath_NoExtension()
    {
        File.WriteAllText(Path.Combine(_root, "README"), "x");

        Assert.Equal(Path.Combine(_root, "README (1)"), SafePaths.GetUniquePath(_root, "README"));
    }

    [Fact]
    public void GetUniquePath_Dotfile()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "x");

        Assert.Equal(Path.Combine(_root, ".gitignore (1)"), SafePaths.GetUniquePath(_root, ".gitignore"));
    }

    [Fact]
    public void GetUniquePath_Nested()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir"));
        File.WriteAllText(Path.Combine(_root, "dir", "a.txt"), "x");

        Assert.Equal(Path.Combine(_root, "dir", "a (1).txt"), SafePaths.GetUniquePath(_root, @"dir\a.txt"));
    }

    [Fact]
    public void GetUniquePath_CreatesNeitherFileNorDirectories()
    {
        string path = SafePaths.GetUniquePath(_root, @"new\a.txt");

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(_root, "new")));
    }

    [Fact]
    public void KnownFolders_GetDownloads_IsExistingDirectory()
    {
        Assert.True(Directory.Exists(KnownFolders.GetDownloads()));
    }
}
