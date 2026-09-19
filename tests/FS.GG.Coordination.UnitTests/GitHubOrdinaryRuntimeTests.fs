#nowarn "3391"

module FS.GG.Coordination.GitHubOrdinaryRuntimeTests

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.GitHub

let private sha character = String.replicate 40 character

let private response status body headers =
    Response
        {
            StatusCode = status
            Headers = headers
            Body = body
            ETag = None
            RateBudget = { Limit = None; Remaining = None; ResetAt = None; Cost = Some 1 }
        }

type private ProviderState() =
    member val RepositoryId = 101L with get, set
    member val NodeId = "PR_kwDOordinary" with get, set
    member val HeadSha = sha "b" with get, set
    member val BaseSha = sha "a" with get, set
    member val PolicySha = sha "c" with get, set
    member val EpochSha = sha "9" with get, set
    member val Epoch = "OpenV2" with get, set
    member val EpochGeneration = 3L with get, set
    member val CheckConclusion = "success" with get, set
    member val JournalContent: string option = None with get, set
    member val JournalBlobSha = sha "e" with get, set
    member val JournalRefSha = sha "d" with get, set
    member val Merged = false with get, set
    member val MergeCommit = sha "f" with get, set
    member val Dispatches = 0 with get, set
    member val Mutations = 0 with get, set
    member val IncompleteChecks = false with get, set
    member val Outage = false with get, set
    member val UnknownDispatch = false with get, set
    member val JournalWrites = 0 with get, set
    member val LoseJournalAcknowledgementAt: int option = None with get, set
    member val UserPush = true with get, set
    member val InstallationRepositoryId = 101L with get, set
    member val InstallationReadStatus = 200 with get, set
    member val InstallationTotalCount = 1 with get, set

type private LoopbackTransport(state: ProviderState) =
    let json (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented = false))
    let encoded (value: string) = Convert.ToBase64String(Encoding.UTF8.GetBytes value)

    interface IOrdinaryGitHubTransport with
        member _.Send request =
            let rest = match request with Rest value -> value | GraphQL _ -> failwith "REST required"
            let path = rest.Uri.AbsolutePath
            if state.Outage && rest.Method = Get then NetworkFailure
            elif rest.Method = Get && path = "/repos/FS-GG/FS.GG.Coordination" then
                response 200 $"{{\"id\":{state.RepositoryId},\"full_name\":\"FS-GG/FS.GG.Coordination\",\"permissions\":{{\"push\":{state.UserPush.ToString().ToLowerInvariant()}}}}}" Map.empty
            elif rest.Method = Get && path = "/installation/repositories" then
                response state.InstallationReadStatus $"{{\"total_count\":{state.InstallationTotalCount},\"repositories\":[{{\"id\":{state.InstallationRepositoryId},\"full_name\":\"FS-GG/FS.GG.Coordination\"}}]}}" Map.empty
            elif rest.Method = Get && path = "/repos/FS-GG/FS.GG.Coordination/pulls/7" then
                response 200 $"{{\"node_id\":\"{state.NodeId}\",\"base\":{{\"ref\":\"main\",\"sha\":\"{state.BaseSha}\"}},\"head\":{{\"sha\":\"{state.HeadSha}\"}},\"merged\":{state.Merged.ToString().ToLowerInvariant()},\"merge_commit_sha\":\"{state.MergeCommit}\"}}" Map.empty
            elif rest.Method = Get && path.EndsWith("/git/ref/heads/policy", StringComparison.Ordinal) then
                response 200 $"{{\"object\":{{\"sha\":\"{state.PolicySha}\"}}}}" Map.empty
            elif rest.Method = Get && path.EndsWith("/git/ref/heads/epoch", StringComparison.Ordinal) then
                response 200 $"{{\"object\":{{\"sha\":\"{state.EpochSha}\"}}}}" Map.empty
            elif rest.Method = Get && path.Contains("/git/ref/heads/fsgg/v2/journal/operation/", StringComparison.Ordinal) then
                response 200 $"{{\"object\":{{\"sha\":\"{state.JournalRefSha}\"}}}}" Map.empty
            elif rest.Method = Get && path.EndsWith("/contents/epoch.json", StringComparison.Ordinal) then
                let epoch = $"{{\"complete\":true,\"generation\":{state.EpochGeneration},\"phase\":\"{state.Epoch}\"}}"
                response 200 $"{{\"content\":\"{encoded epoch}\"}}" Map.empty
            elif rest.Method = Get && path.EndsWith("/protection/required_status_checks", StringComparison.Ordinal) then
                response 200 "{\"checks\":[{\"context\":\"required\",\"app_id\":10}]}" Map.empty
            elif rest.Method = Get && path.EndsWith("/check-runs", StringComparison.Ordinal) then
                let headers =
                    if state.IncompleteChecks then Map.ofList [ "link", $"<https://loopback.invalid/repos/FS-GG/FS.GG.Coordination/commits/{state.HeadSha}/check-runs?per_page=100>; rel=\"next\"" ]
                    else Map.empty
                response 200 $"{{\"total_count\":1,\"check_runs\":[{{\"id\":1,\"name\":\"required\",\"status\":\"completed\",\"conclusion\":\"{state.CheckConclusion}\",\"app\":{{\"id\":10}}}}]}}" headers
            elif rest.Method = Get && path.Contains("/contents/ordinary/", StringComparison.Ordinal) then
                match state.JournalContent with
                | None -> response 404 "{}" Map.empty
                | Some content -> response 200 $"{{\"sha\":\"{state.JournalBlobSha}\",\"content\":\"{encoded content}\"}}" Map.empty
            elif rest.Method = Put && path.Contains("/contents/ordinary/", StringComparison.Ordinal) then
                use body = JsonDocument.Parse(rest.Body.Value)
                let root = body.RootElement
                let mutable supplied = Unchecked.defaultof<JsonElement>
                let hasSha = root.TryGetProperty("sha", &supplied)
                if state.JournalContent.IsSome && (not hasSha || supplied.GetString() <> state.JournalBlobSha) then
                    response 409 "{}" Map.empty
                elif state.JournalContent.IsNone && hasSha then
                    response 409 "{}" Map.empty
                else
                    state.JournalContent <- Some(Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("content").GetString())))
                    state.JournalBlobSha <- sha (string ((int state.JournalBlobSha[0] + 1) % 10))
                    state.Mutations <- state.Mutations + 1
                    state.JournalWrites <- state.JournalWrites + 1
                    if state.LoseJournalAcknowledgementAt = Some state.JournalWrites then NetworkFailure
                    else response 201 "{}" Map.empty
            elif rest.Method = Put && path.EndsWith("/pulls/7/merge", StringComparison.Ordinal) then
                state.Dispatches <- state.Dispatches + 1
                state.Merged <- true
                if state.UnknownDispatch then NetworkFailure
                else response 200 $"{{\"merged\":true,\"sha\":\"{state.MergeCommit}\"}}" Map.empty
            else failwith $"unmatched loopback request: {rest.Method} {rest.Uri}"

