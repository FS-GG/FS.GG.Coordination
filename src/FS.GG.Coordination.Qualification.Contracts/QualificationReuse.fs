module FS.GG.Coordination.Qualification.Contracts.QualificationReuse

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type TrackedFile =
    { Mode: string
      Path: string
      Bytes: byte array }

type QualificationSubject =
    { TreeSha256: string
      PlanSha256: string
      WorkflowSha256: string
      ToolchainSha256: string
      DependencySha256: string
      GateSetSha256: string
      EnvironmentSha256: string
      ReviewPolicySha256: string
      SubjectSha256: string }

type FormalSubjectSelector =
    | Exact of string
    | Prefix of string

type FormalSubject =
    { FilesSha256: string
      SelectorPolicySha256: string
      FileCount: int
      SubjectSha256: string }

type PriorRun =
    { Head: string
      RunId: int64
      Attempt: int
      EvidenceSha256: string
      ArtifactExpiresAt: string
      RunnerMinutes: decimal option }

type DecisionKind =
    | Reuse
    | Execute
    | Refuse

type Decision =
    { Kind: DecisionKind
      Reason: string
      Candidate: string
      SubjectSha256: string
      Prior: PriorRun option
      SelfSha256: string }

let sha256 (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private isLowerSha256 (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && value.Length = 64
    && value |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')

let private isHead (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && value.Length = 40
    && value |> Seq.forall Uri.IsHexDigit

let private compactBytes write =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
    write writer
    writer.Flush()
    stream.ToArray()

let private framedTreeBytes (files: TrackedFile list) =
    let allowedModes = Set.ofList [ "100644"; "100755"; "120000" ]
    let ordered = files |> List.sortBy _.Path
    let paths = ordered |> List.map _.Path
    if paths.Length <> (paths |> List.distinct |> List.length) then invalidArg (nameof files) "tracked paths must be distinct"
    use stream = new MemoryStream()
    let appendText (value: string) =
        let bytes = Encoding.UTF8.GetBytes value
        let prefix = Encoding.ASCII.GetBytes($"%d{bytes.Length}:")
        stream.Write(prefix, 0, prefix.Length)
        stream.Write(bytes, 0, bytes.Length)
    for file in ordered do
        if not (allowedModes.Contains file.Mode) then invalidArg (nameof files) $"unsupported tracked mode: %s{file.Mode}"
        if String.IsNullOrWhiteSpace file.Path
           || Path.IsPathRooted file.Path
           || file.Path.Contains('\\')
           || file.Path.Split('/') |> Array.exists ((=) "..") then
            invalidArg (nameof files) $"unsafe tracked path: %s{file.Path}"
        appendText file.Mode
        appendText file.Path
        appendText (string file.Bytes.Length)
        stream.Write(file.Bytes, 0, file.Bytes.Length)
    stream.ToArray()

let private subjectPayloadBytes (tree: string) (plan: string) (workflow: string) (toolchain: string) (dependencies: string) (gateSet: string) (environment: string) (reviewPolicy: string) =
    compactBytes (fun writer ->
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.coordination.qualification-subject/1")
        writer.WriteString("treeSha256", tree)
        writer.WriteString("planSha256", plan)
        writer.WriteString("workflowSha256", workflow)
        writer.WriteString("toolchainSha256", toolchain)
        writer.WriteString("dependencySha256", dependencies)
        writer.WriteString("gateSetSha256", gateSet)
        writer.WriteString("environmentSha256", environment)
        writer.WriteString("reviewPolicySha256", reviewPolicy)
        writer.WriteEndObject())

let createSubject (files: TrackedFile list) (planBytes: byte array) (workflowBytes: byte array) (environmentBytes: byte array) (reviewPolicyBytes: byte array) =
    if files.IsEmpty then invalidArg (nameof files) "tracked tree must not be empty"
    let tree = framedTreeBytes files |> sha256
    let plan = sha256 planBytes
    let workflow = sha256 workflowBytes
    let subset (predicate: string -> bool) = files |> List.filter (fun file -> predicate file.Path) |> framedTreeBytes |> sha256
    let toolchain = subset (fun path -> path = "global.json")
    let dependencies = subset (fun path -> path = "Directory.Packages.props" || path.EndsWith("/packages.lock.json", StringComparison.Ordinal))
    let gateSet =
        subset (fun path ->
            path = "eng/bootstrap-qualification-plan.json"
            || path = "eng/github-substrate-v2-gates.json"
            || path.StartsWith("eng/bootstrap-gates/", StringComparison.Ordinal)
            || path.StartsWith("eng/qualify-canonical-quint", StringComparison.Ordinal)
            || path.StartsWith("eng/validate-canonical-quint", StringComparison.Ordinal))
    let environment = sha256 environmentBytes
    let reviewPolicy = sha256 reviewPolicyBytes
    let digest = subjectPayloadBytes tree plan workflow toolchain dependencies gateSet environment reviewPolicy |> sha256
    { TreeSha256 = tree
      PlanSha256 = plan
      WorkflowSha256 = workflow
      ToolchainSha256 = toolchain
      DependencySha256 = dependencies
      GateSetSha256 = gateSet
      EnvironmentSha256 = environment
      ReviewPolicySha256 = reviewPolicy
      SubjectSha256 = digest }

let subjectBytes (subject: QualificationSubject) =
    Array.append
        (compactBytes (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "fsgg.coordination.qualification-subject/1")
            writer.WriteString("treeSha256", subject.TreeSha256)
            writer.WriteString("planSha256", subject.PlanSha256)
            writer.WriteString("workflowSha256", subject.WorkflowSha256)
            writer.WriteString("toolchainSha256", subject.ToolchainSha256)
            writer.WriteString("dependencySha256", subject.DependencySha256)
            writer.WriteString("gateSetSha256", subject.GateSetSha256)
            writer.WriteString("environmentSha256", subject.EnvironmentSha256)
            writer.WriteString("reviewPolicySha256", subject.ReviewPolicySha256)
            writer.WriteString("subjectSha256", subject.SubjectSha256)
            writer.WriteEndObject()))
        [| byte '\n' |]

let private selectorText = function Exact path -> $"exact:{path}" | Prefix path -> $"prefix:{path}"

let createFormalSubject (files: TrackedFile list) selectors (policyBytes: byte array) =
    if files.IsEmpty then invalidArg (nameof files) "tracked tree must not be empty"
    if List.isEmpty selectors then invalidArg (nameof selectors) "formal subject selectors must not be empty"
    let selectorNames = selectors |> List.map selectorText
    if selectorNames.Length <> (selectorNames |> List.distinct |> List.length) then invalidArg (nameof selectors) "formal subject selectors must be distinct"
    let matches selector path =
        match selector with
        | Exact expected -> path = expected
        | Prefix prefix -> path.StartsWith(prefix, StringComparison.Ordinal)
    for selector in selectors do
        match selector with
        | Exact path when String.IsNullOrWhiteSpace path || Path.IsPathRooted path || path.Contains('\\') -> invalidArg (nameof selectors) "formal exact selector is unsafe"
        | Prefix prefix when String.IsNullOrWhiteSpace prefix || not (prefix.EndsWith('/')) || Path.IsPathRooted prefix || prefix.Contains('\\') -> invalidArg (nameof selectors) "formal prefix selector is unsafe"
        | _ -> ()
        if files |> List.exists (fun file -> matches selector file.Path) |> not then
            invalidArg (nameof selectors) $"formal subject selector matched no tracked file: {selectorText selector}"
    let selected =
        files
        |> List.choose (fun file ->
            let count = selectors |> List.filter (fun selector -> matches selector file.Path) |> List.length
            if count > 1 then invalidArg (nameof selectors) $"formal subject selector overlap: {file.Path}"
            if count = 1 then Some file else None)
    let filesDigest = framedTreeBytes selected |> sha256
    let selectorPolicy = policyBytes |> sha256
    let payload =
        compactBytes (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "fsgg.coordination.formal-subject/1")
            writer.WriteString("filesSha256", filesDigest)
            writer.WriteString("selectorPolicySha256", selectorPolicy)
            writer.WriteNumber("fileCount", selected.Length)
            writer.WriteEndObject())
    { FilesSha256 = filesDigest; SelectorPolicySha256 = selectorPolicy; FileCount = selected.Length; SubjectSha256 = sha256 payload }

let formalSubjectBytes subject =
    Array.append
        (compactBytes (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "fsgg.coordination.formal-subject/1")
            writer.WriteString("filesSha256", subject.FilesSha256)
            writer.WriteString("selectorPolicySha256", subject.SelectorPolicySha256)
            writer.WriteNumber("fileCount", subject.FileCount)
            writer.WriteString("subjectSha256", subject.SubjectSha256)
            writer.WriteEndObject()))
        [| byte '\n' |]

