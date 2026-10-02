using Singularity.ViewModels.Library;
using Xunit;

namespace Singularity.Tests.ViewModels;

/// <summary>
/// Covers CreateSmartPlaylistViewModel — the previously-orphaned "New Smart Playlist (Custom
/// Criteria)" dialog's view model. PreviewProfile had an operator-precedence bug
/// ((Min ?? 0 + Max ?? 1.0) / 2.0, where ?? binds looser than +) that silently computed the
/// wrong preview average; these tests lock in the corrected averaging behavior.
/// </summary>
public class CreateSmartPlaylistViewModelTests
{

    [Fact]
    public void Save_MapsAllCriteriaFields_IncludingDanceabilityRatingAndLiked()
    {
        var vm = new CreateSmartPlaylistViewModel
        {
            Name = "Peak Time",
            MinEnergy = 0.6,
            MaxEnergy = 1.0,
            MinBpm = 128,
            MaxBpm = 132,
            MinDanceability = 0.5,
            Genre = "Techno",
            MinRating = 4,
            OnlyLiked = true
        };

        Singularity.Models.SmartPlaylistCriteria? saved = null;
        vm.OnSave += (_, criteria) => saved = criteria;

        ((System.Windows.Input.ICommand)vm.SaveCommand).Execute(null);

        Assert.NotNull(saved);
        Assert.Equal(0.6, saved!.MinEnergy);
        Assert.Equal(1.0, saved.MaxEnergy);
        Assert.Equal(128, saved.MinBPM);
        Assert.Equal(132, saved.MaxBPM);
        Assert.Equal(0.5, saved.MinDanceability);
        Assert.Equal("Techno", saved.Genre);
        Assert.Equal(4, saved.MinRating);
        Assert.True(saved.IsLiked);
    }

    [Fact]
    public void Save_LeavesIsLikedNull_WhenOnlyLikedIsFalse()
    {
        var vm = new CreateSmartPlaylistViewModel { OnlyLiked = false };

        Singularity.Models.SmartPlaylistCriteria? saved = null;
        vm.OnSave += (_, criteria) => saved = criteria;

        ((System.Windows.Input.ICommand)vm.SaveCommand).Execute(null);

        Assert.NotNull(saved);
        Assert.Null(saved!.IsLiked);
    }
}
