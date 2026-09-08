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

type ReuseIdentity =
    { BehavioralSha256: string
      CompiledContractSha256: string
      ToolchainProfileSha256: string
      VerificationBoundsSha256: string
      FormalCorpusSha256: string
      HarnessSha256: string
      BindingSha256: string }

type CandidateObligation =
    { Candidate: string
      BaseRevision: string
      TreeSha256: string
      SourceSha256: string
      Identity: ReuseIdentity
      ObligationSha256: string }

type PriorExecution =
    { Candidate: CandidateObligation
      RunId: int64
      Attempt: int
      ExecutedReceiptSha256: string
      CompletedAt: string
      ExpiresAt: string
      Authentic: bool
      Complete: bool }

type SemanticDelta =
    { EvaluatorSha256: string
      DeltaSha256: string
      IsEmpty: bool }

type ReuseDisposition = Current | Reused | Deferred | Failed
type CoherentState = Pending | Running | Passed | Blocked | Disputed

type ReuseSelection =
    { Candidate: CandidateObligation
      Disposition: ReuseDisposition
      Reason: string
      Prior: PriorExecution option
      SemanticDelta: SemanticDelta
      BindingCorrespondenceSha256: string option
      CoherentRunPending: bool
      CoherentState: CoherentState
      SelectionSha256: string }

type PartitionPlan =
    { Candidate: CandidateObligation
      Obligations: string list
      PartitionCount: int
      Partitions: (int * string list) list
      PlanSha256: string }

type PartitionReceipt =
    { PlanSha256: string
      Partition: int
      Obligations: string list
      Passed: bool
      ReceiptSha256: string }

type CoherentAggregateReceipt =
    { CandidateObligationSha256: string
      PlanSha256: string
      PartitionReceiptSha256: string list
      Passed: bool
      ReceiptSha256: string }

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

let private requireDigest (name: string) (value: string) =
    if not (isLowerSha256 value) then invalidArg name $"{name} must be a lowercase SHA-256"

let private identityFields (identity: ReuseIdentity) =
    [ "behavioralSha256", identity.BehavioralSha256
      "compiledContractSha256", identity.CompiledContractSha256
      "toolchainProfileSha256", identity.ToolchainProfileSha256
      "verificationBoundsSha256", identity.VerificationBoundsSha256
      "formalCorpusSha256", identity.FormalCorpusSha256
      "harnessSha256", identity.HarnessSha256
      "bindingSha256", identity.BindingSha256 ]

let private obligationPayload (candidate: string) (baseRevision: string) (tree: string) (source: string) (identity: ReuseIdentity) =
    compactBytes (fun writer ->
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.coordination.candidate-obligation/1")
        writer.WriteString("candidate", candidate)
        writer.WriteString("baseRevision", baseRevision)
        writer.WriteString("treeSha256", tree)
        writer.WriteString("sourceSha256", source)
        for name, value in identityFields identity do writer.WriteString(name, value)
        writer.WriteEndObject())

let createCandidateObligation (candidate: string) (baseRevision: string) (treeSha256: string) (sourceSha256: string) (identity: ReuseIdentity) =
    if not (isHead candidate) || not (isHead baseRevision) then invalidArg (nameof candidate) "candidate and base must be exact 40-hex revisions"
    requireDigest (nameof treeSha256) treeSha256
    requireDigest (nameof sourceSha256) sourceSha256
    identityFields identity |> List.iter (fun (name, value) -> requireDigest name value)
    let normalizedCandidate = candidate.ToLowerInvariant()
    let normalizedBase = baseRevision.ToLowerInvariant()
    { Candidate = normalizedCandidate
      BaseRevision = normalizedBase
      TreeSha256 = treeSha256
      SourceSha256 = sourceSha256
      Identity = identity
      ObligationSha256 = obligationPayload normalizedCandidate normalizedBase treeSha256 sourceSha256 identity |> sha256 }

let candidateObligationBytes (obligation: CandidateObligation) =
    let payload = obligationPayload obligation.Candidate obligation.BaseRevision obligation.TreeSha256 obligation.SourceSha256 obligation.Identity
    if sha256 payload <> obligation.ObligationSha256 then invalidArg (nameof obligation) "candidate obligation digest is stale"
    Array.append
        (compactBytes (fun writer ->
            use document = JsonDocument.Parse payload
            writer.WriteStartObject(); document.RootElement.EnumerateObject() |> Seq.iter (fun property -> property.WriteTo writer)
            writer.WriteString("obligationSha256", obligation.ObligationSha256); writer.WriteEndObject())) [| byte '\n' |]

