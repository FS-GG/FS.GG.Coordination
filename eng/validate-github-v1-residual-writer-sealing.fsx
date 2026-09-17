open System
open System.IO
open System.Text.Json

let fail code message = failwith $"{code}: {message}"

let args = fsi.CommandLineArgs |> Array.skip 1

let root, evidencePath =
    match args with
    | [| root |] ->
        Path.GetFullPath root,
        Path.Combine(Path.GetFullPath root, "evidence/github-substrate-v2/gs2-08-9/sealing-qualification.json")
    | [| root; evidence |] -> Path.GetFullPath root, Path.GetFullPath evidence
    | _ -> fail "GS2089-USAGE" "expected repository root and optional aggregate evidence path"

let read relative = File.ReadAllBytes(Path.Combine(root, relative))
let aggregateDocument = JsonDocument.Parse(File.ReadAllBytes evidencePath)
let aggregate = aggregateDocument.RootElement

let text (value: JsonElement) (name: string) = value.GetProperty(name).GetString()
let boolean (value: JsonElement) (name: string) = value.GetProperty(name).GetBoolean()
let integer (value: JsonElement) (name: string) = value.GetProperty(name).GetInt32()

let require condition code message =
    if not condition then fail code message

require
    (text aggregate "schema" = "fsgg.github-substrate.v1-residual-writer-sealing/1"
     && text aggregate "unit" = "GS2-08.9"
     && text aggregate "state" = "pending-external-evidence"
     && text aggregate "sourceRepository" = "FS-GG/.github"
     && not (boolean aggregate "acceptanceReceiptCreated"))
    "GS2089-ENVELOPE"
    "aggregate must remain a pending, non-receipt GS2-08.9 qualification"

let expectedHead = "81276fbd8572dd5a8b6f9ce1408694cc2b96b303"
let expectedTree = "13e4f1d5a0160f697fa5e880e662199f43dbfb9b"

require
    (text aggregate "sourceHead" = expectedHead && text aggregate "sourceTree" = expectedTree)
    "GS2089-STALE-IDENTITY"
    "protected source head or tree differs"

let expectedPredecessors =
    Map.ofList
        [
            "GS2-08.6", "4b19806d1c4f9d147368e29e04ab7a480dcb6b798aed32e37159e8ac92a1e0cf"
            "GS2-08.7", "0cac376a7f981f56143f14ebbbc6a465529251420fce93b5d115d44282e08e55"
        ]

let predecessors =
    aggregate.GetProperty("acceptedPredecessors").EnumerateArray()
    |> Seq.map (fun item -> text item "unit", text item "receiptSha256")
    |> Map.ofSeq

require (predecessors = expectedPredecessors) "GS2089-PREDECESSORS" "accepted GS2-08.6/08.7 receipts differ"

for unitId, relative in
    [
        "GS2-08.6", "evidence/github-substrate-v2/accepted/GS2-08.6.json"
        "GS2-08.7", "evidence/github-substrate-v2/accepted/GS2-08.7.json"
    ] do
    use receipt = JsonDocument.Parse(read relative)
    let value = receipt.RootElement
    require
        (text value "state" = "accepted" && text value "digest" = expectedPredecessors[unitId])
        "GS2089-PREDECESSORS"
        $"accepted {unitId} receipt bytes differ"

let receiver = aggregate.GetProperty("receiverAdoption")

require
    (text receiver "unit" = "GS2-08.8"
     && text receiver "state" = "pending-native-receipt"
     && text receiver "candidateHead" = "643e76b48bfc317e8a06b38ddc5a0c0f22f93eb4"
     && integer receiver "pullRequest" = 417
     && not (boolean receiver "acceptedReceiptPresent"))
    "GS2089-RECEIVER"
    "GS2-08.8 must remain pending until its protected merge and native receipt"

let expectedSeals =
    Map.ofList
        [
            "telemetry",
            ("b2b9bc5c8b07d8fcaf4ee171e1cfc07639aa87b5",
             "33d992b85c4b8309c0d6350ac1d3db42983ed562",
             "tests/FS.GG.Telemetry.Tests/RemoteTelemetryTests.fs",
             "be74aacdbbc79b2a71508385acdaae36df102f33bf11268ee4ff0ec2bb4c17bd")
            "dispatch-repair",
            ("659d405d87f0b551419528bc0a3e3d2fe1f874a9",
             "30d41314b425b0dd198a6b33f83e60ae2aa2be8a",
             "tests/gs2-08-9-dispatch-repair/disposition.json",
             "0cf7e999204184681682d694fd1eb298914d3d29c887fcb21d732d12f34ea337")
            "release-publication",
            (expectedHead,
             expectedTree,
             "docs/reports/gs2-08-9-release-route-dispositions.json",
             "4ca6c682f120117ba68907369d52584c5058cf84ad95b0f1abb47b2dcfd0f5f0")
        ]

