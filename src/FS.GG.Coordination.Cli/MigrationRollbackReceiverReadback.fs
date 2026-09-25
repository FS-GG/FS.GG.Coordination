namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type DeclaredReceiverRollbackReadback =
    { PlanSeal: string
      CohortSha256: string
      StateSha256: string
      InventoryBound: bool
      Native: MigrationReceiverPinTwoPass }

[<RequireQualifiedAccess>]
module MigrationRollbackReceiverReadback =
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private stateSha256 (proof: MigrationReceiverPinTwoPass) =
        [ yield "fsgg.gs2-09.7.receiver-pin-declared-state/v1"
          yield proof.CohortSha256
          for snapshot in proof.First |> List.sortBy _.Receiver.ReceiverName do
              yield snapshot.Receiver.ReceiverName
              yield snapshot.PinSnapshotSha256 ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let captureDeclared expectedPlanSeal (plan: GitHubRollbackPlan) (cohort: GitHubMigrationCopyCohort)
        (pinsByReceiver: Map<string, MigrationReceiverPinDeclaration list>)
        (template: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        match GitHubRollbackPlanQualification.verify expectedPlanSeal plan with
        | Error _ -> Error "invalid:rollback-plan"
        | Ok _ when not (GitHubMigrationInspect.validCohort cohort) -> Error "invalid:receiver-cohort"
        | Ok _ ->
            let cohortSha = GitHubMigrationInspect.cohortSha256 cohort
            match plan.Steps |> List.tryFind (fun step -> step.Domain = ReceiverPin) with
            | None -> Error "invalid:receiver-pin-rollback-target"
            | Some step when step.TargetIdentity <> $"receiver-cohort:{cohortSha}" ->
                Error "invalid:receiver-pin-rollback-target"
            | Some step ->
                MigrationReceiverCapture.capturePinBytesTwoPass cohort pinsByReceiver template transport
                |> Result.bind (fun proof ->
                    if proof.InventoryBound || proof.CohortSha256 <> cohortSha
                       || proof.First.Length <> cohort.Receivers.Length
                       || (List.zip proof.First proof.Second
                           |> List.exists (fun (first, second) ->
                               first.Receiver.ReceiverName <> second.Receiver.ReceiverName
                               || first.PinSnapshotSha256 <> second.PinSnapshotSha256)) then
                        Error "invalid:receiver-pin-declared-proof"
                    else
                        let actual = stateSha256 proof
                        if actual <> step.CapturedStateSha256 then
                            Error "changed:receiver-pin-rollback-state"
                        else
                            Ok { PlanSeal=plan.Seal; CohortSha256=cohortSha
                                 StateSha256=actual; InventoryBound=false; Native=proof })
