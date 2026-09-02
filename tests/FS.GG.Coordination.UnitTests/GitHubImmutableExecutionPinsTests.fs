module FS.GG.Coordination.GitHubImmutableExecutionPinsTests

open System
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private revision = String.replicate 40 "a"
let private digest = String.replicate 64 "b"
let private action workflow =
    { WorkflowPath = workflow; TargetRepository = "actions/checkout"; TargetPath = None
      Revision = revision; Kind = ThirdPartyAction }
let private workflow path = { Path = path; Sha256 = digest; References = [ action path ] }
let private renovate =
    { Name = "renovate"; Automated = true; PullRequestOnly = true; DirectPush = false
      PolicyRepository = "FS-GG/.github"; PolicyRevision = revision; PolicyPath = "renovate.json"
      PolicySha256 = digest; OwnedManagers = [ "github-actions" ] }
let private snapshot =
    { SchemaVersion = 1; Repository = "FS-GG/example"; SourceRevision = revision
      PrerequisiteReceiptDigest = digest; Complete = true; Workflows = [ workflow ".github/workflows/ci.yml" ]
      Publications = []; UpdaterConfigurations = []; Updaters = [ renovate ]; RequiredManagers = [ "github-actions" ] }
let private compile value = GitHubImmutableExecutionPinsQualification.compile value
let private errors = function Error values -> values | Ok _ -> []

[<Fact>]
let ``full action pins and Renovate-only authority compile and replay exactly`` () =
    let report = compile snapshot |> Result.defaultWith (failwithf "%A")
    Assert.Equal(1, report.WorkflowCount)
    Assert.Equal(1, report.ReferenceCount)
    Assert.Equal("renovate", report.AutomatedUpdater)
    Assert.Equal(Ok report, GitHubImmutableExecutionPinsQualification.verify report.Seal snapshot)

[<Fact>]
let ``mutable action and source revisions fail closed`` () =
    let document = snapshot.Workflows.Head
    let mutableAction = { document.References.Head with Revision = "v4" }
    Assert.True(compile { snapshot with Workflows = [ { document with References = [ mutableAction ] } ] } |> Result.isError)
    Assert.True(compile { snapshot with SourceRevision = "main" } |> Result.isError)

[<Fact>]
let ``reusable workflow reference requires matching callable immutable publication`` () =
    let reusable =
        { WorkflowPath = snapshot.Workflows.Head.Path; TargetRepository = "FS-GG/.github"
          TargetPath = Some ".github/workflows/reusable.yml"; Revision = revision; Kind = ReusableWorkflow }
    let changed = { snapshot with Workflows = [ { snapshot.Workflows.Head with References = [ reusable ] } ] }
    Assert.Contains(MissingImmutablePublication, compile changed |> errors)
    let publication =
        { Repository = "FS-GG/.github"; Path = ".github/workflows/reusable.yml"; Revision = revision
          ContentSha256 = digest; WorkflowCall = true }
    Assert.True(compile { changed with Publications = [ publication ] } |> Result.isOk)
    Assert.Contains(PublicationIsNotReusableWorkflow, compile { changed with Publications = [ { publication with WorkflowCall = false } ] } |> errors)

[<Fact>]
let ``local execution references cannot evade the literal full SHA contract`` () =
    Assert.Equal(
        Error [ LocalExecutionReferenceNotImmutable ],
        GitHubImmutableExecutionPinsQualification.classifyReferenceLiteral "./.github/workflows/reusable.yml")
    Assert.Equal(
        Error [ LocalExecutionReferenceNotImmutable ],
        GitHubImmutableExecutionPinsQualification.classifyReferenceLiteral "./local-action")
    Assert.True(
        GitHubImmutableExecutionPinsQualification.classifyReferenceLiteral
            $"FS-GG/.github/.github/workflows/reusable.yml@{revision}"
        |> Result.isOk)

[<Fact>]
let ``Renovate is sole PR-only automated authority with complete manager ownership`` () =
    Assert.True(compile { snapshot with Updaters = [ { renovate with Name = "dependabot" } ] } |> Result.isError)
    Assert.True(compile { snapshot with Updaters = [ renovate; { renovate with Name = "custom" } ] } |> Result.isError)
    Assert.True(compile { snapshot with Updaters = [ { renovate with DirectPush = true; PullRequestOnly = false } ] } |> Result.isError)
    Assert.True(compile { snapshot with RequiredManagers = [ "github-actions"; "regex" ] } |> Result.isError)
    let dependabot = { Path = ".github/dependabot.yml"; Sha256 = digest; Authority = "dependabot" }
    Assert.Contains(CompetingUpdaterAuthority, compile { snapshot with UpdaterConfigurations = [ dependabot ] } |> errors)

[<Fact>]
let ``qualification requires every generated and independent control`` () =
    let passing =
        GitHubImmutableExecutionPinsQualification.requiredControls
        |> List.map (fun control ->
            { GitHubImmutableExecutionPinsControlResult.Control = control
              ControlPassed = true
              BaselineGreen = true })
    Assert.Equal(Ok(), GitHubImmutableExecutionPinsQualification.validate passing passing)
    let broken = passing |> List.filter (fun value -> value.Control <> RenovateSoleUpdater)
    match GitHubImmutableExecutionPinsQualification.validate passing broken with
    | Ok _ -> Assert.Fail("missing Renovate authority control was accepted")
    | Error findings -> Assert.Contains("IEP-CONTROL-MISSING", findings |> List.map _.Code)
