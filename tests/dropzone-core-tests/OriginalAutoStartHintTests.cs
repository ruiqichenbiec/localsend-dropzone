using Dropzone.Core.Original;

namespace Dropzone.Core.Tests;

public class OriginalAutoStartHintTests
{
    [Theory]
    [InlineData(null)] // never checked before (first run, or an older settings file)
    [InlineData(true)] // it used to start with Windows and no longer does
    public void ShouldShow_TurnedOffOrFirstSeenOff_True(bool? lastSeen)
    {
        Assert.True(OriginalAutoStartHint.ShouldShow(lastSeen, startsWithWindows: false));
    }

    [Fact]
    public void ShouldShow_StillOff_False()
    {
        Assert.False(OriginalAutoStartHint.ShouldShow(lastSeen: false, startsWithWindows: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldShow_StartsWithWindows_False(bool? lastSeen)
    {
        Assert.False(OriginalAutoStartHint.ShouldShow(lastSeen, startsWithWindows: true));
    }
}
