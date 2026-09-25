open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
let rec option name = function
    | key :: value :: _ when key = name -> Some value
    | _ :: tail -> option name tail
    | [] -> None

let root = option "--root" arguments |> Option.defaultValue "." |> Path.GetFullPath
let source = Path.Combine(root, "eng/validate-github-callable-protected-acceptance.py")
if not (File.Exists source) then failwith "protected acceptance validator is missing"
let actual = SHA256.HashData(File.ReadAllBytes source) |> Convert.ToHexString |> _.ToLowerInvariant()
if actual <> "64d2c47bbfbbb01dd2c25694677f2892e51f2e20b9ae1a712afba0f0ea4bb22b" then
    failwith "protected acceptance validator changed"

let info = ProcessStartInfo("python3", WorkingDirectory = root, UseShellExecute = false,
                            RedirectStandardOutput = true, RedirectStandardError = true)
info.Environment["PYTHONDONTWRITEBYTECODE"] <- "1"
info.ArgumentList.Add(source)
let child = Process.Start info
let output = child.StandardOutput.ReadToEndAsync()
let error = child.StandardError.ReadToEndAsync()
child.WaitForExit()
if child.ExitCode <> 0 then failwith $"protected acceptance refused: {error.Result}"
if not (output.Result.Contains("revalidated; no provider mutation", StringComparison.Ordinal)) then
    failwith "protected acceptance success marker is missing"
printf "%s" output.Result