let private dispositionText = function Current -> "current" | Reused -> "reused" | Deferred -> "deferred" | Failed -> "failed"
let private coherentText = function Pending -> "pending" | Running -> "running" | Passed -> "passed" | Blocked -> "blocked" | Disputed -> "disputed"

let private selectionPayload (candidate: CandidateObligation) (disposition: ReuseDisposition) (reason: string) (prior: PriorExecution option) (semantic: SemanticDelta) (correspondence: string option) (pending: bool) (state: CoherentState) =
    compactBytes (fun writer ->
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.coordination.qualification-selection/1")
        writer.WriteString("candidateObligationSha256", candidate.ObligationSha256)
        writer.WriteString("disposition", dispositionText disposition)
        writer.WriteString("reason", reason)
        match prior with
        | Some value ->
            writer.WriteStartObject("prior")
            writer.WriteString("candidateObligationSha256", value.Candidate.ObligationSha256)
            writer.WriteNumber("runId", value.RunId)
            writer.WriteNumber("attempt", value.Attempt)
            writer.WriteString("executedReceiptSha256", value.ExecutedReceiptSha256)
            writer.WriteString("completedAt", value.CompletedAt)
            writer.WriteString("expiresAt", value.ExpiresAt)
            writer.WriteBoolean("authentic", value.Authentic)
            writer.WriteBoolean("complete", value.Complete)
            writer.WriteEndObject()
        | None -> writer.WriteNull("prior")
        writer.WriteStartObject("semanticDelta")
        writer.WriteString("evaluatorSha256", semantic.EvaluatorSha256)
        writer.WriteString("deltaSha256", semantic.DeltaSha256)
        writer.WriteBoolean("empty", semantic.IsEmpty)
        writer.WriteEndObject()
        match correspondence with Some value -> writer.WriteString("bindingCorrespondenceSha256", value) | None -> writer.WriteNull("bindingCorrespondenceSha256")
        writer.WriteBoolean("coherentRunPending", pending)
        writer.WriteString("coherentState", coherentText state)
        writer.WriteEndObject())

let private makeSelection (candidate: CandidateObligation) disposition reason prior semantic correspondence pending state : ReuseSelection =
    let payload = selectionPayload candidate disposition reason prior semantic correspondence pending state
    { Candidate = candidate; Disposition = disposition; Reason = reason; Prior = prior
      SemanticDelta = semantic; BindingCorrespondenceSha256 = correspondence
      CoherentRunPending = pending; CoherentState = state; SelectionSha256 = sha256 payload }

let selectReusable (now: DateTimeOffset) (candidate: CandidateObligation) (prior: PriorExecution option) (semanticDelta: SemanticDelta) (bindingCorrespondenceSha256: string option) =
    requireDigest "semantic evaluator" semanticDelta.EvaluatorSha256
    requireDigest "semantic delta" semanticDelta.DeltaSha256
    bindingCorrespondenceSha256 |> Option.iter (requireDigest "binding correspondence")
    match prior with
    | None -> makeSelection candidate Current "no-prior-execution" None semanticDelta None true Pending
    | Some previous ->
        let identity = candidate.Identity
        let old = previous.Candidate.Identity
        let sameSemanticIdentity =
            identity.BehavioralSha256 = old.BehavioralSha256
            && identity.CompiledContractSha256 = old.CompiledContractSha256
            && identity.ToolchainProfileSha256 = old.ToolchainProfileSha256
            && identity.VerificationBoundsSha256 = old.VerificationBoundsSha256
            && identity.FormalCorpusSha256 = old.FormalCorpusSha256
            && identity.HarnessSha256 = old.HarnessSha256
        let bindingValid =
            identity.BindingSha256 = old.BindingSha256 || Option.isSome bindingCorrespondenceSha256
        let temporal =
            match DateTimeOffset.TryParse previous.CompletedAt, DateTimeOffset.TryParse previous.ExpiresAt with
            | (true, completed), (true, expires) -> completed <= now && now < expires
            | _ -> false
        let receiptValid =
            previous.RunId > 0L && previous.Attempt > 0 && isLowerSha256 previous.ExecutedReceiptSha256
            && previous.Authentic && previous.Complete && temporal
        if not receiptValid then makeSelection candidate Current "prior-execution-incomplete-inauthentic-or-expired" None semanticDelta None true Pending
        elif not semanticDelta.IsEmpty then makeSelection candidate Current "semantic-delta-nonempty" None semanticDelta None true Pending
        elif not sameSemanticIdentity then makeSelection candidate Current "semantic-tool-bounds-corpus-or-harness-mismatch" None semanticDelta None true Pending
        elif not bindingValid then makeSelection candidate Current "changed-binding-correspondence-missing" None semanticDelta None true Pending
        else makeSelection candidate Reused "independently-equivalent-prior-execution" (Some previous) semanticDelta bindingCorrespondenceSha256 true Pending

