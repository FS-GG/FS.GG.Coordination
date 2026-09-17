open System
open System.IO
open System.Text.Json

let fail code message = failwith $"{code}: {message}"

let args = fsi.CommandLineArgs |> Array.skip 1

let root =
    match args with
    | [| value |] -> Path.GetFullPath value
    | _ -> fail "GVPQ-USAGE" "expected repository root"

let read relative =
    File.ReadAllBytes(Path.Combine(root, relative))

let registrationPath =
    "evidence/github-substrate-v2/gs2-08-7/publication-qualification.json"

let casesPath = "evidence/github-substrate-v2/gs2-08-7/independent-cases.json"
let registrationDocument = JsonDocument.Parse(read registrationPath)
let casesDocument = JsonDocument.Parse(read casesPath)
let registration = registrationDocument.RootElement
let cases = casesDocument.RootElement

let text (value: JsonElement) (name: string) = value.GetProperty(name).GetString()

let strings (value: JsonElement) (name: string) =
    value.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList

let require condition code message =
    if not condition then
        fail code message

let sourceMerge = "155f8897424b49906dfb0464ce684e75dd9bda3c"
let sourceTree = "52c9dd051467a7782c4e101dd35e0ca561470f81"

require
    (text registration "schema" = "fsgg.gs2-08.7-publication-qualification/1"
     && text registration "unit" = "GS2-08.7"
     && text registration "state" = "registered"
     && not (registration.GetProperty("accepted").GetBoolean())
     && not (registration.GetProperty("publicationAuthorized").GetBoolean()))
    "GVPQ-REGISTRATION"
    "registration must remain pending, unaccepted and non-authorizing"

let producer = registration.GetProperty("producer")

require
    (text producer "repository" = "FS-GG/.github"
     && text producer "merge" = sourceMerge
     && text producer "tree" = sourceTree
     && text producer "p1Probe" = "tests/bridge-package/run.sh")
    "GVPQ-P1"
    "P1 producer identity differs"

let coherentSet = registration.GetProperty("coherentSet")

require (text coherentSet "observedP1BaselineVersion" = "0.89.0") "GVPQ-BASELINE" "P1 baseline version differs"

let versionSelection = coherentSet.GetProperty("publicationVersion")

require
    (text versionSelection "state" = "unselected"
     && text versionSelection "selectionMilestone" = "P3"
     && text versionSelection "selectionAuthority" = "fresh-required-feed-state"
     && text versionSelection "syntheticQualificationSentinel" = "0.0.0-gs2-08-7-p2-synthetic")
    "GVPQ-VERSION"
    "P2 must not select the publication version"

let packageIds = strings coherentSet "packages"

require
    (packageIds = [ "FS.GG.Coord.Cli"; "FS.GG.Drivers"; "FS.GG.Kit" ])
    "GVPQ-PACKAGES"
    "coherent package closure differs"

let expectedArtifactClosure =
    [
        "package-id"
        "version"
        "archive-sha256"
        "payload-sha256"
        "source-merge"
        "source-tree"
        "signature-subject"
        "attestation-subject"
    ]

require (strings coherentSet "artifactClosure" = expectedArtifactClosure) "GVPQ-CLOSURE" "artifact closure differs"

let requiredFeeds =
    registration.GetProperty("requiredFeeds").EnumerateArray() |> Seq.toList

require
    (requiredFeeds |> List.map (fun feed -> text feed "id") = [ "github-packages"; "nuget-org" ]
     && requiredFeeds
        |> List.forall (fun feed -> text feed "byteIdentity" = "payload-sha256-after-registry-signature-normalization"))
    "GVPQ-FEEDS"
    "required feed or normalized identity semantics differ"

let signing = registration.GetProperty("signing")
let attestation = registration.GetProperty("attestation")

