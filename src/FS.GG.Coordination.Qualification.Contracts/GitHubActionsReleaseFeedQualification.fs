namespace FS.GG.Coordination.Qualification.Contracts

open System

[<RequireQualifiedAccess>]
type GitHubActionsReleaseFeedControl = RunAttempt | Rerun | CheckSuite | MergeGroup | Pagination | ImmutableRelease | AssetDeletion | AttestationSubject | PackageVersion | AuthenticatedFeed | PublicDownload | Redirect | ByteDigest | UploadResponse | Unauthorized | Unavailable | Incomplete | Stale
type GitHubActionsReleaseFeedControlResult = { Control: GitHubActionsReleaseFeedControl; MutationRed: bool; BaselineGreen: bool }
type GitHubActionsReleaseFeedFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubActionsReleaseFeedQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-actions-release-feed-qualification/1"
    let requiredControls = [ GitHubActionsReleaseFeedControl.RunAttempt; GitHubActionsReleaseFeedControl.Rerun; GitHubActionsReleaseFeedControl.CheckSuite; GitHubActionsReleaseFeedControl.MergeGroup; GitHubActionsReleaseFeedControl.Pagination; GitHubActionsReleaseFeedControl.ImmutableRelease; GitHubActionsReleaseFeedControl.AssetDeletion; GitHubActionsReleaseFeedControl.AttestationSubject; GitHubActionsReleaseFeedControl.PackageVersion; GitHubActionsReleaseFeedControl.AuthenticatedFeed; GitHubActionsReleaseFeedControl.PublicDownload; GitHubActionsReleaseFeedControl.Redirect; GitHubActionsReleaseFeedControl.ByteDigest; GitHubActionsReleaseFeedControl.UploadResponse; GitHubActionsReleaseFeedControl.Unauthorized; GitHubActionsReleaseFeedControl.Unavailable; GitHubActionsReleaseFeedControl.Incomplete; GitHubActionsReleaseFeedControl.Stale ]
    let controlId = function
        | GitHubActionsReleaseFeedControl.RunAttempt -> "run-attempt" | GitHubActionsReleaseFeedControl.Rerun -> "rerun"
        | GitHubActionsReleaseFeedControl.CheckSuite -> "check-suite" | GitHubActionsReleaseFeedControl.MergeGroup -> "merge-group"
        | GitHubActionsReleaseFeedControl.Pagination -> "pagination" | GitHubActionsReleaseFeedControl.ImmutableRelease -> "immutable-release"
        | GitHubActionsReleaseFeedControl.AssetDeletion -> "asset-deletion" | GitHubActionsReleaseFeedControl.AttestationSubject -> "attestation-subject"
        | GitHubActionsReleaseFeedControl.PackageVersion -> "package-version" | GitHubActionsReleaseFeedControl.AuthenticatedFeed -> "authenticated-feed"
        | GitHubActionsReleaseFeedControl.PublicDownload -> "public-download" | GitHubActionsReleaseFeedControl.Redirect -> "redirect"
        | GitHubActionsReleaseFeedControl.ByteDigest -> "byte-digest" | GitHubActionsReleaseFeedControl.UploadResponse -> "upload-response"
        | GitHubActionsReleaseFeedControl.Unauthorized -> "unauthorized" | GitHubActionsReleaseFeedControl.Unavailable -> "unavailable"
        | GitHubActionsReleaseFeedControl.Incomplete -> "incomplete" | GitHubActionsReleaseFeedControl.Stale -> "stale"
    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let findings producer results =
            let actual = results |> List.map (fun value -> controlId value.Control)
            let expectedText = String.concat "," expected
            let actualText = String.concat "," actual
            [ if actual <> expected then yield { Code = "GARFQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {actualText}" }
              for result in results do
                  if not result.MutationRed then yield { Code = $"GARFQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = controlId result.Control; Message = "mutation did not turn red" }
                  if not result.BaselineGreen then yield { Code = $"GARFQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = controlId result.Control; Message = "baseline did not remain green" } ]
        let all = findings "generated" generated @ findings "independent" independent
        if all.IsEmpty then Ok () else Error all