let private options =
    {
        ApiBase = Uri "https://loopback.invalid/"
        Token = "loopback-only"
        UserAgent = "fsgg-coordination-tests"
        Repository = "FS-GG/FS.GG.Coordination"
        PullRequestNumber = 7
        PolicyRef = "heads/policy"
        EpochRepository = "FS-GG/.github"
        EpochRef = "heads/epoch"
        EpochPath = "epoch.json"
        JournalRepository = "FS-GG/FS.GG.Coordination"
    }

let private runtime state = OrdinaryGitHubRuntime.Runtime(options, LoopbackTransport state) :> IOrdinaryDeliveryRuntime

let private plan state =
    let provider = runtime state
    let observed = provider.Observe() |> Result.defaultWith failwith
    OrdinaryDelivery.plan observed |> Result.defaultWith (sprintf "%A" >> failwith) |> snd

[<Fact>]
let ``installation token authorizes selected repository despite false user push`` () =
    let state = ProviderState()
    state.UserPush <- false
    Assert.True((runtime state).Observe() |> Result.defaultWith failwith |> fun observed -> observed.Authorized)
    Assert.NotEmpty(plan state)

[<Fact>]
let ``installation token refuses a different repository or unreadable selection`` () =
    let state = ProviderState()
    state.UserPush <- false
    state.InstallationRepositoryId <- 999L
    Assert.False((runtime state).Observe() |> Result.defaultWith failwith |> fun observed -> observed.Authorized)
    state.InstallationReadStatus <- 403
    Assert.Equal(Error "github-read:403", (runtime state).Observe() |> Result.map (fun _ -> ""))

[<Fact>]
let ``installation token refuses incomplete repository selection`` () =
    let state = ProviderState()
    state.UserPush <- false
    state.InstallationTotalCount <- 2
    Assert.Equal(Error "installation-pagination-incomplete", (runtime state).Observe() |> Result.map (fun _ -> ""))

