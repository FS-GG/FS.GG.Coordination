#load "../src/FS.GG.Coordination.GitHub/IssueFields.fs"
#load "../src/FS.GG.Coordination.GitHub/ProjectAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubProjectAdapterQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let fixturePath = Path.Combine(root, "tests/fixtures/github-project-adapter/contract.json")
if not (File.Exists fixturePath) then fail "GPAQ-FIXTURE-MISSING" fixturePath
let fixture = JsonDocument.Parse(File.ReadAllBytes fixturePath)
let json = fixture.RootElement
let exactNames = json.EnumerateObject() |> Seq.map _.Name |> Seq.toList
if exactNames <> [ "controls"; "generated"; "schema"; "synthetic" ] then fail "GPAQ-FIXTURE-SHAPE" (String.concat "," exactNames)
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-project-adapter-fixture/1" then fail "GPAQ-FIXTURE-SCHEMA" fixturePath
if not (json.GetProperty("synthetic").GetBoolean()) then fail "GPAQ-FIXTURE-PROVENANCE" "Q3 fixture must disclose synthetic provenance"
let required = GitHubProjectAdapterQualification.requiredControls |> List.map GitHubProjectAdapterQualification.controlId
let fixtureControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if fixtureControls <> required then fail "GPAQ-FIXTURE-INVENTORY" (String.concat "," fixtureControls)

let liveId value = LiveId.tryCreate value |> Result.defaultWith (fail "GPAQ-ID")
let semantic value = SemanticName.tryCreate value |> Result.defaultWith (fail "GPAQ-NAME")
let repo owner name = { Owner = owner; Name = name }
let item project itemId repository number content archived =
    { ProjectId = liveId project; ItemId = liveId itemId; Content = RepositoryIssue(repository, number, liveId content); Archived = archived }
let page number terminal items = { Number = number; TerminalPage = terminal; Items = items }
let complete revision pages = ProjectComplete(revision, pages)
let snapshot observation = ProjectAdapter.readProject observation |> Result.defaultWith (fail "GPAQ-SNAPSHOT" << sprintf "%A")
let result red green control: GitHubProjectAdapterControlResult = { Control = control; MutationRed = red; BaselineGreen = green }
let statusOption id name = { Id = liveId id; Name = semantic name }
let statusObservation revision project item selected =
    let options = [ statusOption "STATUS_READY" "Ready"; statusOption "STATUS_BACKLOG" "Backlog" ]
    let field = { ProjectId = liveId project; ItemId = liveId item; FieldId = liveId "STATUS_FIELD"; FieldName = semantic "Status"; Options = options; SelectedOptionId = selected |> Option.map liveId }
    StatusComplete(revision, { PageCount = 1; NodeCount = 1; TerminalPage = true }, [ field ])

