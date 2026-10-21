namespace FS.GG.Coordination.Orchestration.Host.Tests

open System
open System.Text
open System.Security.Cryptography
open FS.GG.Coordination.Core
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrdinaryAssignment
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.Host.OrdinaryAssignmentProjection
open OrdinaryAssignmentFixtures
open Xunit

module OrdinaryAssignmentProjectionFixtures =
    type Fixture = {
        Candidate: OrdinaryAssignment.Assignment; Request: MainAdmissionPreparationRequest; Planning: SuppliedPlanningBinding
        Identity: SuppliedIdentityContext option; Bytes: byte array; Manifest: InputManifest option }
    let guid n = Guid.Parse(sprintf "30000000-0000-0000-0000-%012d" n)
    let fixture () =
        let a=authority [framework]
        let s=snapshot a
        let candidate=(OrdinaryAssignmentPolicy.select s |> accepted).Selected.Head
        let bytes=Encoding.UTF8.GetBytes "source-bound ordinary input; planning digest is distinct"
        let planning : SuppliedPlanningBinding = {
            ProjectId=s.Project.ProjectId;WorkflowRevision=Id.revision 3L;PlanningSnapshotSha256=String.replicate 64 "a"
            ProcessOperationId=Id.operation(guid 1);ExecutorBinding="fixture-executor/1";InputMediaType="text/plain"
            Source=source "src/FS.GG.Coordination.Orchestration.Host/MainAdmissionPreparer.fs" "be3c87d45c119a5f81912d435ac72216e5766730cf7c6691a954694dbded3df2" }
        let request : MainAdmissionPreparationRequest = {
            Schema=MainAdmissionPreparer.schema;PreparationId=guid 2;ProjectId=Id.projectValue planning.ProjectId
            WorkflowRevision=3L;CanonicalSha256=planning.PlanningSnapshotSha256;SelectedAt=now
            RouteId=guid 3;AttemptId=Id.attemptValue framework.Attempt;CandidateId=guid 4;BranchRef="refs/heads/ordinary-fixture"
            ClaimResourceId="fixture-claim";ClaimOperationId=guid 5;ProcessOperationId=guid 1;CandidateOperationId=guid 6
            BranchOperationId=guid 7;PullRequestOperationId=guid 8;MergeOperationId=guid 9;ReadbackOperationId=guid 10
            RunnerId=guid 11;RunnerFingerprintSha256=String.replicate 64 "b";SessionId=guid 12
            ReservationId=Id.reservationValue candidate.Specification.Reservation;ExecutionReservationId=guid 13
            RouteProviderRevision="fixture-provider/1";RouteEvidenceSha256=String.replicate 64 "c"
            RepositoryBinding=framework.Workspace.Repositories.Head.Identity;BaselineObjectId=revision
            Workspace=framework.Workspace.Workspace;AllowedPaths=Set.toArray framework.AllowedPaths
            Validations=Set.toArray framework.RequiredChecks;ExecutorBinding=planning.ExecutorBinding
            RequestedModel=candidate.Specification.Requested.Model;RequestedEffort=candidate.Specification.Requested.Effort
            InputMediaType=planning.InputMediaType }
        {Candidate=candidate;Request=request;Planning=planning
         Identity=Some {WorkItem=workItem;Principal="fixture-principal";ExpectedPrincipal="fixture-principal"
                        JournalWorkItem=workItem;JournalAttempt=framework.Attempt;JournalGeneration=framework.Generation.Value;JournalSource=sources.Head}
         Bytes=bytes;Manifest=Some {Sha256=SHA256.HashData bytes |> Convert.ToHexStringLower;SizeBytes=bytes.LongLength}}
    let report f = OrdinaryAssignmentProjection.report f.Candidate f.Request f.Planning f.Identity f.Bytes f.Manifest

open OrdinaryAssignmentProjectionFixtures