let private kindText = function
    | Reuse -> "reuse"
    | Execute -> "execute"
    | Refuse -> "refuse"

let private payloadBytes (kind: DecisionKind) (reason: string) (candidate: string) (subject: string) (prior: PriorRun option) =
    compactBytes (fun writer ->
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.coordination.qualification-reuse-receipt/1")
        writer.WriteString("decision", kindText kind)
        writer.WriteString("reason", reason)
        writer.WriteString("candidate", candidate.ToLowerInvariant())
        writer.WriteString("subjectSha256", subject)
        match prior with
        | None -> writer.WriteNull("prior")
        | Some value ->
            writer.WriteStartObject("prior")
            writer.WriteString("head", value.Head.ToLowerInvariant())
            writer.WriteNumber("runId", value.RunId)
            writer.WriteNumber("attempt", value.Attempt)
            writer.WriteString("evidenceSha256", value.EvidenceSha256)
            writer.WriteString("artifactExpiresAt", value.ArtifactExpiresAt)
            match value.RunnerMinutes with
            | Some minutes -> writer.WriteNumber("runnerMinutes", minutes)
            | None -> writer.WriteNull("runnerMinutes")
            writer.WriteEndObject()
        writer.WriteEndObject())

let private make (kind: DecisionKind) (reason: string) (candidate: string) (subject: string) (prior: PriorRun option) =
    if not (isHead candidate) then invalidArg (nameof candidate) "candidate must be an exact 40-hex SHA"
    if not (isLowerSha256 subject) then invalidArg (nameof subject) "subject must be a lowercase SHA-256"
    if String.IsNullOrWhiteSpace reason then invalidArg (nameof reason) "decision reason is required"
    match kind, prior with
    | Reuse, None -> invalidArg (nameof prior) "reuse requires a prior run"
    | (Execute | Refuse), Some _ -> invalidArg (nameof prior) "execute/refuse must not bind a prior run"
    | _ -> ()
    prior
    |> Option.iter (fun value ->
        if not (isHead value.Head) || value.RunId <= 0L || value.Attempt <= 0 || not (isLowerSha256 value.EvidenceSha256) then
            invalidArg (nameof prior) "prior run identity is invalid"
        value.RunnerMinutes
        |> Option.iter (fun minutes ->
            if minutes < 0M then invalidArg (nameof prior) "prior runner minutes cannot be negative")
        match DateTimeOffset.TryParse value.ArtifactExpiresAt with
        | true, _ -> ()
        | _ -> invalidArg (nameof prior) "prior artifact expiry is invalid")
    let self = payloadBytes kind reason candidate subject prior |> sha256
    { Kind = kind
      Reason = reason
      Candidate = candidate.ToLowerInvariant()
      SubjectSha256 = subject
      Prior = prior
      SelfSha256 = self }

