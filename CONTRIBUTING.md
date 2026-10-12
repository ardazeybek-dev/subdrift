# Contributing

Thanks for helping make subtitles less painful.

1. Fork and create a branch from `main`.
2. `dotnet build && dotnet test` must pass.
3. For alignment changes, add a test in `tests/Subdrift.Tests/AlignTests.fs` that breaks a
   synthetic subtitle (`Synth.breakTiming`) and asserts it is recovered.
4. Keep `Subdrift.Core` free of console output — printing belongs in `Subdrift.Cli`.
5. Open a pull request describing the problem and attach a `--report` HTML if it is about alignment quality.

Bug reports with a report file are the fastest to fix: it contains timings, cue text and a 10 Hz
speech envelope, never the audio itself.
