using System;
using System.IO;
using Singularity.Configuration;
using Xunit;

namespace Singularity.Tests.Configuration;

public class ConfigManagerFrequentSourcesTests
{
    [Fact]
    public void SaveLoad_RoundTripsFrequentSourcesSettings()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"singularity-config-frequent-sources-{Guid.NewGuid():N}.ini");

        try
        {
            var manager = new ConfigManager(tempPath);
            var config = new AppConfig
            {
                EnableFrequentSources = true,
                FrequentSourcesStagingPath = @"C:\singularity\staging"
            };

            manager.Save(config);
            var loaded = manager.Load();

            Assert.True(loaded.EnableFrequentSources);
            Assert.Equal(@"C:\singularity\staging", loaded.FrequentSourcesStagingPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void Load_MissingFile_UsesFrequentSourcesDefaults()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"singularity-config-frequent-sources-defaults-{Guid.NewGuid():N}.ini");

        try
        {
            var manager = new ConfigManager(tempPath);
            var loaded = manager.Load();

            Assert.False(loaded.EnableFrequentSources);
            Assert.Equal(string.Empty, loaded.FrequentSourcesStagingPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
