/// Audio decoding (native WAV, anything else through ffmpeg) and voice activity detection.
module Subdrift.Core.Audio

open System
open System.Diagnostics
open System.IO
open Subdrift.Core.Signal

exception AudioError of string

/// Streams samples and emits one log-energy value per 10 ms frame.
/// A first-order pre-emphasis filter tilts the spectrum towards the voice band
/// so low-frequency rumble and music beds weigh less than speech.
type FrameEnergy(sampleRate: int) =
    let hop = max 1 (sampleRate / int Rate)
    let energies = ResizeArray<float>()
    let mutable prev = 0.0
    let mutable acc = 0.0
    let mutable count = 0

    member _.Push(sample: float) =
        let y = sample - 0.97 * prev
        prev <- sample
        acc <- acc + y * y
        count <- count + 1

        if count = hop then
            energies.Add(log10 (acc / float hop + 1e-10))
            acc <- 0.0
            count <- 0

    member _.Energies = energies.ToArray()

type VadOptions =
    {
        /// Position of the speech threshold between noise floor (0) and loud speech (1).
        Threshold: float
        /// Half-width (frames) of the linear-energy average that bridges gaps between syllables.
        EnergyWindow: int
        /// Moving-average window (frames) applied to the soft decision.
        SmoothFrames: int
    }

let defaultVad =
    { Threshold = 0.35
      EnergyWindow = 10
      SmoothFrames = 9 }

/// Averages energy in the linear domain, so short loud syllables dominate the
/// quiet troughs between them (a log-domain average would do the opposite).
let private smoothEnergy (halfWidth: int) (logEnergies: float[]) =
    let prefix = logEnergies |> Array.map (fun x -> Math.Pow(10.0, x)) |> prefixSums

    Array.init logEnergies.Length (fun i ->
        let a, b = max 0 (i - halfWidth), min logEnergies.Length (i + halfWidth + 1)
        log10 ((prefix[b] - prefix[a]) / float (b - a)))

let private percentile (p: float) (sorted: float[]) =
    sorted[int (p * float (sorted.Length - 1))]

/// Turns per-frame log energies into a soft speech activity in [-1, 1].
let detectSpeech (opts: VadOptions) (energies: float[]) : Activity =
    if energies.Length = 0 then
        { Values = [||] }
    else
        let energies = smoothEnergy opts.EnergyWindow energies
        let sorted = Array.sort energies
        let floor = percentile 0.10 sorted
        let peak = percentile 0.95 sorted
        let range = max 0.3 (peak - floor) // 0.3 log10 units = 3 dB
        let thr = floor + opts.Threshold * range
        let soft = energies |> Array.map (fun e -> tanh ((e - thr) / (0.15 * range)))
        // Centered moving average via prefix sums.
        let prefix = prefixSums soft
        let h = opts.SmoothFrames / 2

        let values =
            Array.init soft.Length (fun i ->
                let a, b = max 0 (i - h), min soft.Length (i + h + 1)
                (prefix[b] - prefix[a]) / float (b - a))

        { Values = values }

// ---------------------------------------------------------------- WAV

type WavFormat =
    { SampleRate: int
      Channels: int
      BitsPerSample: int
      IsFloat: bool }

let private readFormat (r: BinaryReader) (size: int) =
    let tag = int (r.ReadUInt16())
    let channels = int (r.ReadUInt16())
    let rate = r.ReadInt32()
    r.ReadInt32() |> ignore // byte rate
    r.ReadUInt16() |> ignore // block align
    let bits = int (r.ReadUInt16())
    let mutable consumed = 16
    let mutable tag = tag

    if tag = 0xFFFE && size >= 40 then
        r.ReadUInt16() |> ignore // cbSize
        r.ReadUInt16() |> ignore // valid bits
        r.ReadUInt32() |> ignore // channel mask
        tag <- int (r.ReadUInt16()) // first two bytes of the sub-format GUID
        r.ReadBytes 14 |> ignore
        consumed <- 40

    r.ReadBytes(size - consumed) |> ignore

    if tag <> 1 && tag <> 3 then
        raise (AudioError $"unsupported WAV encoding (format tag {tag}); convert to PCM or install ffmpeg")

    { SampleRate = rate
      Channels = channels
      BitsPerSample = bits
      IsFloat = (tag = 3) }

