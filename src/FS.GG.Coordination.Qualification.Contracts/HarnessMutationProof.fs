namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type HarnessMutationProofContext =
    { CandidateCommit: string
      CandidateTreeSha256: string
      UnitContractSha256: string
      ValidatorSha256: string }

type HarnessMutationProofFinding =
    { Code: string; Path: string; Message: string }

[<RequireQualifiedAccess>]
module HarnessMutationProof =
    [<Literal>]
    let Schema = "fsgg.coordination.harness-mutation-proof/1"

    let GateClasses =
        [ "compiler"; "dependencies"; "externalFixtures"; "generatedCases"; "independentCases"
          "model"; "packages"; "results"; "reviewers"; "sources" ]

    let MutationKinds = [ "vacuous"; "absent"; "stale"; "truncated"; "forged"; "generated-only" ]

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private finding code path message = { Code = code; Path = path; Message = message }

    let private canonicalBytes (node: JsonNode) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        let rec write (value: JsonNode) =
            match value with
            | null -> writer.WriteNullValue()
            | :? JsonObject as objectValue ->
                writer.WriteStartObject()
                objectValue |> Seq.sortBy _.Key |> Seq.iter (fun property -> writer.WritePropertyName property.Key; write property.Value)
                writer.WriteEndObject()
            | :? JsonArray as arrayValue -> writer.WriteStartArray(); arrayValue |> Seq.iter write; writer.WriteEndArray()
            | _ -> value.WriteTo writer
        write node
        writer.Flush()
        Array.append (stream.ToArray()) [| byte '\n' |]

    let private canonicalPayload node =
        let bytes = canonicalBytes node
        bytes[..bytes.Length - 2]

    let private digestNode (root: JsonObject) =
        let unsigned = root.DeepClone().AsObject()
        unsigned.Remove("digest") |> ignore
        root["digest"] <- sha256 (canonicalPayload unsigned)
        canonicalBytes root

    let private codes inventory kind bytes =
        match QualificationManifest.validate inventory (ReadOnlyMemory<byte>(bytes)) with
        | Ok _ when kind = "forged" -> [ "HMP-FORGED-FINGERPRINT" ]
        | Ok _ -> []
        | Error values -> values |> List.map _.Code |> List.distinct |> List.sort

    let private reseal (root: JsonObject) =
        let inputs = JsonObject()
        for name in [ "compiler"; "dependencies"; "environment"; "externalFixtures"; "generatedCases"; "independentCases"; "model"; "packages"; "sources" ] do
            inputs[name] <- root[name].DeepClone()
        let inputDigest = sha256 (canonicalPayload inputs)
        root["candidate"].AsObject()["inputSetSha256"] <- inputDigest
        for name in [ "results"; "reviewers" ] do
            for entry in root[name].AsArray() do entry.AsObject()["inputSetSha256"] <- inputDigest
        let unsigned = root.DeepClone().AsObject()
        unsigned.Remove("digest") |> ignore
        root["digest"] <- sha256 (canonicalPayload unsigned)

    let private generatedProducer (baseline: JsonObject) =
        let generated = baseline["generatedCases"].AsArray()
        let first = generated.[0].AsObject()
        first["producer"].GetValue<string>()

    let private mutate (gate: string) (kind: string) (baselineBytes: ReadOnlyMemory<byte>) =
        let root = JsonNode.Parse(baselineBytes.Span).AsObject()
        let entries () = root[gate].AsArray()
        match kind with
        | "vacuous" -> root[gate] <- JsonArray()
        | "absent" -> root.Remove gate |> ignore
        | "stale" -> (entries()).[0].AsObject()["candidateSha"] <- String('9', 40)
        | "truncated" -> entries().RemoveAt(entries().Count - 1)
        | "forged" ->
            (entries()).[0].AsObject()["sha256"] <- String('9', 64)
            reseal root
        | "generated-only" ->
            let producer = generatedProducer root
            match gate with
            | "generatedCases" ->
                for other in GateClasses do if other <> gate then root[other] <- JsonArray()
            | "results" -> for entry in entries() do entry.AsObject()["producer"] <- producer
            | "reviewers" -> for entry in entries() do entry.AsObject()["principal"] <- producer
            | _ -> for entry in entries() do entry.AsObject()["producer"] <- producer
        | _ -> invalidArg "kind" kind
        if kind = "forged" then canonicalBytes root else Encoding.UTF8.GetBytes(root.ToJsonString())

    let private contextErrors context =
        [ if not (Text.RegularExpressions.Regex.IsMatch(context.CandidateCommit, "^[0-9a-f]{40}$")) then yield finding "HMP-CANDIDATE" "/candidateCommit" "expected lowercase 40-hex revision"
          for path, value in [ "/candidateTreeSha256", context.CandidateTreeSha256; "/unitContractSha256", context.UnitContractSha256; "/validatorSha256", context.ValidatorSha256 ] do
              if not (Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$")) then yield finding "HMP-FINGERPRINT" path "expected lowercase SHA-256" ]

    let private expectedDiagnostic gate kind =
        match kind, gate with
        | "vacuous", "results" -> "QM-RESULTS-EMPTY"
        | "vacuous", "reviewers" -> "QM-REVIEWS-EMPTY"
        | "vacuous", _ -> "QM-CATEGORY-EMPTY"
        | "absent", "results" -> "QM-RESULTS"
        | "absent", "reviewers" -> "QM-REVIEWS"
        | "absent", _ -> "QM-CATEGORY"
        | "stale", _ -> "QM-CANDIDATE-BINDING"
        | "truncated", "results" -> "QM-RESULTS-CLOSED"
        | "truncated", "reviewers" -> "QM-REVIEWS-CLOSED"
        | "truncated", _ -> "QM-CATEGORY-CLOSED"
        | "forged", _ -> "HMP-FORGED-FINGERPRINT"
        | "generated-only", "generatedCases" -> "QM-CATEGORY-EMPTY"
        | "generated-only", _ -> "QM-GENERATED-ONLY"
        | _ -> invalidArg "kind" kind

    let private build context (inventory: ReadOnlyMemory<byte>) (baseline: ReadOnlyMemory<byte>) =
        match contextErrors context, QualificationManifest.validate inventory baseline with
        | errors, _ when not errors.IsEmpty -> Error errors
        | _, Error values -> Error [ finding "HMP-BASELINE" "/baseline" (String.concat "," (values |> List.map _.Code |> List.distinct |> List.sort)) ]
        | _ ->
            let root = JsonObject()
            root["baselineSha256"] <- sha256 (baseline.ToArray())
            root["candidateCommit"] <- context.CandidateCommit
            root["candidateTreeSha256"] <- context.CandidateTreeSha256
            let controls = JsonArray()
            for gate in GateClasses do
                let controlDiagnostics = codes inventory "control" (baseline.ToArray())
                let item = JsonObject()
                let diagnosticNodes = controlDiagnostics |> List.map (fun value -> JsonValue.Create(value) :> JsonNode) |> List.toArray
                item["diagnostics"] <- JsonArray(diagnosticNodes)
                item["gateClass"] <- gate
                item["outcome"] <- if controlDiagnostics.IsEmpty then "passed" else "rejected"
                controls.Add item
            root["controls"] <- controls
            let gateNodes = GateClasses |> List.map (fun value -> JsonValue.Create(value) :> JsonNode) |> List.toArray
            root["gateClasses"] <- JsonArray(gateNodes)
            root["inventorySha256"] <- sha256 (inventory.ToArray())
            let mutationNodes = MutationKinds |> List.map (fun value -> JsonValue.Create(value) :> JsonNode) |> List.toArray
            root["mutationKinds"] <- JsonArray(mutationNodes)
            let observations = JsonArray()
            let mutable unexpected = []
            for gate in GateClasses do
                for kind in MutationKinds do
                    let observed = mutate gate kind baseline |> codes inventory kind
                    if observed.IsEmpty then unexpected <- finding "HMP-UNEXPECTED-GREEN" ($"/observations/%s{gate}/%s{kind}") "production validator accepted mutation" :: unexpected
                    let required = expectedDiagnostic gate kind
                    if not (observed |> List.contains required) then
                        let observedText = String.concat "," observed
                        unexpected <- finding "HMP-WRONG-DIAGNOSTIC" ($"/observations/%s{gate}/%s{kind}") ($"expected %s{required}; observed %s{observedText}") :: unexpected
                    let item = JsonObject()
                    let diagnosticNodes = observed |> List.map (fun value -> JsonValue.Create(value) :> JsonNode) |> List.toArray
                    item["diagnostics"] <- JsonArray(diagnosticNodes)
                    item["gateClass"] <- gate
                    item["mutationKind"] <- kind
                    item["outcome"] <- if observed.IsEmpty then "passed" else "rejected"
                    observations.Add item
            root["observations"] <- observations
            root["schema"] <- Schema
            root["unitContractSha256"] <- context.UnitContractSha256
            root["validatorSha256"] <- context.ValidatorSha256
            if unexpected.IsEmpty then Ok(digestNode root) else Error(List.rev unexpected)

    let generate context inventory baseline = build context inventory baseline

    let validate context inventory baseline (proof: ReadOnlyMemory<byte>) =
        match build context inventory baseline with
        | Error errors -> Error errors
        | Ok expected when proof.Span.SequenceEqual expected -> Ok expected
        | Ok expected -> Error [ finding "HMP-PROOF-MISMATCH" "/" ($"expected canonical proof %s{sha256 expected}, observed %s{sha256 (proof.ToArray())}") ]
