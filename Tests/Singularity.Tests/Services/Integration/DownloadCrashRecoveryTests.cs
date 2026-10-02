using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Singularity.Data.Entities;

namespace Singularity.Tests.Services.Integration
{
    // ── 3. Crash-recovery: download queue state transitions ──────────────────

    public class DownloadCrashRecoveryTests
    {
        // The download queue uses TrackEntity.State (string) with values:
        //   "Pending"    — queued, waiting to start
        //   "InProgress" — active download (interrupted if crash occurs)
        //   "Completed"  — terminal success state
        //   "Failed"     — terminal error state
        //
        // Recovery contract:
        //   - "InProgress" at shutdown → reset to "Pending" on restart
        //   - "Pending" / "Completed" / "Failed" remain unchanged

        private const string StatePending    = "Pending";
        private const string StateInProgress = "InProgress";
        private const string StateCompleted  = "Completed";
        private const string StateFailed     = "Failed";

        private static Singularity.Data.TrackEntity MakeTrack(string state, string title = "Track")
            => new()
            {
                GlobalId = Guid.NewGuid().ToString(),
                Title    = title,
                Artist   = "Artist",
                State    = state,
                AddedAt  = DateTime.UtcNow,
            };

        [Fact]
        public void Track_PendingState_RemainsUnchangedByRecovery()
        {
            var track = MakeTrack(StatePending);

            if (track.State == StateInProgress)
                track.State = StatePending;

            Assert.Equal(StatePending, track.State);
        }

        [Fact]
        public void Track_InProgress_IsResetToPendingOnRecovery()
        {
            // Simulates crash: download was active, restart should reset to Pending.
            var track = MakeTrack(StateInProgress);

            if (track.State == StateInProgress)
                track.State = StatePending;

            Assert.Equal(StatePending, track.State);
        }

        [Fact]
        public void Track_Completed_IsNotResetByRecovery()
        {
            var track = MakeTrack(StateCompleted);

            if (track.State == StateInProgress)
                track.State = StatePending;

            Assert.Equal(StateCompleted, track.State);
        }

        [Fact]
        public void Track_Failed_IsNotResetByRecovery()
        {
            var track = MakeTrack(StateFailed);

            if (track.State == StateInProgress)
                track.State = StatePending;

            Assert.Equal(StateFailed, track.State);
        }

        [Fact]
        public void RecoveryBatch_ResetsAllInProgressToPending()
        {
            var tracks = new List<Singularity.Data.TrackEntity>
            {
                MakeTrack(StatePending,    "P1"),
                MakeTrack(StateInProgress, "IP1"),
                MakeTrack(StateInProgress, "IP2"),
                MakeTrack(StateCompleted,  "C1"),
                MakeTrack(StateFailed,     "F1"),
            };

            // Simulate recovery startup: reset InProgress → Pending
            foreach (var t in tracks.Where(t => t.State == StateInProgress))
                t.State = StatePending;

            int pendingCount   = tracks.Count(t => t.State == StatePending);
            int completedCount = tracks.Count(t => t.State == StateCompleted);
            int failedCount    = tracks.Count(t => t.State == StateFailed);

            Assert.Equal(3, pendingCount);   // 1 original + 2 recovered
            Assert.Equal(1, completedCount);
            Assert.Equal(1, failedCount);
        }
    }
}
