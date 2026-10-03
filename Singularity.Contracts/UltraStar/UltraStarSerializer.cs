using System.Globalization;
using System.Text;

namespace Singularity.Contracts.UltraStar;

/// <summary>
/// Reads and writes UltraStar song.txt (format 1.1.0). Writing is strict and canonical (absolute
/// beats, #AUDIO plus #MP3, #P1/#P2 for duets); reading is lenient about what older files contain:
/// comma decimals, #MP3 without #AUDIO, #DUETSINGERP1/2, #RELATIVE:YES, a BOM, CRLF.
/// </summary>
public static class UltraStarSerializer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly HashSet<string> KnownHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "VERSION", "TITLE", "ARTIST", "LANGUAGE", "GENRE", "YEAR", "EDITION", "CREATOR", "TAGS",
        "AUDIO", "MP3", "VOCALS", "INSTRUMENTAL", "COVER", "BACKGROUND", "VIDEO", "VIDEOGAP",
        "BPM", "GAP", "START", "END", "PREVIEWSTART", "MEDLEYSTARTBEAT", "MEDLEYENDBEAT", "RELATIVE",
        "P1", "P2", "DUETSINGERP1", "DUETSINGERP2",
    };

    public static string Write(UltraStarSong song)
    {
        if (song.Bpm <= 0 || double.IsNaN(song.Bpm))
            throw new ArgumentOutOfRangeException(nameof(song), "BPM must be positive.");
        if (song.Voices.Count is < 1 or > 2)
            throw new ArgumentException("A song needs one voice, or two for a duet.", nameof(song));

        var sb = new StringBuilder();
        void Header(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.Contains('\n') || value.Contains('\r'))
                throw new ArgumentException($"Header #{key} contains a line break.", nameof(song));
            sb.Append('#').Append(key).Append(':').Append(value).Append('\n');
        }
        static string Seconds(int ms) => (ms / 1000.0).ToString("0.###", Inv);

        Header("VERSION", UltraStarSong.FormatVersion);
        Header("TITLE", song.Title);
        Header("ARTIST", song.Artist);
        Header("LANGUAGE", song.Language);
        Header("GENRE", song.Genre);
        Header("EDITION", song.Edition);
        Header("CREATOR", song.Creator);
        Header("TAGS", song.Tags);
        Header("YEAR", song.Year?.ToString(Inv));
        Header("AUDIO", song.AudioFile);
        Header("MP3", song.AudioFile);
        Header("VOCALS", song.VocalsFile);
        Header("INSTRUMENTAL", song.InstrumentalFile);
        Header("COVER", song.CoverFile);
        Header("BACKGROUND", song.BackgroundFile);
        Header("VIDEO", song.VideoFile);
        if (song.VideoFile is not null && song.VideoGapMs != 0)
            Header("VIDEOGAP", Seconds(song.VideoGapMs));
        Header("BPM", song.Bpm.ToString("0.###", Inv));
        Header("GAP", song.GapMs.ToString(Inv));
        Header("START", song.StartMs is { } start ? Seconds(start) : null);
        Header("END", song.EndMs?.ToString(Inv));
        Header("PREVIEWSTART", song.PreviewStartMs is { } preview ? Seconds(preview) : null);
        Header("MEDLEYSTARTBEAT", song.MedleyStartBeat?.ToString(Inv));
        Header("MEDLEYENDBEAT", song.MedleyEndBeat?.ToString(Inv));
        if (song.IsDuet)
        {
            Header("P1", song.Voices[0].SingerName);
            Header("P2", song.Voices[1].SingerName);
        }
        foreach (var (key, value) in song.ExtraHeaders)
            Header(key, value);

        for (int v = 0; v < song.Voices.Count; v++)
        {
            if (song.IsDuet) sb.Append('P').Append(v + 1).Append('\n');
            foreach (var note in song.Voices[v].Notes)
                WriteNote(sb, note);
        }

        sb.Append("E\n");
        return sb.ToString();
    }

    private static void WriteNote(StringBuilder sb, UltraStarNote note)
    {
        if (note.Type == NoteType.LineBreak)
        {
            sb.Append("- ").Append(note.StartBeat.ToString(Inv)).Append('\n');
            return;
        }
        if (note.Syllable.Contains('\n') || note.Syllable.Contains('\r'))
            throw new ArgumentException($"Syllable at beat {note.StartBeat} contains a line break.");

        sb.Append(Marker(note.Type)).Append(' ')
          .Append(note.StartBeat.ToString(Inv)).Append(' ')
          .Append(note.DurationBeats.ToString(Inv)).Append(' ')
          .Append((note.MidiTone - UltraStarNote.UltraStarPitchOffset).ToString(Inv)).Append(' ')
          .Append(note.Syllable).Append('\n');
    }

    /// <summary>Reads a song.txt from disk, detecting its encoding (see <see cref="Decode"/>).</summary>
    /// <exception cref="FormatException">The file isn't a readable UltraStar file.</exception>
    public static UltraStarSong ReadFile(string path) => Read(Decode(File.ReadAllBytes(path)));

    /// <summary>
    /// song.txt files carry no reliable encoding marker. Older ones are Windows-1252, newer ones
    /// UTF-8, sometimes with a BOM or an #ENCODING:UTF8 header. Order: BOM, #ENCODING, strict UTF-8,
    /// then Windows-1252 (which differs from Latin-1 in curly quotes and the euro sign).
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        var ascii = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var declared = ascii.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("#ENCODING:", StringComparison.OrdinalIgnoreCase));
        var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        if (declared is not null && declared[10..].Trim().Replace("-", "").Equals("UTF8", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetString(bytes);

        try
        {
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <exception cref="FormatException">The text isn't a readable UltraStar file.</exception>
    public static UltraStarSong Read(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerOrder = new List<string>();
        var voices = new List<List<UltraStarNote>> { new() };
        int current = 0;
        bool sawPlayerMarker = false;
        bool both = false, sawBoth = false;
        void AddNote(UltraStarNote note)
        {
            if (both)
            {
                voices[0].Add(note);
                voices[1].Add(note);
            }
            else
            {
                voices[current].Add(note);
            }
        }
        bool? relative = null;
        int lineOffset = 0; // #RELATIVE mode: beats are counted from the start of the current line
        int lineNo = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            if (line[0] == '#')
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue; // not a header we can read; tolerated like other players do
                var key = line[1..colon].Trim().ToUpperInvariant();
                if (headers.TryAdd(key, line[(colon + 1)..].Trim())) headerOrder.Add(key);
                continue;
            }

            relative ??= headers.TryGetValue("RELATIVE", out var rel) && rel.Equals("YES", StringComparison.OrdinalIgnoreCase);

            if (line[0] == 'E') break; // "E", and in the wild "E E" or "END"; no note type starts with E

            if (line[0] == 'P' && TryParsePlayer(line, out var player))
            {
                // P1 / P2 select a voice. Legacy "P3" means both singers: its notes go into both voices.
                current = player == 2 ? 1 : 0;
                both = player == 3;
                sawBoth |= both;
                while (voices.Count <= (both ? 1 : current)) voices.Add(new List<UltraStarNote>());
                sawPlayerMarker = true;
                lineOffset = 0;
                continue;
            }

            if (line[0] == '-')
            {
                var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, Inv, out var beat))
                    throw new FormatException($"Line {lineNo}: line break without a beat.");
                AddNote(UltraStarNote.LineBreak(lineOffset + beat));
                if (relative == true)
                {
                    // "- <end> <next>": the following line's beats count from <next> (or <end> if absent).
                    lineOffset += parts.Length > 1 && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, Inv, out var next) ? next : beat;
                }
                continue;
            }

            var type = line[0] switch
            {
                ':' => NoteType.Regular,
                '*' => NoteType.Golden,
                'F' => NoteType.Freestyle,
                'R' => NoteType.Rap,
                'G' => NoteType.RapGolden,
                _ => throw new FormatException($"Line {lineNo}: unknown line type '{line[0]}'."),
            };
            var note = ParseNote(type, line, lineNo);
            AddNote(lineOffset == 0 ? note : note with { StartBeat = note.StartBeat + lineOffset });
        }

        if (sawBoth)
        {
            // P3 blocks usually follow the P1 and P2 blocks, so merged voices need putting back in time order.
            for (int v = 0; v < voices.Count; v++)
                voices[v] = InTimeOrder(voices[v]);
        }

        string Required(string key) => headers.TryGetValue(key, out var v) && v.Length > 0
            ? v
            : throw new FormatException($"Missing #{key} header.");
        string? Optional(string key) => headers.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        int? OptionalInt(string key) => Optional(key) is { } v ? (int)Math.Round(ParseDecimal(v, key), MidpointRounding.AwayFromZero) : null;
        int? OptionalSecondsAsMs(string key) => Optional(key) is { } v ? (int)Math.Round(ParseDecimal(v, key) * 1000.0, MidpointRounding.AwayFromZero) : null;

        if (sawPlayerMarker && voices[0].Count == 0 && voices.Count > 1) voices.RemoveAt(0); // notes all under P2 only
        var isDuet = voices.Count > 1;

        return new UltraStarSong
        {
            Title = Required("TITLE"),
            Artist = Required("ARTIST"),
            Bpm = ParseDecimal(Required("BPM"), "BPM"),
            GapMs = OptionalInt("GAP") ?? 0,
            AudioFile = Optional("AUDIO") ?? Required("MP3"),
            VocalsFile = Optional("VOCALS"),
            InstrumentalFile = Optional("INSTRUMENTAL"),
            VideoFile = Optional("VIDEO"),
            VideoGapMs = OptionalSecondsAsMs("VIDEOGAP") ?? 0,
            CoverFile = Optional("COVER"),
            BackgroundFile = Optional("BACKGROUND"),
            Language = Optional("LANGUAGE"),
            Year = Optional("YEAR") is { } y && int.TryParse(y, NumberStyles.Integer, Inv, out var year) ? year : null,
            Genre = Optional("GENRE"),
            Edition = Optional("EDITION"),
            Creator = Optional("CREATOR"),
            Tags = Optional("TAGS"),
            StartMs = OptionalSecondsAsMs("START"),
            EndMs = OptionalInt("END"),
            PreviewStartMs = OptionalSecondsAsMs("PREVIEWSTART"),
            MedleyStartBeat = OptionalInt("MEDLEYSTARTBEAT"),
            MedleyEndBeat = OptionalInt("MEDLEYENDBEAT"),
            ExtraHeaders = headerOrder.Where(k => !KnownHeaders.Contains(k))
                .Select(k => new KeyValuePair<string, string>(k, headers[k])).ToArray(),
            Voices = voices.Select((notes, i) => new UltraStarVoice(notes,
                isDuet ? Optional(i == 0 ? "P1" : "P2") ?? Optional(i == 0 ? "DUETSINGERP1" : "DUETSINGERP2") : null)).ToArray(),
        };
    }

    /// <summary>Sorted by beat (a line break before a note on the same beat), without doubled line breaks.</summary>
    private static List<UltraStarNote> InTimeOrder(List<UltraStarNote> notes)
    {
        var sorted = notes.Select((n, i) => (n, i))
            .OrderBy(x => x.n.StartBeat).ThenBy(x => x.n.Type == NoteType.LineBreak ? 0 : 1).ThenBy(x => x.i)
            .Select(x => x.n).ToList();
        var result = new List<UltraStarNote>(sorted.Count);
        foreach (var n in sorted)
        {
            if (n.Type == NoteType.LineBreak && (result.Count == 0 || result[^1].Type == NoteType.LineBreak)) continue;
            result.Add(n);
        }
        while (result.Count > 0 && result[^1].Type == NoteType.LineBreak) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static bool TryParsePlayer(string line, out int player)
    {
        var rest = line[1..].Trim();
        return int.TryParse(rest, NumberStyles.None, Inv, out player) && player is >= 1 and <= 3;
    }

    private static UltraStarNote ParseNote(NoteType type, string line, int lineNo)
    {
        // "<m> <start> <length> <pitch> <text>" — the text is everything after the single space that
        // follows the pitch, so a leading space in the syllable (word start) is preserved.
        int pos = 1;
        int NextInt(string what)
        {
            while (pos < line.Length && line[pos] == ' ') pos++;
            int start = pos;
            if (pos < line.Length && line[pos] == '-') pos++;
            while (pos < line.Length && char.IsDigit(line[pos])) pos++;
            if (!int.TryParse(line.AsSpan(start, pos - start), NumberStyles.AllowLeadingSign, Inv, out var value))
                throw new FormatException($"Line {lineNo}: invalid {what}.");
            return value;
        }

        int startBeat = NextInt("start beat");
        int length = NextInt("length");
        int pitch = NextInt("pitch");
        var syllable = pos < line.Length && line[pos] == ' ' ? line[(pos + 1)..] : line[pos..];
        return new UltraStarNote(type, startBeat, length, pitch + UltraStarNote.UltraStarPitchOffset, syllable);
    }

    private static double ParseDecimal(string value, string key)
    {
        // Older files often use a comma as the decimal separator ("#BPM:87,2").
        if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, Inv, out var result))
            return result;
        throw new FormatException($"#{key} is not a number: '{value}'.");
    }

    private static char Marker(NoteType type) => type switch
    {
        NoteType.Regular => ':',
        NoteType.Golden => '*',
        NoteType.Freestyle => 'F',
        NoteType.Rap => 'R',
        NoteType.RapGolden => 'G',
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
