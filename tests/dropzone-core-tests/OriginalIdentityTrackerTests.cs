using Dropzone.Core.Original;
using Dropzone.Core.Protocol;

namespace Dropzone.Core.Tests;

public class OriginalIdentityTrackerTests
{
    private static readonly DeviceInfo Identity = new(
        "Fixture Laptop", "2.2", "Windows", "desktop", "ABCDEF0123", 53317, "https", false);

    [Fact]
    public void New_NothingKnown_RetriesQuickly()
    {
        var tracker = new OriginalIdentityTracker();

        Assert.Null(tracker.Live);
        Assert.Null(tracker.LastKnown);
        Assert.Equal(TimeSpan.FromSeconds(5), tracker.NextDelay);
    }

    [Fact]
    public void FailuresBeforeAnySuccess_StayUnknownWithoutLost()
    {
        var tracker = new OriginalIdentityTracker();

        Assert.Equal(IdentityChange.None, tracker.Record(null));
        Assert.Equal(IdentityChange.None, tracker.Record(null));
        Assert.Equal(IdentityChange.None, tracker.Record(null));

        Assert.Null(tracker.Live);
        Assert.Equal(TimeSpan.FromSeconds(5), tracker.NextDelay);
    }

    [Fact]
    public void FirstSuccess_IsLiveAndLastKnown_ThenRefreshesSlowly()
    {
        var tracker = new OriginalIdentityTracker();

        Assert.Equal(IdentityChange.Found, tracker.Record(Identity));

        Assert.Equal(Identity, tracker.Live);
        Assert.Equal(Identity, tracker.LastKnown);
        Assert.Equal(TimeSpan.FromSeconds(60), tracker.NextDelay);
    }

    [Fact]
    public void OneFailure_KeepsLive_ButRetriesQuickly()
    {
        var tracker = new OriginalIdentityTracker();
        tracker.Record(Identity);

        Assert.Equal(IdentityChange.None, tracker.Record(null));

        Assert.Equal(Identity, tracker.Live);
        Assert.Equal(TimeSpan.FromSeconds(5), tracker.NextDelay);
    }

    [Fact]
    public void TwoConsecutiveFailures_LoseLive_KeepLastKnown_ReportLostOnce()
    {
        var tracker = new OriginalIdentityTracker();
        tracker.Record(Identity);

        tracker.Record(null);
        Assert.Equal(IdentityChange.Lost, tracker.Record(null));
        Assert.Equal(IdentityChange.None, tracker.Record(null));

        Assert.Null(tracker.Live);
        Assert.Equal(Identity, tracker.LastKnown);
        Assert.Equal(TimeSpan.FromSeconds(5), tracker.NextDelay);
    }

    [Fact]
    public void FailureThenSuccess_ResetsTheCount()
    {
        var tracker = new OriginalIdentityTracker();
        tracker.Record(Identity);

        tracker.Record(null);
        Assert.Equal(IdentityChange.None, tracker.Record(Identity));
        Assert.Equal(IdentityChange.None, tracker.Record(null));

        Assert.Equal(Identity, tracker.Live);
    }

    [Fact]
    public void BackAfterLost_ReportsFound()
    {
        var tracker = new OriginalIdentityTracker();
        tracker.Record(Identity);
        tracker.Record(null);
        tracker.Record(null);

        Assert.Equal(IdentityChange.Found, tracker.Record(Identity));
        Assert.Equal(Identity, tracker.Live);
        Assert.Equal(TimeSpan.FromSeconds(60), tracker.NextDelay);
    }

    [Fact]
    public void DifferentIdentity_ReportsChanged()
    {
        var tracker = new OriginalIdentityTracker();
        tracker.Record(Identity);
        DeviceInfo renamed = Identity with { Alias = "Renamed" };

        Assert.Equal(IdentityChange.Changed, tracker.Record(renamed));
        Assert.Equal(renamed, tracker.Live);
        Assert.Equal(renamed, tracker.LastKnown);
    }
}
