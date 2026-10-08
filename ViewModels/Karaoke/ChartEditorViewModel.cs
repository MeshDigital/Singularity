using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Singularity.Contracts.Song;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Editing;
using Singularity.Karaoke.Library;
using Singularity.Karaoke.Scoring;
using Singularity.Karaoke.Sync;
using Singularity.Services;
using Singularity.Services.Karaoke;
using Singularity.Views;
using Singularity.Views.Avalonia.Karaoke;

namespace Singularity.ViewModels.Karaoke;

/// <summary>A line in the editor's list.</summary>
public sealed record EditorLineRow(ChartLine Line, string Text, bool ToCheck)
{
    public string Number => Line.Number.ToString();
}

/// <summary>
/// The correction editor for a song Singularity made: its lines (those that don't match the original singer marked),
/// the selected line drawn with the singer's pitch over its notes, and the fixes that matter most: move the whole
/// chart, move or transpose a line, change a note, or take the singer's pitch for a line. Every change can be undone;
/// playing the line shows a playhead. Saving keeps the first version as song.txt.orig and refreshes the check in
/// metadata.json. Songs from the user's own folders aren't edited (Singularity never writes there).
/// </summary>
public sealed class ChartEditorViewModel : ReactiveObject, IDisposable
{
    private readonly KaraokeLibrary _library;
    private readonly INavigationService _navigation;
    private readonly ILogger<ChartEditorViewModel> _logger;
    private readonly SingAudioEngine _audio;
    private readonly Stack<UltraStarSong> _undo = new();
    private readonly DispatcherTimer _playTimer;

    private SongEntry? _entry;
    private UltraStarSong? _song;
    private ReferencePitch? _singer;
    private EditorLineRow? _selectedLine;
    private int _selectedNote = -1;
    private ChartLineFrame? _frame;
    private string _status = "";
    private bool _isDirty;
    private double _playUntilMs;
    private bool _userPickedLine; // until then, the first line to check is shown once the check is known
    private bool _refreshing;

    public ChartEditorViewModel(KaraokeLibrary library, INavigationService navigation, ILogger<ChartEditorViewModel> logger, ILogger<SingAudioEngine> audioLogger)
    {
        _library = library;
        _navigation = navigation;
        _logger = logger;
        _audio = new SingAudioEngine(audioLogger);
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playTimer.Tick += (_, _) => OnPlayTick();

        ShiftAllCommand = new RelayCommand<string>(ms => Edit(s => (ChartEdit.ShiftAll(s, int.Parse(ms!)), $"Whole chart {int.Parse(ms!):+0;-0} ms")));
        ShiftLineCommand = new RelayCommand<string>(beats => EditLine((s, line) =>
        {
            var (moved, by) = ChartEdit.ShiftLine(s, 0, line, int.Parse(beats!));
            return (moved, by == 0 ? "The line can't move further that way" : $"Line {line} {by:+0;-0} beats");
        }));
        TransposeLineCommand = new RelayCommand<string>(st => EditLine((s, line) =>
            (ChartEdit.TransposeLine(s, 0, line, int.Parse(st!)), $"Line {line} {int.Parse(st!):+0;-0} semitone")));
        NoteToneCommand = new RelayCommand<string>(st =>
        {
            if (_selectedNote < 0 || _song is null) return;
            int tone = _song.Voices[0].Notes[_selectedNote].MidiTone + int.Parse(st!);
            Edit(s => (ChartEdit.SetTone(s, 0, _selectedNote, tone), $"Note {Singularity.Karaoke.Pitch.NoteNames.Name(tone)}"));
        });
        UseSingerCommand = new RelayCommand(() => EditLine((s, line) =>
        {
            if (_singer is null) return (s, "The singer's pitch isn't read yet");
            var (fixedSong, changed) = ChartEdit.UseSingerPitch(s, 0, line, _singer);
            return (fixedSong, changed == 0 ? "The singer sings this line as charted (or not clearly enough)" : $"{changed} notes moved to where the singer sings");
        }));
        FitToSingerCommand = new RelayCommand(() => Edit(s =>
        {
            if (_singer is null) return (s, "The singer's pitch isn't read yet");
            var (fitted, ms, semitones, before, after) = ChartEdit.FitToSinger(s, _singer);
            if (ReferenceEquals(fitted, s)) return (s, $"The chart already sits where the singer sings ({before:P0} of the singing on its notes)");
            var how = string.Join(" and ", new[] { ms != 0 ? $"moved {ms:+0;-0} ms" : null, semitones != 0 ? $"{semitones:+0;-0} semitones" : null }.Where(x => x is not null));
            return (fitted, $"Whole chart {how}: {before:P0} → {after:P0} of the singing on its notes");
        }));
        UndoCommand = new RelayCommand(Undo);
        PlayLineCommand = new RelayCommand(PlayLine);
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        BackCommand = new RelayCommand(() =>
        {
            StopPlaying();
            _navigation.NavigateTo("Karaoke");
        });
    }

