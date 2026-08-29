module FS.GG.Coordination.Qualification.Contracts.BootstrapCi

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type GateContract =
    { Id: string
      Artifact: string
      TimeoutMinutes: int
      EntryPoint: string
      Cache: bool
      FetchDepth: int
      AlwaysUpload: bool
      Needs: string list
      Commands: string list }

type ActionPins =
    { Checkout: string
      SetupDotnet: string
      Cache: string
      UploadArtifact: string
      DownloadArtifact: string }

type BootstrapContract =
    { EvidenceSchema: string
      Actions: ActionPins
      CacheKey: string
      CachePath: string
      ConcurrencyGroup: string
      CancelInProgress: bool
      RequiredProjectCount: int
      RequiredProjects: string list
      RequiredVulnerabilitySources: string list
      Jobs: GateContract list
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

let private int64Property (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    let mutable number = 0L
    if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Number && value.TryGetInt64(&number) then
        Some number
    else
        None

let private boolProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.TryGetProperty(name, &value) && (value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False) then
        Some(value.GetBoolean())
    else
        None

let private stringArray (name: string) element =
    arrayProperty name element
    |> Option.defaultValue []
    |> List.choose (fun item -> if item.ValueKind = JsonValueKind.String then item.GetString() |> Option.ofObj else None)

let private loadContract root =
    let path = Path.Combine(root, "eng/bootstrap-qualification-plan.json")
    let bytes = File.ReadAllBytes path
    use document = JsonDocument.Parse bytes
    let value = document.RootElement
    if stringProperty "schema" value <> Some "fsgg.coordination.bootstrap-qualification-plan/2" then
        failwith "bootstrap qualification plan schema is unsupported"
    let evidenceSchema = stringProperty "evidenceSchema" value |> Option.defaultWith (fun () -> failwith "evidenceSchema is missing")
    let actionsValue = value.GetProperty("actions")
    let action name =
        stringProperty name actionsValue
        |> Option.defaultWith (fun () -> failwith $"action pin is missing: %s{name}")
    let actions =
        { Checkout = action "checkout"
          SetupDotnet = action "setupDotnet"
          Cache = action "cache"
          UploadArtifact = action "uploadArtifact"
          DownloadArtifact = action "downloadArtifact" }
    let approvedActions =
        [ actions.Checkout, "3d3c42e5aac5ba805825da76410c181273ba90b1"
          actions.SetupDotnet, "a98b56852c35b8e3190ac28c8c2271da59106c68"
          actions.Cache, "55cc8345863c7cc4c66a329aec7e433d2d1c52a9"
          actions.UploadArtifact, "043fb46d1a93c77aae656e7c1c64a875d1fc6a0a"
          actions.DownloadArtifact, "3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c" ]
    for actionPin, approvedPin in approvedActions do
        if not (isNull actionPin) && (actionPin.Length <> 40 || actionPin |> Seq.exists (Uri.IsHexDigit >> not)) then
            failwith "every action must use an exact immutable SHA"
        if actionPin <> approvedPin then failwith "action pin is not the reviewed Node 24 revision"
    let actionRuntimes = value.GetProperty("actionRuntimes")
    for name in [ "checkout"; "setupDotnet"; "cache"; "uploadArtifact"; "downloadArtifact" ] do
        if stringProperty name actionRuntimes <> Some "node24" then failwith $"action runtime must be node24: %s{name}"
    if stringArray "triggers" value <> [ "pull_request"; "push:main" ] then failwith "triggers must be pull_request and push:main"
    if stringArray "permissions" value <> [ "contents:read" ] then failwith "permissions must be contents:read"
    let concurrency = value.GetProperty("concurrency")
    let concurrencyGroup = stringProperty "group" concurrency |> Option.defaultWith (fun () -> failwith "concurrency group is missing")
    let cancelInProgress = boolProperty "cancelInProgress" concurrency |> Option.defaultWith (fun () -> failwith "cancelInProgress is missing")
    if concurrencyGroup <> "bootstrap-qualification-${{ github.ref }}" || not cancelInProgress then
        failwith "concurrency must cancel superseded attempts within the candidate ref"
    let cacheKey = stringProperty "cacheKey" value |> Option.defaultWith (fun () -> failwith "cacheKey is missing")
    let cachePath = stringProperty "cachePath" value |> Option.defaultWith (fun () -> failwith "cachePath is missing")
    if cacheKey <> "${{ runner.os }}-nuget-${{ hashFiles('global.json', '**/packages.lock.json') }}" then
        failwith "cacheKey must bind runner OS, global.json, and every lock file exactly"
    if cachePath <> "/tmp/fsgg-nuget-packages" then failwith "cachePath must be a stable runner-local literal"
    let requiredProjectCount = value.GetProperty("requiredProjectCount").GetInt32()
    let requiredProjects = stringArray "requiredProjects" value
    if requiredProjects.Length <> requiredProjectCount || (requiredProjects |> List.distinct |> List.length) <> requiredProjectCount then
        failwith "requiredProjects must contain the exact distinct project census"
    let requiredVulnerabilitySources = stringArray "requiredVulnerabilitySources" value
    if List.isEmpty requiredVulnerabilitySources || requiredVulnerabilitySources |> List.exists (fun source -> not (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) then
        failwith "requiredVulnerabilitySources must be a non-empty HTTPS-only set"
    let jobs =
        arrayProperty "jobs" value
        |> Option.defaultWith (fun () -> failwith "jobs are missing")
        |> List.map (fun job ->
            let entryPoint = stringProperty "entryPoint" job |> Option.defaultWith (fun () -> failwith "job entryPoint is missing")
            { Id = stringProperty "id" job |> Option.defaultWith (fun () -> failwith "job id is missing")
              Artifact = stringProperty "artifact" job |> Option.defaultWith (fun () -> failwith "job artifact is missing")
              TimeoutMinutes = job.GetProperty("timeoutMinutes").GetInt32()
              EntryPoint = entryPoint
              Cache = boolProperty "cache" job |> Option.defaultValue false
              FetchDepth = job.GetProperty("fetchDepth").GetInt32()
              AlwaysUpload = boolProperty "alwaysUpload" job |> Option.defaultValue false
              Needs = stringArray "needs" job
              Commands = [ entryPoint ] })
    let expectedIds =
        set [ "deterministic-build"; "compiler-and-tests"; "canonical-quint"; "dependency-and-security"; "package-install-smoke"; "bootstrap-recovery"; "evidence-manifest" ]
    if (jobs |> List.map _.Id |> Set.ofList) <> expectedIds || (jobs |> List.map _.Id |> List.distinct |> List.length) <> expectedIds.Count then
        failwith "jobs must contain the exact seven-gate identity set"
    let prerequisiteIds = expectedIds.Remove "evidence-manifest"
    let cachedIds = set [ "deterministic-build"; "compiler-and-tests"; "package-install-smoke" ]
    for job in jobs do
        if job.TimeoutMinutes < 1 || job.TimeoutMinutes > 35 then failwith $"job timeout is outside the bounded policy: %s{job.Id}"
        if job.EntryPoint <> $"bash eng/bootstrap-gates/%s{job.Id}.sh" then failwith $"job entryPoint is not stable: %s{job.Id}"
        if job.Cache <> cachedIds.Contains job.Id then failwith $"job cache policy is invalid: %s{job.Id}"
        if job.FetchDepth <> (if job.Id = "bootstrap-recovery" then 0 else 1) then failwith $"job fetch depth is invalid: %s{job.Id}"
        if job.AlwaysUpload <> (job.Id = "canonical-quint") then failwith $"job always-upload policy is invalid: %s{job.Id}"
        if job.Id = "evidence-manifest" then
            if Set.ofList job.Needs <> prerequisiteIds || job.Needs.Length <> prerequisiteIds.Count then
                failwith "terminal evidence must depend on every prerequisite exactly once"
        elif not (List.isEmpty job.Needs) then
            failwith $"prerequisite gate must remain independently scheduled: %s{job.Id}"
    { EvidenceSchema = evidenceSchema
      Actions = actions
      CacheKey = cacheKey
      CachePath = cachePath
      ConcurrencyGroup = concurrencyGroup
      CancelInProgress = cancelInProgress
      RequiredProjectCount = requiredProjectCount
      RequiredProjects = requiredProjects
      RequiredVulnerabilitySources = requiredVulnerabilitySources
      Jobs = jobs
      Bytes = bytes }

let private isSha value =
    not (String.IsNullOrWhiteSpace value)
    && value.Length = 40
    && value |> Seq.forall Uri.IsHexDigit

let private optionValue name (arguments: string list) =
    arguments
    |> List.tryFindIndex ((=) name)
    |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

let private renderWorkflow (contract: BootstrapContract) =
    let output = StringBuilder()
    let line (value: string) = output.AppendLine(value) |> ignore
    line "# Generated by eng/generate-bootstrap-workflow.fsx from eng/bootstrap-qualification-plan.json."
    line "# Edit the plan, not this projection."
    line "name: Bootstrap qualification"
    line ""
    line "on:"
    line "  pull_request:"
    line "  push:"
    line "    branches: [main]"
    line ""
    line "permissions:"
    line "  contents: read"
    line ""
    line "concurrency:"
    line $"  group: %s{contract.ConcurrencyGroup}"
    line $"  cancel-in-progress: %s{contract.CancelInProgress.ToString().ToLowerInvariant()}"
    line ""
    line "jobs:"
    for gate in contract.Jobs do
        line $"  %s{gate.Id}:"
        line $"    name: %s{gate.Id}"
        if not (List.isEmpty gate.Needs) then
            let needs = String.concat ", " gate.Needs
            line $"    needs: [%s{needs}]"
        line "    runs-on: ubuntu-latest"
        line $"    timeout-minutes: %d{gate.TimeoutMinutes}"
        if gate.Cache || gate.Id = "canonical-quint" || gate.Id = "dependency-and-security" || gate.Id = "evidence-manifest" then
            line "    env:"
            if gate.Cache then
                line $"      NUGET_PACKAGES: %s{contract.CachePath}"
            else
                line ("      NUGET_PACKAGES: /tmp/fsgg-${{ github.run_id }}-nuget-" + gate.Id)
            if gate.Id = "canonical-quint" then
                line "      FSGG_QUINT_RECEIPT: /tmp/fsgg-${{ github.run_id }}-canonical-quint/qualification.json"
            if gate.Id = "evidence-manifest" then
                line "      FSGG_CANDIDATE_SHA: ${{ github.event.pull_request.head.sha || github.sha }}"
        line "    steps:"
        line "      - name: Check out the exact candidate"
        line $"        uses: actions/checkout@%s{contract.Actions.Checkout}"
        line "        with:"
        line "          ref: ${{ github.event.pull_request.head.sha || github.sha }}"
        if gate.FetchDepth <> 1 then line $"          fetch-depth: %d{gate.FetchDepth}"
        line "      - name: Set up the pinned .NET SDK"
        line $"        uses: actions/setup-dotnet@%s{contract.Actions.SetupDotnet}"
        line "        with:"
        line "          global-json-file: global.json"
        if gate.Cache then
            line "      - name: Restore the exact dependency cache"
            line $"        uses: actions/cache@%s{contract.Actions.Cache}"
            line "        with:"
            line $"          path: %s{contract.CachePath}"
            line $"          key: %s{contract.CacheKey}"
        if gate.Id = "evidence-manifest" then
            line "      - name: Download all prerequisite evidence"
            line $"        uses: actions/download-artifact@%s{contract.Actions.DownloadArtifact}"
            line "        with:"
            line "          path: ${{ runner.temp }}/bootstrap-artifacts"
        line "      - name: Run the stable qualification gate"
        line $"        run: %s{gate.EntryPoint}"
        line "      - name: Upload qualification evidence"
        if gate.AlwaysUpload then line "        if: ${{ always() }}"
        line $"        uses: actions/upload-artifact@%s{contract.Actions.UploadArtifact}"
        line "        with:"
        let uploadName = if gate.Id = "evidence-manifest" then "bootstrap-evidence-manifest" else gate.Id
        let uploadPath =
            if gate.Id = "evidence-manifest" then "${{ runner.temp }}/bootstrap-evidence.json"
            elif gate.Id = "canonical-quint" then "${{ env.FSGG_QUINT_RECEIPT }}"
            else "${{ runner.temp }}/" + gate.Artifact
        line $"          name: %s{uploadName}"
        line $"          path: %s{uploadPath}"
        line "          if-no-files-found: error"
        line ""
    output.ToString().Replace("\r\n", "\n").TrimEnd() + "\n"

let private inspectWorkflow root (contract: BootstrapContract) =
    let path = Path.Combine(root, ".github/workflows/bootstrap-qualification.yml")
    if not (File.Exists path) then
        [ violation "workflow-missing" ".github/workflows/bootstrap-qualification.yml" ]
    else
        let text = File.ReadAllText(path).Replace("\r\n", "\n")
        if text = renderWorkflow contract then []
        else [ violation "workflow-projection-stale" "workflow differs from the canonical qualification plan projection" ]

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

let private inspectVulnerabilityReport path repoRoot (contract: BootstrapContract) =
    try
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let root = document.RootElement
        let sources = stringArray "sources" root
        let projects = arrayProperty "projects" root |> Option.defaultValue []
        let projectPaths = projects |> List.choose (stringProperty "path")
        let normalizedProjectPaths =
            projectPaths
            |> List.map (fun projectPath ->
                let absolute =
                    if Path.IsPathRooted projectPath then Path.GetFullPath projectPath
                    else Path.GetFullPath(Path.Combine(repoRoot, projectPath))
                Path.GetRelativePath(repoRoot, absolute).Replace('\\', '/'))
        let expectedProjects = contract.RequiredProjects |> Set.ofList
        let observedProjects = normalizedProjectPaths |> Set.ofList
        [ if root.GetProperty("version").GetInt32() <> 1 then
              yield violation "vulnerability-report-version" "expected version 1"
          if stringProperty "parameters" root <> Some "--vulnerable --include-transitive" then
              yield violation "vulnerability-report-parameters" "expected exact vulnerable and transitive parameters"
          if Set.ofList sources <> Set.ofList contract.RequiredVulnerabilitySources || sources.Length <> contract.RequiredVulnerabilitySources.Length then
              yield violation "vulnerability-report-source" (String.concat "," sources)
          if projects.Length <> contract.RequiredProjectCount
             || projectPaths.Length <> projects.Length
             || normalizedProjectPaths.Length <> (normalizedProjectPaths |> List.distinct |> List.length)
             || observedProjects <> expectedProjects then
              let observed = observedProjects |> Set.toList |> String.concat ","
              yield violation "vulnerability-report-completeness" $"expected exact solution census; observed=%s{observed}"
          let vulnerable = vulnerabilityCounts root |> Seq.sum
          if vulnerable <> 0 then
              yield violation "vulnerable-package" $"count=%d{vulnerable}" ]
    with exceptionValue ->
        [ violation "vulnerability-report-unreadable" exceptionValue.Message ]

let private safeArtifactPath (root: string) (relative: string) =
    let combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
    let normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
    if combined.StartsWith(normalizedRoot, StringComparison.Ordinal) then Some combined else None

let private recoveryStages =
    [ "clone"; "restore"; "build"; "unit-tests"; "architecture-tests"; "pack"; "install"; "execute" ]

let private isLowerSha256 (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && value.Length = 64
    && value |> Seq.forall (fun character -> character >= '0' && character <= '9' || character >= 'a' && character <= 'f')

let private recoveryCanonicalBytes (head: string) (packageDigest: string) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
    writer.WriteStartObject()
    writer.WriteString("schema", "fsgg.coordination.bootstrap-recovery/1")
    writer.WriteString("candidate", head)
    writer.WriteString("packageSha256", packageDigest)
    writer.WriteStartArray("publishedSources")
    writer.WriteStringValue("https://api.nuget.org/v3/index.json")
    writer.WriteEndArray()
    writer.WriteStartArray("stages")
    for stage in recoveryStages do writer.WriteStringValue stage
    writer.WriteEndArray()
    writer.WriteEndObject()
    writer.Flush()
    Array.append (stream.ToArray()) [| byte '\n' |]

let private inspectRecoveryReceipt (path: string) (head: string) =
    try
        let bytes = File.ReadAllBytes path
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        let properties = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        let expectedProperties = [ "schema"; "candidate"; "packageSha256"; "publishedSources"; "stages" ]
        let packageDigest = stringProperty "packageSha256" root |> Option.defaultValue ""
        let sources = stringArray "publishedSources" root
        let stages = stringArray "stages" root
        [ if root.ValueKind <> JsonValueKind.Object || properties <> expectedProperties then
              yield violation "recovery-receipt-properties" (String.concat "," properties)
          if stringProperty "schema" root <> Some "fsgg.coordination.bootstrap-recovery/1" then
              yield violation "recovery-receipt-schema" "unsupported or absent schema"
          if stringProperty "candidate" root <> Some(head.ToLowerInvariant()) then
              yield violation "recovery-receipt-candidate" $"expected=%s{head}"
          if not (isLowerSha256 packageDigest) then
              yield violation "recovery-receipt-package-digest" packageDigest
          if sources <> [ "https://api.nuget.org/v3/index.json" ] then
              yield violation "recovery-receipt-source" (String.concat "," sources)
          if stages <> recoveryStages then
              yield violation "recovery-receipt-stages" (String.concat "," stages)
          if not (bytes.AsSpan().SequenceEqual((recoveryCanonicalBytes (head.ToLowerInvariant()) packageDigest).AsSpan())) then
              yield violation "recovery-receipt-canonical" "bytes differ from the compact exact contract" ]
    with exceptionValue ->
        [ violation "recovery-receipt-unreadable" exceptionValue.Message ]

let private inspectRecoveryArtifact (artifactRoot: string) (head: string) (contract: BootstrapContract) =
    contract.Jobs
    |> List.tryFind (fun job -> job.Id = "bootstrap-recovery")
    |> Option.map (fun job ->
        match safeArtifactPath artifactRoot job.Artifact with
        | Some path when File.Exists path -> inspectRecoveryReceipt path head
        | _ -> [ violation "recovery-receipt-missing" job.Artifact ])
    |> Option.defaultValue [ violation "recovery-receipt-contract" "bootstrap-recovery job is absent" ]

let private inspectCanonicalQuintReceipt (path: string) =
    try
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let root = document.RootElement
        let properties = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        let expectedProperties =
            [ "schema"; "q1Outcome"; "q2Outcome"; "positiveInvariantCount"; "negativeControlCount"
              "preparationDurationMs"; "q2DurationMs"; "totalDurationMs"; "processCounts"; "tools"; "inputs"
              "preparationSha256"; "failure"; "resultSha256" ]
        let processCounts = root.GetProperty("processCounts")
        let tools = root.GetProperty("tools")
        let inputs = root.GetProperty("inputs")
        let processProperties = processCounts.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        let toolProperties = tools.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        let inputProperties = inputs.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        let preparationDigest = stringProperty "preparationSha256" root |> Option.defaultValue ""
        let expectedResult = sha256Bytes (Encoding.UTF8.GetBytes($"passed|passed|8|56|85|61|14|%s{preparationDigest}|none|none"))
        let preparationMs = int64Property "preparationDurationMs" root |> Option.defaultValue -1L
        let q2Ms = int64Property "q2DurationMs" root |> Option.defaultValue -1L
        let totalMs = int64Property "totalDurationMs" root |> Option.defaultValue -1L
        [ if root.ValueKind <> JsonValueKind.Object || properties <> expectedProperties then
              yield violation "quint-receipt-properties" (String.concat "," properties)
          if stringProperty "schema" root <> Some "fsgg.coordination.canonical-quint-qualification/1" then
              yield violation "quint-receipt-schema" "unsupported or absent schema"
          if stringProperty "q1Outcome" root <> Some "passed" || stringProperty "q2Outcome" root <> Some "passed" then
              yield violation "quint-receipt-outcome" "Q1 and Q2 must both pass"
          if int64Property "positiveInvariantCount" root <> Some 8L || int64Property "negativeControlCount" root <> Some 56L then
              yield violation "quint-receipt-inventory" "expected eight positive invariants and 56 observed negative-control rejections"
          if preparationMs < 0L || q2Ms < 0L || totalMs <> preparationMs + q2Ms then
              yield violation "quint-receipt-timing" $"preparation=%d{preparationMs} q2=%d{q2Ms} total=%d{totalMs}"
          if int64Property "external" processCounts <> Some 85L
             || int64Property "quintCli" processCounts <> Some 61L
             || int64Property "apalacheVerify" processCounts <> Some 14L then
              yield violation "quint-receipt-process-count" "expected exact retained process inventory 85/61/14"
          if processProperties <> [ "external"; "quintCli"; "apalacheVerify" ] then
              yield violation "quint-receipt-process-properties" (String.concat "," processProperties)
          if toolProperties <> [ "toolchainSha256"; "quintSha256"; "apalacheJarSha256" ] then
              yield violation "quint-receipt-tool-properties" (String.concat "," toolProperties)
          if inputProperties <> [ "sourceSha256"; "contractSha256" ] then
              yield violation "quint-receipt-input-properties" (String.concat "," inputProperties)
          let expectedTools =
              [ "toolchainSha256", "79b32dacc5bb150e23c4017eef16f3f688cde062441583d5ea1ffa5cc9e62486"
                "quintSha256", "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
                "apalacheJarSha256", "4753c0ebb2cbb266e2c6ac19ab5ca3827d726cc80fd1fc5d7c1eeb64736cd60b" ]
          for name, expected in expectedTools do
              if stringProperty name tools <> Some expected then
                  yield violation "quint-receipt-tool-digest" name
          let expectedInputs =
              [ "sourceSha256", "b82983e10324c241cef1187cf58ce2ec5222ab4d7e253d53179d5343927c518a"
                "contractSha256", "60bf639dc6c6e4a31ac284c57d85cb10a5cd7c0cce5532552884b5a3ea1b8c76" ]
          for name, expected in expectedInputs do
              if stringProperty name inputs <> Some expected then
                  yield violation "quint-receipt-input-digest" name
          if not (isLowerSha256 preparationDigest) then
              yield violation "quint-receipt-preparation-digest" preparationDigest
          if root.GetProperty("failure").ValueKind <> JsonValueKind.Null then
              yield violation "quint-receipt-failure" "a passing receipt must not carry a failure"
          if stringProperty "resultSha256" root <> Some expectedResult then
              yield violation "quint-receipt-result-digest" "result digest does not bind the outcomes and inventories" ]
    with exceptionValue ->
        [ violation "quint-receipt-unreadable" exceptionValue.Message ]

let private inspectCanonicalQuintArtifact (artifactRoot: string) (contract: BootstrapContract) =
    contract.Jobs
    |> List.tryFind (fun job -> job.Id = "canonical-quint")
    |> Option.map (fun job ->
        match safeArtifactPath artifactRoot job.Artifact with
        | Some path when File.Exists path -> inspectCanonicalQuintReceipt path
        | _ -> [ violation "quint-receipt-missing" job.Artifact ])
    |> Option.defaultValue [ violation "quint-receipt-contract" "canonical-quint job is absent" ]

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
    writer.WriteString("planSha256", sha256Bytes contract.Bytes)
    writer.WriteStartArray("gates")
    for gate, digest in artifacts do
        writer.WriteStartObject()
        writer.WriteString("id", gate.Id)
        writer.WriteString("artifact", gate.Artifact)
        writer.WriteString("sha256", digest)
        writer.WriteStartArray("commands")
        for command in gate.Commands do writer.WriteStringValue command
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
        [ yield! inspectRecoveryArtifact artifactRoot head contract
          yield! inspectCanonicalQuintArtifact artifactRoot contract
          if stringProperty "schema" root <> Some contract.EvidenceSchema then
              yield violation "evidence-schema" "unsupported or absent schema"
          if stringProperty "candidate" root <> Some(head.ToLowerInvariant()) then
              yield violation "evidence-candidate" $"expected=%s{head}"
          if stringProperty "planSha256" root <> Some(sha256Bytes contract.Bytes) then
              yield violation "evidence-plan-digest" "qualification plan bytes do not match"
          if ids.Length <> gates.Length || ids |> List.distinct |> List.length <> ids.Length || Set.ofList ids <> (expected |> Map.keys |> Set.ofSeq) then
              yield violation "evidence-gate-set" (String.concat "," ids)
          for gateValue in gates do
              match stringProperty "id" gateValue |> Option.bind (fun id -> Map.tryFind id expected |> Option.map (fun gate -> id, gate)) with
              | None -> ()
              | Some(id, gate) ->
                  if stringProperty "artifact" gateValue <> Some gate.Artifact then
                      yield violation "evidence-artifact-path" id
                  if stringArray "commands" gateValue <> gate.Commands then
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

let execute (arguments: string list) =
    let arguments = arguments |> List.filter ((<>) "--")
    let mode = arguments |> List.tryHead |> Option.defaultValue "workflow"

    let root =
        optionValue "--root" arguments
        |> Option.defaultValue (Directory.GetCurrentDirectory())
        |> Path.GetFullPath

    let result =
        try
            let contract = loadContract root

            match mode with
            | "generate" ->
                let output =
                    optionValue "--output" arguments
                    |> Option.defaultValue (Path.Combine(root, ".github/workflows/bootstrap-qualification.yml"))
                    |> Path.GetFullPath
                Directory.CreateDirectory(Path.GetDirectoryName output) |> ignore
                File.WriteAllText(output, renderWorkflow contract, UTF8Encoding(false))
                []
            | "workflow" -> inspectWorkflow root contract
            | "vulnerability" ->
                optionValue "--report" arguments
                |> Option.map (fun path -> inspectVulnerabilityReport (Path.GetFullPath path) root contract)
                |> Option.defaultValue [ violation "argument" "--report is required" ]
            | "collect" ->
                match
                    optionValue "--head" arguments,
                    optionValue "--artifacts" arguments,
                    optionValue "--output" arguments
                with
                | Some head, Some artifacts, Some output when isSha head ->
                    let artifactRoot = Path.GetFullPath artifacts

                    let qualificationViolations =
                        inspectRecoveryArtifact artifactRoot head contract
                        @ inspectCanonicalQuintArtifact artifactRoot contract

                    if List.isEmpty qualificationViolations then
                        writeEvidence (Path.GetFullPath output) head artifactRoot contract

                    qualificationViolations
                | _ -> [ violation "argument" "collect requires --head, --artifacts, and --output" ]
            | "evidence" ->
                match
                    optionValue "--head" arguments,
                    optionValue "--artifacts" arguments,
                    optionValue "--file" arguments
                with
                | Some head, Some artifacts, Some path when isSha head ->
                    inspectEvidence (Path.GetFullPath path) head (Path.GetFullPath artifacts) contract
                | _ -> [ violation "argument" "evidence requires an exact --head plus --artifacts and --file" ]
            | unknown -> [ violation "argument" $"unknown mode: %s{unknown}" ]
        with exceptionValue ->
            [ violation "qualification-plan-invalid" exceptionValue.Message ]

    if List.isEmpty result then
        0, $"BOOTSTRAP_CI_OK mode=%s{mode}", ""
    else
        1, "", String.concat Environment.NewLine result
