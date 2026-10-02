namespace Singularity.ViewModels.Library;

public sealed record SmartInsertContextRequest(
    PlaylistTrackViewModel FromTrack,
    PlaylistTrackViewModel ToTrack);