let decide candidate subjectSha256 prior priorSubjectSha256 =
    match prior, priorSubjectSha256 with
    | None, None -> make Execute "no-compatible-prior" candidate subjectSha256 None
    | Some value, Some priorSubject when priorSubject = subjectSha256 -> make Reuse "identical-complete-tree" candidate subjectSha256 (Some value)
    | Some _, Some _ -> make Execute "subject-mismatch" candidate subjectSha256 None
    | _ -> make Refuse "incomplete-prior-authority" candidate subjectSha256 None

let refuse candidate subjectSha256 reason = make Refuse reason candidate subjectSha256 None

let decisionBytes decision =
    let expected = payloadBytes decision.Kind decision.Reason decision.Candidate decision.SubjectSha256 decision.Prior |> sha256
    if expected <> decision.SelfSha256 then invalidArg (nameof decision) "decision self digest is stale"
    Array.append
        (compactBytes (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "fsgg.coordination.qualification-reuse-receipt/1")
            writer.WriteString("decision", kindText decision.Kind)
            writer.WriteString("reason", decision.Reason)
            writer.WriteString("candidate", decision.Candidate)
            writer.WriteString("subjectSha256", decision.SubjectSha256)
            match decision.Prior with
            | None -> writer.WriteNull("prior")
            | Some value ->
                writer.WriteStartObject("prior")
                writer.WriteString("head", value.Head)
                writer.WriteNumber("runId", value.RunId)
                writer.WriteNumber("attempt", value.Attempt)
                writer.WriteString("evidenceSha256", value.EvidenceSha256)
                writer.WriteString("artifactExpiresAt", value.ArtifactExpiresAt)
                match value.RunnerMinutes with
                | Some minutes -> writer.WriteNumber("runnerMinutes", minutes)
                | None -> writer.WriteNull("runnerMinutes")
                writer.WriteEndObject()
            writer.WriteString("selfSha256", decision.SelfSha256)
            writer.WriteEndObject()))
        [| byte '\n' |]

