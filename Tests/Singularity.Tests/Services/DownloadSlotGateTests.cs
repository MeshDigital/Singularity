using Singularity.Models;
using Singularity.Services;
using Xunit;

namespace Singularity.Tests.Services;

public class DownloadSlotGateTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 19, 31, 0, DateTimeKind.Utc);

    [Fact]
    public void EveryWaitingTrackGated_TheNearestGateOpens()
    {
        // The stall of Green Day - Holiday: it waited for slot 4, a search failure waited for slot 4, the counter was at
        // 3, and nothing could start to move it on.
        var tracks = new (PlaylistTrackState, DateTime?, long?)[]
        {
            (PlaylistTrackState.Pending, Now.AddSeconds(-1), 4),
            (PlaylistTrackState.Pending, Now.AddSeconds(-30), 5),
            (PlaylistTrackState.Completed, null, null),
        };
        Assert.Equal(4, DownloadManager.SlotToOpen(tracks, currentSlot: 3, Now));
    }

    [Fact]
    public void AReadyTrack_GoesFirst_TheGateStays()
    {
        var tracks = new (PlaylistTrackState, DateTime?, long?)[]
        {
            (PlaylistTrackState.Pending, Now.AddSeconds(-1), 9),
            (PlaylistTrackState.Pending, null, null),
        };
        Assert.Null(DownloadManager.SlotToOpen(tracks, currentSlot: 3, Now));
    }

    [Fact]
    public void ATrackStillInItsRetryDelay_DoesNotOpenTheGate()
    {
        var tracks = new (PlaylistTrackState, DateTime?, long?)[] { (PlaylistTrackState.Pending, Now.AddSeconds(10), 4) };
        Assert.Null(DownloadManager.SlotToOpen(tracks, currentSlot: 3, Now));
    }

    [Fact]
    public void NothingWaiting_NothingOpens() =>
        Assert.Null(DownloadManager.SlotToOpen(new (PlaylistTrackState, DateTime?, long?)[] { (PlaylistTrackState.Downloading, null, null) }, 3, Now));
}
