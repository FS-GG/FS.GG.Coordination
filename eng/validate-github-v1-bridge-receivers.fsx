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

let expectedRouteIdentities =
    Map.ofList
        [
            "FS-GG/.github",
            [
                "dist/dotnet/.config/dotnet-tools.json", "31bcff6fe195cb13c6ade2389cbdc8a5f5ebdde590f99b6923399ce445189f5e", "bridge-adopted"
                "tests/bridge-receivers/run.sh", "864dbc6a85042b8d0dad62fad9b93dfee75e8f8b8d2ebf9e8613e1e6bc45531d", "read-only-local-only"
                ".github/workflows/release-saga-tooling.yml", "4a9d96d5647ab067ae70c5ff22fa159f7235f4e356e8c1dab02802b0e1049463", "read-only-local-only"
                "docs/coordination/v1-writer-receiver-census.json", "3d7de0dee094991e08aed09b7478ea1191d6ee7f50baaad0087975a88e8f90db", "gs2-08.9-sealing"
            ]
            "FS-GG/FS.GG.SDD",
            [
                "scripts/fsgg-coord", "a0689f0ba8cd0180fd1ca60a63bba55e93513732b36e099c541847336ddb3ccf", "bridge-adopted"
                "fsgg-sdd scaffold", "f508712e6989844e51228e00eea45aae2fca752121cc8160573f1252d6f2c2e7", "bridge-adopted"
                ".github/workflows/release.yml", "a530cc3adb53efb4c79e061c1e8045b8d8f03f61f6030aab742e8b25b075f80d", "gs2-08.9-sealing"
            ]
            "FS-GG/FS.GG.Rendering",
            [
                "scripts/fsgg-coord", "a0689f0ba8cd0180fd1ca60a63bba55e93513732b36e099c541847336ddb3ccf", "bridge-adopted"
                ".config/kit/FS.GG.Kit.receiver.proj", "3a2732d0d6c02cdcaf5409eb53d5ede7ae3caa1039441ea466415fcf846d1ef9", "read-only-local-only"
                ".github/workflows/release.yml", "fa4caa73b2e456bb13efcd1dee307275ad9150dc5326c5b480f3d39b1e577158", "gs2-08.9-sealing"
            ]
            "FS-GG/FS.GG.Governance",
            [
                "scripts/fsgg-coord", "a0689f0ba8cd0180fd1ca60a63bba55e93513732b36e099c541847336ddb3ccf", "bridge-adopted"
                "scripts/materialize-skill-roots.sh", "2eb2821d6529f788d809c506ebdd7ed01393ad4cf4e725e65541411c71b4e866", "read-only-local-only"
                ".github/workflows/publish.yml", "bf9de19f5e39f4c41a1bda488a63a1dff6f4c197246a79c5d94e4b0267a6380c", "gs2-08.9-sealing"
            ]
            "FS-GG/FS.GG.Templates",
            [
                "scripts/fsgg-coord", "a0689f0ba8cd0180fd1ca60a63bba55e93513732b36e099c541847336ddb3ccf", "bridge-adopted"
                "scripts/fsgg-coord-report", "e1f63cda5b499f4fdd4396626c0bac013c49019daf182336df8c7513d24918ba", "read-only-local-only"
                ".github/workflows/release.yml", "2496178a962cfb812622a11d962c1b80afd71aa181b350809d3a7fb6369b914e", "gs2-08.9-sealing"
            ]
            "FS-GG/FS.GG.Game",
            [
                "scripts/fsgg-coord", "a0689f0ba8cd0180fd1ca60a63bba55e93513732b36e099c541847336ddb3ccf", "bridge-adopted"
                "scripts/fsgg-coord-report", "e1f63cda5b499f4fdd4396626c0bac013c49019daf182336df8c7513d24918ba", "read-only-local-only"
                ".github/workflows/release.yml", "1942809b5558786be6edb6551e88114bb1182d5f9fe38cd4acae1069f1efdd34", "gs2-08.9-sealing"
                ".github/workflows/release-skills.yml", "0ae77282741f93390b24c80de847782bf21d034da949b4f89d45d5dd78ccd809", "gs2-08.9-sealing"
                ".github/workflows/skills-package.yml", "4fdbaee168b20af4b58013c1fa20e2484d509214cbd723ecb23826cc9ff50475", "gs2-08.9-sealing"
            ]
            "FS-GG/FS.GG.Audio",
            [
                "scripts/fsgg-coord", "a0689f0ba8cd0180fd1ca60a63bba55e93513732b36e099c541847336ddb3ccf", "bridge-adopted"
                "FS-GG/.github:tests/bridge-package/run.sh@3adada5a9738464291088830c47a30a3a8fc9561", "84e40c27d3f89a8c305eb5aebfe6de148411ca914e334d12c7419360860d8b41", "read-only-local-only"
                ".github/workflows/release.yml", "16bfa564c4f4f691e7d6515c9d4eabc2715541639e8774701e0fb96cb02b4084", "gs2-08.9-sealing"
            ]
            "FS-GG/FS.GG.Net",
            [
                "scripts/fsgg-coord", "a0689f0ba8cd0180fd1ca60a63bba55e93513732b36e099c541847336ddb3ccf", "bridge-adopted"
                ".github/workflows/release.yml", "4a7ee3366f32f0191108ce623f3c2f5007972cc6102b60515f930ee4c43581ed", "gs2-08.9-sealing"
            ]
        ]

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

    let pinnedPackageIds = pins |> List.map (fun pin -> text pin "package") |> Set.ofList
    require (pinnedPackageIds = identity.PackageIds) "GVBR-PINS" $"{repository} package pin identities differ"

    for pin in pins do
        require
            (identity.PackageIds.Contains(text pin "package")
             && text pin "version" = "0.90.0"
             && not (String.IsNullOrWhiteSpace(text pin "path")))
            "GVBR-PINS"
            $"{repository} has an old or unknown pin"

    let receiverRoutes = array receiver "routes"
    require (receiverRoutes.Length = identity.RouteCount) "GVBR-ROUTE-REPORT" $"{repository} route cardinality differs"

    let observedRouteIdentities =
        receiverRoutes
        |> List.map (fun route -> text route "id", text route "sha256", text route "disposition")

    require
        (observedRouteIdentities = expectedRouteIdentities[repository])
        "GVBR-ROUTE-REPORT"
        $"{repository} exact route identity, bytes or disposition differs"

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