let private sampleReader (fmt: WavFormat) : BinaryReader -> float =
    match fmt.IsFloat, fmt.BitsPerSample with
    | false, 8 -> fun r -> (float (r.ReadByte()) - 128.0) / 128.0
    | false, 16 -> fun r -> float (r.ReadInt16()) / 32768.0
    | false, 24 ->
        fun r ->
            let b = r.ReadBytes 3
            let v = (int b[0]) ||| (int b[1] <<< 8) ||| (int (sbyte b[2]) <<< 16)
            float v / 8388608.0
    | false, 32 -> fun r -> float (r.ReadInt32()) / 2147483648.0
    | true, 32 -> fun r -> float (r.ReadSingle())
    | true, 64 -> fun r -> r.ReadDouble()
    | _, bits -> raise (AudioError $"unsupported WAV sample size: {bits} bits")

/// Reads a RIFF/WAVE stream, down-mixes to mono and feeds the energy tracker.
let wavEnergies (stream: Stream) : float[] =
    use r = new BinaryReader(stream)

    let fourCC () =
        String(r.ReadChars 4)

    if fourCC () <> "RIFF" then raise (AudioError "not a RIFF file")
    r.ReadInt32() |> ignore

    if fourCC () <> "WAVE" then raise (AudioError "not a WAVE file")

    let mutable fmt = None
    let mutable result = None

    while result.IsNone do
        let id = fourCC ()

        if id.Length < 4 then
            raise (AudioError "WAV file has no data chunk")

        let size = r.ReadInt32()

        match id, fmt with
        | "fmt ", _ -> fmt <- Some(readFormat r size)
        | "data", Some f ->
            let read = sampleReader f
            let energy = FrameEnergy f.SampleRate
            let frameBytes = f.Channels * f.BitsPerSample / 8
            // size can be 0 / bogus for streamed WAVs: read until EOF in that case.
            let frames = if size <= 0 then Int64.MaxValue else int64 size / int64 frameBytes
            let mutable i = 0L

            try
                while i < frames do
                    let mutable sum = 0.0

                    for _ in 1 .. f.Channels do
                        sum <- sum + read r

                    energy.Push(sum / float f.Channels)
                    i <- i + 1L
            with :? EndOfStreamException ->
                ()

            result <- Some energy.Energies
        | "data", None -> raise (AudioError "WAV data chunk before fmt chunk")
        | _ -> r.ReadBytes(size + (size &&& 1)) |> ignore

    result.Value

// ---------------------------------------------------------------- ffmpeg

/// Decodes any container ffmpeg understands to 16 kHz mono PCM and measures it.
let ffmpegEnergies (ffmpeg: string) (path: string) : float[] =
    let psi = ProcessStartInfo(ffmpeg)

    for a in [ "-nostdin"; "-v"; "error"; "-i"; path; "-vn"; "-ac"; "1"; "-ar"; "16000"; "-f"; "s16le"; "-" ] do
        psi.ArgumentList.Add a

    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false

    let proc =
        try
            Process.Start psi
        with :? ComponentModel.Win32Exception ->
            raise (
                AudioError
                    $"ffmpeg not found ('{ffmpeg}'). Install ffmpeg, set SUBDRIFT_FFMPEG, or pass a .wav / subtitle reference instead"
            )

    let stderrTask = proc.StandardError.ReadToEndAsync()
    let energy = FrameEnergy 16000
    let buffer = Array.zeroCreate<byte> 65536
    let stdout = proc.StandardOutput.BaseStream
    let mutable carry = -1 // dangling low byte when a read splits a sample
    let mutable n = stdout.Read(buffer, 0, buffer.Length)

    while n > 0 do
        let mutable i = 0

        if carry >= 0 then
            energy.Push(float (int16 (carry ||| (int buffer[0] <<< 8))) / 32768.0)
            carry <- -1
            i <- 1

        while i + 1 < n do
            energy.Push(float (BitConverter.ToInt16(buffer, i)) / 32768.0)
            i <- i + 2

        if i < n then carry <- int buffer[i]
        n <- stdout.Read(buffer, 0, buffer.Length)

    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        raise (AudioError $"ffmpeg failed: {stderrTask.Result.Trim()}")

    energy.Energies

/// Speech activity of a media file. WAV is decoded natively; everything else needs ffmpeg.
let activityOfMedia (ffmpeg: string) (vad: VadOptions) (path: string) : Activity =
    let energies =
        if Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) then
            use fs = File.OpenRead path
            wavEnergies (new BufferedStream(fs, 1 <<< 16))
        else
            ffmpegEnergies ffmpeg path

    detectSpeech vad energies
