/// Settings resolution: command-line flag > environment variable > .env file > default.
module Subdrift.Cli.Config

open System
open System.Globalization
open System.IO

/// Loads KEY=VALUE pairs from a .env file into the process environment.
/// Variables that are already set win, so the real environment can override the file.
let loadDotEnv (path: string) =
    if File.Exists path then
        for raw in File.ReadAllLines path do
            let line = raw.Trim()

            if line.Length > 0 && not (line.StartsWith "#") then
                let line = if line.StartsWith "export " then line.Substring 7 else line
                let eq = line.IndexOf '='

                if eq > 0 then
                    let key = line.Substring(0, eq).Trim()
                    let value = line.Substring(eq + 1).Trim().Trim('"', '\'')

                    if isNull (Environment.GetEnvironmentVariable key) then
                        Environment.SetEnvironmentVariable(key, value)

type Settings =
    { Ffmpeg: string
      FallbackEncoding: string
      MaxOffset: float
      SplitPenalty: float
      MaxSplit: float
      VadThreshold: float
      NoColor: bool }

let private env (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

let parseFloat (name: string) (value: string) =
    match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v -> v
    | _ -> failwith $"{name} must be a number, got '{value}'"

let private envFloat name fallback =
    env name |> Option.map (parseFloat name) |> Option.defaultValue fallback

/// Reads every SUBDRIFT_* variable (after .env has been loaded).
let fromEnvironment () =
    { Ffmpeg = env "SUBDRIFT_FFMPEG" |> Option.defaultValue "ffmpeg"
      FallbackEncoding = env "SUBDRIFT_FALLBACK_ENCODING" |> Option.defaultValue "windows-1252"
      MaxOffset = envFloat "SUBDRIFT_MAX_OFFSET" 120.0
      SplitPenalty = envFloat "SUBDRIFT_SPLIT_PENALTY" 6.0
      MaxSplit = envFloat "SUBDRIFT_MAX_SPLIT" 60.0
      VadThreshold = envFloat "SUBDRIFT_VAD_THRESHOLD" 0.35
      NoColor = (env "NO_COLOR").IsSome || Console.IsOutputRedirected }
