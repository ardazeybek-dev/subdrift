module Subdrift.Cli.Program

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Subdrift.Core
open Subdrift.Core.Subtitle
open Subdrift.Cli.Config

let version = "0.1.0"

let usage =
    """subdrift — fix out-of-sync subtitles automatically

USAGE
  subdrift sync  <subtitle> <reference> [options]
  subdrift shift <subtitle> <seconds> [-o <file>]
  subdrift info  <subtitle>
  subdrift demo  [directory]

REFERENCE
  A correctly timed subtitle in any language (.srt / .vtt), a .wav file,
  or any video/audio file when ffmpeg is installed.

SYNC OPTIONS
  -o, --output <file>       Where to write the result (default: <name>.synced.<ext>)
  -r, --report <file>       Also write an interactive HTML report
      --max-offset <s>      Largest constant offset to search    [SUBDRIFT_MAX_OFFSET=120]
      --split-penalty <s>   Cost of one scene jump               [SUBDRIFT_SPLIT_PENALTY=6]
      --max-split <s>       Largest extra shift per scene        [SUBDRIFT_MAX_SPLIT=60]
      --vad-threshold <x>   Speech threshold between 0 and 1     [SUBDRIFT_VAD_THRESHOLD=0.35]
      --encoding <name>     Fallback for non-UTF-8 input         [SUBDRIFT_FALLBACK_ENCODING=windows-1252]
      --no-framerate        Only search a constant offset
      --no-split            Keep one offset for the whole file
      --json                Print the result as JSON on stdout

Settings are read from flags, then environment variables, then ./.env.
"""

exception UsageError of string

/// Splits argv into positional arguments and --flags (with optional values).
let private parseArgs (args: string list) =
    let valued =
        set
            [ "-o"; "--output"; "-r"; "--report"; "--max-offset"; "--split-penalty"
              "--max-split"; "--vad-threshold"; "--encoding" ]

    let rec go args pos flags =
        match args with
        | [] -> List.rev pos, flags
        | f :: v :: rest when valued.Contains f -> go rest pos (Map.add f v flags)
        | f :: _ when valued.Contains f -> raise (UsageError $"{f} needs a value")
        | f :: rest when f.StartsWith "--" -> go rest pos (Map.add f "" flags)
        | p :: rest -> go rest (p :: pos) flags

    go args [] Map.empty

let private flag (flags: Map<string, string>) (names: string list) =
    names |> List.tryPick (fun n -> Map.tryFind n flags)

let private defaultOutput (path: string) =
    let dir = Path.GetDirectoryName(Path.GetFullPath path)
    Path.Combine(dir, $"{Path.GetFileNameWithoutExtension path}.synced{Path.GetExtension path}")

let private requireFile (path: string) =
    if not (File.Exists path) then
        raise (UsageError $"file not found: {path}")

// ------------------------------------------------------------------ sync

type SyncRequest =
    { Subtitle: string
      Reference: string
      Output: string
      Report: string option
      Options: Align.Options
      Vad: Audio.VadOptions
      Settings: Settings
      Json: bool }