[<Fact>]
let ``provider composition refuses before OpenV2 without mutations`` () =
    let state = ProviderState()
    state.Epoch <- "OperatingV1"
    let bytes = plan state
    Assert.Equal(Error [ PreOpenV2Refusal ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    Assert.Equal(0, state.Mutations)
    Assert.Equal(0, state.Dispatches)

[<Theory>]
[<InlineData("intent")>]
[<InlineData("dispatch")>]
[<InlineData("effect")>]
let ``fresh runtime recovers crash cuts with one merge`` cutName =
    let state = ProviderState()
    let bytes = plan state
    let cut, expected =
        match cutName with
        | "intent" -> StopAfterIntent, "before-dispatch"
        | "dispatch" -> StopAfterDispatch, "after-dispatch-before-response"
        | _ -> StopAfterEffect, "after-effect-before-receipt"
    Assert.Equal(Ok(AdvanceInterrupted expected), OrdinaryDelivery.advance bytes cut (runtime state))
    Assert.Equal(Ok(AdvanceSettled state.MergeCommit), OrdinaryDelivery.advance bytes NoCut (runtime state))
    Assert.Equal(Ok(AdvanceAlreadySettled state.MergeCommit), OrdinaryDelivery.advance bytes NoCut (runtime state))
    Assert.Equal(1, state.Dispatches)

[<Fact>]
let ``unknown merge response is pending and restart readback prevents duplicate`` () =
    let state = ProviderState()
    let bytes = plan state
    state.UnknownDispatch <- true
    Assert.Equal(Ok(AdvancePending "dispatch-outcome-unknown"), OrdinaryDelivery.advance bytes NoCut (runtime state))
    state.UnknownDispatch <- false
    Assert.Equal(Ok(AdvanceSettled state.MergeCommit), OrdinaryDelivery.advance bytes NoCut (runtime state))
    Assert.Equal(1, state.Dispatches)

[<Theory>]
[<InlineData(1, "intent-outcome-unknown")>]
[<InlineData(2, "pending-journal-outcome-unknown")>]
[<InlineData(3, "settlement-outcome-unknown")>]
let ``lost journal acknowledgement replays durable authority without duplicate merge`` writeNumber pendingReason =
    let state = ProviderState()
    let bytes = plan state
    state.LoseJournalAcknowledgementAt <- Some writeNumber

    let first = OrdinaryDelivery.advance bytes NoCut (runtime state)
    Assert.Equal(sprintf "%A" (Ok(AdvancePending pendingReason)), sprintf "%A" first)
    state.LoseJournalAcknowledgementAt <- None
    let recovered = OrdinaryDelivery.advance bytes NoCut (runtime state)
    if writeNumber = 3 then Assert.Equal(Ok(AdvanceAlreadySettled state.MergeCommit), recovered)
    else Assert.Equal(Ok(AdvanceSettled state.MergeCommit), recovered)
    Assert.Equal(Ok(AdvanceAlreadySettled state.MergeCommit), OrdinaryDelivery.advance bytes NoCut (runtime state))
    Assert.Equal(1, state.Dispatches)

[<Fact>]
let ``native completion is reconciled from pending journal without another dispatch`` () =
    let state = ProviderState()
    let bytes = plan state
    let first = runtime state
    Assert.Equal(Ok(AdvanceInterrupted "before-dispatch"), OrdinaryDelivery.advance bytes StopAfterIntent first)
    state.Merged <- true

    Assert.Equal(Ok(AdvanceSettled state.MergeCommit), OrdinaryDelivery.advance bytes NoCut (runtime state))
    Assert.Equal(0, state.Dispatches)
    Assert.Equal(Ok(AdvanceAlreadySettled state.MergeCommit), OrdinaryDelivery.advance bytes NoCut (runtime state))

[<Fact>]
let ``provider negatives preserve unknown and immutable decision facts`` () =
    let state = ProviderState()
    let bytes = plan state

    state.PolicySha <- sha "4"
    Assert.Equal(Error [ StalePolicy ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    state.PolicySha <- sha "c"
    state.BaseSha <- sha "6"
    Assert.Equal(Error [ ChangedSource ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    state.BaseSha <- sha "a"
    state.EpochGeneration <- 4L
    Assert.Equal(Error [ StaleEpoch ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    state.EpochGeneration <- 3L
    state.HeadSha <- sha "5"
    Assert.Equal(Error [ ChangedSource ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    state.HeadSha <- sha "b"
    state.CheckConclusion <- "failure"
    Assert.Equal(Error [ ChangedSource ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    state.CheckConclusion <- "success"
    state.NodeId <- "PR_wrong"
    Assert.Equal(Error [ CrossSubjectObservation ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    state.NodeId <- "PR_kwDOordinary"
    state.IncompleteChecks <- true
    Assert.Equal(Error [ JournalUnavailable "check-pagination-incomplete" ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    state.IncompleteChecks <- false
    state.Outage <- true
    Assert.Equal(Error [ JournalUnavailable "network-failure" ], OrdinaryDelivery.advance bytes NoCut (runtime state))

[<Fact>]
let ``competing provider generation cannot reserve the planned operation`` () =
    let state = ProviderState()
    let bytes = plan state
    let competingDigest = sha "0"
    state.JournalContent <- Some $"{{\"generation\":1,\"mergeCommit\":null,\"operationId\":\"competing\",\"planDigest\":\"{competingDigest}\",\"schema\":\"fsgg.coordination.ordinary-delivery-journal/1\",\"stage\":\"intent-persisted\"}}"
    Assert.Equal(Error [ JournalConflict ], OrdinaryDelivery.advance bytes NoCut (runtime state))
    Assert.Equal(0, state.Dispatches)
