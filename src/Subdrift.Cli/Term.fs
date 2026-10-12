/// Minimal ANSI styling for terminal output. Honours NO_COLOR and redirection.
module Subdrift.Cli.Term

open System

let mutable enabled = true

let private sgr (code: string) (s: string) =
    if enabled then $"\u001b[{code}m{s}\u001b[0m" else s

let bold = sgr "1"
let dim = sgr "2"
let red = sgr "31"
let green = sgr "32"
let yellow = sgr "33"
let cyan = sgr "36"
let coral = sgr "38;5;209"
let teal = sgr "38;5;43"

let banner () =
    eprintfn "%s %s" (teal "▍subdrift") (dim "· subtitle alignment")

let step (label: string) (detail: string) = eprintfn "  %s %-14s %s" (teal "•") label (dim detail)

let row (label: string) (value: string) = printfn "  %-18s %s" (dim label) value

let warn (msg: string) = eprintfn "  %s %s" (yellow "!") msg

let error (msg: string) = eprintfn "%s %s" (red "error:") msg

/// A tiny bar for coverage values in [0, 1].
let bar (width: int) (x: float) =
    let filled = int (Math.Round(Math.Clamp(x, 0.0, 1.0) * float width))
    String('█', filled) + dim (String('░', width - filled))

let signed (x: float) =
    let s = if x >= 0.0 then "+" else "−"
    sprintf "%s%.3f s" s (abs x)