let sdd = dependents.GetProperty("sdd")
let sddRelease = sdd.GetProperty("release")
let sddRun = sdd.GetProperty("run")
let sddArtifact = sdd.GetProperty("artifact")
let sddPackages = array sdd "packages"
let sddReceipts = array sdd "publicReceipts"
let sddAdoption = sdd.GetProperty("adoptionEvidence")
let sddAdoptionRoute = sddAdoption.GetProperty("route")
let sddAdoptionQualification = sddAdoption.GetProperty("qualification")
let sddAdoptionGate = sddAdoption.GetProperty("gate")
let sddReleasePreservation = sdd.GetProperty("releaseSourcePreservation")
let sddOutcomes = sdd.GetProperty("outcomeEvidence")

require
    (text sddRelease "tag" = "v2.0.1"
     && text sddRelease "name" = "FS.GG.SDD 2.0.1"
     && text sddRelease "sourceMerge" = "7013aa1915a37c341107fcf9a66f87f3f73cc8c8"
     && text sddRelease "sourceTree" = "7c1603942e83a492a66876325ce28d65a0368e6c"
     && text sddRelease "workflowSha256" = "a530cc3adb53efb4c79e061c1e8045b8d8f03f61f6030aab742e8b25b075f80d"
     && text sddRelease "releaseReadbackSha256" = "861368930c08375311c98bc8a87518e554d2f9e9b1fd0c2246a16ff49440d2ae"
     && text sddRelease "publishedAt" = "2026-09-17T12:29:59Z"
     && not (boolean sddRelease "draft")
     && not (boolean sddRelease "prerelease"))
    "GVBR-SCAFFOLD"
    "SDD public release identity differs"

require
    (integer64 sddRun "id" = 35221457053L
     && text sddRun "head" = "7013aa1915a37c341107fcf9a66f87f3f73cc8c8"
     && text sddRun "status" = "completed"
     && text sddRun "conclusion" = "success"
     && text sddRun "readbackSha256" = "c1925a7a6d949d8079a47d4a3f5fcd0fe622cb5d6408e9c7fa64dc6ba6470bce"
     && text sddRun "logSha256" = "207ba7162b8fc531a524faf80f58cada8cb855d3b397ba0f6f144bca7bd20c2c"
     && integer64 sddRun "publishJobId" = 105205252656L
     && text sddRun "publishJobConclusion" = "success")
    "GVBR-SCAFFOLD"
    "SDD public release run readback differs"

