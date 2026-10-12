/// Subtitle model plus SRT / WebVTT reading and writing.
module Subdrift.Core.Subtitle

open System
open System.IO
open System.Text
open System.Text.RegularExpressions

/// A single subtitle cue. Times are in seconds.
type Cue =
    { Index: int
      Start: float
      End: float
      Text: string }

    member c.Duration = c.End - c.Start

type Format =
    | Srt
    | Vtt

type Document =
    { Cues: Cue list
      Format: Format
      /// Name of the text encoding the file was decoded with (for diagnostics).
      Encoding: string }

exception SubtitleParseError of string

let private timeRegex =
    Regex(
        @"^\s*(?<s>(?:\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})\s*-->\s*(?<e>(?:\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})(?<rest>.*)$",
        RegexOptions.Compiled
    )

/// Parses `hh:mm:ss,mmm`, `hh:mm:ss.mmm` or `mm:ss.mmm` into seconds.
let parseTimestamp (s: string) : float =
    let s = s.Trim().Replace(',', '.')
    let parts = s.Split(':')

    let h, m, sec =
        match parts with
        | [| h; m; sec |] -> float (int h), float (int m), sec
        | [| m; sec |] -> 0.0, float (int m), sec
        | _ -> raise (SubtitleParseError $"bad timestamp '{s}'")

    let secParts = sec.Split('.')
    let whole = float (int secParts[0])

    let frac =
        if secParts.Length > 1 then
            // "5" -> 0.5, "05" -> 0.05, "005" -> 0.005
            float (int secParts[1]) / Math.Pow(10.0, float secParts[1].Length)
        else
            0.0

    h * 3600.0 + m * 60.0 + whole + frac

/// Formats seconds as `hh:mm:ss<sep>mmm`. Negative values clamp to zero.
let formatTimestamp (sep: char) (seconds: float) : string =
    let totalMs = int64 (Math.Round(Math.Max(0.0, seconds) * 1000.0))
    let ms = totalMs % 1000L
    let totalS = totalMs / 1000L
    let s = totalS % 60L
    let m = (totalS / 60L) % 60L
    let h = totalS / 3600L
    sprintf "%02d:%02d:%02d%c%03d" h m s sep ms

let private splitBlocks (text: string) =
    let normalized = text.Replace("\r\n", "\n").Replace('\r', '\n')
    Regex.Split(normalized, @"\n\s*\n") |> Array.filter (fun b -> b.Trim().Length > 0)

let private parseBlocks (blocks: string seq) : Cue list =
    blocks
    |> Seq.choose (fun block ->
        let lines = block.Split('\n') |> Array.map (fun l -> l.TrimEnd())
        // The timing line is the first line containing "-->"; anything before it is an id.
        match lines |> Array.tryFindIndex (fun l -> l.Contains("-->")) with
        | None -> None
        | Some ti ->
            let m = timeRegex.Match(lines[ti])

            if not m.Success then
                raise (SubtitleParseError $"bad timing line '{lines[ti]}'")

            let text = lines[ti + 1 ..] |> String.concat "\n"

            Some
                { Index = 0
                  Start = parseTimestamp m.Groups["s"].Value
                  End = parseTimestamp m.Groups["e"].Value
                  Text = text.Trim('\n') })
    |> Seq.mapi (fun i c -> { c with Index = i + 1 })
    |> List.ofSeq

/// Parses SRT text.
let parseSrt (text: string) : Cue list = splitBlocks text |> parseBlocks

/// Parses WebVTT text. NOTE / STYLE / REGION blocks are skipped.
let parseVtt (text: string) : Cue list =
    let blocks = splitBlocks text

    if blocks.Length = 0 || not (blocks[0].TrimStart('﻿').StartsWith("WEBVTT")) then
        raise (SubtitleParseError "missing WEBVTT header")

    blocks
    |> Seq.skip 1
    |> Seq.filter (fun b ->
        let t = b.TrimStart()
        not (t.StartsWith("NOTE") || t.StartsWith("STYLE") || t.StartsWith("REGION")))
    |> parseBlocks

