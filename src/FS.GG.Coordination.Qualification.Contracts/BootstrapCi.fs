module FS.GG.Coordination.Qualification.Contracts.BootstrapCi

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type GateContract =
    { Id: string
      Artifact: string
      TimeoutMinutes: int
      EntryPoint: string
      FetchDepth: int
      AlwaysUpload: bool
      DownloadArtifacts: bool
      Environment: (string * string) list
      UploadName: string
      UploadPath: string
      ReceiptKind: string option
      Needs: string list
      Commands: string list }

type ActionPins =
    { Checkout: string
      SetupDotnet: string
      UploadArtifact: string
      DownloadArtifact: string }

type ReuseContract =
    { JobId: string
      Artifact: string
      TimeoutMinutes: int
      EntryPoint: string
      UploadName: string
      WorkflowPath: string
      MaxCandidateArtifacts: int
      NotBefore: string
      Runner: string
      Architecture: string
      ReviewPolicy: string }

type BootstrapContract =
    { EvidenceSchema: string
      Actions: ActionPins
      ConcurrencyGroup: string
      CancelInProgress: bool
      RequiredProjectCount: int
      RequiredGateCount: int
      RequiredProjects: string list
      RequiredVulnerabilitySources: string list
      Reuse: ReuseContract
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

let private stringProperties (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Object then
        value.EnumerateObject()
        |> Seq.map (fun property ->
            if property.Value.ValueKind <> JsonValueKind.String then failwith $"%s{name} values must be strings"
            property.Name, property.Value.GetString())
        |> Seq.toList
    else
        []

let private loadContract root =
    let path = Path.Combine(root, "eng/bootstrap-qualification-plan.json")
    let bytes = File.ReadAllBytes path
    use document = JsonDocument.Parse bytes
    let value = document.RootElement
    if stringProperty "schema" value <> Some "fsgg.coordination.bootstrap-qualification-plan/3" then
        failwith "bootstrap qualification plan schema is unsupported"
    let evidenceSchema = stringProperty "evidenceSchema" value |> Option.defaultWith (fun () -> failwith "evidenceSchema is missing")
    let actionsValue = value.GetProperty("actions")
    let action name =
        stringProperty name actionsValue
        |> Option.defaultWith (fun () -> failwith $"action pin is missing: %s{name}")
    let actions =
        { Checkout = action "checkout"
          SetupDotnet = action "setupDotnet"
          UploadArtifact = action "uploadArtifact"
          DownloadArtifact = action "downloadArtifact" }
    let approvedActions =
        [ actions.Checkout, "3d3c42e5aac5ba805825da76410c181273ba90b1"
          actions.SetupDotnet, "a98b56852c35b8e3190ac28c8c2271da59106c68"
          actions.UploadArtifact, "043fb46d1a93c77aae656e7c1c64a875d1fc6a0a"
          actions.DownloadArtifact, "3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c" ]
    for actionPin, approvedPin in approvedActions do
        if not (isNull actionPin) && (actionPin.Length <> 40 || actionPin |> Seq.exists (Uri.IsHexDigit >> not)) then
            failwith "every action must use an exact immutable SHA"
        if actionPin <> approvedPin then failwith "action pin is not the reviewed Node 24 revision"
    let actionRuntimes = value.GetProperty("actionRuntimes")
    for name in [ "checkout"; "setupDotnet"; "uploadArtifact"; "downloadArtifact" ] do
        if stringProperty name actionRuntimes <> Some "node24" then failwith $"action runtime must be node24: %s{name}"
    if stringArray "triggers" value <> [ "pull_request"; "push:main" ] then failwith "triggers must be pull_request and push:main"
    if stringArray "permissions" value <> [ "actions:read"; "contents:read" ] then failwith "permissions must be actions:read and contents:read"
    let concurrency = value.GetProperty("concurrency")
    let concurrencyGroup = stringProperty "group" concurrency |> Option.defaultWith (fun () -> failwith "concurrency group is missing")
    let cancelInProgress = boolProperty "cancelInProgress" concurrency |> Option.defaultWith (fun () -> failwith "cancelInProgress is missing")
    if concurrencyGroup <> "bootstrap-qualification-${{ github.ref }}" || not cancelInProgress then
        failwith "concurrency must cancel superseded attempts within the candidate ref"
    let requiredProjectCount = value.GetProperty("requiredProjectCount").GetInt32()
    let requiredGateCount = value.GetProperty("requiredGateCount").GetInt32()
    let requiredProjects = stringArray "requiredProjects" value
    if requiredProjects.Length <> requiredProjectCount || (requiredProjects |> List.distinct |> List.length) <> requiredProjectCount then
        failwith "requiredProjects must contain the exact distinct project census"
    let requiredVulnerabilitySources = stringArray "requiredVulnerabilitySources" value
    if List.isEmpty requiredVulnerabilitySources || requiredVulnerabilitySources |> List.exists (fun source -> not (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) then
        failwith "requiredVulnerabilitySources must be a non-empty HTTPS-only set"
    let reuseValue = value.GetProperty("reuse")
    let reuse =
        { JobId = stringProperty "jobId" reuseValue |> Option.defaultWith (fun () -> failwith "reuse jobId is missing")
          Artifact = stringProperty "artifact" reuseValue |> Option.defaultWith (fun () -> failwith "reuse artifact is missing")
          TimeoutMinutes = reuseValue.GetProperty("timeoutMinutes").GetInt32()
          EntryPoint = stringProperty "entryPoint" reuseValue |> Option.defaultWith (fun () -> failwith "reuse entryPoint is missing")
          UploadName = stringProperty "uploadName" reuseValue |> Option.defaultWith (fun () -> failwith "reuse uploadName is missing")
          WorkflowPath = stringProperty "workflowPath" reuseValue |> Option.defaultWith (fun () -> failwith "reuse workflowPath is missing")
          MaxCandidateArtifacts = reuseValue.GetProperty("maxCandidateArtifacts").GetInt32()
          NotBefore = stringProperty "notBefore" reuseValue |> Option.defaultWith (fun () -> failwith "reuse notBefore is missing")
          Runner = stringProperty "runner" reuseValue |> Option.defaultWith (fun () -> failwith "reuse runner is missing")
          Architecture = stringProperty "architecture" reuseValue |> Option.defaultWith (fun () -> failwith "reuse architecture is missing")
          ReviewPolicy = stringProperty "reviewPolicy" reuseValue |> Option.defaultWith (fun () -> failwith "reuse reviewPolicy is missing") }
    if reuse <> { JobId = "reuse-decision"; Artifact = "reuse-decision/decision.json"; TimeoutMinutes = 5; EntryPoint = "bash eng/bootstrap-gates/reuse-decision.sh"; UploadName = "reuse-decision"; WorkflowPath = ".github/workflows/bootstrap-qualification.yml"; MaxCandidateArtifacts = 100; NotBefore = "2026-08-29T13:32:00Z"; Runner = "ubuntu-latest"; Architecture = "x64"; ReviewPolicy = "structured-decisions/1" } then
        failwith "reuse policy differs from the reviewed fail-closed contract"
    let jobs =
        arrayProperty "jobs" value
        |> Option.defaultWith (fun () -> failwith "jobs are missing")
        |> List.map (fun job ->
            let entryPoint = stringProperty "entryPoint" job |> Option.defaultWith (fun () -> failwith "job entryPoint is missing")
            { Id = stringProperty "id" job |> Option.defaultWith (fun () -> failwith "job id is missing")
              Artifact = stringProperty "artifact" job |> Option.defaultWith (fun () -> failwith "job artifact is missing")
              TimeoutMinutes = job.GetProperty("timeoutMinutes").GetInt32()
              EntryPoint = entryPoint
              FetchDepth = job.GetProperty("fetchDepth").GetInt32()
              AlwaysUpload = boolProperty "alwaysUpload" job |> Option.defaultValue false
              DownloadArtifacts = boolProperty "downloadArtifacts" job |> Option.defaultValue false
              Environment = stringProperties "environment" job
              UploadName = stringProperty "uploadName" job |> Option.defaultWith (fun () -> failwith "job uploadName is missing")
              UploadPath = stringProperty "uploadPath" job |> Option.defaultWith (fun () -> failwith "job uploadPath is missing")
              ReceiptKind = stringProperty "receiptKind" job
              Needs = stringArray "needs" job
              Commands = [ entryPoint ] })
    let jobIds = jobs |> List.map _.Id
    let jobIdSet = Set.ofList jobIds
    if jobs.Length <> requiredGateCount || requiredGateCount < 2 || jobIdSet.Count <> requiredGateCount then
        failwith "jobs must match requiredGateCount with distinct identities"
    let terminalJobs = jobs |> List.filter _.DownloadArtifacts
    if terminalJobs.Length <> 1 then failwith "exactly one terminal evidence job must download prerequisite artifacts"
    let terminalJob = terminalJobs.Head
    let prerequisiteIds = jobIdSet.Remove terminalJob.Id
    for job in jobs do
        if String.IsNullOrWhiteSpace job.Id || job.Id |> Seq.exists (fun character -> not (Char.IsLower character || Char.IsDigit character || character = '-')) then
            failwith "job identities must be lowercase kebab-case"
        if job.TimeoutMinutes < 1 || job.TimeoutMinutes > 35 then failwith $"job timeout is outside the bounded policy: %s{job.Id}"
        if job.EntryPoint <> $"bash eng/bootstrap-gates/%s{job.Id}.sh" then failwith $"job entryPoint is not stable: %s{job.Id}"
        if job.FetchDepth < 0 || job.FetchDepth > 1 then failwith $"job fetch depth is invalid: %s{job.Id}"
        if String.IsNullOrWhiteSpace job.UploadName || String.IsNullOrWhiteSpace job.UploadPath then failwith $"job upload contract is incomplete: %s{job.Id}"
        if job.UploadPath.Contains("${{ runner.") && not (job.UploadPath.StartsWith("${{ runner.temp }}/", StringComparison.Ordinal)) then
            failwith $"job upload path uses an unavailable runner context: %s{job.Id}"
        let environmentNames = job.Environment |> List.map fst
        if environmentNames.Length <> (environmentNames |> List.distinct |> List.length) then failwith $"job environment names must be distinct: %s{job.Id}"
        if job.Environment |> List.exists (fun (name, environmentValue) -> String.IsNullOrWhiteSpace name || environmentValue.Contains("${{ runner.")) then
            failwith $"job environment is invalid: %s{job.Id}"
        if job.DownloadArtifacts then
            if Set.ofList job.Needs <> prerequisiteIds || job.Needs.Length <> prerequisiteIds.Count then
                failwith "terminal evidence must depend on every prerequisite exactly once"
        elif not (List.isEmpty job.Needs) then
            failwith $"prerequisite gate must remain independently scheduled: %s{job.Id}"
    let receiptKinds = jobs |> List.choose _.ReceiptKind
    if receiptKinds.Length <> (receiptKinds |> List.distinct |> List.length) then failwith "receipt kinds must be unique"
    { EvidenceSchema = evidenceSchema
      Actions = actions
      ConcurrencyGroup = concurrencyGroup
      CancelInProgress = cancelInProgress
      RequiredProjectCount = requiredProjectCount
      RequiredGateCount = requiredGateCount
      RequiredProjects = requiredProjects
      RequiredVulnerabilitySources = requiredVulnerabilitySources
      Reuse = reuse
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
    line "  actions: read"
    line "  contents: read"
    line ""
    line "concurrency:"
    line $"  group: %s{contract.ConcurrencyGroup}"
    line $"  cancel-in-progress: %s{contract.CancelInProgress.ToString().ToLowerInvariant()}"
    line ""
    line "jobs:"
    line $"  %s{contract.Reuse.JobId}:"
    line $"    name: %s{contract.Reuse.JobId}"
    line $"    runs-on: %s{contract.Reuse.Runner}"
    line $"    timeout-minutes: %d{contract.Reuse.TimeoutMinutes}"
    line "    outputs:"
    line "      route: ${{ steps.decide.outputs.route }}"
    line "      prior-run-id: ${{ steps.decide.outputs.prior-run-id }}"
    line "    env:"
    line "      GH_TOKEN: ${{ github.token }}"
    line "      FSGG_CANDIDATE_SHA: ${{ github.event.pull_request.head.sha || github.sha }}"
    line "      FSGG_CURRENT_RUN_ID: ${{ github.run_id }}"
    line "      FSGG_REPOSITORY: ${{ github.repository }}"
    line "      FSGG_RUNNER_TEMP: /tmp/fsgg-${{ github.run_id }}-reuse"
    line "    steps:"
    line "      - name: Check out the exact candidate"
    line $"        uses: actions/checkout@%s{contract.Actions.Checkout}"
    line "        with:"
    line "          ref: ${{ github.event.pull_request.head.sha || github.sha }}"
    line "          fetch-depth: 0"
    line "      - name: Set up the pinned .NET SDK"
    line $"        uses: actions/setup-dotnet@%s{contract.Actions.SetupDotnet}"
    line "        with:"
    line "          global-json-file: global.json"
    line "      - name: Select the exact-head qualification route"
    line "        id: decide"
    line $"        run: %s{contract.Reuse.EntryPoint}"
    line "      - name: Upload the qualification route receipt"
    line $"        uses: actions/upload-artifact@%s{contract.Actions.UploadArtifact}"
    line "        with:"
    line $"          name: %s{contract.Reuse.UploadName}"
    line "          path: ${{ env.FSGG_RUNNER_TEMP }}/decision.json"
    line "          if-no-files-found: error"
    line ""
    for gate in contract.Jobs do
        line $"  %s{gate.Id}:"
        line $"    name: %s{gate.Id}"
        let needs = contract.Reuse.JobId :: gate.Needs
        let renderedNeeds = String.concat ", " needs
        line $"    needs: [%s{renderedNeeds}]"
        if gate.DownloadArtifacts then
            line "    if: ${{ always() && needs.reuse-decision.result == 'success' }}"
        else
            line "    if: ${{ needs.reuse-decision.outputs.route == 'execute' }}"
        line $"    runs-on: %s{contract.Reuse.Runner}"
        line $"    timeout-minutes: %d{gate.TimeoutMinutes}"
        if not (List.isEmpty gate.Environment) then
            line "    env:"
            for name, value in gate.Environment do line $"      %s{name}: %s{value}"
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
        if gate.DownloadArtifacts then
            line "      - name: Download the current route receipt"
            line $"        uses: actions/download-artifact@%s{contract.Actions.DownloadArtifact}"
            line "        with:"
            line $"          name: %s{contract.Reuse.UploadName}"
            line "          path: ${{ runner.temp }}/bootstrap-decision"
            line "      - name: Download current execution evidence"
            line "        if: ${{ needs.reuse-decision.outputs.route == 'execute' }}"
            line $"        uses: actions/download-artifact@%s{contract.Actions.DownloadArtifact}"
            line "        with:"
            line "          path: ${{ runner.temp }}/bootstrap-artifacts"
            line "      - name: Download selected prior evidence"
            line "        if: ${{ needs.reuse-decision.outputs.route == 'reuse' }}"
            line $"        uses: actions/download-artifact@%s{contract.Actions.DownloadArtifact}"
            line "        with:"
            line "          run-id: ${{ needs.reuse-decision.outputs.prior-run-id }}"
            line "          github-token: ${{ github.token }}"
            line "          path: ${{ runner.temp }}/prior-bootstrap-artifacts"
        line "      - name: Run the stable qualification gate"
        line $"        run: %s{gate.EntryPoint}"
        line "      - name: Upload qualification evidence"
        if gate.AlwaysUpload then line "        if: ${{ always() }}"
        line $"        uses: actions/upload-artifact@%s{contract.Actions.UploadArtifact}"
        line "        with:"
        line $"          name: %s{gate.UploadName}"
        line $"          path: %s{gate.UploadPath}"
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

let private trackedFiles root =
    let startInfo = ProcessStartInfo("git")
    startInfo.WorkingDirectory <- root
    startInfo.ArgumentList.Add("ls-files")
    startInfo.ArgumentList.Add("-s")
    startInfo.ArgumentList.Add("-z")
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use childProcess = Process.Start startInfo
    let output = childProcess.StandardOutput.ReadToEnd()
    let error = childProcess.StandardError.ReadToEnd()
    childProcess.WaitForExit()
    if childProcess.ExitCode <> 0 then failwith $"cannot enumerate the tracked qualification tree: %s{error.Trim()}"
    output.Split('\000', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun entry ->
        let tab = entry.IndexOf('\t')
        if tab < 0 then failwith "tracked tree entry is malformed"
        let identity = entry.Substring(0, tab).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        if identity.Length <> 3 || identity[2] <> "0" then failwith "tracked tree entry has an unsupported stage"
        let mode = identity[0]
        let relative = entry.Substring(tab + 1)
        let absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
        let bytes =
            if mode = "120000" then
                let target = FileInfo(absolute).LinkTarget
                if isNull target then failwith $"tracked symbolic link is unreadable: %s{relative}"
                Encoding.UTF8.GetBytes target
            else File.ReadAllBytes absolute
        ({ Mode = mode
           Path = relative
           Bytes = bytes }: QualificationReuse.TrackedFile))
    |> Array.toList

let private qualificationSubject root (contract: BootstrapContract) =
    let planBytes = contract.Bytes
    let workflowBytes = File.ReadAllBytes(Path.Combine(root, contract.Reuse.WorkflowPath))
    let environment =
        Encoding.UTF8.GetBytes(
            String.concat "|"
                [ contract.Reuse.Runner
                  contract.Reuse.Architecture
                  contract.ConcurrencyGroup
                  string contract.CancelInProgress
                  String.concat "," [ "actions:read"; "contents:read" ] ])
    let reviewPolicy = Encoding.UTF8.GetBytes contract.Reuse.ReviewPolicy
    QualificationReuse.createSubject (trackedFiles root) planBytes workflowBytes environment reviewPolicy

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
    |> List.tryFind (fun job -> job.ReceiptKind = Some "recovery")
    |> Option.map (fun job ->
        match safeArtifactPath artifactRoot job.Artifact with
        | Some path when File.Exists path -> inspectRecoveryReceipt path head
        | _ -> [ violation "recovery-receipt-missing" job.Artifact ])
    |> Option.defaultValue [ violation "recovery-receipt-contract" "recovery receipt kind is absent" ]

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
        let expectedResult = sha256Bytes (Encoding.UTF8.GetBytes($"passed|passed|8|71|109|84|14|%s{preparationDigest}|none|none"))
        let preparationMs = int64Property "preparationDurationMs" root |> Option.defaultValue -1L
        let q2Ms = int64Property "q2DurationMs" root |> Option.defaultValue -1L
        let totalMs = int64Property "totalDurationMs" root |> Option.defaultValue -1L
        [ if root.ValueKind <> JsonValueKind.Object || properties <> expectedProperties then
              yield violation "quint-receipt-properties" (String.concat "," properties)
          if stringProperty "schema" root <> Some "fsgg.coordination.canonical-quint-qualification/1" then
              yield violation "quint-receipt-schema" "unsupported or absent schema"
          if stringProperty "q1Outcome" root <> Some "passed" || stringProperty "q2Outcome" root <> Some "passed" then
              yield violation "quint-receipt-outcome" "Q1 and Q2 must both pass"
          if int64Property "positiveInvariantCount" root <> Some 8L || int64Property "negativeControlCount" root <> Some 71L then
              yield violation "quint-receipt-inventory" "expected eight positive invariants and 71 observed negative-control rejections"
          if preparationMs < 0L || q2Ms < 0L || totalMs <> preparationMs + q2Ms then
              yield violation "quint-receipt-timing" $"preparation=%d{preparationMs} q2=%d{q2Ms} total=%d{totalMs}"
          if int64Property "external" processCounts <> Some 109L
             || int64Property "quintCli" processCounts <> Some 84L
             || int64Property "apalacheVerify" processCounts <> Some 14L then
              yield violation "quint-receipt-process-count" "expected exact retained process inventory 109/84/14"
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
              [ "sourceSha256", "750bb30a034ec4a1f742eae3684e9e9d1e9a84e9cd2cba0716ea028bfeec536a"
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
    |> List.tryFind (fun job -> job.ReceiptKind = Some "formal")
    |> Option.map (fun job ->
        match safeArtifactPath artifactRoot job.Artifact with
        | Some path when File.Exists path -> inspectCanonicalQuintReceipt path
        | _ -> [ violation "quint-receipt-missing" job.Artifact ])
    |> Option.defaultValue [ violation "quint-receipt-contract" "formal receipt kind is absent" ]

let private decisionText = function
    | QualificationReuse.Reuse -> "reuse"
    | QualificationReuse.Execute -> "execute"
    | QualificationReuse.Refuse -> "refuse"

let private writeEvidence (output: string) (head: string) (artifactRoot: string) (contract: BootstrapContract) (decision: QualificationReuse.Decision) =
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
    writer.WriteString("route", decisionText decision.Kind)
    writer.WriteString("subjectSha256", decision.SubjectSha256)
    writer.WriteString("decisionSha256", decision.SelfSha256)
    match decision.Prior with
    | None -> writer.WriteNull("prior")
    | Some prior ->
        writer.WriteStartObject("prior")
        writer.WriteString("head", prior.Head)
        writer.WriteNumber("runId", prior.RunId)
        writer.WriteNumber("attempt", prior.Attempt)
        writer.WriteString("evidenceSha256", prior.EvidenceSha256)
        writer.WriteString("artifactExpiresAt", prior.ArtifactExpiresAt)
        writer.WriteEndObject()
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

let private inspectEvidence (path: string) (head: string) (artifactRoot: string) (contract: BootstrapContract) (decision: QualificationReuse.Decision) =
    try
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let root = document.RootElement
        let gates = arrayProperty "gates" root |> Option.defaultValue []
        let expected = contract.Jobs |> List.map (fun gate -> gate.Id, gate) |> Map.ofList
        let ids = gates |> List.choose (stringProperty "id")
        let artifactHead = decision.Prior |> Option.map _.Head |> Option.defaultValue head
        [ yield! inspectRecoveryArtifact artifactRoot artifactHead contract
          yield! inspectCanonicalQuintArtifact artifactRoot contract
          if stringProperty "schema" root <> Some contract.EvidenceSchema then
              yield violation "evidence-schema" "unsupported or absent schema"
          if stringProperty "candidate" root <> Some(head.ToLowerInvariant()) then
              yield violation "evidence-candidate" $"expected=%s{head}"
          if stringProperty "route" root <> Some(decisionText decision.Kind) then
              yield violation "evidence-route" "terminal route does not match the decision receipt"
          if stringProperty "subjectSha256" root <> Some decision.SubjectSha256 then
              yield violation "evidence-subject-digest" "terminal subject does not match the decision receipt"
          if stringProperty "decisionSha256" root <> Some decision.SelfSha256 then
              yield violation "evidence-decision-digest" "terminal decision digest does not match"
          let priorElement = root.GetProperty("prior")
          match decision.Prior with
          | None when priorElement.ValueKind <> JsonValueKind.Null ->
              yield violation "evidence-prior" "execute evidence must not carry prior authority"
          | Some prior when priorElement.ValueKind <> JsonValueKind.Object ->
              yield violation "evidence-prior" "reuse evidence must carry prior authority"
          | Some prior ->
              if stringProperty "head" priorElement <> Some prior.Head
                 || int64Property "runId" priorElement <> Some prior.RunId
                 || priorElement.GetProperty("attempt").GetInt32() <> prior.Attempt
                 || stringProperty "evidenceSha256" priorElement <> Some prior.EvidenceSha256
                 || stringProperty "artifactExpiresAt" priorElement <> Some prior.ArtifactExpiresAt then
                  yield violation "evidence-prior" "prior authority differs from the decision receipt"
          | None -> ()
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

let private loadDecision path =
    match QualificationReuse.parseDecision (File.ReadAllBytes path) with
    | Ok decision -> decision
    | Error problem -> failwith $"reuse decision is invalid: %s{problem}"

let private writeDecision path decision =
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
    File.WriteAllBytes(path, QualificationReuse.decisionBytes decision)

let private selectPriorManifest (path: string) (priorHead: string) (contract: BootstrapContract) =
    use document = JsonDocument.Parse(File.ReadAllBytes path)
    let root = document.RootElement
    if stringProperty "schema" root <> Some contract.EvidenceSchema then failwith "prior terminal evidence schema is unsupported"
    if stringProperty "candidate" root <> Some(priorHead.ToLowerInvariant()) then failwith "prior terminal evidence head does not match its run"
    if stringProperty "route" root <> Some "execute" then failwith "transitive qualification reuse is not selectable"
    if stringProperty "planSha256" root <> Some(sha256Bytes contract.Bytes) then failwith "prior qualification plan differs"
    stringProperty "subjectSha256" root |> Option.defaultWith (fun () -> failwith "prior subject digest is missing")

let private requiredInt64 name arguments =
    match optionValue name arguments with
    | Some value ->
        match Int64.TryParse value with
        | true, parsed when parsed > 0L -> parsed
        | _ -> failwith $"%s{name} must be a positive integer"
    | None -> failwith $"%s{name} is required"

let private requiredInt name arguments =
    let value = requiredInt64 name arguments
    if value > int64 Int32.MaxValue then failwith $"%s{name} is too large"
    int value

let private optionalNonNegativeDecimal name arguments =
    match optionValue name arguments with
    | None -> None
    | Some value ->
        match Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture) with
        | true, parsed when parsed >= 0M -> Some parsed
        | _ -> failwith $"%s{name} must be a non-negative invariant decimal"

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
            | "subject" ->
                match optionValue "--output" arguments with
                | Some output ->
                    qualificationSubject root contract
                    |> QualificationReuse.subjectBytes
                    |> fun bytes -> File.WriteAllBytes(Path.GetFullPath output, bytes)
                    []
                | None -> [ violation "argument" "subject requires --output" ]
            | "select" ->
                match optionValue "--head" arguments, optionValue "--output" arguments with
                | Some head, Some output when isSha head ->
                    let subject = qualificationSubject root contract
                    let decision =
                        match optionValue "--refuse" arguments with
                        | Some reason -> QualificationReuse.refuse head subject.SubjectSha256 reason
                        | None ->
                            match optionValue "--prior-manifest" arguments, optionValue "--prior-head" arguments with
                            | None, None -> QualificationReuse.decide head subject.SubjectSha256 None None
                            | Some manifest, Some priorHead when isSha priorHead ->
                                let evidenceDigest = sha256File (Path.GetFullPath manifest)
                                let prior: QualificationReuse.PriorRun =
                                    { Head = priorHead.ToLowerInvariant()
                                      RunId = requiredInt64 "--prior-run" arguments
                                      Attempt = requiredInt "--prior-attempt" arguments
                                      EvidenceSha256 = evidenceDigest
                                      ArtifactExpiresAt = optionValue "--expires" arguments |> Option.defaultWith (fun () -> failwith "--expires is required")
                                      RunnerMinutes = optionalNonNegativeDecimal "--runner-minutes" arguments }
                                let priorSubject = selectPriorManifest (Path.GetFullPath manifest) prior.Head contract
                                QualificationReuse.decide head subject.SubjectSha256 (Some prior) (Some priorSubject)
                            | _ -> QualificationReuse.refuse head subject.SubjectSha256 "incomplete-prior-selection"
                    writeDecision (Path.GetFullPath output) decision
                    []
                | _ -> [ violation "argument" "select requires an exact --head and --output" ]
            | "vulnerability" ->
                optionValue "--report" arguments
                |> Option.map (fun path -> inspectVulnerabilityReport (Path.GetFullPath path) root contract)
                |> Option.defaultValue [ violation "argument" "--report is required" ]
            | "collect" ->
                match
                    optionValue "--head" arguments,
                    optionValue "--artifacts" arguments,
                    optionValue "--output" arguments,
                    optionValue "--decision" arguments
                with
                | Some head, Some artifacts, Some output, Some decisionPath when isSha head ->
                    let artifactRoot = Path.GetFullPath artifacts
                    let decision = loadDecision (Path.GetFullPath decisionPath)

                    let qualificationViolations =
                        [ if decision.Candidate <> head.ToLowerInvariant() then
                              yield violation "reuse-candidate" "decision is not bound to the current exact head"
                          if decision.Kind = QualificationReuse.Refuse then
                              yield violation "reuse-refused" decision.Reason
                          match decision.Prior with
                          | None ->
                              yield! inspectRecoveryArtifact artifactRoot head contract
                              yield! inspectCanonicalQuintArtifact artifactRoot contract
                          | Some prior ->
                              let priorDecisionPath = Path.Combine(artifactRoot, contract.Reuse.Artifact)
                              let priorManifestPath = Path.Combine(artifactRoot, "bootstrap-evidence-manifest/bootstrap-evidence.json")
                              if not (File.Exists priorDecisionPath) then
                                  yield violation "reuse-prior-decision-missing" contract.Reuse.Artifact
                              elif not (File.Exists priorManifestPath) then
                                  yield violation "reuse-prior-evidence-missing" "bootstrap-evidence-manifest/bootstrap-evidence.json"
                              else
                                  let priorDecision = loadDecision priorDecisionPath
                                  if priorDecision.Kind <> QualificationReuse.Execute
                                     || priorDecision.Candidate <> prior.Head
                                     || priorDecision.SubjectSha256 <> decision.SubjectSha256 then
                                      yield violation "reuse-prior-decision" "prior execution decision is not equivalent"
                                  if sha256File priorManifestPath <> prior.EvidenceSha256 then
                                      yield violation "reuse-prior-evidence-digest" "selected prior manifest bytes changed"
                                  yield! inspectEvidence priorManifestPath prior.Head artifactRoot contract priorDecision ]

                    if List.isEmpty qualificationViolations then
                        writeEvidence (Path.GetFullPath output) head artifactRoot contract decision

                    qualificationViolations
                | _ -> [ violation "argument" "collect requires --head, --artifacts, --output, and --decision" ]
            | "evidence" ->
                match
                    optionValue "--head" arguments,
                    optionValue "--artifacts" arguments,
                    optionValue "--file" arguments,
                    optionValue "--decision" arguments
                with
                | Some head, Some artifacts, Some path, Some decisionPath when isSha head ->
                    let decision = loadDecision (Path.GetFullPath decisionPath)
                    inspectEvidence (Path.GetFullPath path) head (Path.GetFullPath artifacts) contract decision
                | _ -> [ violation "argument" "evidence requires an exact --head plus --artifacts, --file, and --decision" ]
            | unknown -> [ violation "argument" $"unknown mode: %s{unknown}" ]
        with exceptionValue ->
            [ violation "qualification-plan-invalid" exceptionValue.Message ]

    if List.isEmpty result then
        0, $"BOOTSTRAP_CI_OK mode=%s{mode}", ""
    else
        1, "", String.concat Environment.NewLine result