let runSync (req: SyncRequest) =
    let sw = Stopwatch.StartNew()
    requireFile req.Subtitle
    requireFile req.Reference

    let doc = load req.Settings.FallbackEncoding req.Subtitle
    Term.step "subtitle" $"{doc.Cues.Length} cues · {doc.Encoding}"

    let activity, kind =
        if isSubtitlePath req.Reference then
            let reference = load req.Settings.FallbackEncoding req.Reference
            Term.step "reference" $"{reference.Cues.Length} cues · timing from subtitle"
            Signal.activityFromCues reference.Cues, "subtitle"
        else
            let t = Stopwatch.StartNew()
            let a = Audio.activityOfMedia req.Settings.Ffmpeg req.Vad req.Reference
            let speech = a.Values |> Array.filter (fun v -> v > 0.0) |> Array.length

            Term.step
                "reference"
                (sprintf
                    "%.0f min audio · %.0f%% speech · %d ms"
                    (float a.Values.Length / Signal.Rate / 60.0)
                    (100.0 * float speech / float (max 1 a.Values.Length))
                    t.ElapsedMilliseconds)

            a, "audio"

    let result = Align.align req.Options activity doc.Cues
    Term.step "aligned" $"{sw.ElapsedMilliseconds} ms"
    Subtitle.save req.Output result.Aligned

    req.Report
    |> Option.iter (fun path ->
        Report.write
            path
            { Subtitle = req.Subtitle
              Reference = req.Reference
              ReferenceKind = kind
              Output = req.Output
              ElapsedMs = sw.ElapsedMilliseconds }
            activity
            result)

    if req.Json then
        let json =
            {| output = req.Output
               report = req.Report |> Option.toObj
               offset = result.Offset
               scale = result.Scale
               framerate = result.ScaleLabel
               coverageBefore = result.CoverageBefore
               coverageAfter = result.CoverageAfter
               segments =
                result.Segments
                |> List.map (fun s -> {| firstCue = s.FirstCue; lastCue = s.LastCue; shift = s.Shift |}) |}

        printfn "%s" (JsonSerializer.Serialize(json, JsonSerializerOptions(WriteIndented = true)))
    else
        let cuts = result.Segments.Length - 1
        printfn ""
        Term.row "offset" (Term.bold (Term.signed result.Offset))

        Term.row
            "framerate"
            (if result.Scale = 1.0 then
                 "unchanged"
             else
                 Term.bold $"{result.ScaleLabel}" + Term.dim (sprintf "  (×%.5f)" result.Scale))

        Term.row
            "scenes"
            (if cuts = 0 then
                 "one continuous timing"
             else
                 sprintf "%d segments · %d cut%s detected" result.Segments.Length cuts (if cuts > 1 then "s" else ""))

        if cuts > 0 then
            for s in result.Segments |> List.truncate 8 do
                Term.row "" (Term.dim $"cues {s.FirstCue}–{s.LastCue}  " + Term.signed (result.Offset + s.Shift))

        Term.row "on speech before" (sprintf "%s %5.1f%%" (Term.bar 24 result.CoverageBefore) (100.0 * result.CoverageBefore))
        Term.row "on speech after" (sprintf "%s %5.1f%%" (Term.bar 24 result.CoverageAfter) (100.0 * result.CoverageAfter))
        printfn ""
        Term.row "written" (Term.teal req.Output)
        req.Report |> Option.iter (fun p -> Term.row "report" (Term.teal p))

        if result.CoverageAfter < 0.62 then
            Term.warn "low confidence: the reference may not belong to this subtitle"

let private syncCommand (settings: Settings) (pos: string list) (flags: Map<string, string>) =
    match pos with
    | [ sub; reference ] ->
        let num names fallback =
            flag flags names |> Option.map (parseFloat names.Head) |> Option.defaultValue fallback

        let settings =
            { settings with
                FallbackEncoding = flag flags [ "--encoding" ] |> Option.defaultValue settings.FallbackEncoding }

        runSync
            { Subtitle = sub
              Reference = reference
              Output = flag flags [ "-o"; "--output" ] |> Option.defaultValue (defaultOutput sub)
              Report = flag flags [ "-r"; "--report" ]
              Options =
                { MaxOffset = num [ "--max-offset" ] settings.MaxOffset
                  Framerate = not (flags.ContainsKey "--no-framerate")
                  Split = not (flags.ContainsKey "--no-split")
                  SplitPenalty = num [ "--split-penalty" ] settings.SplitPenalty
                  MaxSplit = num [ "--max-split" ] settings.MaxSplit }
              Vad =
                { Audio.defaultVad with
                    Threshold = num [ "--vad-threshold" ] settings.VadThreshold }
              Settings = settings
              Json = flags.ContainsKey "--json" }
    | _ -> raise (UsageError "sync needs <subtitle> and <reference>")

// ------------------------------------------------------------------ shift / info

let private shiftCommand (settings: Settings) (pos: string list) (flags: Map<string, string>) =
    match pos with
    | [ sub; seconds ] ->
        requireFile sub
        let by = parseFloat "seconds" seconds
        let doc = load settings.FallbackEncoding sub
        let out = flag flags [ "-o"; "--output" ] |> Option.defaultValue (defaultOutput sub)
        save out (doc.Cues |> transform 1.0 by |> sanitize)
        Term.row "shifted" (Term.signed by)
        Term.row "written" (Term.teal out)
    | _ -> raise (UsageError "shift needs <subtitle> and <seconds>")

