/// Iterative radix-2 FFT and FFT-based cross-correlation.
module Subdrift.Core.Fft

open System
open System.Numerics

let nextPow2 (n: int) =
    let mutable p = 1

    while p < n do
        p <- p <<< 1

    p

/// In-place Cooley–Tukey FFT. `data.Length` must be a power of two.
let transform (inverse: bool) (data: Complex[]) =
    let n = data.Length

    if n &&& (n - 1) <> 0 then
        invalidArg (nameof data) "length must be a power of two"

    // Bit-reversal permutation.
    let mutable j = 0

    for i in 1 .. n - 1 do
        let mutable bit = n >>> 1

        while j &&& bit <> 0 do
            j <- j ^^^ bit
            bit <- bit >>> 1

        j <- j ^^^ bit

        if i < j then
            let t = data[i]
            data[i] <- data[j]
            data[j] <- t

    let sign = if inverse then 1.0 else -1.0
    let mutable len = 2

    while len <= n do
        let ang = sign * 2.0 * Math.PI / float len
        let wLen = Complex(cos ang, sin ang)
        let half = len / 2
        let mutable i = 0

        while i < n do
            let mutable w = Complex.One

            for k in 0 .. half - 1 do
                let u = data[i + k]
                let v = data[i + k + half] * w
                data[i + k] <- u + v
                data[i + k + half] <- u - v
                w <- w * wLen

            i <- i + len

        len <- len <<< 1

    if inverse then
        let scale = 1.0 / float n

        for i in 0 .. n - 1 do
            data[i] <- data[i] * scale

let private padded (n: int) (x: float[]) =
    let a = Array.create n Complex.Zero

    for i in 0 .. x.Length - 1 do
        a[i] <- Complex(x[i], 0.0)

    a

/// Spectrum of a reference signal, reusable across many correlations.
type Spectrum = { Size: int; Bins: Complex[] }

let spectrum (size: int) (x: float[]) =
    let a = padded size x
    transform false a
    { Size = size; Bins = a }

/// c[k] = Σ_t probe[t] · reference[t + k] for every lag k in [-maxLag, maxLag].
/// Returned array index i corresponds to lag (i - maxLag).
let crossCorrelate (reference: Spectrum) (probe: float[]) (maxLag: int) : float[] =
    let n = reference.Size
    let p = padded n probe
    transform false p

    for i in 0 .. n - 1 do
        p[i] <- reference.Bins[i] * Complex.Conjugate p[i]

    transform true p
    // Negative lags wrap around to the end of the circular result.
    Array.init (2 * maxLag + 1) (fun i ->
        let lag = i - maxLag
        p[((lag % n) + n) % n].Real)

/// O(n·m) reference implementation, used by the tests.
let crossCorrelateNaive (reference: float[]) (probe: float[]) (maxLag: int) =
    Array.init (2 * maxLag + 1) (fun i ->
        let lag = i - maxLag
        let mutable s = 0.0

        for t in 0 .. probe.Length - 1 do
            let r = t + lag

            if r >= 0 && r < reference.Length then
                s <- s + probe[t] * reference[r]

        s)
