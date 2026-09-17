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
let array (value: JsonElement) (name: string) = value.GetProperty(name).EnumerateArray() |> Seq.toList

let require condition code message =
    if not condition then fail code message

let isHex length (value: string) =
    value.Length = length && value |> Seq.forall Uri.IsHexDigit

let expectedRepositories =
    [
        "FS-GG/.github"
        "FS-GG/FS.GG.SDD"
        "FS-GG/FS.GG.Rendering"
        "FS-GG/FS.GG.Governance"
        "FS-GG/FS.GG.Templates"
        "FS-GG/FS.GG.Game"
        "FS-GG/FS.GG.Audio"
        "FS-GG/FS.GG.Net"
    ]

let expectedPackages =
    Map.ofList
        [
            "FS.GG.Coord.Cli", "725b46203eeccbe1f73b42667d95ed07c6581dc2ca0886ac8011145c278ee481"
            "FS.GG.Drivers", "76c7c7ac186431bc8e55e13e7175897cace654cf9f9ba49cb5b081e830f890f3"
            "FS.GG.Kit", "f2a318f0b900d049c496618bfdd7c2def5f50ffd9e649d8998f5c6585384f1f4"
        ]

require
    (text evidence "schema" = "fsgg.gs2-08.8-bridge-receiver-qualification/1"
     && text evidence "unit" = "GS2-08.8"
     && text evidence "state" = "qualified"
     && boolean evidence "qualified"
     && text evidence "network" = "offline")
    "GVBR-STATE"
    "aggregate must be final, qualified and offline"

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
            id, text package "payloadSha256")
        |> Map.ofList

    require (observed = expectedPackages) code "public package payload closure differs"

let checkReceiverPackages repository (packages: JsonElement list) =
    require (not packages.IsEmpty) "GVBR-RECEIVER-PACKAGES" $"{repository} has no resolved public bridge package"

    let ids = packages |> List.map (fun package -> text package "id")
    require ((ids |> Set.ofList).Count = ids.Length) "GVBR-RECEIVER-PACKAGES" $"{repository} repeats a package"

    for package in packages do
        let id = text package "id"

        require
            (expectedPackages.ContainsKey id
             && text package "version" = "0.90.0"
             && text package "payloadSha256" = expectedPackages[id])
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
    let protectedState = receiver.GetProperty("protected")
    let expectedHead = text protectedState "expectedHead"
    let observedHead = text protectedState "observedHead"
    let expectedTree = text protectedState "expectedTree"
    let observedTree = text protectedState "observedTree"

    require
        (isHex 40 expectedHead && expectedHead = observedHead && isHex 40 expectedTree && expectedTree = observedTree)
        "GVBR-PROTECTED"
        $"{repository} protected head/tree is stale or malformed"

    let report = receiver.GetProperty("report")
    let expectedReport = text report "expectedSha256"

    require
        (isHex 64 expectedReport && expectedReport = text report "observedSha256")
        "GVBR-REPORT"
        $"{repository} report digest is stale or malformed"

    checkReceiverPackages repository (array receiver "packages")

    let pins = array receiver "pins"
    require (not pins.IsEmpty) "GVBR-PINS" $"{repository} has no exact package pin"

    for pin in pins do
        require
            (expectedPackages.ContainsKey(text pin "package") && text pin "version" = "0.90.0")
            "GVBR-PINS"
            $"{repository} has an old or unknown pin"

    let receiverRoutes = array receiver "routes"
    require (not receiverRoutes.IsEmpty) "GVBR-ROUTE-REPORT" $"{repository} has no route disposition"

    for route in receiverRoutes do
        let disposition = text route "disposition"
        require (allowedDispositions.Contains disposition) "GVBR-ROUTE-REPORT" $"{repository} has an unknown disposition"
        observedDispositions.Add disposition

    for workflow in array receiver "callableRoutes" do
        let reference = text workflow "ref"
        let immutable = boolean workflow "immutable"
        let disposition = text workflow "disposition"

        require
            (isHex 40 (text workflow "observedRevision")
             && isHex 64 (text workflow "workflowSha256")
             && allowedDispositions.Contains disposition
             && ((immutable && isHex 40 reference)
                 || (not immutable && reference = "main" && disposition = "gs2-08.9-sealing")))
            "GVBR-WORKFLOW"
            $"{repository} callable workflow mutability or revision binding differs"

        observedDispositions.Add disposition

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
let dispositionCounts = routes.GetProperty("dispositionCounts")

let accounted =
    [ "bridge-adopted"; "read-only-local-only"; "gs2-08.9-sealing" ]
    |> List.sumBy (integer dispositionCounts)

let baseline = integer routes "baseline"
let added = integer routes "successorAdded"
let retired = integer routes "successorRetired"

require
    (baseline = 615
     && integer routes "accounted" = baseline
     && accounted = baseline
     && array routes "missingRouteIds" |> List.isEmpty
     && integer routes "successorTotal" = baseline + added - retired
     && added >= 0
     && retired >= 0)
    "GVBR-ROUTES"
    "615-route baseline or successor accounting is incomplete"

let dependents = evidence.GetProperty("dependentScaffolds")

for name in [ "sdd"; "templates" ] do
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

printfn
    "github-v1-bridge-receivers-contract OK receivers=8 baseline=615 successor=%d dispositions=3 packages=3 q=Q3 network=offline state=qualified"
    (integer routes "successorTotal")
