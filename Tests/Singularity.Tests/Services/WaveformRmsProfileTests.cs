using System;
using Singularity.Services;
using Xunit;

namespace Singularity.Tests.Services
{
    public class WaveformRmsProfileTests
    {
        [Fact]
        public void ComputeRmsProfile_EmptyInput_ReturnsEmpty()
        {
            var profile = WaveformCacheService.ComputeRmsProfile(Array.Empty<byte>(), 100);
            Assert.Empty(profile);
        }

        [Fact]
        public void ComputeRmsProfile_ZeroTargetBins_ReturnsEmpty()
        {
            var profile = WaveformCacheService.ComputeRmsProfile(new byte[] { 128 }, 0);
            Assert.Empty(profile);
        }

        [Fact]
        public void ComputeRmsProfile_SingleByte255_ReturnsOnePoint()
        {
            var profile = WaveformCacheService.ComputeRmsProfile(new byte[] { 255 }, 1);
            Assert.Single(profile);
            Assert.Equal(1.0f, profile[0], precision: 5);
        }

        [Fact]
        public void ComputeRmsProfile_SilentBytes_ReturnsZero()
        {
            var src     = new byte[100]; // all zeros
            var profile = WaveformCacheService.ComputeRmsProfile(src, 100);
            Assert.All(profile, v => Assert.Equal(0f, v));
        }

        [Fact]
        public void ComputeRmsProfile_DownsamplesCorrectly()
        {
            // 200 bytes → 100 bins: each bin averages two bytes
            var src = new byte[200];
            for (int i = 0; i < 200; i++) src[i] = 200; // constant 200
            var profile = WaveformCacheService.ComputeRmsProfile(src, 100);
            Assert.Equal(100, profile.Length);
            // 200/255 ≈ 0.7843
            Assert.All(profile, v => Assert.Equal(200f / 255f, v, precision: 4));
        }

        [Fact]
        public void ComputeRmsProfile_UpsamplesCorrectly()
        {
            // 10 bytes → 100 bins: interpolation must not crash
            var src = new byte[10];
            for (int i = 0; i < 10; i++) src[i] = 100;
            var profile = WaveformCacheService.ComputeRmsProfile(src, 100);
            Assert.Equal(100, profile.Length);
            Assert.All(profile, v => Assert.InRange(v, 0f, 1f));
        }
    }
}