require
    (signing.GetProperty("required").GetBoolean()
     && text signing "producerSemantics" = "verified-producer-signature-over-producer-archive-sha256"
     && text signing "registrySemantics" = "served-archive-may-differ-by-repository-signature"
     && text signing "crossFeedIdentity" =
         "normalized-payload-sha256-excluding-registry-signature-and-package-services-metadata"
     && attestation.GetProperty("required").GetBoolean()
     && text attestation "subject" = "exact-producer-package-archive-sha256"
     && text attestation "source" = "exact-producer-merge-and-tree"
     && text attestation "builder" = "exact-saga-preparation-and-component-publisher-workflow-identities")
    "GVPQ-PROVENANCE"
    "signature and attestation semantics differ"

let workflows =
    registration.GetProperty("workflowCorrespondence").EnumerateArray()
    |> Seq.map (fun workflow -> text workflow "path", text workflow "sha256")
    |> Map.ofSeq

let expectedWorkflows =
    Map.ofList
        [
            ".github/workflows/release-kit.yml", "4d9afe857dfe3a08c1e6e7bfbae54c655700fe5c8ee9992114184e264b4d2433"
            ".github/workflows/release-drivers.yml", "61e77fb8184521c6a0015cfc6b3aa57374bd381555d62ff75b651ed7c7a46736"
            ".github/workflows/release-coord-engine.yml",
            "703d51abf54e75638ea3ce959b95093a0b320adbc799edb4257ab486163e2363"
            ".github/workflows/release-saga-start.yml",
            "3bbe4278a08d8797faa5b600bd97cfef48fa26887f0889f6de781f719530880f"
            ".github/workflows/release-saga-prepare.yml",
            "6498e57524e0552c0beda369b864a12c8b747e57f5c758da21c0cc4a9b0693d3"
            ".github/workflows/release-saga-promote.yml",
            "d16b8cc4ab40de440e17013431e6c9e028b462cd92a02e081e0830db828d2d56"
        ]

require (workflows = expectedWorkflows) "GVPQ-WORKFLOWS" "release workflow identities differ"

let publicInstall = registration.GetProperty("publicInstall")

require
    (text publicInstall "feed" = "nuget-org"
     && text publicInstall "authentication" = "anonymous"
     && text publicInstall "ambientSources" = "cleared"
     && text publicInstall "caches" = "private")
    "GVPQ-PUBLIC-INSTALL"
    "public install isolation differs"

let allowed = strings (registration.GetProperty("operationCeiling")) "allowed"
let forbidden = strings (registration.GetProperty("operationCeiling")) "forbidden"

let expectedForbidden =
    [
        "package-publication"
        "publication-authorization"
        "unit-acceptance"
        "receiver-adoption"
        "live-github-mutation"
        "credential-use"
    ]

require
    (allowed = [ "repository-local-registration"; "offline-synthetic-qualification" ]
     && forbidden = expectedForbidden)
    "GVPQ-CEILING"
    "operation ceiling differs"

let expectedRemaining =
    [
        "P3-release-path-qualification-provenance-binding-and-version-preparation"
        "P4-protected-coherent-publication"
        "P5-public-only-readback-and-native-GS2-08.7-acceptance"
    ]

require (strings registration "remaining" = expectedRemaining) "GVPQ-HORIZON" "P3-P5 boundaries differ"

require
    (text cases "schema" = "fsgg.gs2-08.7-publication-independent-cases/1"
     && cases.GetProperty("synthetic").GetBoolean()
     && text cases "credentials" = "fake"
     && text cases "network" = "offline")
    "GVPQ-CASES"
    "independent cases must remain offline and synthetic"

type PackageEvidence =
    {
        Id: string
        Version: string
        ProducerArchive: string
        Payload: string
        SourceMerge: string
        SourceTree: string
        SignatureVerified: bool
        SignatureSubject: string
        AttestationSubject: string
        PublisherWorkflow: string
        PreparationWorkflow: string
        PromotionWorkflow: string
    }

type FeedEvidence =
    {
        PackageId: string
        Feed: string
        ServedArchive: string
        Payload: string
    }