let private stringProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then value.GetString() |> Option.ofObj
    else None

let parseDecision (bytes: byte array) =
    try
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        let properties = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        let expected = [ "schema"; "decision"; "reason"; "candidate"; "subjectSha256"; "prior"; "selfSha256" ]
        if properties <> expected then Error "reuse receipt properties are not the exact canonical set"
        elif stringProperty "schema" root <> Some "fsgg.coordination.qualification-reuse-receipt/1" then Error "reuse receipt schema is unsupported"
        else
            let kind =
                match stringProperty "decision" root with
                | Some "reuse" -> Reuse
                | Some "execute" -> Execute
                | Some "refuse" -> Refuse
                | _ -> failwith "reuse decision is unsupported"
            let reason = stringProperty "reason" root |> Option.defaultWith (fun () -> failwith "reuse reason is missing")
            let candidate = stringProperty "candidate" root |> Option.defaultWith (fun () -> failwith "reuse candidate is missing")
            let subject = stringProperty "subjectSha256" root |> Option.defaultWith (fun () -> failwith "reuse subject is missing")
            let priorElement = root.GetProperty("prior")
            let prior =
                if priorElement.ValueKind = JsonValueKind.Null then None
                elif priorElement.ValueKind = JsonValueKind.Object then
                    let priorProperties = priorElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    if priorProperties <> [ "head"; "runId"; "attempt"; "evidenceSha256"; "artifactExpiresAt"; "runnerMinutes" ] then
                        failwith "reuse prior properties are not canonical"
                    Some
                        { Head = stringProperty "head" priorElement |> Option.defaultWith (fun () -> failwith "prior head is missing")
                          RunId = priorElement.GetProperty("runId").GetInt64()
                          Attempt = priorElement.GetProperty("attempt").GetInt32()
                          EvidenceSha256 = stringProperty "evidenceSha256" priorElement |> Option.defaultWith (fun () -> failwith "prior evidence digest is missing")
                          ArtifactExpiresAt = stringProperty "artifactExpiresAt" priorElement |> Option.defaultWith (fun () -> failwith "prior expiry is missing")
                          RunnerMinutes =
                              let minutes = priorElement.GetProperty("runnerMinutes")
                              if minutes.ValueKind = JsonValueKind.Null then None
                              elif minutes.ValueKind = JsonValueKind.Number then Some(minutes.GetDecimal())
                              else failwith "prior runner minutes must be a number or null" }
                else failwith "reuse prior must be an object or null"
            let parsed = make kind reason candidate subject prior
            let self = stringProperty "selfSha256" root |> Option.defaultWith (fun () -> failwith "reuse self digest is missing")
            let final = { parsed with SelfSha256 = self }
            if self <> parsed.SelfSha256 then Error "reuse self digest does not match"
            elif bytes <> decisionBytes final then Error "reuse receipt bytes are not canonical"
            else Ok final
    with exceptionValue -> Error exceptionValue.Message
