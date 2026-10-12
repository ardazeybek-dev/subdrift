/// Speech-activity timelines sampled at a fixed frame rate.
module Subdrift.Core.Signal

open Subdrift.Core.Subtitle

/// Frames per second used for every timeline (10 ms resolution).
[<Literal>]
let Rate = 100.0

let toFrame (seconds: float) = int (System.Math.Round(seconds * Rate))
let toSeconds (frame: int) = float frame / Rate

/// Reference activity: +1 where someone is speaking, -1 where nobody is.
/// Values in between express uncertainty (e.g. soft VAD output).
type Activity = { Values: float[] }

/// Marks every frame covered by a cue. `on`/`off` choose the encoding.
let private rasterize (on: float) (off: float) (length: int) (cues: Cue seq) =
    let v = Array.create length off

    for c in cues do
        let a = max 0 (toFrame c.Start)
        let b = min length (toFrame c.End)

        for i in a .. b - 1 do
            v[i] <- on

    v

let private lengthFor (cues: Cue seq) padSeconds =
    let last = if Seq.isEmpty cues then 0.0 else cues |> Seq.map (fun c -> c.End) |> Seq.max
    toFrame (last + padSeconds)

/// Reference activity derived from a correctly-timed subtitle file.
let activityFromCues (cues: Cue list) : Activity =
    { Values = rasterize 1.0 -1.0 (lengthFor cues 1.0) cues }

/// 0/1 indicator of the subtitle that is being aligned.
let indicatorFromCues (cues: Cue list) : float[] = rasterize 1.0 0.0 (lengthFor cues 1.0) cues

/// Prefix sums for O(1) range sums over an activity timeline.
let prefixSums (values: float[]) =
    let p = Array.zeroCreate (values.Length + 1)

    for i in 0 .. values.Length - 1 do
        p[i + 1] <- p[i] + values[i]

    p

/// Sum of `values[a..b)` clipped to the timeline; frames outside count as 0.
let rangeSum (prefix: float[]) (a: int) (b: int) =
    let n = prefix.Length - 1
    let a = max 0 (min n a)
    let b = max 0 (min n b)
    if b <= a then 0.0 else prefix[b] - prefix[a]

/// Fraction of subtitle time that lands on speech, in [0, 1].
let coverage (activity: Activity) (cues: Cue list) =
    let prefix = prefixSums activity.Values
    let mutable score = 0.0
    let mutable total = 0.0

    for c in cues do
        let a, b = toFrame c.Start, toFrame c.End
        score <- score + rangeSum prefix a b
        total <- total + float (b - a)

    if total <= 0.0 then 0.0 else (score / total + 1.0) / 2.0
