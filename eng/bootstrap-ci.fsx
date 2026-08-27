open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type GateContract =
    { Id: string
      Artifact: string
      RequiredRunFragments: string list }

type BootstrapContract =
    { EvidenceSchema: string
      RequiredProjectCount: int
      Jobs: GateContract list
      ForbiddenWorkflowTokens: string list
      Bytes: byte array }

let private violation rule detail =
    $"BOOTSTRAP_CI_VIOLATION rule=%s{rule} detail=%s{detail}"

let private sha256Bytes (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private sha256File path = File.ReadAllBytes path |> sha256Bytes

let private stringProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
        value.GetString() |> Option.ofObj
    else
        None

let private arrayProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Array then
        Some(value.EnumerateArray() |> Seq.toList)
    else
        None

let private stringArray (name: string) element =
    arrayProperty name element
    |> Option.defaultValue []
    |> List.choose (fun item -> if item.ValueKind = JsonValueKind.String then item.GetString() |> Option.ofObj else None)

let private loadContract root =
    let path = Path.Combine(root, "eng/bootstrap-ci-contract.json")
    let bytes = File.ReadAllBytes path
    use document = JsonDocument.Parse bytes
    let value = document.RootElement
    if stringProperty "schema" value <> Some "fsgg.coordination.bootstrap-ci-contract/1" then
        failwith "bootstrap contract schema is unsupported"
    let evidenceSchema = stringProperty "evidenceSchema" value |> Option.defaultWith (fun () -> failwith "evidenceSchema is missing")
    let requiredProjectCount = value.GetProperty("requiredProjectCount").GetInt32()
    let jobs =
        arrayProperty "jobs" value
        |> Option.defaultWith (fun () -> failwith "jobs are missing")
        |> List.map (fun job ->
            { Id = stringProperty "id" job |> Option.defaultWith (fun () -> failwith "job id is missing")
              Artifact = stringProperty "artifact" job |> Option.defaultWith (fun () -> failwith "job artifact is missing")
              RequiredRunFragments = stringArray "requiredRunFragments" job })
    { EvidenceSchema = evidenceSchema
      RequiredProjectCount = requiredProjectCount
      Jobs = jobs
      ForbiddenWorkflowTokens = stringArray "forbiddenWorkflowTokens" value
      Bytes = bytes }

let private isSha value =
    not (String.IsNullOrWhiteSpace value)
    && value.Length = 40
    && value |> Seq.forall Uri.IsHexDigit

let private optionValue name (arguments: string list) =
    arguments
    |> List.tryFindIndex ((=) name)
    |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

let private inspectWorkflow root (contract: BootstrapContract) =
    let path = Path.Combine(root, ".github/workflows/bootstrap-qualification.yml")
    if not (File.Exists path) then
        [ violation "workflow-missing" ".github/workflows/bootstrap-qualification.yml" ]
    else
        let text = File.ReadAllText(path).Replace("\r\n", "\n")
        let lines = text.Split '\n' |> Array.toList
        let jobHeader = Regex("^  ([a-z0-9-]+):\\s*$", RegexOptions.Compiled)
        let jobsStart =
            lines
            |> List.tryFindIndex ((=) "jobs:")
            |> Option.map ((+) 1)
            |> Option.defaultValue lines.Length
        let indexedJobs =
            lines
            |> List.skip jobsStart
            |> List.indexed
            |> List.choose (fun (index, line) ->
                let matched = jobHeader.Match line
                if matched.Success then Some(index + jobsStart, matched.Groups[1].Value) else None)
        let jobBlocks =
            indexedJobs
            |> List.mapi (fun position (startIndex, id) ->
                let endIndex =
                    indexedJobs
                    |> List.tryItem (position + 1)
                    |> Option.map fst
                    |> Option.defaultValue lines.Length
                id, String.concat "\n" lines[startIndex .. endIndex - 1])
            |> Map.ofList
        let expectedJobs = contract.Jobs |> List.map _.Id |> Set.ofList
        let actualJobs = jobBlocks |> Map.keys |> Set.ofSeq
        let permissionIndex = lines |> List.tryFindIndex ((=) "permissions:")
        let permissionLines =
            permissionIndex
            |> Option.map (fun start ->
                lines
                |> List.skip (start + 1)
                |> List.takeWhile (fun line -> String.IsNullOrWhiteSpace line || line.StartsWith " ")
                |> List.filter (String.IsNullOrWhiteSpace >> not))
            |> Option.defaultValue []
        let usesPattern = Regex("uses:\\s*[^@\\s]+@(?<ref>[^\\s#]+)", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)
        let actionRefs = usesPattern.Matches text |> Seq.cast<Match> |> Seq.map (fun matched -> matched.Groups["ref"].Value) |> Seq.toList
        let expectedJobNames = expectedJobs |> Set.toList |> String.concat ","
        let actualJobNames = actualJobs |> Set.toList |> String.concat ","
        [ if actualJobs <> expectedJobs then
              yield violation "workflow-job-set" $"expected=%s{expectedJobNames} actual=%s{actualJobNames}"
          if permissionLines <> [ "  contents: read" ] then
              yield violation "workflow-permissions" (String.concat ";" permissionLines)
          if not (text.Contains("pull_request:", StringComparison.Ordinal))
             || not (text.Contains("push:", StringComparison.Ordinal))
             || not (text.Contains("branches: [main]", StringComparison.Ordinal)) then
              yield violation "workflow-trigger" "pull_request and main push are required"
          if List.isEmpty actionRefs then
              yield violation "workflow-action-pin" "no action references found"
          for actionRef in actionRefs do
              if not (isSha actionRef) then
                  yield violation "workflow-action-pin" actionRef
          for token in contract.ForbiddenWorkflowTokens do
              if text.Contains(token, StringComparison.OrdinalIgnoreCase) then
                  yield violation "workflow-authority-ceiling" token
          for job in contract.Jobs do
              match Map.tryFind job.Id jobBlocks with
              | None -> ()
              | Some block ->
                  for fragment in job.RequiredRunFragments do
                      if not (block.Contains(fragment, StringComparison.Ordinal)) then
                          yield violation "workflow-command-contract" $"job=%s{job.Id} missing=%s{fragment}" ]

let rec private vulnerabilityCounts (element: JsonElement) =
    seq {
        match element.ValueKind with
        | JsonValueKind.Object ->
            for property in element.EnumerateObject() do
                if property.NameEquals "vulnerabilities" && property.Value.ValueKind = JsonValueKind.Array then
                    yield property.Value.GetArrayLength()
                yield! vulnerabilityCounts property.Value
        | JsonValueKind.Array ->
            for item in element.EnumerateArray() do
                yield! vulnerabilityCounts item
        | _ -> ()
    }

let private inspectVulnerabilityReport path (contract: BootstrapContract) =
    try
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let root = document.RootElement
        let sources = stringArray "sources" root
        let projects = arrayProperty "projects" root |> Option.defaultValue []
        let projectPaths = projects |> List.choose (stringProperty "path")
        [ if root.GetProperty("version").GetInt32() <> 1 then
              yield violation "vulnerability-report-version" "expected version 1"
          if not ((stringProperty "parameters" root |> Option.defaultValue "").Contains("--vulnerable", StringComparison.Ordinal)) then
              yield violation "vulnerability-report-parameters" "--vulnerable is absent"
          if List.isEmpty sources || sources |> List.exists (fun source -> not (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) then
              yield violation "vulnerability-report-source" (String.concat "," sources)
          if projects.Length <> contract.RequiredProjectCount || projectPaths.Length <> projects.Length then
              yield violation "vulnerability-report-completeness" $"expected=%d{contract.RequiredProjectCount} actual=%d{projects.Length}"
          if projectPaths |> List.distinct |> List.length <> projectPaths.Length then
              yield violation "vulnerability-report-completeness" "duplicate project paths"
          let vulnerable = vulnerabilityCounts root |> Seq.sum
          if vulnerable <> 0 then
              yield violation "vulnerable-package" $"count=%d{vulnerable}" ]
    with exceptionValue ->
        [ violation "vulnerability-report-unreadable" exceptionValue.Message ]

let private safeArtifactPath (root: string) (relative: string) =
    let combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
    let normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
    if combined.StartsWith(normalizedRoot, StringComparison.Ordinal) then Some combined else None

let private writeEvidence (output: string) (head: string) (artifactRoot: string) (contract: BootstrapContract) =
    if not (isSha head) then failwith "candidate head must be an exact 40-hex SHA"
    let artifacts =
        contract.Jobs
        |> List.map (fun gate ->
            let path = safeArtifactPath artifactRoot gate.Artifact |> Option.defaultWith (fun () -> failwith $"unsafe artifact path: %s{gate.Artifact}")
            if not (File.Exists path) then failwith $"required gate artifact is missing: %s{gate.Artifact}"
            gate, sha256File path)
    let options = JsonWriterOptions(Indented = true)
    use stream = File.Create output
    use writer = new Utf8JsonWriter(stream, options)
    writer.WriteStartObject()
    writer.WriteString("schema", contract.EvidenceSchema)
    writer.WriteString("candidate", head.ToLowerInvariant())
    writer.WriteString("contractSha256", sha256Bytes contract.Bytes)
    writer.WriteStartArray("gates")
    for gate, digest in artifacts do
        writer.WriteStartObject()
        writer.WriteString("id", gate.Id)
        writer.WriteString("artifact", gate.Artifact)
        writer.WriteString("sha256", digest)
        writer.WriteStartArray("commands")
        for command in gate.RequiredRunFragments do writer.WriteStringValue command
        writer.WriteEndArray()
        writer.WriteEndObject()
    writer.WriteEndArray()
    writer.WriteEndObject()
    writer.Flush()

let private inspectEvidence (path: string) (head: string) (artifactRoot: string) (contract: BootstrapContract) =
    try
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let root = document.RootElement
        let gates = arrayProperty "gates" root |> Option.defaultValue []
        let expected = contract.Jobs |> List.map (fun gate -> gate.Id, gate) |> Map.ofList
        let ids = gates |> List.choose (stringProperty "id")
        [ if stringProperty "schema" root <> Some contract.EvidenceSchema then
              yield violation "evidence-schema" "unsupported or absent schema"
          if stringProperty "candidate" root <> Some(head.ToLowerInvariant()) then
              yield violation "evidence-candidate" $"expected=%s{head}"
          if stringProperty "contractSha256" root <> Some(sha256Bytes contract.Bytes) then
              yield violation "evidence-contract-digest" "contract bytes do not match"
          if ids.Length <> gates.Length || ids |> List.distinct |> List.length <> ids.Length || Set.ofList ids <> (expected |> Map.keys |> Set.ofSeq) then
              yield violation "evidence-gate-set" (String.concat "," ids)
          for gateValue in gates do
              match stringProperty "id" gateValue |> Option.bind (fun id -> Map.tryFind id expected |> Option.map (fun gate -> id, gate)) with
              | None -> ()
              | Some(id, gate) ->
                  if stringProperty "artifact" gateValue <> Some gate.Artifact then
                      yield violation "evidence-artifact-path" id
                  if stringArray "commands" gateValue <> gate.RequiredRunFragments then
                      yield violation "evidence-command-contract" id
                  match safeArtifactPath artifactRoot gate.Artifact with
                  | None -> yield violation "evidence-artifact-path" gate.Artifact
                  | Some artifactPath when not (File.Exists artifactPath) ->
                      yield violation "evidence-artifact-missing" gate.Artifact
                  | Some artifactPath ->
                      let observed = sha256File artifactPath
                      if stringProperty "sha256" gateValue <> Some observed then
                          yield violation "evidence-artifact-digest" $"gate=%s{id} observed=%s{observed}" ]
    with exceptionValue ->
        [ violation "evidence-unreadable" exceptionValue.Message ]

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList
let mode = arguments |> List.tryHead |> Option.defaultValue "workflow"
let root = optionValue "--root" arguments |> Option.defaultValue (Directory.GetCurrentDirectory()) |> Path.GetFullPath

let result =
    try
        let contract = loadContract root
        match mode with
        | "workflow" -> inspectWorkflow root contract
        | "vulnerability" ->
            optionValue "--report" arguments
            |> Option.map (fun path -> inspectVulnerabilityReport (Path.GetFullPath path) contract)
            |> Option.defaultValue [ violation "argument" "--report is required" ]
        | "collect" ->
            match optionValue "--head" arguments, optionValue "--artifacts" arguments, optionValue "--output" arguments with
            | Some head, Some artifacts, Some output ->
                writeEvidence (Path.GetFullPath output) head (Path.GetFullPath artifacts) contract
                []
            | _ -> [ violation "argument" "collect requires --head, --artifacts, and --output" ]
        | "evidence" ->
            match optionValue "--head" arguments, optionValue "--artifacts" arguments, optionValue "--file" arguments with
            | Some head, Some artifacts, Some path when isSha head ->
                inspectEvidence (Path.GetFullPath path) head (Path.GetFullPath artifacts) contract
            | _ -> [ violation "argument" "evidence requires an exact --head plus --artifacts and --file" ]
        | unknown -> [ violation "argument" $"unknown mode: %s{unknown}" ]
    with exceptionValue ->
        [ violation "unreadable-contract" exceptionValue.Message ]

if List.isEmpty result then
    printfn "BOOTSTRAP_CI_OK mode=%s" mode
    exit 0
else
    result |> List.iter (eprintfn "%s")
    exit 1
