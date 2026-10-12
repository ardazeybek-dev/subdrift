module Subdrift.Tests.AlignTests

open System
open System.IO
open Xunit
open Subdrift.Core
open Subdrift.Core.Subtitle

[<Fact>]
let ``FFT correlation matches the naive definition`` () =
    let rng = Random 3
    let reference = Array.init 500 (fun _ -> rng.NextDouble() * 2.0 - 1.0)
    let probe = Array.init 180 (fun _ -> float (rng.Next 2))
    let size = Fft.nextPow2 (reference.Length + probe.Length + 60)
    let fast = Fft.crossCorrelate (Fft.spectrum size reference) probe 60
    let slow = Fft.crossCorrelateNaive reference probe 60
    Array.iter2 (fun (a: float) b -> Assert.Equal(b, a, 6)) fast slow

let private truth = Synth.cues 11 900.0 false
let private reference = Signal.activityFromCues truth

/// Max absolute start-time error of the aligned cues against the truth.
let private maxError (result: Align.Result) =
    List.map2 (fun (a: Cue) (t: Cue) -> abs (a.Start - t.Start)) result.Aligned truth |> List.max

[<Fact>]
let ``recovers a constant offset`` () =
    let broken = truth |> transform 1.0 -7.3
    let r = Align.align Align.defaults reference broken
    Assert.Equal(7.3, r.Offset, 2)
    Assert.Equal(1.0, r.Scale)
    Assert.Single r.Segments |> ignore
    Assert.True(maxError r < 0.02)

[<Fact>]
let ``recovers a PAL framerate conversion`` () =
    let broken = truth |> Synth.breakTiming 25.0 23.976 1.5 1e9 0.0
    let r = Align.align Align.defaults reference broken
    Assert.Equal(25.0 / 23.976, r.Scale, 6)
    Assert.True(maxError r < 0.05, $"max error {maxError r}")

[<Fact>]
let ``detects a scene cut and shifts only the cues after it`` () =
    let broken = truth |> Synth.breakTiming 24.0 24.0 0.0 400.0 4.0
    let r = Align.align Align.defaults reference broken
    Assert.Equal(2, r.Segments.Length)
    let cut = r.Segments[1]
    let firstAfter = truth |> List.findIndex (fun c -> c.Start >= 400.0)
    Assert.Equal(firstAfter + 1, cut.FirstCue)
    Assert.True(maxError r < 0.02)

[<Fact>]
let ``no split keeps a single segment`` () =
    let broken = truth |> Synth.breakTiming 24.0 24.0 0.0 400.0 4.0
    let r = Align.align { Align.defaults with Split = false } reference broken
    Assert.Single r.Segments |> ignore

[<Fact>]
let ``already aligned input stays put`` () =
    let r = Align.align Align.defaults reference truth
    Assert.Equal(0.0, r.Offset, 2)
    Assert.True(r.CoverageAfter > 0.99)
    Assert.True(maxError r < 0.011)

[<Fact>]
let ``aligns against audio through the WAV decoder and VAD`` () =
    let duration = 240.0
    let cues = Synth.cues 5 duration false
    let wav = Path.Combine(Path.GetTempPath(), $"subdrift-{Guid.NewGuid():N}.wav")

    try
        Synth.writeWav wav 5 duration cues
        let activity = Audio.activityOfMedia "ffmpeg" Audio.defaultVad wav
        Assert.InRange(float activity.Values.Length / Signal.Rate, duration - 0.1, duration + 0.1)
        let broken = cues |> transform 1.0 3.25
        let r = Align.align Align.defaults activity broken
        Assert.InRange(r.Offset, -3.35, -3.15)
        Assert.True(r.CoverageAfter > 0.9, $"coverage {r.CoverageAfter}")
    finally
        File.Delete wav

[<Fact>]
let ``report embeds data and escapes closing script tags`` () =
    let cues = [ { Index = 1; Start = 1.0; End = 2.0; Text = "</script><b>x</b>" } ]
    let r = Align.align Align.defaults (Signal.activityFromCues cues) cues

    let html =
        Report.render
            { Subtitle = "a.srt"
              Reference = "b.srt"
              ReferenceKind = "subtitle"
              Output = "c.srt"
              ElapsedMs = 1L }
            (Signal.activityFromCues cues)
            r

    Assert.DoesNotContain("/*__SUBDRIFT_DATA__*/null", html)
    Assert.DoesNotContain("</script><b>", html)
    Assert.Contains("\"scaleLabel\"", html)