let applyCoherentOutcome merged passed (selection: ReuseSelection) =
    if passed then makeSelection selection.Candidate selection.Disposition selection.Reason selection.Prior selection.SemanticDelta selection.BindingCorrespondenceSha256 false Passed
    elif merged then makeSelection selection.Candidate Failed "post-merge-coherent-failure" selection.Prior selection.SemanticDelta selection.BindingCorrespondenceSha256 false Disputed
    else makeSelection selection.Candidate Failed "pre-merge-coherent-failure" selection.Prior selection.SemanticDelta selection.BindingCorrespondenceSha256 false Blocked

let selectionBytes (selection: ReuseSelection) =
    let expected = selectionPayload selection.Candidate selection.Disposition selection.Reason selection.Prior selection.SemanticDelta selection.BindingCorrespondenceSha256 selection.CoherentRunPending selection.CoherentState |> sha256
    if expected <> selection.SelectionSha256 then invalidArg (nameof selection) "selection self digest is stale"
    Array.append
        (compactBytes (fun writer ->
            use document = JsonDocument.Parse(selectionPayload selection.Candidate selection.Disposition selection.Reason selection.Prior selection.SemanticDelta selection.BindingCorrespondenceSha256 selection.CoherentRunPending selection.CoherentState)
            writer.WriteStartObject()
            for property in document.RootElement.EnumerateObject() do property.WriteTo writer
            writer.WriteString("selectionSha256", selection.SelectionSha256)
            writer.WriteEndObject()))
        [| byte '\n' |]

let parseCandidateObligation (bytes: byte array) =
    try
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        let text name = stringProperty name root |> Option.defaultWith (fun () -> failwith $"candidate obligation is missing {name}")
        let identity =
            { BehavioralSha256 = text "behavioralSha256"; CompiledContractSha256 = text "compiledContractSha256"
              ToolchainProfileSha256 = text "toolchainProfileSha256"; VerificationBoundsSha256 = text "verificationBoundsSha256"
              FormalCorpusSha256 = text "formalCorpusSha256"; HarnessSha256 = text "harnessSha256"; BindingSha256 = text "bindingSha256" }
        let value = createCandidateObligation (text "candidate") (text "baseRevision") (text "treeSha256") (text "sourceSha256") identity
        if text "schema" <> "fsgg.coordination.candidate-obligation/1" then Error "candidate obligation schema is unsupported"
        elif text "obligationSha256" <> value.ObligationSha256 then Error "candidate obligation digest differs"
        elif bytes <> candidateObligationBytes value then Error "candidate obligation bytes are not canonical"
        else Ok value
    with error -> Error error.Message

let private partitionPayload (obligationSha: string) (obligations: string list) (partitions: (int * string list) list) =
    compactBytes (fun writer ->
        writer.WriteStartObject(); writer.WriteString("schema", "fsgg.coordination.coherent-partition-plan/1")
        writer.WriteString("candidateObligationSha256", obligationSha)
        writer.WriteStartArray("obligations"); obligations |> List.iter writer.WriteStringValue; writer.WriteEndArray()
        writer.WriteStartArray("partitions")
        for index, values in partitions do
            writer.WriteStartObject(); writer.WriteNumber("index", index); writer.WriteStartArray("obligations")
            values |> List.iter writer.WriteStringValue; writer.WriteEndArray(); writer.WriteEndObject()
        writer.WriteEndArray(); writer.WriteEndObject())

let createPartitionPlan (candidate: CandidateObligation) maxPartitions (obligations: string list) =
    if maxPartitions < 1 || maxPartitions > 6 then invalidArg (nameof maxPartitions) "partition count must be between one and six"
    let ordered = obligations |> List.sort
    if ordered.IsEmpty || ordered <> List.distinct ordered || ordered |> List.exists String.IsNullOrWhiteSpace then invalidArg (nameof obligations) "obligations must be non-empty and distinct"
    let count = min maxPartitions ordered.Length
    let partitions = [ for index in 0 .. count - 1 -> index, (ordered |> List.mapi (fun i value -> i, value) |> List.choose (fun (i, value) -> if i % count = index then Some value else None)) ]
    { Candidate = candidate; Obligations = ordered; PartitionCount = count; Partitions = partitions
      PlanSha256 = partitionPayload candidate.ObligationSha256 ordered partitions |> sha256 }

