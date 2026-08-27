namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text.Json
open FS.GG.SDD.Artifacts.TypedSpecifications

[<RequireQualifiedAccess>]
type QualificationResult =
    | Passed
    | Failed of rule: string

type QualificationReceipt =
    { Rule: string
      Result: QualificationResult }

type PublishedQuintKernelIdentity =
    { PackageId: string
      PackageVersion: string
      ManifestPath: string
      ManifestSha256: string
      Schema: string
      Profile: string
      ProducerMerge: string
      ConsumerMerge: string
      QuintVersion: string
      QuintBinarySha256: string
      GuidanceSource: string
      GuidanceTreeSha256: string }

type PublishedQuintKernelFinding =
    { Code: string
      Path: string
      Expected: string
      Actual: string }

[<RequireQualifiedAccess>]
module PublishedQuintKernel =
    let expected =
        { PackageId = "FS.GG.SDD.Artifacts"
          PackageVersion = "1.5.0"
          ManifestPath = "quint/q1-identity-manifest.json"
          ManifestSha256 = "abd9c18e8146ac3855be58ce88f1efbf5e74a4b1e42c8bc35927478cc74393b2"
          Schema = "fsgg.quint.q2-toolchain-identity/1"
          Profile = "fsgg-quint-profile/1"
          ProducerMerge = "FS-GG/FS.GG.SDD@60351fd0614a5c8e4bdf286c21f185196116fd69"
          ConsumerMerge = "EHotwagner/S.I.R.@77e56d11867a5e2e7ad99f4d61b0f0c9fff61a5f"
          QuintVersion = "0.32.0"
          QuintBinarySha256 = "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
          GuidanceSource = "quint-co/quint-llm-kit@cc75369f741af7d490936f82002c2d28e3b3d78d"
          GuidanceTreeSha256 = "68a11d403846de3af26759eef97f4a35eff5e71d561d41ea17d96e535c171556" }

    let referencedAssemblyName = typeof<SpecificationId>.Assembly.GetName().Name

    let private sha256 (bytes: ReadOnlyMemory<byte>) =
        SHA256.HashData(bytes.Span)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private actualOrMissing value =
        match value with
        | Some text -> text
        | None -> "<missing>"

    let private readString (root: JsonElement) (segments: string list) =
        let rec loop (current: JsonElement) (remaining: string list) =
            match remaining with
            | [] when current.ValueKind = JsonValueKind.String -> current.GetString() |> Option.ofObj
            | [] -> None
            | segment :: tail when current.ValueKind = JsonValueKind.Object ->
                let mutable child = Unchecked.defaultof<JsonElement>
                if current.TryGetProperty(segment, &child) then loop child tail else None
            | _ -> None

        loop root segments

    let private finding code path expected actual =
        { Code = code
          Path = path
          Expected = expected
          Actual = actualOrMissing actual }

    let validateManifest (manifest: ReadOnlyMemory<byte>) =
        let digest = sha256 manifest

        let digestFindings =
            if digest = expected.ManifestSha256 then []
            else [ finding "KERNEL-MANIFEST-DIGEST" "/" expected.ManifestSha256 (Some digest) ]

        try
            use document = JsonDocument.Parse manifest
            let root = document.RootElement

            let expectedFields =
                [ "KERNEL-SCHEMA", "/schema", [ "schema" ], expected.Schema
                  "KERNEL-PROFILE", "/qualification/profile", [ "qualification"; "profile" ], expected.Profile
                  "KERNEL-PRODUCER", "/qualification/producerMerge", [ "qualification"; "producerMerge" ], expected.ProducerMerge
                  "KERNEL-CONSUMER", "/qualification/consumerMerge", [ "qualification"; "consumerMerge" ], expected.ConsumerMerge
                  "KERNEL-QUINT-VERSION", "/tools/quint/version", [ "tools"; "quint"; "version" ], expected.QuintVersion
                  "KERNEL-QUINT-BINARY", "/tools/quint/binarySha256", [ "tools"; "quint"; "binarySha256" ], expected.QuintBinarySha256
                  "KERNEL-GUIDANCE-SOURCE", "/guidance/source", [ "guidance"; "source" ], expected.GuidanceSource
                  "KERNEL-GUIDANCE-TREE", "/guidance/trackedTreeSha256", [ "guidance"; "trackedTreeSha256" ], expected.GuidanceTreeSha256 ]

            let fieldFindings =
                expectedFields
                |> List.choose (fun (code, path, segments, expectedValue) ->
                    let actual = readString root segments
                    if actual = Some expectedValue then None
                    else Some(finding code path expectedValue actual))

            match digestFindings @ fieldFindings with
            | [] when referencedAssemblyName = expected.PackageId -> Ok expected
            | [] ->
                Error
                    [ finding
                          "KERNEL-ASSEMBLY-IDENTITY"
                          "/package/assembly"
                          expected.PackageId
                          (Some referencedAssemblyName) ]
            | findings -> Error findings
        with :? JsonException as error ->
            Error(
                digestFindings
                @ [ finding "KERNEL-MANIFEST-JSON" "/" "valid JSON" (Some error.Message) ]
            )
