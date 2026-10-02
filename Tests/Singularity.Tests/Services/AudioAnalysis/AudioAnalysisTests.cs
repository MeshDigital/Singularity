using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Singularity.Data.Entities;
using Singularity.Data.Essentia;
using Singularity.Services.Audio;
using Singularity.Services.Audio.Separation;
using Singularity.Services.AudioAnalysis;

namespace Singularity.Tests.Services.AudioAnalysis
{
    // ─────────────────────────────────────────────────────────────────────────
    // KeyDetectionService tests (Issue 1.3 / #21)
    // ─────────────────────────────────────────────────────────────────────────

    public class KeyDetectionServiceTests
    {
        private readonly KeyDetectionService _sut = new();

        [Theory]
        [InlineData("C",  "major", "8B")]
        [InlineData("A",  "minor", "8A")]
        [InlineData("G",  "major", "9B")]
        [InlineData("F#", "minor", "11A")]
        [InlineData("Db", "major", "3B")]
        public void ToCamelotKey_ReturnsCorrectCode(string key, string scale, string expected)
        {
            Assert.Equal(expected, KeyDetectionService.ToCamelotKey(key, scale));
        }

        [Fact]
        public void Detect_WritesKeyScaleCamelot()
        {
            var output = MakeKeyOutput(edmaKey: "C", edmaScale: "major", edmaStrength: 0.85f);
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.Equal("C",     target.Key);
            Assert.Equal("major", target.Scale);
            Assert.Equal("8B",    target.CamelotKey);
            Assert.Equal(0.85f,   target.KeyConfidence, precision: 5);
        }

        [Fact]
        public void Detect_PrefersEdmaOverKrumhanslWhenStronger()
        {
            var output = new EssentiaOutput
            {
                Tonal = new TonalData
                {
                    KeyEdma       = new KeyData { Key = "A", Scale = "minor", Strength = 0.9f },
                    KeyKrumhansl  = new KeyData { Key = "C", Scale = "major", Strength = 0.5f },
                }
            };
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.Equal("A", target.Key);
        }

        [Fact]
        public void Detect_FallsBackToKrumhanslWhenEdmaMissing()
        {
            var output = new EssentiaOutput
            {
                Tonal = new TonalData
                {
                    KeyEdma      = null,
                    KeyKrumhansl = new KeyData { Key = "G", Scale = "major", Strength = 0.7f },
                }
            };
            var target = new AudioFeaturesEntity();
            _sut.Detect(output, target);
            Assert.Equal("G", target.Key);
        }

        [Fact]
        public void Detect_NullTonal_DoesNotWriteFields()
        {
            var output = new EssentiaOutput { Tonal = null };
            var target = new AudioFeaturesEntity { Key = "original" };
            _sut.Detect(output, target);
            Assert.Equal("original", target.Key);
        }

        [Fact]
        public void ToOpenKey_ReturnsCorrectCode()
        {
            Assert.Equal("1d", KeyDetectionService.ToOpenKey("C", "major"));
            Assert.Equal("1m", KeyDetectionService.ToOpenKey("A", "minor"));
        }

        private static EssentiaOutput MakeKeyOutput(string edmaKey, string edmaScale,
            float edmaStrength)
            => new()
            {
                Tonal = new TonalData
                {
                    KeyEdma      = new KeyData { Key = edmaKey, Scale = edmaScale,
                                                  Strength = edmaStrength },
                    KeyKrumhansl = new KeyData { Key = "X", Scale = "major", Strength = 0.0f },
                }
            };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DemucsModelManager tests (Issue 3.1 / #28)
    // ─────────────────────────────────────────────────────────────────────────

    public class DemucsModelManagerTests
    {
        [Fact]
        public void ModelTag_WhenMissing_ReturnsPlaceholder()
        {
            var mgr = new DemucsModelManager(customModelPath: "/nonexistent/path/demucs.onnx");
            Assert.StartsWith("demucs-4s-missing", mgr.ModelTag);
        }

        [Fact]
        public void IsAvailable_WhenMissing_ReturnsFalse()
        {
            var mgr = new DemucsModelManager(customModelPath: "/nonexistent/path/demucs.onnx");
            Assert.False(mgr.IsAvailable);
        }

        [Fact]
        public void ModelFileName_IsCorrect()
        {
            Assert.Equal("demucs-4s.onnx", DemucsModelManager.ModelFileName);
        }

        [Fact]
        public void GetUserModelDirectory_ReturnsExistingDirectory()
        {
            string dir = DemucsModelManager.GetUserModelDirectory();
            Assert.True(System.IO.Directory.Exists(dir));
        }
    }
}
