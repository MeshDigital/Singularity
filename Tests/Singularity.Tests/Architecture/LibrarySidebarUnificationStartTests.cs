using System;
using System.IO;
using Xunit;

namespace Singularity.Tests.Architecture;

public class LibrarySidebarUnificationStartTests
{

    [Fact]
    public void LibraryViewModel_DoesNotRetainLegacySidebarModeCompatibilityState()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("LibrarySidebarMode", source);
        Assert.DoesNotContain("IsLibrarySidebarPlayerMode", source);
        Assert.DoesNotContain("IsLibrarySidebarTrackInspectorMode", source);
        Assert.DoesNotContain("IsLibrarySidebarDoubleInspectorMode", source);
        Assert.DoesNotContain("IsLibrarySidebarIntelligenceMode", source);
        Assert.DoesNotContain("EvaluateSidebarMode(", source);
    }

    [Fact]
    public void LibrarySidebarLane_DoesNotRetainStaleClosureLanguageMarkers()
    {
        var eventsSource = ReadLibraryEventsSource();
        var commandsSource = ReadLibraryCommandsSource();

        Assert.DoesNotContain("Legacy: In-Memory Smart Playlists", eventsSource);
        Assert.DoesNotContain("CS8618 Fix: Initialize with null!", commandsSource);
    }

    [Fact]
    public void LibraryEvents_PublishesExplicitInspectorWrapperContexts()
    {
        var eventsSource = ReadLibraryEventsSource();

        Assert.Contains("OpenInspectorEvent.Create(selectedTracks[0], \"Library.TrackSelection.Single\")", eventsSource);
        Assert.Contains("ReactiveUI.MessageBus.Current.SendMessage(new CloseInspectorEvent());", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(this, \"DOUBLE INSPECTOR\", \"🔗\")", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(this, \"INTELLIGENCE\", \"🧠\")", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(single, source: \"Library.TrackSelection.Single\")", eventsSource);
    }

    [Fact]
    public void MainViewModel_HandlesCloseInspectorEvent()
    {
        var source = ReadMainViewModelSource();

        Assert.Contains("ShouldApplyInspectorPayload(evt.ViewModel)", source);
        Assert.Contains("NormalizeInspectorOpenSource(evt.Source)", source);
        Assert.Contains("ShouldApplyInspectorOpenForCurrentPage(source, CurrentPageType)", source);
        Assert.Contains("Listen<Singularity.Events.CloseInspectorEvent>()", source);
        Assert.Contains("_rightPanelService.ClosePanel();", source);
    }

    [Fact]
    public void OpenInspectorEvent_UsesSharedPresentationResolver()
    {
        var source = ReadOpenInspectorEventSource();

        Assert.Contains("ResolvePresentationDefaults", source);
        Assert.Contains("Create(object viewModel, string? source = null)", source);
        Assert.DoesNotContain("string title = \"INSPECTOR\"", source);
        Assert.DoesNotContain("string icon = \"ℹ️\"", source);
    }

    [Fact]
    public void LibraryPage_UsesCardVmProjectListBindings()
    {
        var xaml = ReadLibraryPageXaml();

        // Playlist Folders: the sidebar list is now a nested tree (RootTreeNodes) whose leaves
        // wrap LibraryPlaylistCardViewModel via PlaylistTreeCardNodeViewModel.Card, rather than
        // binding ItemsSource directly to the flat FilteredProjectCards collection.
        Assert.Contains("ItemsSource=\"{Binding Projects.RootTreeNodes}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding Projects.SelectedTreeNode, Mode=TwoWay}\"", xaml);
        Assert.DoesNotContain("ItemsSource=\"{Binding Projects.FilteredProjects}\"", xaml);
    }

    [Fact]
    public void CompactPlaylistTemplate_UsesCardVmAndMosaicCoverBinding()
    {
        var xaml = ReadCompactPlaylistTemplateXaml();

        Assert.Contains("x:DataType=\"vm:LibraryPlaylistCardViewModel\"", xaml);
        Assert.Contains("<Image Source=\"{Binding CoverBitmap}\" Stretch=\"UniformToFill\"/>", xaml);
        Assert.Contains("CommandParameter=\"{Binding Model}\"", xaml);
        Assert.DoesNotContain("x:DataType=\"models:PlaylistJob\"", xaml);
        Assert.DoesNotContain("DisplayArtUrl, Converter={StaticResource BitmapValueConverter}", xaml);
    }

    private static string ReadLibraryPageXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "LibraryPage.axaml");
        Assert.True(File.Exists(filePath), $"Expected library view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryEventsSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.Events.cs");
        Assert.True(File.Exists(filePath), $"Expected library events source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryViewModelSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected library view model source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryCommandsSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.Commands.cs");
        Assert.True(File.Exists(filePath), $"Expected library commands source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadCompactPlaylistTemplateXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "Controls", "CompactPlaylistTemplate.axaml");
        Assert.True(File.Exists(filePath), $"Expected compact playlist template view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadMainViewModelSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "MainViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected main view model source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadOpenInspectorEventSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Events", "OpenInspectorEvent.cs");
        Assert.True(File.Exists(filePath), $"Expected inspector event source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string FindSourceRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "Singularity.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        var candidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(candidate, "Singularity.csproj")))
        {
            return candidate;
        }

        return string.Empty;
    }
}
