namespace FS.GG.Coordination.Core

open System
open Orchestration

/// Pure ordinary-work data. No value is an admission or a reservation allocation.
module OrdinaryAssignment =
    type WorkIdentity = Board of WorkItemId | Local of workspace: string * key: string
    type Source = { Repository: string; Revision: string; Path: string; Sha256: string }
    type Repository = { Identity: string; Revision: string }
    type Component = { Id: string; Root: string; Language: string; Commands: Set<string>; Sources: Source list }
    type WorkspaceExecutionContract = {
        Version: string; Workspace: string; Provider: string; Repositories: Repository list
        Components: Component list; Sources: Source list }
    type WorkAssessment = {
        Version: string; Phase: string; Outcome: string; Objective: string; Plan: string
        Facts: Set<string>; Interpretation: string; Difficulty: int; Uncertainty: int
        EffectRisk: int; MissingFacts: Set<string> }
    /// Named units: cpu-millicores, memory-bytes, storage-bytes, io-bytes-per-second,
    /// task-slots and quota:<provider>:<unit>. None is unknown, never zero.
    type Resources = Map<string, int64 option>
    type Recovery = { OriginalStartedAt: DateTimeOffset; OriginalDeadline: DateTimeOffset; MaxAttempts: int; AttemptOrdinal: int }
    type Requirements = {
        Key: string; Identity: WorkIdentity; Workspace: WorkspaceExecutionContract; Assessment: WorkAssessment
        Attempt: AttemptId; Generation: Generation option; SelectedAt: DateTimeOffset; Deadline: DateTimeOffset option
        Recovery: Recovery option; Dependencies: Set<string>; RequiredChecks: Set<string>
        Capabilities: Set<string>; Tools: Set<string>; Skills: Set<string>; Context: Set<string>
        AllowedPaths: Set<string>; AuthorizedEffects: Set<string>; RequiredOutputs: Set<string>; RequiredReviews: Set<string>
        Demand: Resources; Priority: int }
    type Configuration = { Model: string; Effort: string; Runtime: string }
    type CapabilityObservation = { Available: bool; Evidence: Source option }
    type Economics = { Currency: string; ProvenanceClass: string; CostMicroUnits: int64; DurationMilliseconds: int64; ReworkMilliseconds: int64 }
    type ExecutionProfile = {
        Id: string; Version: string; Requested: Configuration; Resolved: Configuration option; Observed: Configuration option
        Capabilities: Map<string, CapabilityObservation>; Tools: Set<string>; Skills: Set<string>; Context: Set<string>
        Effects: Set<string>; Outputs: Set<string>; Reviews: Set<string>; AdditionalDemand: Resources
        RecoverySupported: bool; Economics: Economics option }
    /// Completeness is supplied by the authoritative producer, not inferred by the planner.
    type Completeness = { ExpectedWorkKeys: Set<string>; Sources: Source list; SourceBound: bool; ManifestSha256: string }
    type AuthoritativeObligations = { Version: string; Works: Requirements list; Completeness: Completeness }
    type Reservation = { Id: ReservationId; Work: WorkIdentity; Demand: Resources; Paths: Set<string>; TerminalState: string }
    type Snapshot = {
        Version: string; AuthoritySha256: string; Works: Requirements list; Profiles: ExecutionProfile list
        Now: DateTimeOffset; Capacity: Resources; RecoveryHeadroom: Resources; Reservations: Reservation list
        ReservationPopulationComplete: bool; Project: ProjectState; SatisfiedDependencies: Set<string> }
    type AgentSpecification = {
        Requirements: Requirements; Profile: ExecutionProfile; Demand: Resources; Reservation: ReservationId
        Requested: Configuration; Resolved: Configuration option; Observed: Configuration option }
    type Assignment = {
        PolicyVersion: string; AuthoritySha256: string; SnapshotSha256: string; AssignmentSha256: string
        Specification: AgentSpecification; ExperimentEnrollment: string option }
    type Preference = Economic of currency: string * provenanceClass: string * cost: int64 * duration: int64 * rework: int64 | NonEconomicFallback
    type Exclusion = { WorkKey: string; ProfileId: string; Reasons: string list }
    type Decision = {
        Eligible: Assignment list; Excluded: Exclusion list; Selected: Assignment list
        Preference: Preference; Reasons: string list }
    type Refusal = NeedsEvidence of string list | ConflictingEvidence of string list | NeedsDecomposition of string list | WaitingForCapability of string list | RequiresOperatorDecision of string list

    open System.Collections
    open System.Globalization
    open System.IO
    open System.Security.Cryptography
    open System.Text
    open Microsoft.FSharp.Reflection

    let policyVersion = "ordinary-assignment-rules/1"
    let maximumEvaluations = 1000000
    let maximumInputBytes = 2 * 1024 * 1024
    let maximumResultBytes = 512 * 1024

    // A length-prefixed typed canonical encoding; records, union tags and field names
    // are part of identity. Sets/maps enumerate in their structural order; lists keep order.
    // Public normalization below sorts the two unordered work/profile populations.
    let private encode (value: obj) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        let mutable evaluations = 0
        let rec write (v: obj) =
            evaluations <- evaluations + 1
            if evaluations > maximumEvaluations || stream.Length > int64 maximumInputBytes then
                invalidArg "value" "canonical-input-bound"
            if isNull v then writer.Write(byte 0)
            else
                let t = v.GetType()
                writer.Write(t.FullName)
                match v with
                | :? string as s ->
                    if s.Length>512 || Encoding.UTF8.GetByteCount s > 512 then invalidArg "value" "text-bound"
                    writer.Write(s)
                | :? DateTimeOffset as d -> writer.Write(d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                | :? Guid as g -> writer.Write(g.ToString("D"))
                | :? int64 as n -> writer.Write(n)
                | :? int as n -> writer.Write(n)
                | :? bool as b -> writer.Write(b)
                | _ when FSharpType.IsRecord(t, true) ->
                    for field, item in Array.zip (FSharpType.GetRecordFields(t, true)) (FSharpValue.GetRecordFields(v, true)) do
                        writer.Write(field.Name); write item
                | _ when FSharpType.IsTuple t -> for item in FSharpValue.GetTupleFields v do write item
                | :? IEnumerable as items ->
                    let mutable count = 0
                    for item in items do
                        count <- count + 1
                        if count > 256 then invalidArg "value" "collection-bound"
                        write item
                    writer.Write(count)
                | _ when FSharpType.IsUnion(t, true) ->
                    let case, fields = FSharpValue.GetUnionFields(v, t, true)
                    writer.Write(case.Name)
                    for item in fields do write item
                | _ when t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.KeyValuePair<_,_>> ->
                    write (t.GetProperty("Key").GetValue v); write (t.GetProperty("Value").GetValue v)
                | _ -> invalidArg "value" "unsupported-canonical-type"
        write value
        writer.Flush()
        if stream.Length > int64 maximumInputBytes then invalidArg "value" "canonical-input-bound"
        stream.ToArray(), evaluations

    let canonicalMeasure value = let bytes,nodes=encode (box value) in bytes.Length,nodes
    let canonicalSize value = canonicalMeasure value |> fst
    let digest value = encode (box value) |> fst |> SHA256.HashData |> Convert.ToHexStringLower
    let private orderedWorks works = works |> List.sortBy (fun (w: Requirements) -> w.Key)
    let authorityDigest (authority: AuthoritativeObligations) = digest (authority.Version, orderedWorks authority.Works, authority.Completeness.ExpectedWorkKeys, authority.Completeness.Sources |> List.sort, authority.Completeness.SourceBound)
    let snapshotDigest (snapshot: Snapshot) = digest { snapshot with Works = orderedWorks snapshot.Works; Profiles = snapshot.Profiles |> List.sortBy (fun p -> p.Id); Reservations = snapshot.Reservations |> List.sortBy (fun r -> r.Id) }
    let assignmentDigest (assignment: Assignment) = digest { assignment with AssignmentSha256 = "" }
    let bindAuthority (authority: AuthoritativeObligations) = { authority with Completeness = { authority.Completeness with ManifestSha256 = authorityDigest authority } }
    let bindAssignment (assignment: Assignment) = { assignment with AssignmentSha256 = assignmentDigest assignment }
    let knownNonnegative (resources: Resources) =
        resources.Count<=64 && (resources |> Map.forall (fun unitName value ->
            (Set.contains unitName (set ["cpu-millicores";"memory-bytes";"storage-bytes";"io-bytes-per-second";"task-slots"])
             || (unitName.StartsWith("quota:", StringComparison.Ordinal) && unitName.Split(':').Length=3))
            && (value |> Option.exists (fun n -> n>=0L))))
    let addResources (left: Resources) (right: Resources) =
        let keys = Set.union (left |> Map.keys |> Set.ofSeq) (right |> Map.keys |> Set.ofSeq)
        keys |> Seq.map (fun key ->
            let a = Map.tryFind key left |> Option.defaultValue (Some 0L)
            let b = Map.tryFind key right |> Option.defaultValue (Some 0L)
            let total = match a,b with Some x,Some y when x >= 0L && y >= 0L && x <= Int64.MaxValue-y -> Some(x+y) | _ -> None
            key,total) |> Map.ofSeq
    let pathsConflict (left: Set<string>) (right: Set<string>) =
        left |> Set.exists (fun a -> right |> Set.exists (fun b -> a=b || a.StartsWith(b+"/", StringComparison.Ordinal) || b.StartsWith(a+"/", StringComparison.Ordinal)))
    let private sha (s: string) = not (isNull s) && s.Length=64 && (s |> Seq.forall (fun c -> Char.IsAsciiHexDigit c && not (Char.IsUpper c)))
    let private safePath (s: string) = not (String.IsNullOrWhiteSpace s) && not (Path.IsPathRooted s) && not (s.Contains('\\')) && (s.Split('/') |> Array.forall (fun x -> x<>".." && x<>"." && x<>""))
    let private sourcesValid sources = sources |> List.forall (fun s -> sha s.Sha256 && not (String.IsNullOrWhiteSpace s.Revision) && safePath s.Path)
    let requirementsErrors (w: Requirements) =
        [ if String.IsNullOrWhiteSpace w.Key then "work-key-missing"
          if w.Workspace.Repositories.IsEmpty || w.Workspace.Repositories.Length>8 || w.Workspace.Components.IsEmpty || w.Workspace.Components.Length>16 then "workspace-bound"
          if w.Workspace.Repositories |> List.map _.Identity |> Set.ofList |> Set.count <> w.Workspace.Repositories.Length then "repository-conflict"
          if w.Workspace.Sources.IsEmpty || not (sourcesValid w.Workspace.Sources) then "workspace-source-evidence"
          if w.Workspace.Components |> List.exists (fun c -> not (safePath c.Root) || c.Sources.IsEmpty || not (sourcesValid c.Sources)) then "component-source-evidence"
          if w.Workspace.Sources |> List.exists (fun source -> not (w.Workspace.Repositories |> List.exists (fun repository -> repository.Identity=source.Repository && repository.Revision=source.Revision))) then "source-repository-revision-mismatch"
          if w.Workspace.Components |> List.exists (fun c -> not (Set.isSubset (Set.ofList c.Sources) (Set.ofList w.Workspace.Sources))) then "component-source-closure-missing"
          if w.Attempt |> Id.attemptValue = Guid.Empty then "attempt-missing"
          if w.Generation.IsNone then "generation-missing"
          if w.Workspace.Version<>"workspace-execution/1" || w.Assessment.Version<>"work-assessment/1" then "contract-version"
          if [w.Workspace.Workspace;w.Workspace.Provider;w.Assessment.Phase;w.Assessment.Outcome;w.Assessment.Objective;w.Assessment.Plan] |> List.exists String.IsNullOrWhiteSpace then "required-text-missing"
          match w.Identity with
          | Local(workspace,key) when String.IsNullOrWhiteSpace workspace || String.IsNullOrWhiteSpace key -> "local-identity-missing"
          | _ -> ()
          if w.Deadline.IsNone then "deadline-missing"
          if w.Deadline |> Option.exists (fun d -> d<=w.SelectedAt) then "deadline-invalid"
          match w.Recovery with
          | None -> "recovery-missing"
          | Some r when r.MaxAttempts<=0 || r.AttemptOrdinal<=0 || r.AttemptOrdinal>r.MaxAttempts || r.OriginalStartedAt>w.SelectedAt || w.Deadline<>Some r.OriginalDeadline -> "original-recovery-budget-invalid"
          | _ -> ()
          if not (knownNonnegative w.Demand) || w.Demand.IsEmpty then "hard-demand-unknown"
          if w.RequiredChecks.IsEmpty || w.RequiredOutputs.IsEmpty || w.RequiredReviews.IsEmpty then "acceptance-missing"
          if w.AllowedPaths.IsEmpty || not (w.AllowedPaths |> Set.forall safePath) then "write-scope-invalid"
          if w.Assessment.MissingFacts.Count>0 then "assessment-missing-facts"
          if w.Assessment.Difficulty<0 || w.Assessment.Difficulty>100 || w.Assessment.Uncertainty<0 || w.Assessment.Uncertainty>100 || w.Assessment.EffectRisk<0 || w.Assessment.EffectRisk>100 then "assessment-range"
          if [w.Dependencies.Count;w.RequiredChecks.Count;w.Capabilities.Count;w.Tools.Count;w.Skills.Count;w.Context.Count;w.RequiredOutputs.Count;w.RequiredReviews.Count] |> List.sum > 128 then "obligation-bound" ]
    let validateAuthority (authority: AuthoritativeObligations) =
        try
            canonicalSize authority |> ignore
            let errors =
                [ if authority.Version<>"ordinary-authority/1" then "authority-version"
                  if authority.Works.IsEmpty || authority.Works.Length>32 then "work-bound"
                  if authority.Works |> List.map _.Key |> Set.ofList |> Set.count <> authority.Works.Length then "duplicate-work"
                  if not authority.Completeness.SourceBound || authority.Completeness.Sources.IsEmpty || not (sourcesValid authority.Completeness.Sources) then "completeness-evidence"
                  if authority.Completeness.ExpectedWorkKeys <> (authority.Works |> List.map _.Key |> Set.ofList) then "authoritative-input-omitted"
                  if authority.Completeness.ManifestSha256 <> authorityDigest authority then "authority-digest-mismatch"
                  yield! authority.Works |> List.collect requirementsErrors ]
            if errors.IsEmpty then Ok authority else Error(NeedsEvidence errors)
        with :? ArgumentException as ex -> Error(NeedsDecomposition [ex.Message])
    let snapshotEvaluationBound (snapshot: Snapshot) =
        let _,nodes=encode (box snapshot)
        let alternatives=int64 snapshot.Works.Length * int64 snapshot.Profiles.Length
        let pathCount=snapshot.Works |> List.sumBy (fun w -> w.AllowedPaths.Count)
        let heldPathCount=snapshot.Reservations |> List.sumBy (fun r -> r.Paths.Count)
        // Each alternative is checked initially and at most eight selection passes.
        // Charge sixteen primitive operations per prefix/equality path pair, including
        // cross-products with every retained reservation and already selected work.
        let pathProofs=16L * 9L * int64 snapshot.Profiles.Length * int64 pathCount * (int64 pathCount+int64 heldPathCount)
        int64 nodes * (20L+4L*alternatives) + alternatives*2048L + pathProofs

    let validateSnapshot (snapshot: Snapshot) =
        try
            canonicalSize snapshot |> ignore
            let errors =
                [ if snapshot.Version<>policyVersion then "policy-version"
                  if not (sha snapshot.AuthoritySha256) then "authority-binding-missing"
                  if snapshot.Works.IsEmpty || snapshot.Works.Length>32 || snapshot.Profiles.IsEmpty || snapshot.Profiles.Length>8 || snapshot.Reservations.Length>64 then "population-bound"
                  if snapshot.Works |> List.sumBy (fun w -> w.Dependencies.Count)>256 then "dependency-bound"
                  if snapshot.Works |> List.map _.Key |> Set.ofList |> Set.count <> snapshot.Works.Length then "duplicate-work"
                  if snapshot.Profiles |> List.map _.Id |> Set.ofList |> Set.count <> snapshot.Profiles.Length then "duplicate-profile"
                  if not snapshot.ReservationPopulationComplete then "reservation-population-incomplete"
                  if not (knownNonnegative snapshot.Capacity) || snapshot.Capacity.IsEmpty || not (knownNonnegative snapshot.RecoveryHeadroom) then "hard-capacity-unknown"
                  if snapshot.Reservations |> List.exists (fun r -> not (knownNonnegative r.Demand)) then "held-demand-unknown"
                  if snapshot.Reservations |> List.map _.Id |> Set.ofList |> Set.count <> snapshot.Reservations.Length then "duplicate-reservation"
                  if (snapshot.Reservations |> List.map _.Id |> Set.ofList) <> (snapshot.Project.Reservations |> Map.keys |> Set.ofSeq) then "scheduler-reservation-population-mismatch"
                  if snapshot.Reservations |> List.exists (fun r -> match r.Work with Board work -> Map.tryFind r.Id snapshot.Project.Reservations<>Some work | Local _ -> true) then "scheduler-reservation-work-mismatch"
                  if snapshot.Project.Capacity<=0 || snapshot.Project.RecoveryCapacity<=0 || snapshot.Project.RecoveryCapacity>=snapshot.Project.Capacity || snapshot.Project.Reservations.Count>snapshot.Project.Capacity then "scheduler-capacity-invalid"
                  if snapshot.Project.Paused || not (SchedulerReservation.recoveryCapacityPreserved snapshot.Project) then "scheduler-unavailable"
                  if snapshot.Reservations |> List.exists (fun r -> r.Paths.IsEmpty || not (r.Paths |> Set.forall safePath) || r.Demand.IsEmpty) then "held-scope-or-demand-missing"
                  yield! snapshot.Works |> List.collect requirementsErrors
                  for p in snapshot.Profiles do
                      if not (knownNonnegative p.AdditionalDemand) then "profile-demand-unknown"
                      if [p.Id;p.Version;p.Requested.Model;p.Requested.Effort;p.Requested.Runtime] |> List.exists String.IsNullOrWhiteSpace then "profile-configuration-missing"
                      if p.Capabilities |> Map.exists (fun _ o -> o.Evidence |> Option.exists (fun e -> not (sourcesValid [e]))) then "capability-evidence-invalid"
                      match p.Economics with
                      | Some e when e.CostMicroUnits<0L || e.DurationMilliseconds<0L || e.ReworkMilliseconds<0L -> "economics-negative"
                      | _ -> () ]
            // Conservative whole-call accounting: canonical-node traversals (including
            // validation/hash/result traversals), alternative proofs and selected pair
            // comparisons. Charging the full snapshot per alternative overcounts the
            // smaller assignment graph; no partially checked result escapes the bound.
            if snapshotEvaluationBound snapshot>int64 maximumEvaluations then invalidArg "snapshot" "whole-call-evaluation-bound"
            snapshotDigest snapshot |> ignore
            if errors.IsEmpty then Ok snapshot else Error(NeedsEvidence errors)
        with :? ArgumentException as ex -> Error(NeedsDecomposition [ex.Message])
