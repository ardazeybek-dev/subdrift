/// Self-contained, interactive HTML report of an alignment run.
module Subdrift.Core.Report

open System
open System.IO
open System.Text.Json
open Subdrift.Core.Signal
open Subdrift.Core.Subtitle
open Subdrift.Core.Align

type Meta =
    { Subtitle: string
      Reference: string
      ReferenceKind: string
      Output: string
      ElapsedMs: int64 }

/// Reference activity at 10 Hz, as integers in [-100, 100].
let private compactActivity (activity: Activity) =
    let step = 10

    Array.init ((activity.Values.Length + step - 1) / step) (fun i ->
        let a = i * step
        let b = min activity.Values.Length (a + step)
        let mean = Array.average activity.Values[a .. b - 1]
        int (Math.Round(mean * 100.0)))

let private round3 (x: float) = Math.Round(x, 3)

let private cueRows (cues: Cue list) =
    cues |> List.map (fun c -> [| box (round3 c.Start); box (round3 c.End); box c.Text |])

let payload (meta: Meta) (activity: Activity) (r: Result) =
    let duration =
        [ float activity.Values.Length / Rate
          (r.Original |> List.map (fun c -> c.End) |> List.max)
          (r.Aligned |> List.map (fun c -> c.End) |> List.max) ]
        |> List.max

    {| meta =
        {| subtitle = meta.Subtitle
           reference = meta.Reference
           referenceKind = meta.ReferenceKind
           output = meta.Output
           elapsedMs = meta.ElapsedMs
           generated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'") |}
       duration = round3 duration
       activity = compactActivity activity
       original = cueRows r.Original
       aligned = cueRows r.Aligned
       corrections = r.Corrections |> Array.map round3
       scale = r.Scale
       scaleLabel = r.ScaleLabel
       offset = round3 r.Offset
       coverageBefore = r.CoverageBefore
       coverageAfter = r.CoverageAfter
       curve = r.Curve |> Array.map (fun x -> Math.Round(x, 4))
       curveRange = [| fst r.CurveRange; snd r.CurveRange |]
       candidates =
        r.Candidates
        |> List.map (fun c ->
            {| label = c.Label
               scale = c.Scale
               offset = round3 c.Offset
               coverage = c.Coverage |})
       segments =
        r.Segments
        |> List.map (fun s ->
            {| first = s.FirstCue
               last = s.LastCue
               shift = round3 s.Shift |}) |}

let private template =
    lazy
        (let asm = Reflection.Assembly.GetExecutingAssembly()
         use s = asm.GetManifestResourceStream "Subdrift.Core.report.html"
         use r = new StreamReader(s)
         r.ReadToEnd())

/// Renders the report HTML. The template ships embedded in the assembly.
let render (meta: Meta) (activity: Activity) (r: Result) =
    let json = JsonSerializer.Serialize(payload meta activity r)
    // Keep "</script>" inside strings from closing the data block.
    template.Value.Replace("/*__SUBDRIFT_DATA__*/null", json.Replace("</", "<\\/"))

let write (path: string) (meta: Meta) (activity: Activity) (r: Result) =
    File.WriteAllText(path, render meta activity r)
