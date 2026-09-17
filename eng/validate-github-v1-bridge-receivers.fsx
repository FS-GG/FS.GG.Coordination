open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

let fail code message = failwith $"{code}: {message}"

let args = fsi.CommandLineArgs |> Array.skip 1

let root, evidencePath =
    match args with
    | [| root |] ->
        Path.GetFullPath root,
        "evidence/github-substrate-v2/gs2-08-8/receiver-qualification.json"
    | [| root; evidence |] -> Path.GetFullPath root, evidence
    | _ -> fail "GVBR-USAGE" "expected repository root and optional aggregate evidence path"

let resolve (path: string) =
    if Path.IsPathRooted path then path else Path.Combine(root, path)

let read (path: string) = File.ReadAllBytes(resolve path)
let sha256 (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
let digest (path: string) = read path |> sha256
let document = JsonDocument.Parse(read evidencePath)
let evidence = document.RootElement
let text (value: JsonElement) (name: string) = value.GetProperty(name).GetString()
let boolean (value: JsonElement) (name: string) = value.GetProperty(name).GetBoolean()
let integer (value: JsonElement) (name: string) = value.GetProperty(name).GetInt32()
let integer64 (value: JsonElement) (name: string) = value.GetProperty(name).GetInt64()
let array (value: JsonElement) (name: string) = value.GetProperty(name).EnumerateArray() |> Seq.toList

let require condition code message =
    if not condition then fail code message

let isHex length (value: string) =
    value.Length = length && value |> Seq.forall Uri.IsHexDigit

type ReceiverIdentity =
    {
        Repository: string
        Head: string
        Tree: string
        ReportSha256: string
        PackageIds: Set<string>
        RouteCount: int
        CallableCount: int
        DispositionCounts: Map<string, int>
    }

let expectedReceivers =
    [
        { Repository = "FS-GG/.github"; Head = "85a70e9a30e5e6a471d61afddb06f7f597d020ce"; Tree = "23ba8852d93d09713271c516aa85a09173fdd8a2"; ReportSha256 = "8e69b2863c549beffe26ebca9c7bd50498133f7b05922fce1c127bb107756383"; PackageIds = set [ "FS.GG.Coord.Cli"; "FS.GG.Kit" ]; RouteCount = 4; CallableCount = 1; DispositionCounts = Map.ofList [ "bridge-adopted", 1; "read-only-local-only", 3; "gs2-08.9-sealing", 1 ] }
        { Repository = "FS-GG/FS.GG.SDD"; Head = "f48204831a2e90db9bfbbbdd59d49d0139c2bf19"; Tree = "6de1bef60009435abc6a0c907e916de6956185db"; ReportSha256 = "d6c8f7a9d7aa2d2d8cb8d120ccf05bfc03cda6812f67763bdce73cbed9038bed"; PackageIds = set [ "FS.GG.Coord.Cli"; "FS.GG.Drivers"; "FS.GG.Kit" ]; RouteCount = 3; CallableCount = 4; DispositionCounts = Map.ofList [ "bridge-adopted", 2; "read-only-local-only", 0; "gs2-08.9-sealing", 5 ] }
        { Repository = "FS-GG/FS.GG.Rendering"; Head = "66836abdaef87d601fc084d90658fdb605fbe023"; Tree = "57c4d8a8775997f1db3d89c524fc2a7bfb09237d"; ReportSha256 = "542b758b478f1ab4f212cc21dfe455103866367e4f3b7f1f51bdcc3a743a5617"; PackageIds = set [ "FS.GG.Coord.Cli"; "FS.GG.Kit" ]; RouteCount = 3; CallableCount = 8; DispositionCounts = Map.ofList [ "bridge-adopted", 1; "read-only-local-only", 1; "gs2-08.9-sealing", 9 ] }
        { Repository = "FS-GG/FS.GG.Governance"; Head = "df47d597fbddbbf41b8facc034185fc9755ef6f6"; Tree = "5b8af68aa9f3285fa22abbf6b56364b81098d2e3"; ReportSha256 = "528c76e86d228b583115e02156a8310761f0880e9935a93832132240d575ea6b"; PackageIds = set [ "FS.GG.Coord.Cli"; "FS.GG.Kit" ]; RouteCount = 3; CallableCount = 4; DispositionCounts = Map.ofList [ "bridge-adopted", 1; "read-only-local-only", 1; "gs2-08.9-sealing", 5 ] }
        { Repository = "FS-GG/FS.GG.Templates"; Head = "d568f24ac44b3954d96898f95f72d969632807b1"; Tree = "fcd1952270c6ee37a28210dd55bd788355a45792"; ReportSha256 = "0016148e26fdca2f0c4e51341f48a9dfd8d384596dcf7a1e4912390cd26285fc"; PackageIds = set [ "FS.GG.Coord.Cli"; "FS.GG.Kit" ]; RouteCount = 3; CallableCount = 3; DispositionCounts = Map.ofList [ "bridge-adopted", 1; "read-only-local-only", 1; "gs2-08.9-sealing", 4 ] }
        { Repository = "FS-GG/FS.GG.Game"; Head = "d399c36f35a2f85a669a7bc7c37eed0b98896a03"; Tree = "fcedf69488fc673fd4cd72112dd4c9d0fd81d83f"; ReportSha256 = "4ed892533b141e0535ab2090470594b5ef29d0fb85f8164e78936a2879848914"; PackageIds = set [ "FS.GG.Coord.Cli"; "FS.GG.Kit" ]; RouteCount = 5; CallableCount = 5; DispositionCounts = Map.ofList [ "bridge-adopted", 1; "read-only-local-only", 1; "gs2-08.9-sealing", 8 ] }
        { Repository = "FS-GG/FS.GG.Audio"; Head = "d8ce8c057880c6c26547b813851510bf2f56bd3b"; Tree = "ca9d30fe360bf984121b1973bd7899707925eb12"; ReportSha256 = "ebad9695da77064ab882eb4ac063a26a37d87b9c2c9223b3b11b9d44c0389042"; PackageIds = set [ "FS.GG.Coord.Cli"; "FS.GG.Kit" ]; RouteCount = 3; CallableCount = 4; DispositionCounts = Map.ofList [ "bridge-adopted", 1; "read-only-local-only", 1; "gs2-08.9-sealing", 5 ] }
        { Repository = "FS-GG/FS.GG.Net"; Head = "8c2855fba8fc2f03c2badb8f9c7695d91111dd58"; Tree = "55c16719da437262257d7fc42a608f38bce8edb2"; ReportSha256 = "7bfde3e20fb2c7719eb53a206a79bf59fd0d9e39b409468c277ef5533e95a072"; PackageIds = set [ "FS.GG.Coord.Cli"; "FS.GG.Kit" ]; RouteCount = 2; CallableCount = 4; DispositionCounts = Map.ofList [ "bridge-adopted", 1; "read-only-local-only", 0; "gs2-08.9-sealing", 5 ] }
    ]

let expectedRepositories = expectedReceivers |> List.map _.Repository
let receiverIdentities = expectedReceivers |> List.map (fun identity -> identity.Repository, identity) |> Map.ofList

let expectedPackages =
    Map.ofList
        [
            "FS.GG.Coord.Cli", ("69a7100358e01c846216cedb3f5ce17f72e99e8328a23be8e75b077dfd82d3e6", "sha256:725b46203eeccbe1f73b42667d95ed07c6581dc2ca0886ac8011145c278ee481")
            "FS.GG.Drivers", ("103292c164806bb0fb887edf268a60053e706545d0fdd9e1a9c5707e9f3873d5", "sha256:76c7c7ac186431bc8e55e13e7175897cace654cf9f9ba49cb5b081e830f890f3")
            "FS.GG.Kit", ("bea35100645f1acb459e385e303d0ef4ff7aa6576fa3f032ef8325cfef38b858", "sha256:f2a318f0b900d049c496618bfdd7c2def5f50ffd9e649d8998f5c6585384f1f4")
        ]

require
    (text evidence "schema" = "fsgg.gs2-08.8-bridge-receiver-qualification/1"
     && text evidence "unit" = "GS2-08.8"
     && text evidence "network" = "offline")
    "GVBR-STATE"
    "aggregate schema, unit or offline boundary differs"

let prerequisite = evidence.GetProperty("prerequisite")

require
    (text prerequisite "unit" = "GS2-08.7"
     && text prerequisite "receiptSha256" = digest "evidence/github-substrate-v2/accepted/GS2-08.7.json"
     && text prerequisite "receiptDigest" = "0cac376a7f981f56143f14ebbbc6a465529251420fce93b5d115d44282e08e55")
    "GVBR-PREREQUISITE"
    "accepted GS2-08.7 receipt binding differs"

let bridge = evidence.GetProperty("bridge")

require
    (text bridge "version" = "0.90.0"
     && text bridge "sourceMerge" = "3adada5a9738464291088830c47a30a3a8fc9561"
     && text bridge "sourceTree" = "0f075e251d90a2d33efe556df1dac38394b0a388"
     && text bridge "releaseContentId" = "sha256:52b2774de277855c16a3c0852bc5113deea13d9076b866e6a7de0acc84f4b9c4"
     && text bridge "manifestSha256" = "1bbb77f3de10ba3116f9de2ea1df3f5edee38be0de07fa0eaf7c173da3a8456a")
    "GVBR-BRIDGE"
    "public 0.90.0 source, content or manifest identity differs"

let checkPackages code (packages: JsonElement list) =
    let observed =
        packages
        |> List.map (fun package ->
            let id = text package "id"
            require (text package "version" = "0.90.0") code $"package {id} is not public 0.90.0"
            id, (text package "publicArchiveSha256", text package "payloadSha256"))
        |> Map.ofList

    require (observed = expectedPackages) code "public package payload closure differs"

let checkReceiverPackages repository expectedIds (packages: JsonElement list) =
    require (not packages.IsEmpty) "GVBR-RECEIVER-PACKAGES" $"{repository} has no resolved public bridge package"

    let ids = packages |> List.map (fun package -> text package "id")
    require ((ids |> Set.ofList) = expectedIds && ids.Length = expectedIds.Count) "GVBR-RECEIVER-PACKAGES" $"{repository} package closure differs"

    for package in packages do
        let id = text package "id"

        let archive, payload = expectedPackages[id]

        require
            (text package "version" = "0.90.0"
             && text package "publicArchiveSha256" = archive
             && text package "payloadSha256" = payload)
            "GVBR-RECEIVER-PACKAGES"
            $"{repository} resolves an old or substituted package"

checkPackages "GVBR-PACKAGES" (array bridge "packages")

let receivers = array evidence "receivers"
let repositories = receivers |> List.map (fun receiver -> text receiver "repository")

require
    (receivers.Length = 8 && repositories = expectedRepositories && (repositories |> Set.ofList).Count = 8)
    "GVBR-RECEIVERS"
    "all eight receivers must appear once in canonical order"

let allowedDispositions =
    Set.ofList [ "bridge-adopted"; "read-only-local-only"; "gs2-08.9-sealing" ]

let observedDispositions = ResizeArray<string>()

for receiver in receivers do
    let repository = text receiver "repository"
    let identity = receiverIdentities[repository]
    let protectedState = receiver.GetProperty("protected")
    let expectedHead = text protectedState "expectedHead"
    let observedHead = text protectedState "observedHead"
    let expectedTree = text protectedState "expectedTree"
    let observedTree = text protectedState "observedTree"

    require
        (expectedHead = identity.Head && observedHead = identity.Head && expectedTree = identity.Tree && observedTree = identity.Tree)
        "GVBR-PROTECTED"
        $"{repository} protected head/tree is stale or malformed"

    let report = receiver.GetProperty("report")
    let expectedReport = text report "expectedSha256"

    require
        (text report "path" = "docs/reports/gs2-08-8-bridge-adoption.json"
         && text report "schema" = "fsgg.gs2-08.8-receiver-adoption/1"
         && text report "receiver" = repository
         && expectedReport = identity.ReportSha256
         && text report "observedSha256" = identity.ReportSha256)
        "GVBR-REPORT"
        $"{repository} report digest is stale or malformed"

    checkReceiverPackages repository identity.PackageIds (array receiver "packages")

    let pins = array receiver "pins"
    require (pins.Length = identity.PackageIds.Count) "GVBR-PINS" $"{repository} package pin closure differs"

    for pin in pins do
        require
            (identity.PackageIds.Contains(text pin "package")
             && text pin "version" = "0.90.0"
             && not (String.IsNullOrWhiteSpace(text pin "path")))
            "GVBR-PINS"
            $"{repository} has an old or unknown pin"

    let receiverRoutes = array receiver "routes"
    require (receiverRoutes.Length = identity.RouteCount) "GVBR-ROUTE-REPORT" $"{repository} route cardinality differs"

    for route in receiverRoutes do
        let disposition = text route "disposition"
        require
            (allowedDispositions.Contains disposition
             && not (String.IsNullOrWhiteSpace(text route "id"))
             && isHex 64 (text route "sha256"))
            "GVBR-ROUTE-REPORT"
            $"{repository} has a malformed route disposition"
        observedDispositions.Add disposition

    let callableRoutes = array receiver "callableRoutes"
    require (callableRoutes.Length = identity.CallableCount) "GVBR-WORKFLOW" $"{repository} callable route cardinality differs"

    for workflow in callableRoutes do
        let reference = text workflow "ref"
        let immutable = boolean workflow "immutable"
        let disposition = text workflow "disposition"

        require
            (isHex 64 (text workflow "callerSha256")
             && isHex 64 (text workflow "workflowSha256")
             && allowedDispositions.Contains disposition
             && ((immutable && isHex 40 reference && text workflow "observedRevision" = reference)
                 || (not immutable
                     && reference = "main"
                     && disposition = "gs2-08.9-sealing"
                     && text workflow "observedRevision" = "3adada5a9738464291088830c47a30a3a8fc9561")
                 || (not immutable
                     && repository = "FS-GG/.github"
                     && reference = "receiver-revision"
                     && text workflow "observedRevision" = "qualification-input"
                     && disposition = "read-only-local-only")))
            "GVBR-WORKFLOW"
            $"{repository} callable workflow mutability or revision binding differs"

        observedDispositions.Add disposition

    let receiverDispositionCounts =
        observedDispositions
        |> Seq.skip (observedDispositions.Count - identity.RouteCount - identity.CallableCount)
        |> Seq.countBy id
        |> Map.ofSeq

    for disposition in allowedDispositions do
        require
            (Map.tryFind disposition receiverDispositionCounts |> Option.defaultValue 0 = identity.DispositionCounts[disposition])
            "GVBR-ROUTE-REPORT"
            $"{repository} disposition accounting differs"

    let qualification = receiver.GetProperty("qualification")

    require
        (boolean qualification "publicRestore"
         && boolean qualification "installedCommand"
         && text qualification "productionMutation" = "refused-unavailable-fence"
         && integer qualification "providerRequests" = 0
         && not (boolean qualification "dashboardNotificationPresentedAsAdoption"))
        "GVBR-QUALIFICATION"
        $"{repository} public resolution, installed refusal, production mutation or adoption claim differs"

require
    ((observedDispositions |> Set.ofSeq) = allowedDispositions)
    "GVBR-DISPOSITION-CLOSURE"
    "route disposition closure differs"

let routes = evidence.GetProperty("routeAccounting")
let baselineCounts = routes.GetProperty("baselineEffectCounts")
let reportedCounts = routes.GetProperty("reportedDispositionCounts")
let successorAddedCounts = routes.GetProperty("successorAddedDispositionCounts")
let dispositions = [ "bridge-adopted"; "read-only-local-only"; "gs2-08.9-sealing" ]
let sumCounts (counts: JsonElement) = dispositions |> List.sumBy (integer counts)

let baseline = integer routes "baseline"
let added = integer routes "successorAdded"
let retired = integer routes "successorRetired"
let addedIds = array routes "successorAddedRouteIds" |> List.map _.GetString()
let retiredIds = array routes "successorRetiredRouteIds" |> List.map _.GetString()
let observedRouteCount = receivers |> List.sumBy (fun receiver -> array receiver "routes" |> List.length)
let observedCallableCount = receivers |> List.sumBy (fun receiver -> array receiver "callableRoutes" |> List.length)

require
    (baseline = 615
     && integer routes "accounted" = baseline
     && integer baselineCounts "conditional" = 197
     && integer baselineCounts "inert" = 353
     && integer baselineCounts "protected-admin" = 27
     && integer baselineCounts "read-only" = 38
     && integer baselineCounts "conditional"
        + integer baselineCounts "inert"
        + integer baselineCounts "protected-admin"
        + integer baselineCounts "read-only" = baseline
     && array routes "missingRouteIds" |> List.isEmpty
     && integer routes "reportedReceiverRoutes" = observedRouteCount
     && integer routes "reportedCallableRoutes" = observedCallableCount
     && observedRouteCount = 26
     && observedCallableCount = 33
     && integer reportedCounts "bridge-adopted" = 9
     && integer reportedCounts "read-only-local-only" = 8
     && integer reportedCounts "gs2-08.9-sealing" = 42
     && sumCounts reportedCounts = observedRouteCount + observedCallableCount
     && addedIds.Length = added
     && retiredIds.Length = retired
     && (addedIds |> Set.ofList).Count = addedIds.Length
     && (retiredIds |> Set.ofList).Count = retiredIds.Length
     && integer routes "successorTotal" = baseline + added - retired
     && added = 5
     && retired = 0
     && integer successorAddedCounts "bridge-adopted" = 1
     && integer successorAddedCounts "read-only-local-only" = 3
     && integer successorAddedCounts "gs2-08.9-sealing" = 1
     && sumCounts successorAddedCounts = added
     && text routes "successorAccountingBasis" = "GS2-08.3 covers seven product receivers; the five Hub report route and callable identities are additions, and no protected receiver report claims a retirement.")
    "GVBR-ROUTES"
    "615-route baseline or successor accounting is incomplete"

let dependents = evidence.GetProperty("dependentScaffolds")

for name in [ "templates"; "sdd" ] do
    let outcome = dependents.GetProperty(name)

    require
        (text outcome "state" = "passed"
         && text outcome "packageSource" = "nuget-org"
         && text outcome "bridgeVersion" = "0.90.0"
         && boolean outcome "cleanCreation"
         && boolean outcome "upgrade"
         && boolean outcome "oldClientRefused"
         && isHex 64 (text outcome "reportSha256"))
        "GVBR-SCAFFOLD"
        $"dependent {name} public scaffold outcome differs"

let templates = dependents.GetProperty("templates")
let templatesWorkflow = templates.GetProperty("workflow")
let templateJobs = array templatesWorkflow "jobs"

require
    (boolean templates "freshCreation"
     && boolean templates "retrofit"
     && text templatesWorkflow "repository" = "FS-GG/FS.GG.Templates"
     && text templatesWorkflow "path" = ".github/workflows/gs2-08-8-bridge-adoption.yml"
     && text templatesWorkflow "sha256" = "2c65fda0208d271fba144a50b16c15f009520b442bb033bb6326ddd55e980857"
     && integer64 templatesWorkflow "runId" = 35217614942L
     && text templatesWorkflow "head" = "d568f24ac44b3954d96898f95f72d969632807b1"
     && text templatesWorkflow "tree" = "fcd1952270c6ee37a28210dd55bd788355a45792"
     && text templatesWorkflow "hubAdoptionRef" = "85a70e9a30e5e6a471d61afddb06f7f597d020ce"
     && text templatesWorkflow "status" = "completed"
     && text templatesWorkflow "conclusion" = "success"
     && text templatesWorkflow "readbackSha256" = "4bd560fe606922acb95ea9e40ed9d052b4e64c68788ece19cb7f53f9ba15a06c"
     && text templatesWorkflow "logSha256" = "c1375c9965f879053d62adb3757254a40e7e230e0b2802f3504bb205ea910e83"
     && text templates "reportSha256" = text templatesWorkflow "logSha256"
     && templateJobs.Length = 2
     && integer64 templateJobs[0] "id" = 105189727497L
     && text templateJobs[0] "name" = "Explicit hub fresh and retrofit"
     && text templateJobs[0] "conclusion" = "success"
     && integer64 templateJobs[1] "id" = 105189727768L
     && text templateJobs[1] "name" = "Public bridge receiver"
     && text templateJobs[1] "conclusion" = "success")
    "GVBR-SCAFFOLD"
    "Templates protected fresh/retrofit workflow readback or log identity differs"

require
    (text (dependents.GetProperty("sdd")) "productVersion" = "2.0.1")
    "GVBR-SCAFFOLD"
    "dependent SDD public scaffold must use 2.0.1"

require
    (text evidence "state" = "qualified"
     && boolean evidence "qualified"
     && not (boolean evidence "replaceBeforeQualification"))
    "GVBR-STATE"
    "aggregate must be final and qualified"

printfn
    "github-v1-bridge-receivers-contract OK receivers=8 baseline=615 successor=%d dispositions=3 packages=3 q=Q3 network=offline state=qualified"
    (integer routes "successorTotal")
