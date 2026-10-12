module Subdrift.Tests.SubtitleTests

open System.Text
open Xunit
open Subdrift.Core.Subtitle

let private srt =
    "1\r\n00:00:01,500 --> 00:00:03,250\r\nHello there.\r\nSecond line\r\n\r\n2\r\n00:00:04,000 --> 00:00:05,000\r\nBye.\r\n"

[<Fact>]
let ``parses SRT with CRLF and multi-line text`` () =
    let cues = parseSrt srt
    Assert.Equal(2, cues.Length)
    Assert.Equal(1.5, cues[0].Start, 6)
    Assert.Equal(3.25, cues[0].End, 6)
    Assert.Equal("Hello there.\nSecond line", cues[0].Text)

[<Fact>]
let ``parses WebVTT with short timestamps, ids, settings and NOTE blocks`` () =
    let vtt =
        "WEBVTT\n\nNOTE written by hand\n\nintro\n00:01.000 --> 00:02.500 align:start\nHi\n\n01:00:00.000 --> 01:00:01.000\nLate\n"

    let cues = parseVtt vtt
    Assert.Equal(2, cues.Length)
    Assert.Equal(1.0, cues[0].Start, 6)
    Assert.Equal("Hi", cues[0].Text)
    Assert.Equal(3600.0, cues[1].Start, 6)

[<Theory>]
[<InlineData("00:00:01,5", 1.5)>]
[<InlineData("00:00:01.05", 1.05)>]
[<InlineData("1:02:03,004", 3723.004)>]
let ``parses fractional seconds of any width`` (text: string, expected: float) =
    Assert.Equal(expected, parseTimestamp text, 6)

[<Fact>]
let ``SRT round-trips through render`` () =
    let cues = parseSrt srt
    Assert.Equal<Cue list>(cues, parseSrt (render Srt cues))

[<Fact>]
let ``VTT round-trips through render`` () =
    let cues = parseSrt srt
    let back = parseVtt (render Vtt cues)
    Assert.Equal<float list>(cues |> List.map (fun c -> c.Start), back |> List.map (fun c -> c.Start))

[<Fact>]
let ``formatTimestamp clamps negatives and rounds to milliseconds`` () =
    Assert.Equal("00:00:00,000", formatTimestamp ',' -3.0)
    Assert.Equal("01:01:01.235", formatTimestamp '.' 3661.2346)

[<Fact>]
let ``decode falls back to a legacy code page for invalid UTF-8`` () =
    Encoding.RegisterProvider CodePagesEncodingProvider.Instance
    let bytes = Encoding.GetEncoding("windows-1254").GetBytes "Çok güzel, değil mi?"
    let text, enc = decode "windows-1254" bytes
    Assert.Equal("Çok güzel, değil mi?", text)
    Assert.Equal("windows-1254", enc)

[<Fact>]
let ``decode strips a UTF-8 BOM`` () =
    let bytes = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (Encoding.UTF8.GetBytes "1")
    Assert.Equal("1", fst (decode "windows-1252" bytes))

[<Fact>]
let ``sanitize removes overlaps and drops cues before zero`` () =
    let cues =
        [ { Index = 1; Start = -5.0; End = -1.0; Text = "gone" }
          { Index = 2; Start = -0.5; End = 2.0; Text = "a" }
          { Index = 3; Start = 1.5; End = 3.0; Text = "b" } ]

    let out = sanitize cues
    Assert.Equal(2, out.Length)
    Assert.Equal(0.0, out[0].Start)
    Assert.True(out[0].End <= out[1].Start)
    Assert.Equal<int list>([ 1; 2 ], out |> List.map (fun c -> c.Index))
