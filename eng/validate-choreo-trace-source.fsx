#load "../src/FS.GG.Coordination.Qualification.Contracts/CanonicalProtocolSourceIdentity.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts

let require condition detail =
    if not condition then
        invalidOp ("CHOREO_SOURCE_REFUSED " + detail)

let sha256 (bytes: byte array) =
    bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let exactRegion (text: string) (startMarker: string) (endMarker: string) =
    let start = text.IndexOf(startMarker, StringComparison.Ordinal)
    require (start >= 0) ("missing region start " + startMarker)

    require
        (text.IndexOf(startMarker, start + startMarker.Length, StringComparison.Ordinal) < 0)
        "duplicate region start"

    let finish = text.IndexOf(endMarker, start, StringComparison.Ordinal)
    require (finish > start) ("missing region end " + endMarker)
    text.Substring(start, finish - start)

let choreoRegion (text: string) =
    let lines = text.Replace("\r\n", "\n").Split('\n')

    let headers =
        [
            "O2HostedWriterChoreoModel"
            "O2HostedWriterChoreoProviderBounded"
            "O2HostedWriterChoreoRunnerBounded"
            "O2HostedWriterChoreoProgressQualification"
            "O2HostedWriterChoreoFaultQualification"
            "O2HostedWriterChoreoTests"
            "ChoreoSourcePinSmoke"
        ]
        |> List.map (fun name -> $"module {name} {{")

    let indices =
        headers
        |> List.map (fun header ->
            let matches =
                lines |> Array.indexed |> Array.filter (fun (_, line) -> line.Trim() = header)

            require (matches.Length = 1) "Choreo module boundary differs"
            fst matches[0])

    let first = List.head indices
    let lastHeader = List.last indices

    let last =
        lines
        |> Array.indexed
        |> Array.find (fun (index, line) -> index > lastHeader && line = "}")
        |> fst

    let actualHeaders =
        lines[first..last]
        |> Array.filter (fun line -> line.StartsWith("module ", StringComparison.Ordinal))
        |> Array.toList

    require (actualHeaders = headers) "Choreo module roster differs"
    let mutable after = last + 1

    while after < lines.Length && String.IsNullOrWhiteSpace(lines[after]) do
        after <- after + 1

    (lines[first .. after - 1] |> String.concat "\n") + "\n"

let validateRegions (historical: string) (current: string) =
    let historicalChoreo = choreoRegion historical

    require
        (sha256 (Encoding.UTF8.GetBytes historicalChoreo) =
            "cd5b58dea391bf1afd6a84eb5e8b74f99b9dc3cf665f9ad78881e53cf7c9b6e1")
        "historical Choreo region differs"

    require (choreoRegion current = historicalChoreo) "current Choreo region differs"

    for startMarker, endMarker in
        [
            "// BEGIN PINNED quint-co/choreo spells/basicSpells.qnt", "module ChoreoSourcePinSmoke {"
            "module O2HostedWriterModel {", "// GS2-03.10 model 1:"
        ] do
        require
            (exactRegion current startMarker endMarker = exactRegion historical startMarker endMarker)
            "current execution model differs from historical source"

let arguments =
    fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList

let root =
    match arguments with
    | [ "--root"; root ] -> Path.GetFullPath root
    | _ -> invalidArg "arguments" "expected --root ROOT"

CanonicalProtocolSourceIdentity.requireCurrent root

let fixtures =
    Path.Combine(root, "tests/FS.GG.Coordination.Orchestration.Host.Tests/Fixtures/Choreo")

let manifestBytes = File.ReadAllBytes(Path.Combine(fixtures, "manifest.json"))

require
    (sha256 manifestBytes = "a000f990130b0def9bcd8009deb493829654aefd72eb593f8a83438923945a61")
    "historical manifest bytes differ"

let manifest = JsonDocument.Parse manifestBytes
let source = manifest.RootElement.GetProperty("source")

require
    (source.GetProperty("path").GetString() = "src/FS.GG.Coordination.Protocol/Protocol.md")
    "historical source path differs"

require
    (source.GetProperty("commit").GetString() = "38820f22535eabedefc3aa2590a05ab7498cb5c6")
    "historical source revision differs"

let historicalBytes =
    File.ReadAllBytes(Path.Combine(fixtures, "HistoricalProtocol.md"))

let historicalSha =
    "740c9e55cc02067d04f43eeeaae26a71ab492c96c921eb012bade0883a35d937"

require
    (source.GetProperty("sha256").GetString() = historicalSha
     && sha256 historicalBytes = historicalSha)
    "historical complete source differs"

let historical = Encoding.UTF8.GetString historicalBytes

let current =
    File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md"))

validateRegions historical current

let scenarios =
    manifest.RootElement.GetProperty("scenarios").EnumerateArray() |> Seq.toList

require (scenarios.Length = 8) "historical trace roster differs"

for scenario in scenarios do
    let raw =
        File.ReadAllBytes(Path.Combine(fixtures, scenario.GetProperty("file").GetString()))

    require (sha256 raw = scenario.GetProperty("traceSha256").GetString()) "historical trace bytes differ"

printfn "CHOREO_SOURCE_OK current=%s historical=%s traces=8" CanonicalProtocolSourceIdentity.CurrentSha256 historicalSha
manifest.Dispose()
