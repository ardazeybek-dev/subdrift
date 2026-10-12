/// The alignment engine.
///
/// 1. Global: for every candidate framerate ratio, rasterize the subtitle and
///    cross-correlate it with the reference activity via FFT. The best
///    (scale, offset) pair maximises the share of subtitle time spent on speech.
/// 2. Piecewise: a Viterbi pass over the cues lets the offset change where a
///    scene was cut or an ad break was removed, paying a fixed penalty per jump.
module Subdrift.Core.Align

open System
open Subdrift.Core.Signal
open Subdrift.Core.Subtitle

type Options =
    {
        /// Largest constant offset to search, in seconds (both directions).
        MaxOffset: float
        /// Also try common framerate conversions (23.976 / 24 / 25 / 29.97 / 30).
        Framerate: bool
        /// Allow the offset to change between scenes.
        Split: bool
        /// Cost of one offset change, in seconds of perfectly matched speech.
        SplitPenalty: float
        /// Largest extra shift a single segment may receive, in seconds.
        MaxSplit: float
    }

let defaults =
    { MaxOffset = 120.0
      Framerate = true
      Split = true
      SplitPenalty = 6.0
      MaxSplit = 60.0 }

type Candidate =
    { Label: string
      Scale: float
      Offset: float
      Coverage: float }

/// A run of consecutive cues (1-based, inclusive) sharing one extra shift.
type Segment =
    { FirstCue: int
      LastCue: int
      Shift: float }

type Result =
    { Scale: float
      ScaleLabel: string
      Offset: float
      Candidates: Candidate list
      /// Coverage as a function of offset for the winning scale.
      Curve: float[]
      CurveRange: float * float
      Segments: Segment list
      /// Input cues sorted by start time.
      Original: Cue list
      /// Total correction applied to each original cue's start, in seconds.
      Corrections: float[]
      Aligned: Cue list
      CoverageBefore: float
      CoverageAfter: float }

let private framerates = [ 23.976; 24.0; 25.0; 29.97; 30.0 ]

/// (label, scale) pairs; scale maps subtitle time onto reference time.
let scaleCandidates (enabled: bool) =
    let conversions =
        if not enabled then
            []
        else
            [ for src in framerates do
                  for dst in framerates do
                      if src <> dst then
                          $"{src} → {dst} fps", src / dst ]

    ("none", 1.0) :: conversions
    // 24/23.976 and 30/29.97 are the same ratio: keep the first label.
    |> List.distinctBy (fun (_, s) -> Math.Round(s, 5))

/// Coverage bonus a framerate change must beat before it is preferred over 1.0.
let private scaleBias = 0.005

let private downsampleMax (target: int) (xs: float[]) =
    if xs.Length <= target then
        xs
    else
        Array.init target (fun i ->
            let a = i * xs.Length / target
            let b = max (a + 1) ((i + 1) * xs.Length / target)
            Array.max xs[a .. b - 1])

/// Scores every (scale, offset) pair; returns candidates best-first with their offset curves.
let globalAlign (opts: Options) (reference: Activity) (cues: Cue list) =
    let maxLag = toFrame opts.MaxOffset
    let scales = scaleCandidates opts.Framerate
    let largestScale = scales |> List.map snd |> List.max
    let probeLen = (indicatorFromCues (transform largestScale 0.0 cues)).Length
    let size = Fft.nextPow2 (reference.Values.Length + probeLen + maxLag + 1)
    let spec = Fft.spectrum size reference.Values

    // Each ratio is independent: score them on all cores.
    let evaluated =
        scales
        |> Array.ofList
        |> Array.Parallel.map (fun (label, scale) ->
            let probe = indicatorFromCues (transform scale 0.0 cues)
            let total = Array.sum probe
            let corr = Fft.crossCorrelate spec probe maxLag
            let curve = corr |> Array.map (fun c -> (c / total + 1.0) / 2.0)
            let best = Array.findIndex ((=) (Array.max curve)) curve

            { Label = label
              Scale = scale
              Offset = toSeconds (best - maxLag)
              Coverage = curve[best] },
            curve)
        |> List.ofArray

    // Best first; a framerate change has to beat "unchanged" by a small margin.
    evaluated
    |> List.sortByDescending (fun (c, _) -> c.Coverage - (if c.Scale = 1.0 then 0.0 else scaleBias))