require
    (integer64 sddArtifact "id" = 10497658518L
     && text sddArtifact "name" = "sdd-2.0.1-dual-feed-receipt-7013aa1915a37c341107fcf9a66f87f3f73cc8c8"
     && integer sddArtifact "size" = 10718313
     && text sddArtifact "sha256" = "8783a1ce7f44052cdba04077040e4b9154c142d1bd9f98a4fac30d340b809eff"
     && text sdd "reportSha256" = text sddArtifact "sha256"
     && not (boolean sddArtifact "expired"))
    "GVBR-SCAFFOLD"
    "SDD dual-feed receipt artifact identity differs"

let expectedSddPackages =
    [
        "FS.GG.SDD.Artifacts", "50e771c8e036af72c784a49c6895e41788317fe22ed8771608684c048c0881ac", "230af8fe1cf08481c0284012e227868d7820a6908eca369c224429e0820b6bcc", "ac2e6812e7720e1a8b53876a7a10fff05091716523b448fda794fe57f5cb7f9c", "87120ce72372cef2f6ae21ab8d36d5140093d4287d8a16c813cb076dffa555e0"
        "FS.GG.SDD.Cli", "4f5f20011932e4d8cc334b1ff1ce0b596ecb70cda5b35ff9ccf9f7a7371ab93c", "210cb2bc667efe1d9ebd0f8329e6c57f79a60fe8410c959350ead81d9d7853e9", "ad14882a3eb475de5d21dd5046342b1499ccae2c564a2c7ee6ac8fac6ca9ec3f", "aedc307920d8dffe5ff9a6e64006d21a47c654f2c3c9ddff151f7ea8db7bf5c9"
    ]

require (sddPackages.Length = expectedSddPackages.Length) "GVBR-SCAFFOLD" "SDD public package closure differs"

for package, (id, nugetArchive, githubArchive, entries, payload) in List.zip sddPackages expectedSddPackages do
    require
        (text package "id" = id
         && text package "version" = "2.0.1"
         && text package "nugetArchiveSha256" = nugetArchive
         && text package "githubArchiveSha256" = githubArchive
         && text package "entriesSha256" = entries
         && text package "normalizedPayloadSha256" = payload)
        "GVBR-SCAFFOLD"
        $"SDD public package identity differs for {id}"

let expectedSddReceipts =
    [
        "feed-readback/quint-q2-public.junit.xml", "9458b24dd7a9bccff4b989d40ddec99c5cab8622779ebbbfdbc15da69e4d4546", 16
        "feed-readback/quint-q2-exact-ir-public.junit.xml", "abbb6b71fa0a9c056c16b5b211f4bfc044b455e68d2ac02e5cab0856e67cff67", 20
        "feed-readback/quint-q3-public.junit.xml", "27567bf2188fe7f12c042f808428aaf54f98a359ee36ddd2896191f687e35ade", 22
        "feed-readback/quint-q3-toolchain.json", "22f5bff23d8a0e6419f53275852936a021071123b908ec60bf2948b4dc848209", 0
    ]

require (sddReceipts.Length = expectedSddReceipts.Length) "GVBR-SCAFFOLD" "SDD public receipt closure differs"

for receipt, (path, receiptSha, tests) in List.zip sddReceipts expectedSddReceipts do
    require
        (text receipt "path" = path
         && text receipt "sha256" = receiptSha
         && integer receipt "tests" = tests
         && integer receipt "failures" = 0)
        "GVBR-SCAFFOLD"
        $"SDD public receipt differs for {path}"

