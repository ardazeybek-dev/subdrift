/// Deterministic synthetic material: dialogue-like audio, matching subtitles and
/// realistically broken copies. Powers `subdrift demo` and the test-suite.
module Subdrift.Core.Synth

open System
open System.IO
open Subdrift.Core.Subtitle

let private english =
    [| "Where were you last night?"
       "I told you, I was at the lab."
       "Nobody saw you there."
       "Then nobody was looking."
       "We need to leave before sunrise."
       "Is the car ready?"
       "Almost. Give me ten minutes."
       "You said that an hour ago."
       "Listen to me. This is important."
       "I'm not going without her."
       "Then we wait."
       "The signal is getting weaker."
       "Turn it up, I can barely hear it."
       "Did you hear that?"
       "It's just the wind."
       "Keep moving, don't look back." |]

let private spanish =
    [| "¿Dónde estabas anoche?"
       "Te lo dije, estaba en el laboratorio."
       "Nadie te vio allí."
       "Entonces nadie estaba mirando."
       "Tenemos que irnos antes del amanecer."
       "¿Está listo el coche?"
       "Casi. Dame diez minutos."
       "Dijiste eso hace una hora."
       "Escúchame. Esto es importante."
       "No me voy sin ella."
       "Entonces esperamos."
       "La señal se está debilitando."
       "Súbele, apenas la oigo."
       "¿Oíste eso?"
       "Es solo el viento."
       "Sigue adelante, no mires atrás." |]

/// Dialogue cues over `duration` seconds, with the occasional long quiet scene.
let cues (seed: int) (duration: float) (spanishText: bool) : Cue list =
    let rng = Random seed
    let lines = if spanishText then spanish else english
    let range (a: float) b = a + rng.NextDouble() * (b - a)

    let rec loop t i acc =
        let len = range 1.0 4.2

        if t + len > duration - 1.0 then
            List.rev acc
        else
            let cue =
                { Index = i
                  Start = t
                  End = t + len
                  Text = lines[(i - 1) % lines.Length] }

            let gap = if rng.NextDouble() < 0.08 then range 8.0 20.0 else range 0.3 4.5
            loop (t + len + gap) (i + 1) (cue :: acc)

    loop (range 1.5 4.0) 1 []

/// Same timing, different language: a stand-in for "a correct subtitle in another language".
let translate (cues: Cue list) =
    cues |> List.map (fun c -> { c with Text = spanish[(c.Index - 1) % spanish.Length] })

/// Breaks a correct subtitle the way real downloads are broken: a framerate
/// mismatch, a constant delay, and an extra jump after a cut at `cutAt` seconds.
let breakTiming (fpsFrom: float) (fpsTo: float) (offset: float) (cutAt: float) (cutShift: float) (cues: Cue list) =
    cues
    |> List.map (fun c ->
        let extra = if c.Start >= cutAt then cutShift else 0.0
        let f t = t * fpsTo / fpsFrom + offset + extra

        { c with Start = f c.Start; End = f c.End })

/// Writes a 16 kHz mono 16-bit WAV: soft room noise, a quiet music bed in long
/// gaps, and voiced, syllable-modulated "speech" inside each cue.
let writeWav (path: string) (seed: int) (duration: float) (cues: Cue list) =
    let rate = 16000
    let total = int (duration * float rate)
    let rng = Random(seed + 7)
    let cueArr = cues |> Array.ofList
    use fs = File.Create path
    use w = new BinaryWriter(fs)
    let dataBytes = total * 2
    w.Write("RIFF"B)
    w.Write(36 + dataBytes)
    w.Write("WAVE"B)
    w.Write("fmt "B)
    w.Write 16
    w.Write 1s // PCM
    w.Write 1s // mono
    w.Write rate
    w.Write(rate * 2)
    w.Write 2s
    w.Write 16s
    w.Write("data"B)
    w.Write dataBytes

    let mutable ci = 0
    let mutable phase = 0.0
    let mutable musicPhase = 0.0
    let voices = cueArr |> Array.map (fun c -> 95.0 + float (c.Index * 37 % 120))

    for n in 0 .. total - 1 do
        let t = float n / float rate

        while ci < cueArr.Length && t > cueArr[ci].End do
            ci <- ci + 1

        let noise = (rng.NextDouble() * 2.0 - 1.0) * 0.006
        let mutable s = noise

        if ci < cueArr.Length && t >= cueArr[ci].Start + 0.08 && t <= cueArr[ci].End - 0.12 then
            let c = cueArr[ci]
            let local = t - c.Start
            // Pitch glides a little; syllables at ~4.5 Hz.
            let f0 = voices[ci] * (1.0 + 0.08 * sin (2.0 * Math.PI * 0.7 * local))
            phase <- phase + 2.0 * Math.PI * f0 / float rate
            let syllable = Math.Pow(abs (sin (Math.PI * 4.5 * local)), 1.5)
            let mutable voiced = 0.0

            for h in 1 .. 8 do
                voiced <- voiced + sin (float h * phase) / float h

            s <- s + 0.22 * syllable * voiced
        elif ci < cueArr.Length && cueArr[ci].Start - t > 6.0 then
            // Long silent stretch: a soft low pad, like a score under a scene.
            musicPhase <- musicPhase + 2.0 * Math.PI * 82.4 / float rate
            s <- s + 0.02 * (sin musicPhase + 0.5 * sin (1.5 * musicPhase))

        w.Write(int16 (Math.Clamp(s, -1.0, 1.0) * 32767.0))
