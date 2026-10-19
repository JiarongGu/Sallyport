using Sallyport.Observer;

namespace Sallyport.Observer.Tests;

public sealed class ProcessTrackerTests
{
    private static readonly string Game = Path.GetFullPath("game.exe");
    private static readonly string Helper = Path.GetFullPath("helper.exe");
    private static ProcessSample Sample(uint pid, uint parent, long created, string? path = null) =>
        new(pid, parent, new(IdentityState.Available, new(new(pid, created), path ?? Game)));
    private static readonly IdentityResult Unavailable = new(IdentityState.Unavailable, Error: 5);
    private static readonly IdentityResult Exited = new(IdentityState.Exited);

    [Fact]
    public void FullPathMatchingIsCaseInsensitiveAndDoesNotMatchBasenames()
    {
        var tracker = new ProcessTracker([Game]);
        ProcessSample root = Sample(10, 0, 10, Game.ToUpperInvariant());
        tracker.Update([root, Sample(20, 0, 10, Path.Combine(Path.GetDirectoryName(Game)!, "other", "game.exe"))], 30);
        Assert.True(tracker.KnowsPid(10));
        Assert.False(tracker.KnowsPid(20));
        Assert.Equal("running", tracker.SelectedPathState(Game, true));
    }

    [Fact]
    public void DescendantsAreOrderIndependentAndRemainWatchedAfterLauncherExits()
    {
        var tracker = new ProcessTracker([Game]);
        ProcessSample child = Sample(20, 10, 20, Helper);
        ProcessSample grandchild = Sample(30, 20, 25, Helper);
        tracker.Update([grandchild, child, Sample(10, 0, 10)], 30);
        Assert.True(tracker.KnowsPid(30));
        tracker.Update([child, grandchild], 40);
        Assert.Equal(child.Result.Identity, tracker.Attribute(20, 39, child.Result, 41));
        Assert.Equal(grandchild.Result.Identity, tracker.Attribute(30, 39, grandchild.Result, 41));
    }

    [Fact]
    public void ReusedParentPidDoesNotAdoptAnUnrelatedChild()
    {
        var tracker = new ProcessTracker([Game]);
        tracker.Update([Sample(10, 0, 10)], 30);
        tracker.Update([Sample(10, 0, 40, Helper), Sample(20, 10, 45, Helper)], 50);
        Assert.False(tracker.KnowsPid(20));
        Assert.Null(tracker.Attribute(10, 45, Sample(10, 0, 40, Helper).Result, 50));
    }

    [Fact]
    public void AParentCreatedAfterTheChildCannotEstablishAncestry()
    {
        var tracker = new ProcessTracker([Game]);
        tracker.Update([Sample(10, 0, 30), Sample(20, 10, 20, Helper)], 40);
        Assert.False(tracker.KnowsPid(20));
    }

    [Fact]
    public void ExitedParentOnlyProvesChildrenBornDuringItsValidatedLifetime()
    {
        var tracker = new ProcessTracker([Game]);
        tracker.Update([Sample(10, 0, 10)], 30);
        tracker.Update([Sample(20, 10, 25, Helper), Sample(30, 10, 35, Helper)], 40);
        Assert.True(tracker.KnowsPid(20));
        Assert.False(tracker.KnowsPid(30));
    }

    [Fact]
    public void BufferedEventsUseTheirOwnInstanceAndGapsStayUnknown()
    {
        var tracker = new ProcessTracker([Game]);
        ProcessSample old = Sample(10, 0, 10);
        ProcessSample replacement = Sample(10, 0, 40);
        tracker.Update([old], 30);
        tracker.Update([replacement], 50);
        Assert.Equal(old.Result.Identity, tracker.Attribute(10, 25, replacement.Result, 60));
        Assert.Equal(replacement.Result.Identity, tracker.Attribute(10, 55, replacement.Result, 60));
        Assert.Null(tracker.Attribute(10, 35, replacement.Result, 60));
        Assert.Null(tracker.Attribute(10, 9, old.Result, 30));
    }

    [Fact]
    public void AccessDeniedDoesNotExtendTheProvenLifetimeOrBecomeAbsence()
    {
        var tracker = new ProcessTracker([Game]);
        tracker.Update([Sample(10, 0, 10)], 30);
        tracker.Update([new(10, 0, Unavailable)], 40);
        Assert.Null(tracker.Attribute(10, 35, Unavailable, 40));
        Assert.NotNull(tracker.Attribute(10, 25, Unavailable, 40));
        Assert.Equal("identity-unavailable", tracker.SelectedPathState(Game, true));
        tracker.Update([new(10, 0, Exited)], 50);
        // Seen during the run, and its PID gone: an answer, not an absence.
        Assert.Equal("seen, since exited", tracker.SelectedPathState(Game, true));
        Assert.Equal("identity-unavailable", tracker.SelectedPathState(Game, false));
    }

    [Fact]
    public void AnUnreadableParentCannotAdoptAChild()
    {
        var tracker = new ProcessTracker([Game]);
        tracker.Update([new(10, 0, Unavailable), Sample(20, 10, 20, Helper)], 40);
        Assert.False(tracker.KnowsPid(20));
    }

    [Fact]
    public void RecycledSnapshotPidCannotBorrowItsPreviousOccupantsParent()
    {
        var tracker = new ProcessTracker([Game]);
        tracker.Update([Sample(10, 0, 10), Sample(20, 10, 35, Helper)], 30);
        Assert.False(tracker.KnowsPid(20));
    }

    [Fact]
    public void AProgramNeverSeenIsNotRunningWhenEveryIdentityReads()
    {
        var tracker = new ProcessTracker([Game]);
        tracker.Update([Sample(20, 0, 10, Helper)], 30);
        Assert.Equal("not-running", tracker.SelectedPathState(Game, true));
    }
}