let seals = aggregate.GetProperty("sourceSeals").EnumerateArray() |> Seq.toList
require (seals.Length = 3) "GS2089-SOURCE-SEALS" "expected exactly three protected source seals"

for seal in seals do
    let partition = text seal "partition"
    require (expectedSeals.ContainsKey partition) "GS2089-SOURCE-SEALS" $"unexpected source partition {partition}"
    let commit, tree, path, sha = expectedSeals[partition]
    require
        (text seal "commit" = commit
         && text seal "tree" = tree
         && text seal "state" = "merged-protected"
         && text seal "evidencePath" = path
         && text seal "evidenceSha256" = sha)
        "GS2089-STALE-IDENTITY"
        $"stale {partition} source or evidence identity"

let blockersDocument = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-6/gs2-08-9-blockers.json")

let expectedRoutes =
    blockersDocument.RootElement.GetProperty("externalWriterSources").EnumerateArray()
    |> Seq.map (fun route -> text route "path")
    |> Seq.toList

let routes = aggregate.GetProperty("routes").EnumerateArray() |> Seq.toList
let routePaths = routes |> List.map (fun route -> text route "path")

require
    (integer (aggregate.GetProperty("baseline")) "routeCount" = 22
     && expectedRoutes.Length = 22
     && routePaths.Length = 22
     && Set.count (Set.ofList routePaths) = 22
     && Set.ofList routePaths = Set.ofList expectedRoutes)
    "GS2089-ROUTES"
    "aggregate has a missing, duplicate or unknown residual route"

let validRouteStates =
    Map.ofList
        [
            "dispatch-repair", Set.ofList [ "current-source-sealed" ]
            "release-publication", Set.ofList [ "current-source-sealed" ]
            "release-administration", Set.ofList [ "admin-disabled" ]
            "helper", Set.ofList [ "pending-merge-and-copy-retirement" ]
            "telemetry", Set.ofList [ "submission-only" ]
        ]

for route in routes do
    let partition, state = text route "partition", text route "state"
    let path = text route "path"
    require
        (validRouteStates.ContainsKey partition && validRouteStates[partition].Contains state)
        "GS2089-ROUTE-DISPOSITION"
        $"invalid disposition for {path}"

let clients = aggregate.GetProperty("historicalClients").EnumerateArray() |> Seq.toList
require (clients.Length = 2) "GS2089-OLD-CLIENT" "expected both historical clients"
let unavailable = clients |> List.find (fun client -> text client "version" = "0.58.0")
let bypass = clients |> List.find (fun client -> text client "version" = "0.75.4")

require
    (text unavailable "artifact" = "unavailable"
     && not (boolean unavailable "refusalAttempted")
     && text unavailable "accounting" = "receiver-adoption-retirement")
    "GS2089-FALSE-UNAVAILABLE-REFUSAL"
    "artifact unavailability cannot be presented as an executed refusal"

require
    (text bypass "artifact" = "available-bypass-observed"
     && boolean bypass "refusalAttempted"
     && not (boolean bypass "historicalShaCredentialed")
     && text bypass "accounting" = "rendering-selected-repository-scope-retired")
    "GS2089-HISTORICAL-CAPABILITY"
    "historical 0.75.4 capability must remain retired after Rendering lost both selected-repository secret scopes"

let dispatchAdministration = aggregate.GetProperty("dispatchAdministration")
let dispatchRepository = dispatchAdministration.GetProperty("repository")
let dispatchScopes = dispatchAdministration.GetProperty("secretScopes").EnumerateArray() |> Seq.toList
let historicalCaller = dispatchAdministration.GetProperty("historicalCaller")

require
    (text dispatchAdministration "schema" = "fsgg.gs2-08.9.rendering-dispatch-secret-scope-retirement/1"
     && text dispatchAdministration "mailboxCommit" = "202bbb3e0799027cc448fecfef084f186eba4f08"
     && text dispatchAdministration "evidenceSha256" = "8cea64e7dc551fbec46479331f71787384b4c7495f6b75b012d7a0d8d3295a1c"
     && dispatchRepository.GetProperty("id").GetInt64() = 1269292235L
     && text dispatchRepository "name" = "FS-GG/FS.GG.Rendering"
     && dispatchScopes.Length = 2
     && (dispatchScopes |> List.map (fun scope -> text scope "secretName") |> Set.ofList) =
        Set.ofList [ "FSGG_DISPATCH_APP_ID"; "FSGG_DISPATCH_APP_PRIVATE_KEY" ]
     && dispatchScopes
        |> List.forall (fun scope ->
            text scope "visibilityBefore" = "selected"
            && text scope "visibilityAfter" = "selected"
            && integer scope "deleteStatus" = 204
            && boolean scope "renderingPresentBefore"
            && not (boolean scope "renderingPresentAfter"))
     && text historicalCaller "renderingRevision" = "66836abdaef87d601fc084d90658fdb605fbe023"
     && text historicalCaller "dispatchSenderRevision" = "5fed2838f9ed085ffca09f4cc18b4f7bc59c1294"
     && not (boolean historicalCaller "canResolveBothCredentials")
     && not (boolean historicalCaller "executed"))
    "GS2089-DISPATCH-ADMIN"
    "Rendering dispatch secret-scope removal or non-execution readback differs"

