namespace FS.GG.Coordination.Cli

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
module QualificationManifestCommand =
    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private usage () =
        eprintfn "qualification-manifest validate --file FILE [--json|--text]"
        2

    let private options arguments =
        let rec loop (file: string option) (format: string) (remaining: string list) =
            match remaining with
            | [] -> Ok(file, format)
            | "--file" :: value :: tail when file.IsNone -> loop (Some value) format tail
            | "--json" :: tail -> loop file "json" tail
            | "--text" :: tail -> loop file "text" tail
            | token :: _ -> Error $"unknown or repeated argument: %s{token}"
        loop None "json" arguments

    let run arguments =
        match arguments |> Array.toList with
        | "validate" :: rest ->
            match options rest with
            | Error error ->
                eprintfn "%s" error
                usage ()
            | Ok(None, _) -> usage ()
            | Ok(Some path, format) when not (File.Exists path) ->
                eprintfn "qualification manifest does not exist: %s" path
                2
            | Ok(Some path, format) ->
                let bytes = File.ReadAllBytes path
                match QualificationManifest.validate (ReadOnlyMemory<byte>(bytes)) with
                | Ok canonical ->
                    let digest = sha256 canonical
                    if format = "text" then
                        printfn "QUALIFICATION_MANIFEST_OK path=%s bytes=%d sha256=%s" path canonical.Length digest
                    else
                        printfn "%s" (JsonSerializer.Serialize {| schema = "fsgg.coordination.qualification-manifest-result/1"; outcome = "passed"; path = path; bytes = canonical.Length; sha256 = digest; findings = [||] |})
                    0
                | Error findings ->
                    if format = "text" then
                        for item in findings do
                            eprintfn "%s path=%s expected=%s actual=%s" item.Code item.Path item.Expected item.Actual
                    else
                        eprintfn "%s" (JsonSerializer.Serialize {| schema = "fsgg.coordination.qualification-manifest-result/1"; outcome = "failed"; path = path; findings = findings |})
                    3
        | _ -> usage ()
