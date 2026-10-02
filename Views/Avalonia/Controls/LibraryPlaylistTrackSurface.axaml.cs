using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Singularity.Services;
using Singularity.ViewModels;
using Singularity.ViewModels.Library;
using Singularity.Views.Avalonia;

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace Singularity.Views.Avalonia.Controls;

public partial class LibraryPlaylistTrackSurface : UserControl
{
    private int _selectionAnchorIndex = -1;
    private int _focusedIndex = -1;

    // Drag-initiation state: a track row press becomes a drag once the pointer moves past the
    // threshold, so it can be dropped onto a playlist in the sidebar or the player queue.
    private Point? _dragStartPoint;
    private PlaylistTrackViewModel? _dragCandidateTrack;

    public LibraryPlaylistTrackSurface()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
    }

    private void OnRootPointerExited(object? sender, PointerEventArgs e)
    {

        // Mouse left the library surface — stop any active hover-preview
        if (DataContext is TrackListViewModel vm)
            vm.StopPreview();
    }

    private void OnTrackRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Control source && source.GetSelfAndVisualAncestors().OfType<Button>().Any())
            return;

        if (DataContext is not TrackListViewModel vm || sender is not Border row || row.DataContext is not PlaylistTrackViewModel track)
            return;

        if (track.IsPlaceholder)
            return;

        var index = vm.FilteredTracks.IndexOf(track);
        if (index < 0)
            return;

        _dragStartPoint = e.GetPosition(this);
        _dragCandidateTrack = track;

        ApplyPointerSelection(vm, index, e.KeyModifiers);
        _focusedIndex = index;
        UpdateFocusedRowVisual(vm);
        Focus();
        e.Handled = true;
    }

    private void OnTrackRowPointerEntered(object? sender, PointerEventArgs e)
    {
        // Trigger hover-preview for downloaded tracks (debounced inside the service)
        if (DataContext is TrackListViewModel vm && sender is Border row && row.DataContext is PlaylistTrackViewModel track && !track.IsPlaceholder)
            vm.PreviewTrack(track);

        OnTrackRowPointerMoved(sender, e);
    }

    private async void OnTrackRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not TrackListViewModel vm || sender is not Border row || row.DataContext is not PlaylistTrackViewModel track)
            return;

        if (track.IsPlaceholder)
            return;

        if (_dragStartPoint.HasValue && ReferenceEquals(_dragCandidateTrack, track))
        {
            var current = e.GetCurrentPoint(this);
            if (!current.Properties.IsLeftButtonPressed)
            {
                _dragStartPoint = null;
                _dragCandidateTrack = null;
            }
            else
            {
                var diff = current.Position - _dragStartPoint.Value;
                if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
                {
                    _dragStartPoint = null;
                    _dragCandidateTrack = null;

                    if (!string.IsNullOrEmpty(track.GlobalId))
                    {
                        var data = new DataObject();
                        data.Set(DragContext.LibraryTrackFormat, track.GlobalId);
                        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
                    }

                    return;
                }
            }
        }

    }

    private void OnTrackRowPointerExited(object? sender, PointerEventArgs e)
    {
        // Keep current magnetic state until another row/gap claims hover.
        _dragStartPoint = null;
        _dragCandidateTrack = null;
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TrackListViewModel vm || vm.FilteredTracks.Count == 0)
            return;

        var currentIndex = _focusedIndex >= 0 ? _focusedIndex : ResolveLeadSelectionIndex(vm);
        if (currentIndex < 0)
            currentIndex = 0;

        var targetIndex = currentIndex;
        switch (e.Key)
        {
            case Key.Up:
                targetIndex = currentIndex - 1;
                break;
            case Key.Down:
                targetIndex = currentIndex + 1;
                break;
            case Key.PageUp:
                targetIndex = currentIndex - 10;
                break;
            case Key.PageDown:
                targetIndex = currentIndex + 10;
                break;
            case Key.Home:
                targetIndex = 0;
                break;
            case Key.End:
                targetIndex = vm.FilteredTracks.Count - 1;
                break;
            case Key.Enter:
                ExecuteLeadTrackPrimaryAction(vm);
                e.Handled = true;
                return;
            case Key.Space:
                ExecuteFocusedSpaceAction(vm, e.KeyModifiers);
                e.Handled = true;
                return;
            default:
                return;
        }

        targetIndex = Clamp(targetIndex, 0, vm.FilteredTracks.Count - 1);
        ApplyKeyboardSelection(vm, targetIndex, e.KeyModifiers);
        _focusedIndex = targetIndex;
        UpdateFocusedRowVisual(vm);
        e.Handled = true;
    }

    private void ApplyPointerSelection(TrackListViewModel vm, int index, KeyModifiers modifiers)
    {
        var candidate = vm.FilteredTracks[index];
        if (candidate.IsPlaceholder)
            return;

        if ((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift && _selectionAnchorIndex >= 0)
        {
            SetRangeSelection(vm, _selectionAnchorIndex, index);
            return;
        }

        if ((modifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            var next = vm.SelectedTracks.ToList();
            if (next.Contains(candidate))
                next.Remove(candidate);
            else
                next.Add(candidate);

            _selectionAnchorIndex = index;
            ApplySelectionSet(vm, next);
            return;
        }

        _selectionAnchorIndex = index;
        ApplySelectionSet(vm, new[] { candidate });
    }

    private void ApplyKeyboardSelection(TrackListViewModel vm, int index, KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Control) == KeyModifiers.Control)
            return;

        if ((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift && _selectionAnchorIndex >= 0)
        {
            SetRangeSelection(vm, _selectionAnchorIndex, index);
            return;
        }

        var candidate = vm.FilteredTracks[index];
        if (candidate.IsPlaceholder)
            return;

        _selectionAnchorIndex = index;
        ApplySelectionSet(vm, new[] { candidate });
    }

    private void SetRangeSelection(TrackListViewModel vm, int startIndex, int endIndex)
    {
        var from = startIndex <= endIndex ? startIndex : endIndex;
        var to = startIndex <= endIndex ? endIndex : startIndex;
        var range = new List<PlaylistTrackViewModel>();
        for (var idx = from; idx <= to; idx++)
        {
            var candidate = vm.FilteredTracks[idx];
            if (!candidate.IsPlaceholder)
                range.Add(candidate);
        }

        ApplySelectionSet(vm, range);
    }

    private void ApplySelectionSet(TrackListViewModel vm, IEnumerable<PlaylistTrackViewModel> nextSelection)
    {
        var selected = nextSelection.Distinct().ToList();

        foreach (var track in vm.SelectedTracks.ToList())
            track.IsSelected = false;

        foreach (var track in selected)
            track.IsSelected = true;

        vm.UpdateSelection(selected);
        UpdateFocusedRowVisual(vm);
    }

    private void ExecuteFocusedSpaceAction(TrackListViewModel vm, KeyModifiers modifiers)
    {
        var index = _focusedIndex >= 0 ? _focusedIndex : ResolveLeadSelectionIndex(vm);
        if (index < 0 || index >= vm.FilteredTracks.Count)
            return;

        var track = vm.FilteredTracks[index];
        if (track.IsPlaceholder)
            return;

        if ((modifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            ApplyPointerSelection(vm, index, KeyModifiers.Control);
            return;
        }

        ApplySelectionSet(vm, new[] { track });
    }

    private void UpdateFocusedRowVisual(TrackListViewModel vm)
    {
        var focusedTrack = (_focusedIndex >= 0 && _focusedIndex < vm.FilteredTracks.Count)
            ? vm.FilteredTracks[_focusedIndex]
            : vm.LeadSelectedTrack;

        // The placeholder is a single shared instance across every not-yet-loaded row, so if it
        // were left in place here every currently-realized placeholder row would satisfy the
        // ReferenceEquals check below simultaneously and all light up as "focused" at once.
        if (focusedTrack is { IsPlaceholder: true })
            focusedTrack = null;

        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            if (!border.Classes.Contains("track-row"))
                continue;

            if (focusedTrack is not null && ReferenceEquals(border.DataContext, focusedTrack))
                border.Classes.Add("focused");
            else
                border.Classes.Remove("focused");
        }
    }

    private static int ResolveLeadSelectionIndex(TrackListViewModel vm)
    {
        var lead = vm.LeadSelectedTrack;
        if (lead is null)
            return -1;

        return vm.FilteredTracks.IndexOf(lead);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static void ExecuteLeadTrackPrimaryAction(TrackListViewModel vm)
    {
        var lead = vm.LeadSelectedTrack;
        if (lead is null || vm.Operations?.PlayTrackCommand is null)
            return;

        if (vm.Operations.PlayTrackCommand.CanExecute(lead))
            vm.Operations.PlayTrackCommand.Execute(lead);
    }
}