    public string Title => _song?.Title ?? "";
    public string Artist => _song?.Artist ?? "";

    public ObservableCollection<EditorLineRow> Lines { get; } = new();

    public EditorLineRow? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (_refreshing) return; // the list being refilled, not the user
            if (!ReferenceEquals(value, _selectedLine)) _userPickedLine = true;
            this.RaiseAndSetIfChanged(ref _selectedLine, value);
            _selectedNote = -1;
            StopPlaying();
            UpdateFrame();
        }
    }

    public ChartLineFrame? Frame { get => _frame; private set => this.RaiseAndSetIfChanged(ref _frame, value); }
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }
    public bool IsDirty { get => _isDirty; private set => this.RaiseAndSetIfChanged(ref _isDirty, value); }

    private string _summary = "";

    /// <summary>"82% of the notes match the singer · 3 lines to check".</summary>
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    public ICommand ShiftAllCommand { get; }
    public ICommand ShiftLineCommand { get; }
    public ICommand TransposeLineCommand { get; }
    public ICommand NoteToneCommand { get; }
    public ICommand UseSingerCommand { get; }
    public ICommand FitToSingerCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand PlayLineCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand BackCommand { get; }

    /// <summary>Whether a song can be opened here: one Singularity made (it has metadata.json), so its folder is ours.</summary>
    public static bool CanEdit(SongEntry entry) =>
        entry.IsPlayable && File.Exists(Path.Combine(entry.Folder, SongPackage.MetadataFileName));

    public void Open(SongEntry entry)
    {
        StopPlaying();
        _entry = entry;
        _song = UltraStarSerializer.ReadFile(entry.TxtPath);
        _singer = null;
        _undo.Clear();
        IsDirty = false;
        _userPickedLine = false;
        Status = "Reading the singer's pitch…";
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Artist));
        RefreshLines(keepSelection: false);

        if (_song.VocalsFile is { } vocals && File.Exists(Path.Combine(entry.Folder, vocals)))
        {
            var song = _song;
            _ = Task.Run(() =>
            {
                try
                {
                    var (mono, rate) = SingViewModel.ReadMono(Path.Combine(entry.Folder, vocals));
                    var singer = ReferencePitch.FromVocals(mono, rate);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ReferenceEquals(_entry, entry)) return;
                        _singer = singer;
                        Status = "The blue dots are the original singer: notes should sit on them.";
                        RefreshLines(keepSelection: _userPickedLine);
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException)
                {
                    _logger.LogWarning(ex, "Chart editor: couldn't read the vocals of {Folder}", entry.Folder);
                    Dispatcher.UIThread.Post(() => Status = "The singer's pitch couldn't be read; the chart can still be edited.");
                }
            });
        }
        else Status = "This song has no separated vocals: the singer's pitch can't be shown.";
    }

    private void RefreshLines(bool keepSelection)
    {
        if (_song is null) return;
        int selected = _selectedLine?.Line.Number ?? 0;
        var check = _singer is null ? null : ChartNoteCheck.Check(_song, _singer);
        var toCheck = check?.LinesToCheck.ToHashSet() ?? new HashSet<int>();
        var notes = _song.Voices[0].Notes;
        _refreshing = true;
        try
        {
            Lines.Clear();
            foreach (var line in ChartEdit.Lines(_song.Voices[0]))
            {
                var text = string.Concat(Enumerable.Range(line.From, line.Count).Select(i => notes[i].Syllable)).Trim();
                Lines.Add(new EditorLineRow(line, text.Replace("~", ""), toCheck.Contains(line.Number)));
            }
        }
        finally
        {
            _refreshing = false;
        }
        Summary = check is null ? $"{Lines.Count} lines"
            : $"{check.Agreement:P0} of the notes match the singer · {toCheck.Count} of {Lines.Count} lines to check";
        _selectedLine = keepSelection ? Lines.FirstOrDefault(l => l.Line.Number == selected) : null;
        _selectedLine ??= Lines.FirstOrDefault(l => l.ToCheck) ?? Lines.FirstOrDefault();
        this.RaisePropertyChanged(nameof(SelectedLine));
        UpdateFrame();
    }

    public void SelectNote(int index)
    {
        _selectedNote = index;
        UpdateFrame();
        if (_song is not null && index >= 0)
            Status = $"Note \"{_song.Voices[0].Notes[index].Syllable.Trim()}\" selected: ▲▼ change its pitch.";
    }

    private void UpdateFrame()
    {
        Frame = _song is null || _selectedLine is null ? null
            : new ChartLineFrame(_song, 0, _selectedLine.Line, _singer, _selectedNote, _playTimer.IsEnabled ? _audio.PositionMs : null);
    }

    private void EditLine(Func<UltraStarSong, int, (UltraStarSong Song, string Message)> change)
    {
        if (_selectedLine is { } line) Edit(s => change(s, line.Line.Number));
    }

    private void Edit(Func<UltraStarSong, (UltraStarSong Song, string Message)> change)
    {
        if (_song is null) return;
        var (edited, message) = change(_song);
        Status = message;
        if (ReferenceEquals(edited, _song)) return;
        _undo.Push(_song);
        _song = edited;
        IsDirty = true;
        RefreshLines(keepSelection: true);
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _song = _undo.Pop();
        IsDirty = _undo.Count > 0;
        Status = "Undone";
        RefreshLines(keepSelection: true);
    }

    private void PlayLine()
    {
        if (_entry?.AudioPath is not { } audio || _song is null || _selectedLine is null) return;
        if (_playTimer.IsEnabled)
        {
            StopPlaying();
            return;
        }
        var notes = _song.Voices[0].Notes;
        var line = _selectedLine.Line;
        double from = _song.BeatToMs(notes[line.From].StartBeat) - 600;
        _playUntilMs = _song.BeatToMs(Enumerable.Range(line.From, line.Count).Max(i => notes[i].StartBeat + notes[i].DurationBeats)) + 600;
        try
        {
            _audio.Load(audio, Math.Max(0, from));
            _audio.Play();
            _playTimer.Start();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = "The song couldn't be played: " + ex.Message;
        }
    }

    private void OnPlayTick()
    {
        if (_audio.PositionMs >= _playUntilMs) StopPlaying();
        else UpdateFrame();
    }

    private void StopPlaying()
    {
        if (!_playTimer.IsEnabled) return;
        _playTimer.Stop();
        _audio.Stop();
        UpdateFrame();
    }

    private async Task SaveAsync()
    {
        if (_entry is null || _song is null || !IsDirty) return;
        try
        {
            var original = _entry.TxtPath + ".orig";
            if (!File.Exists(original)) File.Copy(_entry.TxtPath, original);
            await File.WriteAllTextAsync(_entry.TxtPath, UltraStarSerializer.Write(_song), new UTF8Encoding(false));

            // The check in metadata.json follows the corrected chart.
            if (_singer is not null && File.Exists(Path.Combine(_entry.Folder, SongPackage.MetadataFileName)))
            {
                var metadata = await SongPackage.ReadMetadataAsync(_entry.Folder);
                var check = ChartNoteCheck.Check(_song, _singer);
                await SongPackage.WriteMetadataAsync(_entry.Folder, metadata with
                {
                    Check = new ChartCheck(Math.Round(check.Agreement, 3), check.LinesToCheck, metadata.Check?.NotesCorrected ?? 0, check.Mismatch),
                });
            }
            _undo.Clear();
            IsDirty = false;
            Status = "Saved. The first version is kept as song.txt.orig.";
            _logger.LogInformation("Chart editor: saved {Path}", _entry.TxtPath);
            _library.NotifySongsAdded();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't save: " + ex.Message;
        }
    }

    public void Dispose()
    {
        _playTimer.Stop();
        _audio.Dispose();
    }
}