let formatOfPath (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".vtt" -> Vtt
    | _ -> Srt

let isSubtitlePath (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".srt"
    | ".vtt" -> true
    | _ -> false

let private strictUtf8 = UTF8Encoding(false, true)

/// Decodes subtitle bytes: honours a BOM, tries strict UTF-8, then falls back to
/// a legacy code page (most old subtitles are windows-125x).
let decode (fallbackEncoding: string) (bytes: byte[]) : string * string =
    let hasPrefix (p: byte[]) =
        bytes.Length >= p.Length && Array.forall2 (=) p bytes[.. p.Length - 1]

    if hasPrefix [| 0xEFuy; 0xBBuy; 0xBFuy |] then
        Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "utf-8"
    elif hasPrefix [| 0xFFuy; 0xFEuy |] then
        Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16le"
    elif hasPrefix [| 0xFEuy; 0xFFuy |] then
        Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be"
    else
        try
            strictUtf8.GetString bytes, "utf-8"
        with :? DecoderFallbackException ->
            Encoding.RegisterProvider CodePagesEncodingProvider.Instance
            let enc = Encoding.GetEncoding fallbackEncoding
            enc.GetString bytes, enc.WebName

let parse (format: Format) (text: string) =
    match format with
    | Srt -> parseSrt text
    | Vtt -> parseVtt text

/// Loads a subtitle file, detecting the encoding and the format (by extension).
let load (fallbackEncoding: string) (path: string) : Document =
    let text, enc = File.ReadAllBytes path |> decode fallbackEncoding
    let format = formatOfPath path
    let cues = parse format text

    if cues.IsEmpty then
        raise (SubtitleParseError $"no cues found in '{path}'")

    { Cues = cues; Format = format; Encoding = enc }

let render (format: Format) (cues: Cue list) : string =
    let sb = StringBuilder()

    match format with
    | Srt ->
        cues
        |> List.iteri (fun i c ->
            sb
                .Append(i + 1)
                .Append('\n')
                .Append(formatTimestamp ',' c.Start)
                .Append(" --> ")
                .Append(formatTimestamp ',' c.End)
                .Append('\n')
                .Append(c.Text)
                .Append("\n\n")
            |> ignore)
    | Vtt ->
        sb.Append("WEBVTT\n\n") |> ignore

        cues
        |> List.iter (fun c ->
            sb
                .Append(formatTimestamp '.' c.Start)
                .Append(" --> ")
                .Append(formatTimestamp '.' c.End)
                .Append('\n')
                .Append(c.Text)
                .Append("\n\n")
            |> ignore)

    sb.ToString()

/// Writes cues as UTF-8 (no BOM) in the format implied by the path's extension.
let save (path: string) (cues: Cue list) =
    File.WriteAllText(path, render (formatOfPath path) cues, UTF8Encoding(false))

/// Applies `t -> scale * t + offset` to every cue.
let transform (scale: float) (offset: float) (cues: Cue list) =
    cues
    |> List.map (fun c ->
        { c with
            Start = scale * c.Start + offset
            End = scale * c.End + offset })

/// Repairs what a piecewise shift can break: negative times and overlaps at
/// segment boundaries. Cues pushed entirely before zero are dropped.
let sanitize (cues: Cue list) =
    let sorted =
        cues
        |> List.filter (fun c -> c.End > 0.0)
        |> List.map (fun c -> { c with Start = max 0.0 c.Start })
        |> List.sortBy (fun c -> c.Start)
        |> Array.ofList

    for i in 0 .. sorted.Length - 2 do
        let a, b = sorted[i], sorted[i + 1]

        if a.End > b.Start then
            sorted[i] <- { a with End = max (a.Start + 0.05) (b.Start - 0.001) }

    sorted |> Array.mapi (fun i c -> { c with Index = i + 1 }) |> List.ofArray
