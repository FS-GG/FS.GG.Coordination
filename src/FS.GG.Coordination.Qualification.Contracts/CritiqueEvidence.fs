namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type CritiquePerspective =
    | Architecture
    | Security
    | Adapter
    | Migration
    | Cutover

[<RequireQualifiedAccess>]
type CritiqueDecision =
    | Passed
    | ChangesRequired

type CritiqueCandidate =
    { CommitSha: string
      TreeSha256: string
      UnitContractSha256: string }

type CritiqueEvidenceFingerprint =
    { Id: string
      Sha256: string }

type CritiqueFindingInput =
    { Id: string
      Perspective: CritiquePerspective
      PhaseId: string
      Author: string
      Decision: CritiqueDecision
      ContentSha256: string
      CompletedAt: DateTimeOffset }

type CritiqueEvidenceInput =
    { Candidate: CritiqueCandidate
      Evidence: CritiqueEvidenceFingerprint list
      AccountableOwner: string
      Findings: CritiqueFindingInput list
      CreatedAt: DateTimeOffset }

type CritiqueEvidenceSummary =
    { CandidateFingerprintSha256: string
      EvidenceSetSha256: string
      FindingSetSha256: string
      Outcome: string
      Digest: string }

type CritiqueEvidenceFinding =
    { Code: string
      Path: string
      Expected: string
      Actual: string }