type InstallEvidence =
    {
        PackageId: string
        Feed: string
        Authentication: string
        AmbientSourcesCleared: bool
        PrivateCaches: bool
        Payload: string
        Executed: bool
    }

type Candidate =
    {
        Packages: PackageEvidence list
        Feeds: FeedEvidence list
        Installs: InstallEvidence list
        AdoptionEvidenceKinds: string list
        Complete: bool
    }

let digest character = String.replicate 64 character

let directWorkflow packageId =
    match packageId with
    | "FS.GG.Coord.Cli" -> ".github/workflows/release-coord-engine.yml"
    | "FS.GG.Drivers" -> ".github/workflows/release-drivers.yml"
    | "FS.GG.Kit" -> ".github/workflows/release-kit.yml"
    | other -> fail "GVPQ-PACKAGE" other

let packageEvidence =
    packageIds
    |> List.mapi (fun index packageId ->
        let archive = digest (string (index + 1))

        {
            Id = packageId
            Version = text versionSelection "syntheticQualificationSentinel"
            ProducerArchive = archive
            Payload = digest (string (index + 4))
            SourceMerge = sourceMerge
            SourceTree = sourceTree
            SignatureVerified = true
            SignatureSubject = archive
            AttestationSubject = archive
            PublisherWorkflow = directWorkflow packageId
            PreparationWorkflow = ".github/workflows/release-saga-prepare.yml"
            PromotionWorkflow = ".github/workflows/release-saga-promote.yml"
        })

let feedEvidence =
    [
        for package in packageEvidence do
            for feed in [ "github-packages"; "nuget-org" ] do
                yield
                    {
                        PackageId = package.Id
                        Feed = feed
                        ServedArchive = digest (if feed = "github-packages" then "8" else "9")
                        Payload = package.Payload
                    }
    ]

let installEvidence =
    packageEvidence
    |> List.map (fun package ->
        {
            PackageId = package.Id
            Feed = "nuget-org"
            Authentication = "anonymous"
            AmbientSourcesCleared = true
            PrivateCaches = true
            Payload = package.Payload
            Executed = package.Id = "FS.GG.Coord.Cli"
        })

let baseline =
    {
        Packages = packageEvidence
        Feeds = feedEvidence
        Installs = installEvidence
        AdoptionEvidenceKinds = []
        Complete = true
    }

let validate candidate =
    let actualIds = candidate.Packages |> List.map _.Id |> List.sort

    if actualIds <> packageIds then
        [ "incomplete-coherent-set" ]
    else
        let failures = ResizeArray<string>()
        let versions = candidate.Packages |> List.map _.Version |> Set.ofList

        if versions.Count <> 1 then
            failures.Add "incomplete-coherent-set"

        if
            candidate.Packages
            |> List.exists (fun package -> package.SourceMerge <> sourceMerge || package.SourceTree <> sourceTree)
        then
            failures.Add "stale-or-mixed-source"

        if
            candidate.Packages
            |> List.exists (fun package ->
                not package.SignatureVerified
                || package.SignatureSubject <> package.ProducerArchive)
        then
            failures.Add "missing-or-invalid-signature"

        if
            candidate.Packages
            |> List.exists (fun package -> package.AttestationSubject <> package.ProducerArchive)
        then
            failures.Add "wrong-attestation-subject"

        if
            candidate.Packages
            |> List.exists (fun package ->
                not (workflows.ContainsKey package.PublisherWorkflow)
                || package.PublisherWorkflow <> directWorkflow package.Id
                || package.PreparationWorkflow <> ".github/workflows/release-saga-prepare.yml"
                || package.PromotionWorkflow <> ".github/workflows/release-saga-promote.yml"
                || not (workflows.ContainsKey package.PreparationWorkflow)
                || not (workflows.ContainsKey package.PromotionWorkflow))
        then
            failures.Add "workflow-mismatch"

        let requiredCopies =
            Set.ofList
                [
                    for packageId in packageIds do
                        for feed in [ "github-packages"; "nuget-org" ] -> packageId, feed
                ]

        let actualCopies =
            candidate.Feeds
            |> List.map (fun feed -> feed.PackageId, feed.Feed)
            |> Set.ofList

        if actualCopies <> requiredCopies || candidate.Feeds.Length <> requiredCopies.Count then
            failures.Add "partial-feeds"
        elif
            candidate.Feeds
            |> List.exists (fun feed ->
                candidate.Packages
                |> List.find (fun package -> package.Id = feed.PackageId)
                |> fun package -> package.Payload <> feed.Payload)
        then
            failures.Add "substituted-package"

        if
            candidate.Installs.Length <> packageIds.Length
            || candidate.Installs
               |> List.exists (fun install ->
                   install.Feed <> "nuget-org"
                   || install.Authentication <> "anonymous"
                   || not install.AmbientSourcesCleared
                   || not install.PrivateCaches
                   || (candidate.Packages
                       |> List.find (fun package -> package.Id = install.PackageId)
                       |> fun package -> package.Payload <> install.Payload))
            || not (
                candidate.Installs
                |> List.exists (fun install -> install.PackageId = "FS.GG.Coord.Cli" && install.Executed)
            )
        then
            failures.Add "missing-public-install"

        if
            candidate.AdoptionEvidenceKinds
            |> List.contains "dashboard-notification-receipt"
        then
            failures.Add "dashboard-receipt-is-not-adoption"

        if not candidate.Complete then
            failures.Add "incomplete-evidence"

        failures |> Seq.distinct |> Seq.toList

