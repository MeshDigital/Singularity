using System;
using System.Collections.Generic;
using System.IO;
using Singularity.Models;
using Singularity.ViewModels;
using Xunit;

namespace Singularity.Tests.ViewModels;

public class PlayerViewModelUiTests
{
    [Fact]
    public void BuildTrackContextSummary_ReturnsFriendlyFallback_WhenNoTrackLoaded()
    {
        var summary = PlayerViewModel.BuildTrackContextSummary(null);

        Assert.Contains("Load a track", summary);
    }

    [Fact]
    public void BuildTrackContextSummary_IncludesBpmKeyAndGenre_WhenAvailable()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Track",
            BPM = 124,
            MusicalKey = "Gm",
            PrimaryGenre = "Melodic House",
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = "C:/music/test.mp3"
        });

        var summary = PlayerViewModel.BuildTrackContextSummary(vm);

        Assert.Contains("124 BPM", summary);
        Assert.Contains("6A", summary);
        Assert.Contains("Melodic House", summary);
    }

}
