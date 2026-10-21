namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
open FS.GG.Coordination.Core
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrdinaryAssignment
open FS.GG.Coordination.Orchestration.Pilot

/// Informational correspondence only. This module never calls Main preparation.
module OrdinaryAssignmentProjection =
    type SuppliedPlanningBinding = {
        ProjectId: ProjectId; WorkflowRevision: WorkflowRevision; PlanningSnapshotSha256: string
        ProcessOperationId: OperationId; ExecutorBinding: string; InputMediaType: string; Source: Source }
    type SuppliedIdentityContext = {
        WorkItem: WorkItemId; Principal: string; ExpectedPrincipal: string
        JournalWorkItem: WorkItemId; JournalAttempt: AttemptId; JournalGeneration: Generation
        JournalSource: Source }
    type InputManifest = { Sha256: string; SizeBytes: int64 }
    type FieldMatch = { Field: string; Provenance: string }
    type Mismatch = { Field: string; Reason: string }
    type Residual = { Obligation: string; Owner: string; Reason: string }
    type InformationalCompleteness = FieldCorrespondenceWithResiduals | Mismatched | UnsupportedProtocol
    type CorrespondenceReport = {
        MatchedFields: FieldMatch list; Mismatches: Mismatch list; ResidualObligations: Residual list
        Completeness: InformationalCompleteness }

    let report (candidate: OrdinaryAssignment.Assignment) (request: MainAdmissionPreparationRequest)
               (planning: SuppliedPlanningBinding) (identity: SuppliedIdentityContext option)
               (inputBytes: byte array) (manifest: InputManifest option) =
        let matched = ResizeArray<FieldMatch>()
        let mismatches = ResizeArray<Mismatch>()
        let residuals = ResizeArray<Residual>()
        let compare field provenance condition =
            if condition then matched.Add {Field=field;Provenance=provenance}
            else mismatches.Add {Field=field;Reason="source-field-correspondence-mismatch"}
        let residual obligation owner reason = residuals.Add {Obligation=obligation;Owner=owner;Reason=reason}
        let spec = candidate.Specification
        let work = spec.Requirements
        compare "AssignmentSha256" "ordinary canonical assignment" (candidate.AssignmentSha256=assignmentDigest candidate)
        compare "Schema" "actual Main request protocol" (request.Schema=MainAdmissionPreparer.schema)
        compare "ProjectId" "supplied planning source" (request.ProjectId=Id.projectValue planning.ProjectId)
        compare "WorkflowRevision" "supplied planning source" (request.WorkflowRevision=Id.revisionValue planning.WorkflowRevision)
        compare "CanonicalSha256" "planning snapshot digest; NOT input digest" (request.CanonicalSha256=planning.PlanningSnapshotSha256)
        compare "SelectedAt" "original ordinary selection" (request.SelectedAt=work.SelectedAt)
        compare "AttemptId" "ordinary attempt" (request.AttemptId=Id.attemptValue work.Attempt)
        compare "ProcessOperationId" "supplied process operation; Main launch AssignmentId" (request.ProcessOperationId=Id.operationValue planning.ProcessOperationId)
        compare "ReservationId" "ordinary reservation reference; no allocation proof" (request.ReservationId=Id.reservationValue spec.Reservation)
        compare "Workspace" "workspace contract" (request.Workspace=work.Workspace.Workspace)
        compare "AllowedPaths" "authorized ordinary paths" (not (isNull request.AllowedPaths) && Set.ofArray request.AllowedPaths=work.AllowedPaths && request.AllowedPaths.Length=work.AllowedPaths.Count)
        compare "Validations" "source-bound required checks" (not (isNull request.Validations) && Set.ofArray request.Validations=work.RequiredChecks && request.Validations.Length=work.RequiredChecks.Count)
        compare "RequestedModel" "requested profile; not resolved configuration" (request.RequestedModel=spec.Requested.Model)
        compare "RequestedEffort" "requested profile; not observed configuration" (request.RequestedEffort=spec.Requested.Effort)
        compare "ExecutorBinding" "supplied runner preparation source" (request.ExecutorBinding=planning.ExecutorBinding)
        compare "InputMediaType" "supplied runner preparation source" (request.InputMediaType=planning.InputMediaType)
        let mutable unsupported = false
        match work.Workspace.Repositories with
        | [repository] ->
            compare "RepositoryBinding" "single repository contract" (request.RepositoryBinding=repository.Identity)
            compare "BaselineObjectId" "single repository original revision" (request.BaselineObjectId=repository.Revision)
        | _ ->
            unsupported <- true
            residual "all repository/component revisions" "runner protocol" "current Main request supports one repository; full ordinary contract is retained"
        match work.Identity with
        | Local _ ->
            unsupported <- true
            residual "local work identity" "work-item preparation / runner protocol" "current Main preparation requires a board WorkItemId; no identity is manufactured"
        | Board expected ->
            match identity with
            | None -> residual "work identity, principal and generation correspondence" "work-item / journal preparation" "separate prepare arguments and retained journal evidence were not supplied"
            | Some supplied ->
                compare "prepare.WorkItemId" "separately supplied prepare argument" (expected=supplied.WorkItem && expected=supplied.JournalWorkItem)
                compare "prepare.Principal" "separately supplied prepare argument" (not (String.IsNullOrWhiteSpace supplied.ExpectedPrincipal) && supplied.Principal=supplied.ExpectedPrincipal)
                compare "journal.Generation" "explicit retained journal evidence; not a request field" (work.Generation=Some supplied.JournalGeneration && work.Attempt=supplied.JournalAttempt)
        if isNull inputBytes || inputBytes.Length=0 || inputBytes.Length>1024*1024 then
            mismatches.Add {Field="input.Bytes";Reason="actual Main input byte bound"}
        else
            let actual = SHA256.HashData inputBytes |> Convert.ToHexStringLower
            match manifest with
            | Some declared -> compare "input.Manifest" "digest and length recomputed from actual bytes" (declared.Sha256=actual && declared.SizeBytes=inputBytes.LongLength)
            | None -> residual "input manifest binding" "runner preparation" ("actual bytes hash="+actual+"; no independent ordinary manifest supplied")
        let budget = SubscriptionPilot.createBudget request.SelectedAt
        residual "original deadlines and retry budget" "budget / admission" (sprintf "Main derives fixed %d attempt(s), %d ms runtime, execution %O and delivery %O from SelectedAt; ordinary deadlines remain mandatory" budget.AttemptLimit (int64 budget.MaximumRuntime.TotalMilliseconds) budget.ExecutionDeadline budget.DeliveryDeadline)
        residual "resource vectors and held recovery reservations" "budget / admission" "request does not encode the ordinary resource vector or prove reservation admission"
        residual "mandatory context, skills, tools and effects" "runner preparation" "request carries requested model/effort; no general enforcing specification manifest"
        residual "resolved and observed worker configuration" "runner preparation" "requested values do not establish resolution or observation"
        residual "required outputs, reviews and outcome" "acceptance" "Validations are requested commands, not accepted results or review evidence"
        residual "journal custody and generation freshness" "work-item / journal preparation" "supplied correspondence does not recover or admit actual durable state"
        residual "route, claim, candidate, branch, runner, session and execution reservation identifiers" "Main route preparation" "route-owned operation identities remain distinct from ordinary assignment identity; no admission is inferred"
        {MatchedFields=List.ofSeq matched;Mismatches=List.ofSeq mismatches;ResidualObligations=List.ofSeq residuals
         Completeness=if unsupported then UnsupportedProtocol elif mismatches.Count>0 then Mismatched else FieldCorrespondenceWithResiduals}
