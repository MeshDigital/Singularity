using System;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Singularity.Models;
using Singularity.ViewModels.Library;
using Singularity.Data;
using Singularity.Data.Entities;
using Singularity.Views;
using Singularity.Events;

namespace Singularity.ViewModels;

public partial class LibraryViewModel
{
    private void OnLibraryTrackRemoved(string globalId)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var toRemove = Tracks.CurrentProjectTracks
                .Where(t => t.GlobalId == globalId)
                .ToList();
            foreach (var t in toRemove)
                Tracks.CurrentProjectTracks.Remove(t);
        });
    }

    private async void OnProjectAdded(ProjectAddedEvent evt)
    {
        try
        {
            _logger.LogInformation("[IMPORT TRACE] LibraryViewModel.OnProjectAdded: Received event for job {JobId}", evt.ProjectId);
            
            // Wait a moment for DB to settle
            await Task.Delay(500);
            
            await LoadProjectsAsync();
            _logger.LogInformation("[IMPORT TRACE] LoadProjectsAsync completed. AllProjects count: {Count}", Projects.AllProjects.Count);
            
            // Select the newly added project
            _logger.LogInformation("[IMPORT TRACE] Attempting to select project {JobId}", evt.ProjectId);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var newProject = Projects.AllProjects.FirstOrDefault(p => p.Id == evt.ProjectId);
                if (newProject != null)
                {
                    Projects.SelectedProject = newProject;
                    _logger.LogInformation("[IMPORT TRACE] Successfully selected project {JobId}", evt.ProjectId);
                }
                else
                {
                    _logger.LogWarning("Could not find project {JobId} in AllProjects after import", evt.ProjectId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle post-import navigation for project {JobId}", evt.ProjectId);
        }
    }

    private void OnTrackSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var selectedTracks = Tracks.SelectedTracks.ToList();

        if (selectedTracks.Count == 1)
        {
            ReactiveUI.MessageBus.Current.SendMessage(OpenInspectorEvent.Create(selectedTracks[0], "Library.TrackSelection.Single"));
        }
        else if (selectedTracks.Count == 0)
        {
            ReactiveUI.MessageBus.Current.SendMessage(new CloseInspectorEvent());
        }
    }

    /// <summary>
    /// Loads all projects from the database.
    /// Delegates to ProjectListViewModel.
    /// </summary>
    public async Task LoadProjectsAsync()
    {
        await Projects.LoadProjectsAsync();
    }

    /// <summary>
    /// Handles project selection event from ProjectListViewModel.
    /// Coordinates loading tracks in TrackListViewModel.
    /// </summary>
    private async void OnProjectSelected(object? sender, PlaylistJob? project)
    {
        // LibraryViewModel.SelectedProject is a pass-through wrapper over Projects.SelectedProject
        // (kept for XAML binding convenience) that never raised its own PropertyChanged — anything
        // bound to {Binding SelectedProject} on this ViewModel (rather than Projects.SelectedProject
        // directly) saw only whatever value was current when the binding first attached and then
        // silently never updated again, since ProjectListViewModel's own notification obviously
        // can't know about this outer wrapper. Only surfaced once something (the playlist header)
        // actually needed live updates through the wrapper instead of the direct path.
        OnPropertyChanged(nameof(SelectedProject));

        if (project == null)
        {
            ReactiveUI.MessageBus.Current.SendMessage(new CloseInspectorEvent());
            return;
        }

        // Note: the "All Tracks" pseudo-project (ProjectListViewModel._allTracksJob) also uses
        // Guid.Empty as its Id, so it must NOT be treated as "nothing selected" here — it needs
        // the same load below as any real project. A prior pass added the null-check above and
        // mistakenly folded project.Id == Guid.Empty into it, which made LoadProjectTracksAsync
        // never run for "All Tracks": the grid stayed on whatever the previous project loaded
        // (or empty) until an unrelated event — typing in the search box — triggered the first
        // real load, which read as "search is broken and incomplete."
        _logger.LogInformation("LibraryViewModel.OnProjectSelected: Switching to project {Title} (ID: {Id})", project.SourceTitle, project.Id);
        var selectSw = System.Diagnostics.Stopwatch.StartNew();
        await Tracks.LoadProjectTracksAsync(project);
        _logger.LogInformation("[PERF] Project select '{Title}': LoadProjectTracks {LoadMs}ms",
            project.SourceTitle, selectSw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Handles smart playlist selection event from SmartPlaylistViewModel.
    /// Coordinates updating track list.
    /// </summary>
    private async void OnSmartPlaylistSelected(object? sender, Library.SmartPlaylist? playlist)
    {
        if (playlist == null) return;

        try
        {
            IsLoading = true;
            
            // Phase 23: Smart Crates (DB-backed)
            if (playlist.Definition != null)
            {
                _notificationService.Show("Smart Crate", $"Evaluating rules for '{playlist.Name}'...", NotificationType.Information);
                
                // 1. Evaluate rules against database (Global Index)
                var ids = await _smartCrateService.GetMatchingTrackIdsAsync(playlist.Definition);
                
                // 2. Load matching tracks via TrackListViewModel
                await Tracks.LoadSmartCrateAsync(ids);
                
                _logger.LogInformation("Loaded Smart Crate '{Name}' with {Count} tracks", playlist.Name, ids.Count);
            }
            else
            {
                // In-memory smart playlist fallback for playlists without DB-backed definitions.
                _notificationService.Show("Smart Playlist", $"Loading {playlist.Name}", NotificationType.Information);
                
                // Execute filter on loaded memory state
                var tracks = SmartPlaylists.RefreshSmartPlaylist(playlist);
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                {
                    Tracks.CurrentProjectTracks = tracks;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load smart playlist {Name}", playlist.Name);
            _notificationService.Show("Error", "Failed to load crate.", NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }


}