let expectedWorkflows =
    Map.ofList
        [
            311748898, ".github/workflows/release-coord-engine.yml"
            317170238, ".github/workflows/release-drivers.yml"
            316772183, ".github/workflows/release-kit.yml"
            334278878, ".github/workflows/release-saga-prepare.yml"
            346289776, ".github/workflows/release-saga-start.yml"
        ]

let administration = aggregate.GetProperty("workflowAdministration")
let workflows = administration.GetProperty("workflows").EnumerateArray() |> Seq.toList

require
    (text administration "repository" = "FS-GG/.github"
     && workflows.Length = 5
     && workflows |> List.map (fun workflow -> workflow.GetProperty("id").GetInt32()) |> Set.ofList |> Set.count = 5)
    "GS2089-ADMIN"
    "workflow administration population differs"

for workflow in workflows do
    let id = workflow.GetProperty("id").GetInt32()
    require
        (expectedWorkflows.ContainsKey id
         && text workflow "path" = expectedWorkflows[id]
         && boolean workflow "sourceSealed"
         && text workflow "state" = "disabled_manually")
        "GS2089-ADMIN"
        $"workflow {id} source seal is not paired with disabled_manually readback"

let telemetry = aggregate.GetProperty("telemetryBoundary")

require
    (text telemetry "scope" = "telemetry-submission-only"
     && text telemetry "authorityEscalation" = "unauthorized-scope"
     && text telemetry "redirect" = "refused"
     && integer telemetry "providerMutationCount" = 0)
    "GS2089-TELEMETRY"
    "telemetry scope, redirect refusal or zero-effect evidence differs"

let helper = aggregate.GetProperty("helperBoundary")
let helperTemplate = helper.GetProperty("requiredEvidenceTemplate")

require
    (text helper "candidateCommit" = "adbb08e86c00bf2753e57381527ba271654aa9d0"
     && text helper "candidateTree" = "24c1d7d9037bc04f8d44e6c6fdd199f2ae852fc7"
     && integer helper "pullRequest" = 3530
     && text helper "reviewerDisposition" = "GO"
     && not (boolean helper "mergedToMain")
     && text helper "newSddWorkspaceSha256" = "b3e3ebe3b88f67b56ea7d85be4865c6f401ae4e94d9cf451c8bae957c15cea3f"
     && text helper "routineDeliverySha256" = "03adc237c89fc4fd05d29c7fe9191d0d2ab701c3ff8de54c24eacb2e77d0b429"
     && integer helper "mutationAttempts" = 0
     && not (boolean helper "admissionRefusalReadbackAttributedAsDelivery")
     && not (boolean helper "publishedCopiesRetired")
     && not (boolean helper "callersRetired")
     && text helperTemplate "immutablePublicPackages" = "retained-history-allowed"
     && helperTemplate.GetProperty("inventories").GetArrayLength() = 6
     && helperTemplate.GetProperty("readOnlyCommands").GetArrayLength() = 6
     && text helperTemplate "knownCaller" =
        "SystemAdmin/Containers/Containerfile.fsharp installs FS.GG.NewSddWorkspace and requires replacement or an explicit non-active image disposition.")
    "GS2089-HELPER"
    "helper merge, zero-attempt refusal, readback attribution or historical retirement differs"

let pending = aggregate.GetProperty("pendingBlockers").EnumerateArray() |> Seq.toList
let pendingIds = pending |> List.map (fun item -> text item "id")

require
    (pendingIds =
        [
            "gs2-08.8-native-receipt"
            "helper-source-merge"
            "helper-container-runtime-retirement"
        ]
     && text pending[0] "state" = "pending-pr-417-merge"
     && text pending[1] "state" = "pending-pr-3530-merge"
     && text pending[2] "state" = "pending-admin-readback")
    "GS2089-PENDING-SET"
    "the three explicit blocking evidence items must remain ordered and pending"

let q4 = aggregate.GetProperty("q4")
require
    (text q4 "state" = "unclaimed" && not (boolean q4 "providerMutationClaimed"))
    "GS2089-Q4"
    "Q4 must remain unclaimed"

eprintfn
    "GS2089-PENDING: GS2-08.8 PR #417 merge/receipt, helper PR #3530 merge, and container/runtime retirement remain required; Q4 is unclaimed"

Environment.Exit 78
