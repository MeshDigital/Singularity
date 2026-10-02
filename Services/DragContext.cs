using System.Collections.Generic;

namespace Singularity.Services
{
    /// <summary>
    /// Global context for drag-and-drop operations.
    /// </summary>
    public static class DragContext
    {
        // Data format identifiers
        public const string QueueTrackFormat = "SINGULARITY_QueueTrack";
        public const string LibraryTrackFormat = "SINGULARITY_LibraryTrack";

        // Playlist folder tree: dragging a playlist card or folder node to reorganize the tree
        public const string PlaylistCardNodeFormat = "SINGULARITY_PlaylistCardNode";
        public const string PlaylistFolderNodeFormat = "SINGULARITY_PlaylistFolderNode";
        
        /// <summary>
        /// Temporary storage for drag data (fallback for platforms that don't support custom formats).
        /// </summary>
        public static object? Current { get; set; }
    }
}