require (validate baseline = []) "GVPQ-BASELINE-CANDIDATE" "synthetic complete candidate did not qualify"

let mutate name candidate =
    match name with
    | "substitute-package" ->
        { candidate with
            Feeds =
                candidate.Feeds
                |> List.map (fun feed ->
                    if feed.PackageId = "FS.GG.Coord.Cli" && feed.Feed = "nuget-org" then
                        { feed with Payload = digest "0" }
                    else
                        feed)
        }
    | "mix-source" ->
        { candidate with
            Packages =
                candidate.Packages
                |> List.map (fun package ->
                    if package.Id = "FS.GG.Drivers" then
                        { package with
                            SourceMerge = String.replicate 40 "0"
                        }
                    else
                        package)
        }
    | "remove-package" ->
        { candidate with
            Packages = candidate.Packages |> List.filter (fun package -> package.Id <> "FS.GG.Kit")
        }
    | "replace-attestation-subject" ->
        { candidate with
            Packages =
                candidate.Packages
                |> List.map (fun package ->
                    if package.Id = "FS.GG.Coord.Cli" then
                        { package with
                            AttestationSubject = digest "0"
                        }
                    else
                        package)
        }
    | "remove-feed-copy" ->
        { candidate with
            Feeds =
                candidate.Feeds
                |> List.filter (fun feed -> not (feed.PackageId = "FS.GG.Drivers" && feed.Feed = "nuget-org"))
        }
    | "present-dashboard-receipt-as-adoption" ->
        { candidate with
            AdoptionEvidenceKinds = [ "dashboard-notification-receipt" ]
        }
    | other -> fail "GVPQ-MUTATION" other

let independentCases = cases.GetProperty("cases").EnumerateArray() |> Seq.toList

require (independentCases.Length = 6) "GVPQ-CASE-COUNT" "expected six independent refusal controls"

for independentCase in independentCases do
    let id = text independentCase "id"
    let expectedFailure = text independentCase "expectedFailure"
    let failures = mutate (text independentCase "mutation") baseline |> validate
    let failureText = String.concat "," failures

    require (failures = [ expectedFailure ]) "GVPQ-NEGATIVE" $"{id} expected only {expectedFailure}, got {failureText}"

printfn
    "github-v1-bridge-publication-contract OK packages=%d feeds=%d controls=%d q=Q3 network=offline state=registered"
    packageIds.Length
    requiredFeeds.Length
    independentCases.Length