let partitionPlanBytes (plan: PartitionPlan) =
    let payload = partitionPayload plan.Candidate.ObligationSha256 plan.Obligations plan.Partitions
    if sha256 payload <> plan.PlanSha256 then invalidArg (nameof plan) "partition plan digest is stale"
    Array.append
        (compactBytes (fun writer ->
            use document = JsonDocument.Parse payload
            writer.WriteStartObject(); document.RootElement.EnumerateObject() |> Seq.iter (fun property -> property.WriteTo writer)
            writer.WriteString("planSha256", plan.PlanSha256); writer.WriteEndObject())) [| byte '\n' |]

let parsePartitionPlan (candidate: CandidateObligation) (bytes: byte array) =
    try
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        let strings (element: JsonElement) = element.EnumerateArray() |> Seq.map (_.GetString()) |> Seq.toList
        let obligations = strings (root.GetProperty "obligations")
        let partitions =
            root.GetProperty("partitions").EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("index").GetInt32(), strings (item.GetProperty "obligations")) |> Seq.toList
        let count = partitions.Length
        let value = { Candidate = candidate; Obligations = obligations; PartitionCount = count; Partitions = partitions; PlanSha256 = root.GetProperty("planSha256").GetString() }
        if root.GetProperty("schema").GetString() <> "fsgg.coordination.coherent-partition-plan/1" then Error "partition plan schema is unsupported"
        elif root.GetProperty("candidateObligationSha256").GetString() <> candidate.ObligationSha256 then Error "partition plan candidate differs"
        elif value.PlanSha256 <> (partitionPayload candidate.ObligationSha256 obligations partitions |> sha256) then Error "partition plan digest differs"
        elif bytes <> partitionPlanBytes value then Error "partition plan bytes are not canonical"
        else Ok value
    with error -> Error error.Message

let private receiptPayload (planSha: string) (partition: int) (obligations: string list) passed =
    compactBytes (fun writer ->
        writer.WriteStartObject(); writer.WriteString("schema", "fsgg.coordination.coherent-partition-receipt/1")
        writer.WriteString("planSha256", planSha); writer.WriteNumber("partition", partition)
        writer.WriteStartArray("obligations"); obligations |> List.iter writer.WriteStringValue; writer.WriteEndArray()
        writer.WriteBoolean("passed", passed); writer.WriteEndObject())

let createPartitionReceipt (plan: PartitionPlan) partition obligations passed =
    let expected = plan.Partitions |> List.tryFind (fst >> (=) partition) |> Option.map snd |> Option.defaultWith (fun () -> invalidArg (nameof partition) "partition is not in plan")
    if obligations <> expected then invalidArg (nameof obligations) "partition obligations differ from immutable plan"
    { PlanSha256 = plan.PlanSha256; Partition = partition; Obligations = obligations; Passed = passed
      ReceiptSha256 = receiptPayload plan.PlanSha256 partition obligations passed |> sha256 }

let partitionReceiptBytes (receipt: PartitionReceipt) =
    let payload = receiptPayload receipt.PlanSha256 receipt.Partition receipt.Obligations receipt.Passed
    if sha256 payload <> receipt.ReceiptSha256 then invalidArg (nameof receipt) "partition receipt digest is stale"
    Array.append
        (compactBytes (fun writer ->
            use document = JsonDocument.Parse payload
            writer.WriteStartObject(); document.RootElement.EnumerateObject() |> Seq.iter (fun property -> property.WriteTo writer)
            writer.WriteString("receiptSha256", receipt.ReceiptSha256); writer.WriteEndObject())) [| byte '\n' |]

let parsePartitionReceipt (bytes: byte array) =
    try
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        let obligations = root.GetProperty("obligations").EnumerateArray() |> Seq.map (_.GetString()) |> Seq.toList
        let receipt =
            { PlanSha256 = root.GetProperty("planSha256").GetString(); Partition = root.GetProperty("partition").GetInt32()
              Obligations = obligations; Passed = root.GetProperty("passed").GetBoolean(); ReceiptSha256 = root.GetProperty("receiptSha256").GetString() }
        if root.GetProperty("schema").GetString() <> "fsgg.coordination.coherent-partition-receipt/1" then Error "partition receipt schema is unsupported"
        elif receipt.ReceiptSha256 <> (receiptPayload receipt.PlanSha256 receipt.Partition receipt.Obligations receipt.Passed |> sha256) then Error "partition receipt digest differs"
        elif bytes <> partitionReceiptBytes receipt then Error "partition receipt bytes are not canonical"
        else Ok receipt
    with error -> Error error.Message