let private infoCommand (settings: Settings) (pos: string list) =
    match pos with
    | [ sub ] ->
        requireFile sub
        let doc = load settings.FallbackEncoding sub
        let cues = doc.Cues |> List.sortBy (fun c -> c.Start)
        let pairs = List.pairwise cues
        let overlaps = pairs |> List.filter (fun (a, b) -> a.End > b.Start) |> List.length
        let chars (c: Cue) = c.Text.Replace("\n", " ").Length
        let fast = cues |> List.filter (fun c -> c.Duration > 0.0 && float (chars c) / c.Duration > 21.0)
        let first, last = cues.Head, List.last cues

        Term.row "format" (sprintf "%A · %s" doc.Format doc.Encoding)
        Term.row "cues" (string cues.Length)
        Term.row "span" $"{formatTimestamp '.' first.Start} → {formatTimestamp '.' last.End}"
        Term.row "avg duration" (sprintf "%.2f s" (cues |> List.averageBy (fun c -> c.Duration)))

        Term.row
            "overlaps"
            (if overlaps = 0 then Term.green "none" else Term.yellow $"{overlaps}")

        Term.row
            "too fast (>21 cps)"
            (if fast.IsEmpty then Term.green "none" else Term.yellow $"{fast.Length} cues")
    | _ -> raise (UsageError "info needs <subtitle>")

// ------------------------------------------------------------------ demo

let private demoCommand (settings: Settings) (pos: string list) =
    let dir = pos |> List.tryHead |> Option.defaultValue "subdrift-demo"
    Directory.CreateDirectory dir |> ignore
    let duration = 300.0
    let truth = Synth.cues 42 duration false
    let movie = Path.Combine(dir, "movie.wav")
    let reference = Path.Combine(dir, "movie.en.srt")
    let broken = Path.Combine(dir, "movie.es.srt")

    Term.step "generating" "5 min of synthetic dialogue + subtitles"
    Synth.writeWav movie 42 duration truth
    save reference truth
    // Spanish subtitle ripped from a 25 fps PAL release, 2.4 s late, with a scene cut at 2:30.
    truth |> Synth.translate |> Synth.breakTiming 25.0 23.976 2.4 150.0 -3.1 |> save broken
    Term.step "broken" "25 fps PAL timing · +2.4 s · −3.1 s after a cut"

    runSync
        { Subtitle = broken
          Reference = movie
          Output = Path.Combine(dir, "movie.es.synced.srt")
          Report = Some(Path.Combine(dir, "report.html"))
          Options =
            { MaxOffset = settings.MaxOffset
              Framerate = true
              Split = true
              SplitPenalty = settings.SplitPenalty
              MaxSplit = settings.MaxSplit }
          Vad = { Audio.defaultVad with Threshold = settings.VadThreshold }
          Settings = settings
          Json = false }

    printfn ""
    printfn "  %s" (Term.dim $"Try it against the subtitle reference too:  subdrift sync {broken} {reference}")

// ------------------------------------------------------------------ main

[<EntryPoint>]
let main argv =
    Console.OutputEncoding <- Text.Encoding.UTF8
    Term.enabled <- isNull (Environment.GetEnvironmentVariable "NO_COLOR") && not Console.IsErrorRedirected

    try
        loadDotEnv (Path.Combine(Environment.CurrentDirectory, ".env"))
        let settings = fromEnvironment ()
        Term.enabled <- not settings.NoColor

        match List.ofArray argv with
        | []
        | [ "-h" ]
        | [ "--help" ]
        | [ "help" ] ->
            printf "%s" usage
            0
        | [ "-V" ]
        | [ "--version" ] ->
            printfn "subdrift %s" version
            0
        | cmd :: rest ->
            let pos, flags = parseArgs rest

            if not (List.contains cmd [ "info"; "shift" ]) && not (flags.ContainsKey "--json") then
                Term.banner ()

            match cmd with
            | "sync" -> syncCommand settings pos flags
            | "shift" -> shiftCommand settings pos flags
            | "info" -> infoCommand settings pos
            | "demo" -> demoCommand settings pos
            | other -> raise (UsageError $"unknown command '{other}'")

            0
    with
    | UsageError msg ->
        Term.error msg
        eprintfn "Run 'subdrift --help' for usage."
        1
    | SubtitleParseError msg
    | Audio.AudioError msg ->
        Term.error msg
        2
    | :? IOException as e ->
        Term.error e.Message
        2
    | Failure msg ->
        Term.error msg
        2
