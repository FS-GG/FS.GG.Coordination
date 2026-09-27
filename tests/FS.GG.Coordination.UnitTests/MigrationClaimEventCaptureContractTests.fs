module FS.GG.Coordination.MigrationClaimEventCaptureContractTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Cli

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

let private read path next =
    let request =
        { Kind="rest"; Method="Get"; Uri="https://api.github.test/" + path
          Headers=Map.empty; Body=None; Variables=Map.empty; ApiVersion="2022-11-28"; Idempotency="ReplaySafe" }
    { Request=request; RequestSha256=MigrationReviewDeliveryCaptureContract.requestSha256 request
      StatusCode=200; ResponseHeaders=Map.empty; RawBody="[]"; RawSha256=sha "[]"; NextRequestUri=next }

let private repository =
    { RepositoryId=1L; NodeId="R_1"; FullName="FS-GG/copy"
      Read=read "repos/FS-GG/copy" None }

let private producerRevision = "ca6dd7bd5d14cd3c44f54c89ee87f602c3a3abce"

let private producerRead family =
    let bytes = Encoding.UTF8.GetBytes($"protected source for {family}")
    let gitBytes = Array.append (Encoding.ASCII.GetBytes($"blob {bytes.LongLength}\u0000")) bytes
    let gitSha = gitBytes |> SHA1.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let rawBody =
        JsonSerializer.Serialize(
            {| sha=gitSha; encoding="base64"; content=Convert.ToBase64String bytes |})
    let request =
        { Kind="rest"; Method="Get"
          Uri=$"https://api.github.com/repos/FS-GG/.github/contents/src/{family}.fs?ref={producerRevision}"
          Headers=Map.empty; Body=None; Variables=Map.empty; ApiVersion="2022-11-28"; Idempotency="ReplaySafe" }
    let value =
        { Request=request; RequestSha256=MigrationReviewDeliveryCaptureContract.requestSha256 request
          StatusCode=200; ResponseHeaders=Map.empty; RawBody=rawBody; RawSha256=sha rawBody; NextRequestUri=None }
    let identity = request.Uri + "#sha256:" + (bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant())
    value, identity

let private legacyInventory () =
    let rows =
        MigrationClaimEventCaptureContract.requiredLegacySchemaFamilies
        |> List.map (fun family ->
            let read, identity = producerRead family
            let kind =
                match family with
                | "delivery-receipt" | "legacy-done-receipt" -> ProtectedParserOnly
                | "intake-receipt" -> LocalCacheOnly
                | _ -> ProtectedProducer
            read,
            { ProducerId="FS-GG/.github:" + family
              ProducerRevision=producerRevision
              SourceIdentity=identity
              SchemaFamily=family
              SourceKind=kind })
    let partial =
        { ProducerReads=rows |> List.map fst
          Sources=rows |> List.map snd
          Fingerprint="" }
    { partial with Fingerprint=MigrationClaimEventCaptureContract.legacyInventoryFingerprint partial }

