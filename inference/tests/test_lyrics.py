from singularity_inference.lyrics import (
    AlignedWord,
    LyricLineText,
    Word,
    distribute_evenly,
    fill_unaligned,
    line_windows_from_transcript,
    normalize,
    parse_lrc,
    parse_plain,
    split_word_timing,
    syllables,
    windows_from_lrc,
    words_of,
)


def test_parse_lrc_handles_tags_multi_stamps_word_stamps_and_blanks():
    lrc = "\n".join([
        "[ar:Queen]",
        "[ti:Bohemian Rhapsody]",
        "[00:01.25] Is this the real life",
        "[00:04.1]<00:04.10> Is <00:04.50>this just fantasy",
        "[00:08.00]",
        "[00:10.000][01:00.50] Mama",
    ])
    lines = parse_lrc(lrc, duration_ms=70_000)
    assert [(l.text, l.start_ms, l.end_ms) for l in lines] == [
        ("Is this the real life", 1250, 4100),
        ("Is this just fantasy", 4100, 8000),  # the blank stamp ends it
        ("Mama", 10_000, 60_500),
        ("Mama", 60_500, 70_000),
    ]


def test_parse_plain_drops_blank_lines_and_section_headers():
    assert [l.text for l in parse_plain("[Verse 1]\nIs this the real life\n\n(Chorus)\nMama, just killed a man\n")] == [
        "Is this the real life", "Mama, just killed a man",
    ]


def test_normalize_keeps_only_alignable_characters():
    assert normalize("Mama,") == "mama"
    assert normalize("Don’t") == "don't"
    assert normalize("Café") == "cafe"
    assert normalize("'til") == "til"
    assert normalize("1975") == ""
    assert normalize("愛") == ""


def test_syllables_use_language_dictionary():
    assert syllables("fantasy", "en") == ["fan", "ta", "sy"]
    assert syllables("vrijheid", "nl") == ["vrij", "heid"]
    assert syllables("fantasy", None) == ["fantasy"]
    assert syllables("fantasy", "xx") == ["fantasy"]
    assert syllables("the", "en") == ["the"]


def test_split_word_timing_is_proportional_and_contiguous():
    parts = split_word_timing(AlignedWord("fantasy", 1000, 1700, 0.9), "en")
    assert [p[0] for p in parts] == ["fan", "ta", "sy"]
    assert parts[0][1] == 1000 and parts[-1][2] == 1700
    assert all(a[2] == b[1] for a, b in zip(parts, parts[1:]))


def test_distribute_evenly_weights_by_length():
    words = words_of("a bbb")
    out = distribute_evenly(words, 0, 400, 0.5)
    assert [(w.start_ms, w.end_ms) for w in out] == [(0, 100), (100, 400)]
    assert all(w.confidence == 0.5 for w in out)


def test_fill_unaligned_spreads_missing_words_between_neighbours():
    words = words_of("one 2 3 four")
    aligned = {0: AlignedWord("one", 0, 100, 0.9), 3: AlignedWord("four", 500, 600, 0.9)}
    out = fill_unaligned(words, aligned, 0, 700)
    assert [w.text for w in out] == ["one", "2", "3", "four"]
    assert (out[1].start_ms, out[2].end_ms) == (100, 500)
    assert out[1].confidence == 0.0


def test_windows_from_lrc_pad_and_clamp():
    lines = [LyricLineText("a b", 100, 2000), LyricLineText("c", 2000, None)]
    windows = windows_from_lrc(lines, duration_ms=2500)
    assert [(w.start_ms, w.end_ms) for w in windows] == [(0, 2300), (1700, 2500)]
    assert windows[0].words == (Word("a", "a"), Word("b", "b"))


def test_transcript_windows_follow_matches_and_interpolate_gaps():
    lines = [LyricLineText("is this the real life"), LyricLineText("ooh ooh ooh"), LyricLineText("is this just fantasy")]
    hyp = [AlignedWord(t, s, s + 200, 0.9) for t, s in [
        ("Is", 1000), ("this", 1200), ("the", 1400), ("real", 1600), ("life", 1800),
        ("is", 5000), ("this", 5200), ("just", 5400), ("fantasy", 5600),
    ]]
    windows = line_windows_from_transcript(lines, hyp, duration_ms=10_000, pad_ms=0)
    assert (windows[0].start_ms, windows[0].end_ms) == (1000, 2000)
    assert (windows[1].start_ms, windows[1].end_ms) == (2000, 5000)  # unmatched: the gap between neighbours
    assert (windows[2].start_ms, windows[2].end_ms) == (5000, 5800)


def test_transcript_windows_with_no_matches_split_the_whole_song():
    lines = [LyricLineText("a"), LyricLineText("b")]
    windows = line_windows_from_transcript(lines, [], duration_ms=1000, pad_ms=0)
    assert [(w.start_ms, w.end_ms) for w in windows] == [(0, 500), (500, 1000)]