let aggregatePartitions (plan: PartitionPlan) (receipts: PartitionReceipt list) =
    let ordered = receipts |> List.sortBy _.Partition
    if ordered.Length <> plan.PartitionCount then Error "partition coverage incomplete"
    elif ordered |> List.map _.Partition <> [ 0 .. plan.PartitionCount - 1 ] then Error "partition indexes are missing or duplicated"
    else
        let invalid =
            ordered
            |> List.tryFind (fun receipt ->
                let expected = plan.Partitions |> List.find (fst >> (=) receipt.Partition) |> snd
                receipt.PlanSha256 <> plan.PlanSha256 || receipt.Obligations <> expected
                || receipt.ReceiptSha256 <> (receiptPayload receipt.PlanSha256 receipt.Partition receipt.Obligations receipt.Passed |> sha256))
        match invalid with
        | Some _ -> Error "partition receipt is substituted or stale"
        | None -> Ok(ordered |> List.forall _.Passed)

let private aggregatePayload (candidate: string) (plan: string) (receipts: string list) passed =
    compactBytes (fun writer ->
        writer.WriteStartObject(); writer.WriteString("schema", "fsgg.coordination.coherent-aggregate-receipt/1")
        writer.WriteString("candidateObligationSha256", candidate); writer.WriteString("planSha256", plan)
        writer.WriteStartArray("partitionReceiptSha256"); receipts |> List.iter writer.WriteStringValue; writer.WriteEndArray()
        writer.WriteBoolean("passed", passed); writer.WriteEndObject())

let createCoherentAggregateReceipt (plan: PartitionPlan) (receipts: PartitionReceipt list) =
    match aggregatePartitions plan receipts with
    | Error error -> Error error
    | Ok passed ->
        let digests = receipts |> List.sortBy _.Partition |> List.map _.ReceiptSha256
        let payload = aggregatePayload plan.Candidate.ObligationSha256 plan.PlanSha256 digests passed
        Ok { CandidateObligationSha256 = plan.Candidate.ObligationSha256; PlanSha256 = plan.PlanSha256
             PartitionReceiptSha256 = digests; Passed = passed; ReceiptSha256 = sha256 payload }

let coherentAggregateReceiptBytes (receipt: CoherentAggregateReceipt) =
    let payload = aggregatePayload receipt.CandidateObligationSha256 receipt.PlanSha256 receipt.PartitionReceiptSha256 receipt.Passed
    if sha256 payload <> receipt.ReceiptSha256 then invalidArg (nameof receipt) "aggregate receipt digest is stale"
    Array.append
        (compactBytes (fun writer ->
            use document = JsonDocument.Parse payload
            writer.WriteStartObject(); document.RootElement.EnumerateObject() |> Seq.iter (fun property -> property.WriteTo writer)
            writer.WriteString("receiptSha256", receipt.ReceiptSha256); writer.WriteEndObject())) [| byte '\n' |]

let parseCoherentAggregateReceipt (bytes: byte array) =
    try
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        let receipt =
            { CandidateObligationSha256 = root.GetProperty("candidateObligationSha256").GetString()
              PlanSha256 = root.GetProperty("planSha256").GetString()
              PartitionReceiptSha256 = root.GetProperty("partitionReceiptSha256").EnumerateArray() |> Seq.map (_.GetString()) |> Seq.toList
              Passed = root.GetProperty("passed").GetBoolean(); ReceiptSha256 = root.GetProperty("receiptSha256").GetString() }
        if root.GetProperty("schema").GetString() <> "fsgg.coordination.coherent-aggregate-receipt/1" then Error "aggregate receipt schema is unsupported"
        elif not receipt.Passed || receipt.PartitionReceiptSha256.Length <> 6
             || (receipt.PartitionReceiptSha256 |> List.distinct |> List.length) <> 6
             || receipt.PartitionReceiptSha256 |> List.exists (isLowerSha256 >> not) then Error "aggregate receipt is incomplete or failed"
        elif receipt.ReceiptSha256 <> (aggregatePayload receipt.CandidateObligationSha256 receipt.PlanSha256 receipt.PartitionReceiptSha256 receipt.Passed |> sha256) then Error "aggregate receipt digest differs"
        elif bytes <> coherentAggregateReceiptBytes receipt then Error "aggregate receipt bytes are not canonical"
        else Ok receipt
    with error -> Error error.Message