let private census (namespace': MigrationClaimJournalNamespace) (prefix: string) =
    let relative = prefix.Substring("refs/".Length)
    { ClaimNamespace=namespace'; ClaimPrefix=prefix
      ClaimNamespaceReads=[ read ($"repos/FS-GG/copy/git/matching-refs/{relative}") None ]
      ClaimRefs=[] }

let private emptyPass () =
    let partial =
        { ClaimRepository=repository
          ClaimNamespaces=
            [ census ClaimJournalNamespace MigrationClaimEventCaptureContract.claimRefPrefix
              census OperationJournalNamespace MigrationClaimEventCaptureContract.operationRefPrefix ]
          ClaimHistories=[]; ClaimFingerprint="" }
    { partial with ClaimFingerprint=MigrationClaimEventCaptureContract.passFingerprint partial }

[<Fact>]
let ``claim event contract freezes namespaces native roster and schema families`` () =
    Assert.Equal<string list>(
        [ "refs/heads/fsgg/v2/journal/claim/"
          "refs/heads/fsgg/v2/journal/operation/" ],
        MigrationClaimEventCaptureContract.journalRefPrefixes)
    Assert.Equal<MigrationClaimNativeStreamKind list>(
        [ NativeIssueComments; NativeIssueEvents; NativeIssueTimeline ],
        MigrationClaimEventCaptureContract.requiredNativeStreams NativeIssue)
    Assert.Equal<MigrationClaimNativeStreamKind list>(
        [ NativeIssueComments; NativeIssueEvents; NativeIssueTimeline ],
        MigrationClaimEventCaptureContract.requiredNativeStreams NativePullRequest)
    Assert.True(MigrationClaimEventCaptureContract.schemaFamilyAllowed ClaimJournalNamespace ClaimSchemaFamily)
    Assert.False(MigrationClaimEventCaptureContract.schemaFamilyAllowed ClaimJournalNamespace OrdinarySchemaFamily)
    Assert.True(MigrationClaimEventCaptureContract.schemaFamilyAllowed OperationJournalNamespace AdmissionSchemaFamily)
    Assert.True(MigrationClaimEventCaptureContract.schemaFamilyAllowed OperationJournalNamespace OrdinarySchemaFamily)
    Assert.True(MigrationClaimEventCaptureContract.schemaFamilyAllowed OperationJournalNamespace ReviewSchemaFamily)
    Assert.False(MigrationClaimEventCaptureContract.schemaFamilyAllowed OperationJournalNamespace ClaimSchemaFamily)
    Assert.Equal<string list>(
        [ "claim-marker"; "review-decision"; "review-wait"; "delivery-obligation"
          "delivery-receipt"; "intake-marker"; "intake-receipt"; "typed-completion"
          "completion-correction"; "legacy-done-receipt" ],
        MigrationClaimEventCaptureContract.requiredLegacySchemaFamilies)

[<Fact>]
let ``immutable legacy source inventory validates but unresolved producers refuse qualification`` () =
    let inventory = legacyInventory ()
    Assert.Equal(Ok inventory, MigrationClaimEventCaptureContract.validateLegacyInventory inventory)
    Assert.Equal(
        Error "legacy-inventory-producer-unavailable:delivery-receipt,intake-receipt,legacy-done-receipt",
        MigrationClaimEventCaptureContract.qualifyLegacyInventory inventory)

[<Fact>]
let ``legacy inventory refuses missing family altered bytes and caller role upgrades`` () =
    let inventory = legacyInventory ()
    let missing =
        { inventory with
            ProducerReads=inventory.ProducerReads |> List.take (inventory.ProducerReads.Length - 1)
            Sources=inventory.Sources |> List.filter (_.SchemaFamily >> (<>) "legacy-done-receipt")
            Fingerprint="" }
    let missing = { missing with Fingerprint=MigrationClaimEventCaptureContract.legacyInventoryFingerprint missing }
    Assert.Equal(
        Error "legacy-inventory-family-roster",
        MigrationClaimEventCaptureContract.validateLegacyInventory missing)

    let first = inventory.ProducerReads.Head
    let altered = { first with RawBody=first.RawBody + " " }
    let changed =
        { inventory with ProducerReads=altered :: inventory.ProducerReads.Tail; Fingerprint="" }
    let changed = { changed with Fingerprint=MigrationClaimEventCaptureContract.legacyInventoryFingerprint changed }
    Assert.Equal(
        Error "legacy-inventory-source-read",
        MigrationClaimEventCaptureContract.validateLegacyInventory changed)

    let upgraded =
        { inventory with
            Sources=inventory.Sources |> List.map (fun source -> { source with SourceKind=ProtectedProducer })
            Fingerprint="" }
    let upgraded = { upgraded with Fingerprint=MigrationClaimEventCaptureContract.legacyInventoryFingerprint upgraded }
    Assert.Equal(
        Error "legacy-inventory-producer-unavailable:delivery-receipt,intake-receipt,legacy-done-receipt",
        MigrationClaimEventCaptureContract.qualifyLegacyInventory upgraded)

[<Fact>]
let ``empty terminal namespace censuses validate across two equal passes`` () =
    let pass = emptyPass ()
    Assert.Equal(
        Ok { ClaimFirst=pass; ClaimSecond=pass },
        MigrationClaimEventCaptureContract.validateTwoPass { ClaimFirst=pass; ClaimSecond=pass })

[<Fact>]
let ``matching refs continuation and pass drift refuse`` () =
    let pass = emptyPass ()
    let claim = pass.ClaimNamespaces.Head
    let paged =
        { claim with
            ClaimNamespaceReads =
                [ read "repos/FS-GG/copy/git/matching-refs/heads/fsgg/v2/journal/claim/"
                    (Some "https://api.github.test/next") ] }
    let changed = { pass with ClaimNamespaces=paged :: pass.ClaimNamespaces.Tail }
    let changed = { changed with ClaimFingerprint=MigrationClaimEventCaptureContract.passFingerprint changed }
    Assert.Equal(
        Error "claim-journal-namespace-census",
        MigrationClaimEventCaptureContract.validateTwoPass { ClaimFirst=changed; ClaimSecond=changed })
    let second = { pass with ClaimRepository={ repository with NodeId="R_2" }; ClaimFingerprint="" }
    let second = { second with ClaimFingerprint=MigrationClaimEventCaptureContract.passFingerprint second }
    Assert.Equal(
        Error "claim-journal-pass-drift",
        MigrationClaimEventCaptureContract.validateTwoPass { ClaimFirst=pass; ClaimSecond=second })