type OrdinaryAssignmentProjectionTests() =
    [<Fact>]
    member _.``actual Main request matches fields with explicit nonempty owned residuals``() =
        let f=fixture()
        let r=report f
        Assert.Empty(r.Mismatches)
        Assert.Equal(FieldCorrespondenceWithResiduals,r.Completeness)
        Assert.NotEmpty(r.MatchedFields)
        Assert.NotEmpty(r.ResidualObligations)
        Assert.Contains(r.MatchedFields,fun field -> field.Field="CanonicalSha256" && field.Provenance.Contains("NOT input"))
        Assert.Contains(r.ResidualObligations,fun residual -> residual.Obligation="original deadlines and retry budget" && residual.Reason.Contains("1800000 ms"))
        Assert.Contains(r.ResidualObligations,fun residual -> residual.Owner="acceptance")
        Assert.NotEqual<string>(f.Request.CanonicalSha256,f.Manifest.Value.Sha256)

    [<Fact>]
    member _.``each matched field mutation is detected at the actual request seam``() =
        let f=fixture()
        let mutations : (string * (Fixture -> Fixture)) list = [
            "AssignmentSha256",fun f -> {f with Candidate={f.Candidate with AssignmentSha256=String.replicate 64 "f"}}
            "Schema",fun f -> {f with Request={f.Request with Schema="wrong"}}
            "ProjectId",fun f -> {f with Request={f.Request with ProjectId=guid 99}}
            "WorkflowRevision",fun f -> {f with Request={f.Request with WorkflowRevision=4L}}
            "CanonicalSha256",fun f -> {f with Request={f.Request with CanonicalSha256=f.Manifest.Value.Sha256}}
            "SelectedAt",fun f -> {f with Request={f.Request with SelectedAt=now.AddSeconds 1.}}
            "AttemptId",fun f -> {f with Request={f.Request with AttemptId=guid 99}}
            "ProcessOperationId",fun f -> {f with Request={f.Request with ProcessOperationId=guid 99}}
            "ReservationId",fun f -> {f with Request={f.Request with ReservationId=guid 99}}
            "Workspace",fun f -> {f with Request={f.Request with Workspace="other-workspace"}}
            "AllowedPaths",fun f -> {f with Request={f.Request with AllowedPaths=[|"other/path"|]}}
            "Validations",fun f -> {f with Request={f.Request with Validations=[|"false"|]}}
            "RequestedModel",fun f -> {f with Request={f.Request with RequestedModel="other-model"}}
            "RequestedEffort",fun f -> {f with Request={f.Request with RequestedEffort="other-effort"}}
            "ExecutorBinding",fun f -> {f with Request={f.Request with ExecutorBinding="other-executor"}}
            "InputMediaType",fun f -> {f with Request={f.Request with InputMediaType="application/json"}}
            "RepositoryBinding",fun f -> {f with Request={f.Request with RepositoryBinding="other-repository"}}
            "BaselineObjectId",fun f -> {f with Request={f.Request with BaselineObjectId=String.replicate 40 "0"}}
            "prepare.WorkItemId",fun f -> {f with Identity=Some {f.Identity.Value with WorkItem=productItem}}
            "prepare.Principal",fun f -> {f with Identity=Some {f.Identity.Value with Principal="other-principal"}}
            "journal.Generation",fun f -> {f with Identity=Some {f.Identity.Value with JournalGeneration=Id.generation 2L}}
            "input.Manifest",fun f -> {f with Bytes=Encoding.UTF8.GetBytes "changed actual input"} ]
        Assert.Equal<Set<string>>(report f |> _.MatchedFields |> List.map _.Field |> Set.ofList, mutations |> List.map fst |> Set.ofList)
        for field,mutate in mutations do
            let r=report (mutate f)
            Assert.Equal(Mismatched,r.Completeness)
            Assert.Contains(r.Mismatches,fun mismatch -> mismatch.Field=field)

    [<Fact>]
    member _.``constructed multi repository and local work stay complete and unsupported``() =
        let f=fixture()
        let extra : Repository = {Identity="constructed/second-repository";Revision=String.replicate 40 "d"}
        let work={framework with Workspace={framework.Workspace with Repositories=framework.Workspace.Repositories @ [extra]}}
        let candidate=bindAssignment {f.Candidate with Specification={f.Candidate.Specification with Requirements=work}}
        let r=report {f with Candidate=candidate}
        Assert.Equal(UnsupportedProtocol,r.Completeness)
        Assert.Equal(2,candidate.Specification.Requirements.Workspace.Repositories.Length)
        Assert.Contains(r.ResidualObligations,fun residual -> residual.Obligation="all repository/component revisions")
        let local={work with Identity=Local("constructed-local-workspace","local-work-key")}
        let candidate=bindAssignment {candidate with Specification={candidate.Specification with Requirements=local}}
        let r=report {f with Candidate=candidate;Identity=None}
        Assert.Equal(UnsupportedProtocol,r.Completeness)
        Assert.Equal(Local("constructed-local-workspace","local-work-key"),candidate.Specification.Requirements.Identity)
        Assert.Contains(r.ResidualObligations,fun residual -> residual.Obligation="local work identity")

    [<Fact>]
    member _.``missing identity and manifest remain residual and forged manifests fail``() =
        let f=fixture()
        let r=report {f with Identity=None;Manifest=None}
        Assert.Equal(FieldCorrespondenceWithResiduals,r.Completeness)
        Assert.Contains(r.ResidualObligations,fun residual -> residual.Obligation="work identity, principal and generation correspondence")
        Assert.Contains(r.ResidualObligations,fun residual -> residual.Obligation="input manifest binding")
        let r=report {f with Manifest=Some {f.Manifest.Value with SizeBytes=1L}}
        Assert.Equal(Mismatched,r.Completeness)
        let r=report {f with Bytes=Array.empty}
        Assert.Contains(r.Mismatches,fun mismatch -> mismatch.Field="input.Bytes")
