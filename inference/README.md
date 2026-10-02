# singularity-inference

The Python worker that turns a song's audio into an UltraStar chart: it separates the vocal stem, aligns lyric syllables to it and tracks their pitch. The Singularity app launches it as a child process. The two talk over newline-delimited JSON: commands go in on stdin and events come out on stdout. Diagnostics go to stderr, which the app never parses.

The message shapes are defined in `Singularity.Contracts` (C#), and `singularity_inference/schemas.py` mirrors them. Both test suites parse the same fixture files in `../Singularity.Contracts/Fixtures`, so if either side drifts its tests fail.

```powershell
py -3.11 -m venv .venv
.venv\Scripts\python -m pip install -e .[dev]
.venv\Scripts\python -m pytest
```