/// Viterbi over cues with states = extra shift in frames. Staying costs nothing,
/// jumping to any other shift costs `penalty`. Returns one shift per cue.
let piecewiseShifts (opts: Options) (reference: Activity) (cues: Cue[]) : int[] =
    let n = cues.Length
    let m = toFrame opts.MaxSplit
    let states = 2 * m + 1
    let penalty = opts.SplitPenalty * Rate
    let prefix = prefixSums reference.Values
    // Tiny pull towards the global solution so flat plateaus don't wander.
    let epsilon = 1e-4

    let emission (c: Cue) (d: int) =
        rangeSum prefix (toFrame c.Start + d) (toFrame c.End + d) - epsilon * float (abs d)

    let jumped = Array.init n (fun _ -> Array.zeroCreate<bool> states)
    let bestPrev = Array.zeroCreate<int> n
    let mutable v = Array.init states (fun s -> emission cues[0] (s - m))
    let next = Array.zeroCreate<float> states

    for i in 1 .. n - 1 do
        let mutable bi = 0

        for s in 1 .. states - 1 do
            if v[s] > v[bi] then bi <- s

        bestPrev[i - 1] <- bi
        let jumpScore = v[bi] - penalty

        for s in 0 .. states - 1 do
            let stay = v[s]

            if jumpScore > stay then
                next[s] <- jumpScore + emission cues[i] (s - m)
                jumped[i][s] <- true
            else
                next[s] <- stay + emission cues[i] (s - m)

        Array.blit next 0 v 0 states

    let path = Array.zeroCreate<int> n
    let mutable s = Array.findIndex ((=) (Array.max v)) v

    for i in n - 1 .. -1 .. 0 do
        path[i] <- s - m

        if i > 0 && jumped[i][s] then
            s <- bestPrev[i - 1]

    path

let private segmentsOf (shifts: int[]) =
    [ let mutable start = 0

      for i in 1 .. shifts.Length do
          if i = shifts.Length || shifts[i] <> shifts[start] then
              yield
                  { FirstCue = start + 1
                    LastCue = i
                    Shift = toSeconds shifts[start] }

              start <- i ]

/// Global candidates this close to the winner are re-ranked after the piecewise
/// pass. 25→24 and 25→23.976 differ by 0.1 %: over a short file the global
/// score can barely tell them apart, the refined result can.
let private contenderMargin = 0.02

/// Runs the whole pipeline: global alignment, then optional piecewise refinement.
let align (opts: Options) (reference: Activity) (cues: Cue list) : Result =
    let cues = cues |> List.sortBy (fun c -> c.Start)
    let ranked = globalAlign opts reference cues
    let best = fst ranked.Head

    let refine (c: Candidate) =
        let global' = transform c.Scale c.Offset cues |> Array.ofList

        let segments, aligned =
            if opts.Split && cues.Length > 1 then
                let shifts = piecewiseShifts opts reference global'
                segmentsOf shifts, global' |> Array.mapi (fun i c -> transform 1.0 (toSeconds shifts[i]) [ c ] |> List.head)
            else
                [ { FirstCue = 1
                    LastCue = cues.Length
                    Shift = 0.0 } ],
                global'

        let sanitized = sanitize (List.ofArray aligned)
        // Each extra segment must pay for itself.
        let score = coverage reference sanitized - 0.002 * float (segments.Length - 1)
        score, segments, aligned, sanitized

    let (winner, curve), (_, segments, aligned, sanitized) =
        ranked
        |> List.filter (fun (c, _) -> best.Coverage - c.Coverage <= contenderMargin)
        |> List.truncate 4
        |> Array.ofList
        |> Array.Parallel.map (fun (c, curve) -> (c, curve), refine c)
        |> Array.maxBy (fun (_, (score, _, _, _)) -> score)

    let corrections = Array.map2 (fun (a: Cue) (o: Cue) -> a.Start - o.Start) aligned (Array.ofList cues)

    { Scale = winner.Scale
      ScaleLabel = winner.Label
      Offset = winner.Offset
      Candidates = ranked |> List.map fst |> List.sortByDescending (fun c -> c.Coverage)
      Curve = downsampleMax 800 curve
      CurveRange = -opts.MaxOffset, opts.MaxOffset
      Segments = segments
      Original = cues
      Corrections = corrections
      Aligned = sanitized
      CoverageBefore = coverage reference cues
      CoverageAfter = coverage reference sanitized }