[<RequireQualifiedAccess>]
module CritiqueEvidence =
    [<Literal>]
    let Schema = "fsgg.coordination.critique-evidence/1"

    let private requiredPerspectives =
        [ CritiquePerspective.Architecture
          CritiquePerspective.Security
          CritiquePerspective.Adapter
          CritiquePerspective.Migration
          CritiquePerspective.Cutover ]

    let private perspectiveName = function
        | CritiquePerspective.Architecture -> "architecture"
        | CritiquePerspective.Security -> "security"
        | CritiquePerspective.Adapter -> "adapter"
        | CritiquePerspective.Migration -> "migration"
        | CritiquePerspective.Cutover -> "cutover"

    let private decisionName = function
        | CritiqueDecision.Passed -> "passed"
        | CritiqueDecision.ChangesRequired -> "changes-required"

    let private finding code path expected actual =
        { Code = code
          Path = path
          Expected = expected
          Actual = actual }

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private canonicalTime (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

    let private writeCanonical (writer: Utf8JsonWriter) (node: JsonNode) =
        let rec write (value: JsonNode) =
            match value with
            | null -> writer.WriteNullValue()
            | :? JsonObject as objectValue ->
                writer.WriteStartObject()
                objectValue
                |> Seq.sortWith (fun left right -> String.CompareOrdinal(left.Key, right.Key))
                |> Seq.iter (fun property ->
                    writer.WritePropertyName property.Key
                    write property.Value)
                writer.WriteEndObject()
            | :? JsonArray as arrayValue ->
                writer.WriteStartArray()
                arrayValue |> Seq.iter write
                writer.WriteEndArray()
            | _ -> value.WriteTo writer

        write node

    let private canonicalBytes (node: JsonNode) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
        writeCanonical writer node
        writer.Flush()
        stream.ToArray()

    let private addString (node: JsonObject) (name: string) (value: string) = node.Add(name, JsonValue.Create value)

    let private selfBoundNode (node: JsonObject) =
        addString node "digest" ""
        let digest = canonicalBytes node |> sha256
        node["digest"] <- digest
        node, digest

    let private candidateNode (candidate: CritiqueCandidate) =
        let node = JsonObject()
        addString node "commitSha" candidate.CommitSha
        addString node "treeSha256" candidate.TreeSha256
        addString node "unitContractSha256" candidate.UnitContractSha256
        node

    let private evidenceNode (entry: CritiqueEvidenceFingerprint) =
        let node = JsonObject()
        addString node "id" entry.Id
        addString node "sha256" entry.Sha256
        node

    let private idPattern = Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)
    let private shaPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private revisionPattern = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)

    let private validateInput (input: CritiqueEvidenceInput) =
        let mutable findings = []
        let add code path expected actual = findings <- finding code path expected actual :: findings
        if isNull input.Candidate.CommitSha || not (revisionPattern.IsMatch input.Candidate.CommitSha) then
            add "CE-CANDIDATE-REVISION" "/candidate/commitSha" "lowercase 40-character Git revision" input.Candidate.CommitSha
        for name, value in
            [ "treeSha256", input.Candidate.TreeSha256
              "unitContractSha256", input.Candidate.UnitContractSha256 ] do
            if isNull value || not (shaPattern.IsMatch value) then
                add "CE-CANDIDATE-DIGEST" ($"/candidate/%s{name}") "lowercase SHA-256" value
        if isNull input.AccountableOwner || not (idPattern.IsMatch input.AccountableOwner) then
            add "CE-OWNER" "/accountableOwner" "stable owner identity" input.AccountableOwner
        if input.Evidence.IsEmpty then add "CE-EVIDENCE-EMPTY" "/evidence" "at least one fingerprint" "empty"
        let evidenceIds = input.Evidence |> List.map _.Id
        if evidenceIds |> List.exists (fun value -> isNull value || not (idPattern.IsMatch value)) then
            add "CE-EVIDENCE-ID" "/evidence" "stable evidence ids" (String.concat "," evidenceIds)
        if evidenceIds |> List.distinct |> List.length <> evidenceIds.Length then
            add "CE-EVIDENCE-DUPLICATE" "/evidence" "unique evidence ids" (String.concat "," evidenceIds)
        input.Evidence
        |> List.iteri (fun index entry ->
            if isNull entry.Sha256 || not (shaPattern.IsMatch entry.Sha256) then
                add "CE-EVIDENCE-DIGEST" ($"/evidence/%d{index}/sha256") "lowercase SHA-256" entry.Sha256)
        let perspectives = input.Findings |> List.map _.Perspective
        if (perspectives |> List.sortBy perspectiveName) <> (requiredPerspectives |> List.sortBy perspectiveName) then
            add "CE-PERSPECTIVE-INVENTORY" "/findings" (requiredPerspectives |> List.map perspectiveName |> String.concat ",") (perspectives |> List.map perspectiveName |> String.concat ",")
        let findingIds = input.Findings |> List.map _.Id
        if findingIds |> List.exists (fun value -> isNull value || not (idPattern.IsMatch value)) || (findingIds |> List.distinct |> List.length <> findingIds.Length) then
            add "CE-FINDING-ID" "/findings" "unique stable finding ids" (String.concat "," findingIds)
        let phases = input.Findings |> List.map _.PhaseId
        if phases |> List.exists (fun value -> isNull value || not (idPattern.IsMatch value)) || (phases |> List.distinct |> List.length <> phases.Length) then
            add "CE-PHASE-IDENTITY" "/findings" "five unique stable phase identities" (String.concat "," phases)
        input.Findings
        |> List.iteri (fun index item ->
            if item.Author <> input.AccountableOwner then
                add "CE-AUTHORITY" ($"/findings/%d{index}/author") input.AccountableOwner item.Author
            if isNull item.ContentSha256 || not (shaPattern.IsMatch item.ContentSha256) then
                add "CE-CONTENT-DIGEST" ($"/findings/%d{index}/contentSha256") "lowercase SHA-256" item.ContentSha256
            if item.CompletedAt > input.CreatedAt then
                add "CE-TIME-ORDER" ($"/findings/%d{index}/completedAt") "not later than createdAt" (canonicalTime item.CompletedAt))
        List.rev findings

    let private render (input: CritiqueEvidenceInput) =
        let candidate = candidateNode input.Candidate
        let candidateFingerprint = canonicalBytes candidate |> sha256
        addString candidate "fingerprintSha256" candidateFingerprint

        let evidenceEntries = input.Evidence |> List.sortBy _.Id
        let evidence = JsonArray()
        evidenceEntries |> List.iter (fun entry -> evidence.Add(evidenceNode entry))
        let evidenceSet = canonicalBytes evidence |> sha256

        let findingNodes =
            input.Findings
            |> List.sortBy (fun item -> perspectiveName item.Perspective)
            |> List.map (fun item ->
                let node = JsonObject()
                addString node "author" item.Author
                addString node "candidateFingerprintSha256" candidateFingerprint
                addString node "completedAt" (canonicalTime item.CompletedAt)
                addString node "contentSha256" item.ContentSha256
                addString node "decision" (decisionName item.Decision)
                addString node "evidenceSetSha256" evidenceSet
                addString node "id" item.Id
                addString node "perspective" (perspectiveName item.Perspective)
                addString node "phaseId" item.PhaseId
                selfBoundNode node |> fst)

        let findingsArray = JsonArray()
        findingNodes |> List.iter findingsArray.Add
        let findingSet = canonicalBytes findingsArray |> sha256
        let passing =
            input.Findings
            |> List.filter (fun item -> item.Decision = CritiqueDecision.Passed)
            |> List.map (fun item -> perspectiveName item.Perspective)
            |> List.sort
        let outcome = if passing.Length = requiredPerspectives.Length then "passed" else "changes-required"
        let stringArray (values: string list) = JsonArray(values |> List.map (fun value -> JsonValue.Create value :> JsonNode) |> List.toArray)
        let rollup = JsonObject()
        addString rollup "acceptanceAuthority" "accountable-owner-only"
        addString rollup "accountableOwner" input.AccountableOwner
        addString rollup "derivation" "all-required-bound-green/1"
        addString rollup "findingSetSha256" findingSet
        addString rollup "outcome" outcome
        rollup.Add("passingPerspectives", stringArray passing)
        rollup.Add("requiredPerspectives", requiredPerspectives |> List.map perspectiveName |> List.sort |> stringArray)
        let rollup, _ = selfBoundNode rollup

        let root = JsonObject()
        addString root "accountableOwner" input.AccountableOwner
        root.Add("candidate", candidate)
        addString root "createdAt" (canonicalTime input.CreatedAt)
        root.Add("evidence", evidence)
        addString root "evidenceSetSha256" evidenceSet
        root.Add("findings", findingsArray)
        root.Add("rollup", rollup)
        addString root "schema" Schema
        let root, digest = selfBoundNode root
        let bytes = Array.append (canonicalBytes root) [| byte '\n' |]
        bytes,
        { CandidateFingerprintSha256 = candidateFingerprint
          EvidenceSetSha256 = evidenceSet
          FindingSetSha256 = findingSet
          Outcome = outcome
          Digest = digest }

    let generate input =
        match validateInput input with
        | [] -> render input |> fst |> Ok
        | findings -> Error findings

    let validate expected (artifact: ReadOnlyMemory<byte>) =
        match validateInput expected with
        | _ :: _ as findings -> Error findings
        | [] ->
            let bytes, summary = render expected
            if artifact.Span.SequenceEqual(ReadOnlySpan<byte>(bytes)) then Ok summary
            else
                let actual =
                    try
                        use document = JsonDocument.Parse artifact
                        if document.RootElement.ValueKind = JsonValueKind.Object then
                            let mutable schema = Unchecked.defaultof<JsonElement>
                            if document.RootElement.TryGetProperty("schema", &schema) && schema.ValueKind = JsonValueKind.String then schema.GetString()
                            else "<missing>"
                        else document.RootElement.ValueKind.ToString()
                    with :? JsonException -> "<malformed>"
                Error [ finding "CE-BUNDLE-MISMATCH" "" Schema actual ]