require
    (text sdd "publicReceiptsScope" = "typed-sdd-quint-only-not-scaffold-bridge-proof"
     && text sddAdoption "protectedMerge" = "f48204831a2e90db9bfbbbdd59d49d0139c2bf19"
     && text sddAdoption "protectedTree" = "6de1bef60009435abc6a0c907e916de6956185db"
     && text sddAdoption "reportSha256" = "d6c8f7a9d7aa2d2d8cb8d120ccf05bfc03cda6812f67763bdce73cbed9038bed"
     && text sddAdoptionRoute "entrypoint" = "fsgg-sdd scaffold"
     && text sddAdoptionRoute "sourcePath" = "src/FS.GG.SDD.Commands/CommandWorkflow/ScaffoldMutation.fs"
     && text sddAdoptionRoute "sha256" = "f508712e6989844e51228e00eea45aae2fca752121cc8160573f1252d6f2c2e7"
     && text sddAdoptionRoute "disposition" = "bridge-adopted"
     && text sddAdoptionRoute "behavior" = "fresh-emits-exact-existing-preserves-unrelated-conflict-refuses"
     && boolean sddAdoptionQualification "publicRestore"
     && boolean sddAdoptionQualification "installedCommand"
     && text sddAdoptionQualification "productionMutation" = "refused-unavailable-fence"
     && integer sddAdoptionQualification "providerRequests" = 0
     && integer sddAdoptionQualification "materializerFiles" = 37
     && integer sddAdoptionQualification "materializerWritten" = 0
     && text sddAdoptionQualification "materializerSource" = "FS.GG.Kit 0.90.0"
     && integer sddAdoptionQualification "scaffoldAndDriverPassed" = 140
     && text sddAdoptionQualification "materializerContract" = "passed"
     && integer sddAdoptionQualification "failed" = 0)
    "GVBR-SCAFFOLD"
    "SDD protected bridge scaffold report binding differs"

require
    (integer64 sddAdoptionGate "runId" = 35215576977L
     && text sddAdoptionGate "head" = "58521f34d1b89c6d04df16255f51ea73ba3cc018"
     && text sddAdoptionGate "tree" = "6de1bef60009435abc6a0c907e916de6956185db"
     && text sddAdoptionGate "workflowSha256" = "8d284a599298ce4bdc12164f967ff16ce1f0e6f17add3db4a09fe97bdab95080"
     && text sddAdoptionGate "readbackSha256" = "9b98e84e1740ebd4777285a49780a5ece64473c4abfafd7e670dd4472ba2ba20"
     && text sddAdoptionGate "logSha256" = "625eeb5300e7ab2155bc5ac9e09c191c2c5767f800eb338d02f450ab632ce021"
     && integer64 sddAdoptionGate "jobId" = 105183136033L
     && text sddAdoptionGate "conclusion" = "success"
     && integer sddAdoptionGate "commandsPassed" = 1355
     && integer sddAdoptionGate "acceptancePassed" = 46
     && integer sddAdoptionGate "acceptanceSkipped" = 5
     && integer sddAdoptionGate "failed" = 0)
    "GVBR-SCAFFOLD"
    "SDD protected bridge scaffold gate binding differs"

require
    (text sddReleasePreservation "adoptionMerge" = "f48204831a2e90db9bfbbbdd59d49d0139c2bf19"
     && text sddReleasePreservation "adoptionTree" = "6de1bef60009435abc6a0c907e916de6956185db"
     && text sddReleasePreservation "releaseMerge" = "7013aa1915a37c341107fcf9a66f87f3f73cc8c8"
     && text sddReleasePreservation "releaseTree" = "7c1603942e83a492a66876325ce28d65a0368e6c"
     && boolean sddReleasePreservation "adoptionIsAncestor"
     && text sddReleasePreservation "scaffoldSourcePath" = "src/FS.GG.SDD.Commands/CommandWorkflow/ScaffoldMutation.fs"
     && text sddReleasePreservation "adoptionScaffoldSha256" = "f508712e6989844e51228e00eea45aae2fca752121cc8160573f1252d6f2c2e7"
     && text sddReleasePreservation "releaseScaffoldSha256" = "f508712e6989844e51228e00eea45aae2fca752121cc8160573f1252d6f2c2e7"
     && text sddOutcomes "cleanCreation" = "protected-report:fresh-emits-exact"
     && text sddOutcomes "upgrade" = "protected-report:existing-preserves-unrelated-conflict-refuses"
     && text sddOutcomes "oldClientRefused" = "protected-report:installed-cli-refused-unavailable-fence-zero-provider-requests"
     && text sddOutcomes "publicInstall" = "release-run:clean-public-install-2.0.1"
     && text sddOutcomes "composition" = "protected-adoption-tree-gate+unchanged-release-source+public-release-readback")
    "GVBR-SCAFFOLD"
    "SDD public install, creation, upgrade or refusal outcome binding differs"

require
    (text evidence "state" = "qualified"
     && boolean evidence "qualified"
     && not (boolean evidence "replaceBeforeQualification"))
    "GVBR-STATE"
    "aggregate must be final and qualified"

printfn
    "github-v1-bridge-receivers-contract OK receivers=8 baseline=615 successor=%d dispositions=3 packages=3 q=Q3 network=offline state=qualified"
    (integer routes "successorTotal")
