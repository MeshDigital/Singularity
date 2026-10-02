using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NAudio.Wave;
using Singularity.Models.Stem;
using Singularity.Services.Audio;
using Singularity.Services.Audio.Separation;

namespace Singularity.Tests.Services
{
    // ─────────────────────────────────────────────────────────────────────────────
    // DemucsOnnxSeparator chunked-inference helpers — pure logic, no ONNX model needed
    // ─────────────────────────────────────────────────────────────────────────────

    public class DemucsOnnxSeparatorChunkingTests
    {
        [Fact]
        public void ResolveSegmentSamplesCore_UsesDeclaredFixedDimension_WhenGraphIsNotDynamic()
        {
            // e.g. a model exported with a hard-coded input shape [1, 2, 343980]
            var declared = new[] { 1, 2, 343980 };

            var result = DemucsOnnxSeparator.ResolveSegmentSamplesCore(declared, sampleRate: 44100, totalFrames: 10_000_000);

            Assert.Equal(343980, result);
        }

        [Fact]
        public void ResolveSegmentSamplesCore_FallsBackToDefaultSegment_WhenDimensionIsDynamic()
        {
            // -1 (or any non-positive value) marks a dynamic axis in ONNX metadata
            var declared = new[] { 1, 2, -1 };
            const int sampleRate = 44100;

            var result = DemucsOnnxSeparatorChunkingTests_LongTrack(declared, sampleRate);

            Assert.Equal((int)(10.0 * sampleRate), result);
        }

        private static int DemucsOnnxSeparatorChunkingTests_LongTrack(int[] declared, int sampleRate)
            => DemucsOnnxSeparator.ResolveSegmentSamplesCore(declared, sampleRate, totalFrames: sampleRate * 300);

        [Fact]
        public void ResolveSegmentSamplesCore_CapsToTrackLength_ForShortTracksOnDynamicGraph()
        {
            const int sampleRate = 44100;
            int shortTrackFrames = sampleRate * 3; // 3-second track, shorter than the 10s default segment

            var result = DemucsOnnxSeparator.ResolveSegmentSamplesCore(
                declaredDimensions: null, sampleRate, totalFrames: shortTrackFrames);

            Assert.Equal(shortTrackFrames, result);
        }

        [Fact]
        public void ResolveSegmentSamplesCore_NeverReturnsZeroOrNegative_ForDegenerateInput()
        {
            var result = DemucsOnnxSeparator.ResolveSegmentSamplesCore(
                declaredDimensions: null, sampleRate: 44100, totalFrames: 0);

            Assert.True(result > 0);
        }

        [Fact]
        public void BuildOverlapWindow_PeaksAtOne_InTheMiddle()
        {
            var window = DemucsOnnxSeparator.BuildOverlapWindow(101);

            Assert.Equal(1f, window[50], precision: 3);
        }

        [Fact]
        public void BuildOverlapWindow_NeverReachesExactZero_AtTheEdges()
        {
            // Guards against divide-by-zero during overlap-add normalisation at the very
            // start/end of a track, where only one chunk contributes.
            var window = DemucsOnnxSeparator.BuildOverlapWindow(64);

            Assert.True(window[0] > 0f);
            Assert.True(window[^1] > 0f);
        }

        [Fact]
        public void BuildOverlapWindow_IsSymmetric()
        {
            var window = DemucsOnnxSeparator.BuildOverlapWindow(50);

            for (int i = 0; i < window.Length / 2; i++)
            {
                Assert.Equal(window[i], window[^(i + 1)], precision: 5);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void BuildOverlapWindow_HandlesDegenerateLengths_WithoutThrowing(int length)
        {
            var window = DemucsOnnxSeparator.BuildOverlapWindow(length);

            Assert.Equal(length, window.Length);
        }

        [Fact]
        public void WeightedOverlapAdd_ReconstructsFlatValue_OutsideTheOverlapRegion()
        {
            // Simulates the actual reconstruction the real inference loop performs (weighted-sum
            // then divide-by-accumulated-weight), using two overlapping synthetic "model outputs"
            // (constant 2.0 for chunk A, constant 3.0 for chunk B) instead of real ONNX results.
            // Outside the overlap, each sample has exactly one contributor, so normalisation must
            // return that contributor's value exactly — this is what keeps the un-overlapped
            // majority of every track byte-identical to a single-chunk pass.
            const int segment = 1000;
            const int stride = 750; // segment - overlap(250), matching OverlapRatio
            const int totalFrames = stride + segment; // two chunks: [0,1000) and [750,1750)
            var window = DemucsOnnxSeparator.BuildOverlapWindow(segment);

            var accum = new float[totalFrames];
            var weight = new float[totalFrames];

            AddChunk(accum, weight, window, chunkStart: 0, value: 2.0f);
            AddChunk(accum, weight, window, chunkStart: stride, value: 3.0f);

            for (int i = 0; i < totalFrames; i++)
            {
                accum[i] /= weight[i];
            }

            // Well before the overlap (chunk A only) → exactly chunk A's value.
            Assert.Equal(2.0f, accum[100], precision: 4);
            // Well after the overlap (chunk B only) → exactly chunk B's value.
            Assert.Equal(3.0f, accum[1600], precision: 4);
            // Inside the overlap, the blend must move monotonically from A's value toward B's.
            Assert.InRange(accum[750], 2.0f, 3.0f);
            Assert.InRange(accum[900], 2.0f, 3.0f);
            Assert.True(accum[900] > accum[750], "Blend should move further toward chunk B's value as the overlap progresses.");
        }

        private static void AddChunk(float[] accum, float[] weight, float[] window, int chunkStart, float value)
        {
            for (int i = 0; i < window.Length; i++)
            {
                accum[chunkStart + i]  += value * window[i];
                weight[chunkStart + i] += window[i];
            }
        }
    }
}