let controls revision causation projectId itemId contentId unrelatedItemId unrelatedContentId repository =
    let main = item projectId itemId repository 1 contentId false
    let unrelated = item projectId unrelatedItemId repository 2 unrelatedContentId false
    let baselineObservation = complete revision [ page 1 true [ main; unrelated ] ]
    let baseline = snapshot baselineObservation
    let baselineStatus = ProjectAdapter.readStatus (liveId projectId) (liveId itemId) (statusObservation revision projectId itemId (Some "STATUS_READY")) |> Result.defaultWith (fail "GPAQ-STATUS" << sprintf "%A")
    let statusPlan = match ProjectAdapter.planStatus revision causation (SetStatus(liveId "STATUS_BACKLOG")) baselineStatus with Ok(StatusPlanned value) -> value | value -> fail "GPAQ-STATUS-PLAN" (sprintf "%A" value)
    [ result
          (ProjectAdapter.readProject (complete revision [ page 2 true [ main; unrelated ] ]) = Error InvalidProjectPageChain)
          (ProjectAdapter.readProject baselineObservation |> Result.isOk)
          GitHubProjectAdapterControl.Pagination
      let archived = { main with Archived = true }
      let archivedSnapshot = snapshot (complete revision [ page 1 true [ archived; unrelated ] ])
      result
          (match ProjectAdapter.planMembership revision causation repository (EnsureMember(liveId projectId, liveId contentId)) archivedSnapshot with Error(MembershipMutationIneligible(ArchivedMembership _)) -> true | _ -> false)
          (match ProjectAdapter.resolveMembership repository (liveId contentId) baseline with Ok(ActiveMembership _) -> true | _ -> false)
          GitHubProjectAdapterControl.ArchivedItem
      let duplicate = { main with ItemId = liveId (itemId + "_DUP") }
      result
          (ProjectAdapter.readProject (complete revision [ page 1 true [ main; duplicate ] ]) = Error(DuplicateProjectContent(liveId contentId)))
          (ProjectAdapter.readProject baselineObservation |> Result.isOk)
          GitHubProjectAdapterControl.DuplicateItem
      let externalRepo = repo "External" repository.Name
      let external = item projectId itemId externalRepo 1 contentId false
      let externalSnapshot = snapshot (complete revision [ page 1 true [ external; unrelated ] ])
      result
          (match ProjectAdapter.planMembership revision causation repository (EnsureMember(liveId projectId, liveId contentId)) externalSnapshot with Error(MembershipMutationIneligible(ExternalRepositoryMembership _)) -> true | _ -> false)
          (match ProjectAdapter.resolveMembership repository (liveId contentId) baseline with Ok(ActiveMembership _) -> true | _ -> false)
          GitHubProjectAdapterControl.ExternalItem
      let draftItem = { ProjectId = liveId projectId; ItemId = liveId itemId; Content = DraftIssue(liveId contentId); Archived = false }
      let draftSnapshot = snapshot (complete revision [ page 1 true [ draftItem; unrelated ] ])
      result
          (match ProjectAdapter.planMembership revision causation repository (EnsureMember(liveId projectId, liveId contentId)) draftSnapshot with Error(MembershipMutationIneligible(DraftMembership _)) -> true | _ -> false)
          (match ProjectAdapter.resolveMembership repository (liveId contentId) baseline with Ok(ActiveMembership _) -> true | _ -> false)
          GitHubProjectAdapterControl.DraftItem
      result
          (match ProjectAdapter.readProject (ProjectIncomplete("truncated", Some "cursor")) with Error(ProjectObservationRefused(ObservationIncomplete _)) -> true | _ -> false)
          (match ProjectAdapter.resolveMembership repository (liveId "ABSENT") baseline with Ok MissingMembership -> true | _ -> false)
          GitHubProjectAdapterControl.MissingItem
      result
          (ProjectAdapter.readProject (ProjectUnreadable "transport") = Error(ProjectObservationUnreadable "transport"))
          (ProjectAdapter.readProject baselineObservation |> Result.isOk)
          GitHubProjectAdapterControl.UnreadableObservation
      result
          (ProjectAdapter.planStatus (revision + "-stale") causation (SetStatus(liveId "STATUS_BACKLOG")) baselineStatus = Error(StatusStaleExpectedRevision revision))
          (ProjectAdapter.planStatus revision causation (SetStatus(liveId "STATUS_BACKLOG")) baselineStatus |> Result.isOk)
          GitHubProjectAdapterControl.StaleRevision
      result
          (match ProjectAdapter.checkStatusPreState statusPlan (statusObservation revision projectId itemId None) with Error ConcurrentStatusChange -> true | _ -> false)
          (ProjectAdapter.checkStatusPreState statusPlan (statusObservation revision projectId itemId (Some "STATUS_READY")) |> Result.isOk)
          GitHubProjectAdapterControl.ConcurrentChange
      result
          (match ProjectAdapter.planStatus revision causation (SetStatus(liveId "STATUS_READY")) baselineStatus with Ok(StatusNoOp _) -> true | _ -> false)
          (match ProjectAdapter.planStatus revision causation (SetStatus(liveId "STATUS_BACKLOG")) baselineStatus with Ok(StatusPlanned _) -> true | _ -> false)
          GitHubProjectAdapterControl.NoOpMutation ]

let generatedResults () =
    let data = json.GetProperty("generated")
    controls
        (data.GetProperty("revision").GetString())
        (data.GetProperty("causation").GetString())
        (data.GetProperty("projectId").GetString())
        (data.GetProperty("itemId").GetString())
        (data.GetProperty("contentId").GetString())
        (data.GetProperty("unrelatedItemId").GetString())
        (data.GetProperty("unrelatedContentId").GetString())
        (repo (data.GetProperty("repositoryOwner").GetString()) (data.GetProperty("repositoryName").GetString()))

// Separately authored identities and revisions prevent the fixture producer from proving itself.
let independentResults () =
    controls "independent-rev-23" "independent-cause" "P_INDEPENDENT" "ITEM_INDEPENDENT" "CONTENT_INDEPENDENT" "ITEM_OTHER_INDEPENDENT" "CONTENT_OTHER_INDEPENDENT" (repo "Independent" "Repository")

let generated = generatedResults ()
let independent = independentResults ()
match GitHubProjectAdapterQualification.validate generated independent with
| Ok () -> printfn "github-project-adapter-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings ->
    findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message)
    fail "GPAQ-FAILED" $"{findings.Length} finding(s)"
fixture.Dispose()
