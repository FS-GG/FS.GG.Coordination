namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type GitObjectId = private GitObjectId of string
type Sha256Digest = private Sha256Digest of string

type ClaimBinding =
    | TypedClaim of claimId: string * generation: int64
    | NoClaimRequired

type AdmissionPhase =
    | AdmissionsOpen
    | AdmissionsClosing
    | AdmissionsSealed

type StrongAbsenceEvidence =
    | ProviderIdempotencyExclusion of originalRequestDigest: Sha256Digest * evidenceDigest: Sha256Digest
    | ConditionalFenceExclusion of originalRequestDigest: Sha256Digest * evidenceDigest: Sha256Digest
    | OriginalRequestRetirement of originalRequestDigest: Sha256Digest * evidenceDigest: Sha256Digest

type EffectSettlement =
    | EffectSettlementApplied of responseDigest: Sha256Digest
    | EffectSettlementProvenAbsent of StrongAbsenceEvidence
    | EffectSettlementPartial of reason: string
    | EffectSettlementIndeterminate of reason: string

type MutationContext =
    {
        Round: int64
        Manifest: Sha256Digest
        OperationId: string
        OperationGeneration: int64
        Actor: string
        Receiver: string
        Kind: string
        CanonicalTarget: string
        Claim: ClaimBinding
        IntentDigest: Sha256Digest
        TouchSetDigest: Sha256Digest
        OriginatingEpochCommit: GitObjectId
        OriginatingEpochGeneration: int64
    }

type EffectPreconditions =
    {
        ExpectedEpochCommit: GitObjectId
        ExpectedEpochGeneration: int64
        ExpectedClaimGeneration: int64 option
        ExpectedOperationGeneration: int64
    }

type MutationRequest =
    {
        EffectId: string
        RequestDigest: Sha256Digest
        CanonicalRequestBytes: byte array
        Preconditions: EffectPreconditions
    }

type ReadRequest = { Resource: string }

type OutboundRequest =
    | ReadRequest of ReadRequest
    | MutationRequest of MutationRequest

type AuthorityGitObjects =
    {
        Repository: string
        RepositoryId: int64
        Ref: string
        FirstHead: GitObjectId
        TagTarget: GitObjectId
        Commit: GitObjectId
        Parent: GitObjectId option
        GenesisCommit: GitObjectId
        Ancestry: GitObjectId list
        CommitTree: GitObjectId
        CommitBytes: byte array
        TreeBytes: byte array
        TreeEntries: Map<string, GitObjectId>
        EventBlob: GitObjectId * byte array
        HeadBlob: GitObjectId * byte array
        TrustAnchorSha256: Sha256Digest
        ManifestSha256: Sha256Digest
        ClaimJournals: Map<string, JournalObservation>
    }

type AuthorityGitPort =
    {
        ReadObjects: unit -> Result<AuthorityGitObjects, string>
        RereadHead: unit -> Result<GitObjectId, string>
    }

type private Snapshot =
    {
        Commit: GitObjectId
        Generation: int64
        Phase: V1EpochPhase
        Manifest: Sha256Digest
        Trust: Sha256Digest
        Claims: Map<string, int64>
        AdmissionSeal: (GitObjectId * int64 * Sha256Digest) option
    }

type VerifiedAuthoritySnapshot = private VerifiedAuthoritySnapshot of Snapshot

type private Admission =
    {
        Context: MutationContext
        BindingDigest: Sha256Digest
    }

type private Handle =
    {
        Context: MutationContext
        BindingDigest: Sha256Digest
    }

type OperationHandle = private OperationHandle of Handle

type private EffectRecord =
    {
        OperationId: string
        OperationGeneration: int64
        RequestDigest: Sha256Digest
        RequestBytes: byte array
        Preconditions: EffectPreconditions
        Owner: string
        Attempt: int64
        Settlement: EffectSettlement option
    }

type private Registry =
    {
        Address: AggregateAddress
        Head: GitObjectId
        Generation: int64
        ExpectedParent: GitObjectId option
        Persisted: bool
        PendingCommand: string option
        ObservedCommits: Set<GitObjectId>
        Round: int64
        Manifest: Sha256Digest
        Phase: AdmissionPhase
        Admissions: Map<string, Admission>
        Effects: Map<string, EffectRecord>
        SealCommit: GitObjectId option
        SealGeneration: int64 option
        SealDigest: Sha256Digest option
    }

type AdmissionRegistry = private AdmissionRegistry of Registry

type RegistryGitObjects =
    {
        EventObjectId: GitObjectId
        EventBytes: byte array
        HeadObjectId: GitObjectId
        HeadBytes: byte array
        TreeObjectId: GitObjectId
        TreeBytes: byte array
        CommitObjectId: GitObjectId
        CommitBytes: byte array
    }

type private GenesisPlanData =
    {
        Address: AggregateAddress
        AuthorityCommit: GitObjectId
        Manifest: Sha256Digest
        TrustDigest: Sha256Digest
        Commit: JournalCommit
        Objects: RegistryGitObjects
    }

type RegistryGenesisPlan = private RegistryGenesisPlan of GenesisPlanData

type private CommandData =
    | InitializeCommand
    | AdmitCommand of MutationContext * Sha256Digest
    | CloseCommand
    | SealCommand of (string * int64 * Sha256Digest) list * Sha256Digest
    | ReopenCommand
    | IntentCommand of string * EffectRecord
    | SettleCommand of string * string * int64 * EffectSettlement
    | RetryCommand of string * EffectRecord

type RegistryCommand = private RegistryCommand of commandId: string * round: int64 * manifest: Sha256Digest * CommandData

type private ProposalData =
    {
        Cas: CasProposal
        Objects: RegistryGitObjects
        InitialEffect: (string * string * int64 * Sha256Digest) option
        PermitIssued: int ref
    }

type RegistryAppendProposal = private RegistryAppendProposal of ProposalData

type private Permit =
    {
        ConfirmedCommit: GitObjectId
        EffectId: string
        Owner: string
        Attempt: int64
        RequestDigest: Sha256Digest
        Consumed: int ref
    }

type InitialDispatchPermit = private InitialDispatchPermit of Permit

type ProviderEffectObservation =
    | ProviderApplied of responseDigest: Sha256Digest
    | ProviderStronglyAbsent of StrongAbsenceEvidence
    | ProviderPartial of reason: string
    | ProviderIndeterminate of reason: string

type ProviderReconciliationPort =
    {
        Read: string -> string -> int64 -> byte array -> Result<ProviderEffectObservation, string>
    }

type private ProviderProof =
    {
        OperationId: string
        EffectId: string
        Attempt: int64
        RequestDigest: Sha256Digest
        Settlement: EffectSettlement
    }

type VerifiedProviderObservation = private VerifiedProviderObservation of ProviderProof

type RegistryJournalRead =
    {
        Repository: string
        RepositoryId: int64
        Ref: string
        FirstHead: GitObjectId option
        SecondHead: GitObjectId option
        Observation: JournalObservation
        CommitBytes: Map<string, byte array>
        TreeBytes: Map<string, byte array>
    }

type RegistryJournalPort =
    {
        Read: AggregateAddress -> RegistryJournalRead
        Write: RegistryAppendProposal -> ReceivePackOutcome
    }

type DurableAppendDecision =
    | DurableAppendAccepted of AdmissionRegistry * InitialDispatchPermit option
    | DurableAppendParentConflict of AdmissionRegistry option
    | DurableAppendRefused of reason: string * AdmissionRegistry option
    | DurableAppendIndeterminate of string list * AdmissionRegistry option

type private Fence =
    {
        Snapshot: Snapshot
        RegistryHead: GitObjectId
        RegistryGeneration: int64
        HandleDigest: Sha256Digest
        Owner: string
        EffectId: string
        Consumed: int ref
    }

type DispatchFence = private DispatchFence of Fence

type AdmissionDecision =
    | RegistryAdmissionAppended of AdmissionRegistry
    | RegistryAdmissionAlreadyPresent of OperationHandle
    | RegistryAdmissionRefused of string list

type RegistryDecision =
    | RegistryAppended of AdmissionRegistry
    | RegistryRefused of string list

type EffectDecision =
    | EffectIntentAppended of AdmissionRegistry
    | EffectAlreadyInFlight of owner: string
    | EffectAlreadySettled of EffectSettlement
    | EffectRefused of string list

type DispatchDecision =
    | DispatchAuthorized
    | DispatchRefused of string list

[<RequireQualifiedAccess>]
module V1AdmissionRegistry =
    let private lowerHex length (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = length
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let gitObjectId value =
        if lowerHex 40 value then
            Ok(GitObjectId value)
        else
            Error "invalid-git-object-id"

    let sha256Digest value =
        if lowerHex 64 value then
            Ok(Sha256Digest value)
        else
            Error "invalid-sha256-digest"

    let gitObjectIdValue (GitObjectId value) = value
    let sha256Value (Sha256Digest value) = value

    let private bytesSha256 (bytes: byte array) =
        SHA256.HashData bytes
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
        |> Sha256Digest

    let private gitOid kind (bytes: byte array) =
        let header = Encoding.UTF8.GetBytes($"{kind} {bytes.Length}\u0000")

        SHA1.HashData(Array.append header bytes)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
        |> GitObjectId

    let private nextHead (GitObjectId parent) generation discriminator =
        Encoding.UTF8.GetBytes($"{parent}|{generation}|{discriminator}")
        |> SHA1.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
        |> GitObjectId

    let private parseEvent (bytes: byte array) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            let duplicates =
                names
                |> List.countBy id
                |> List.choose (fun (n, count) -> if count > 1 then Some n else None)

            if not duplicates.IsEmpty then
                Error "event-duplicate-field"
            else
                let schema = root.GetProperty("schema").GetString()
                let phase = root.GetProperty("phase").GetString()
                let fleet = root.GetProperty("fleetId").GetString()
                let manifest = root.GetProperty("manifestSha256").GetString()
                let trust = root.GetProperty("trustAnchorSha256").GetString()

                if
                    schema <> "fsgg.github-substrate.epoch-event/1"
                    && schema <> "fsgg.github-substrate.epoch-event/2"
                then
                    Error "event-schema"
                elif fleet <> "fs-gg-production" then
                    Error "event-fleet"
                else
                    match phase, sha256Digest manifest, sha256Digest trust with
                    | "OperatingV1", Ok manifest, Ok trust -> Ok(V1OperatingV1, manifest, trust, None)
                    | "Preparing", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        match
                            gitObjectId (root.GetProperty("admissionSealCommit").GetString()),
                            sha256Digest (root.GetProperty("admissionCohortSha256").GetString())
                        with
                        | Ok commit, Ok digest ->
                            Ok(
                                V1Preparing,
                                manifest,
                                trust,
                                Some(commit, root.GetProperty("admissionSealGeneration").GetInt64(), digest)
                            )
                        | _ -> Error "event-admission-seal"
                    | "FreezeRequested", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1FreezeRequested, manifest, trust, None)
                    | "Frozen", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1Frozen, manifest, trust, None)
                    | "SwitchedV2", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1SwitchedV2, manifest, trust, None)
                    | "VerifiedV2", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1VerifiedV2, manifest, trust, None)
                    | "OpenV2", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1OpenV2, manifest, trust, None)
                    | "ObservingV2", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1ObservingV2, manifest, trust, None)
                    | "ContractingV1", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1ContractingV1, manifest, trust, None)
                    | "OperatingV2", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1OperatingV2, manifest, trust, None)
                    | "RollingBack", Ok manifest, Ok trust when schema.EndsWith("/2", StringComparison.Ordinal) ->
                        Ok(V1RollingBack, manifest, trust, None)
                    | _ -> Error "event-phase-or-digest"
        with
        | :? JsonException -> Error "event-json"
        | :? KeyNotFoundException -> Error "event-field"
        | :? InvalidOperationException -> Error "event-field"

    let private parseHead eventBytes (bytes: byte array) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let generation = root.GetProperty("generation").GetInt64()
            let eventDigest = root.GetProperty("eventDigest").GetString()
            let expected = bytesSha256 eventBytes |> sha256Value

            let address =
                ShardedJournalAdapter.address Cutover "fleet-cutover:fs-gg-production"
                |> Result.defaultWith (string >> invalidOp)

            let canonical =
                ShardedJournalAdapter.canonicalJson (Encoding.UTF8.GetString bytes)
                |> Result.map (fun value -> value.AsSpan().SequenceEqual bytes)
                |> Result.defaultValue false

            if not canonical then
                Error "head-canonical-json"
            elif root.GetProperty("schemaVersion").GetInt32() <> 1 then
                Error "head-schema"
            elif
                root.GetProperty("aggregateId").GetString() <> address.CanonicalId
                || root.GetProperty("aggregateDigest").GetString() <> address.Digest
                || root.GetProperty("journalKind").GetString() <> "cutover"
                || root.GetProperty("shard").GetString() <> address.Shard
            then
                Error "head-address"
            elif root.GetProperty("terminal").GetBoolean() then
                Error "head-terminal"
            elif generation < 1L then
                Error "journal-generation"
            elif eventDigest <> expected then
                Error "journal-event-digest"
            else
                Ok generation
        with
        | :? JsonException -> Error "head-json"
        | :? KeyNotFoundException -> Error "head-field"
        | :? InvalidOperationException -> Error "head-field"

    let private parseTree (bytes: byte array) =
        try
            let entries = ResizeArray<string * GitObjectId>()
            let names = HashSet<string>(StringComparer.Ordinal)
            let mutable offset = 0

            while offset < bytes.Length do
                let nul = Array.IndexOf(bytes, 0uy, offset)

                if nul < offset then
                    invalidOp "tree-entry"

                let header = Encoding.UTF8.GetString(bytes, offset, nul - offset)
                let separator = header.IndexOf ' '

                if
                    separator < 1
                    || header.Substring(0, separator) <> "100644"
                    || nul + 21 > bytes.Length
                then
                    invalidOp "tree-entry"

                let name = header.Substring(separator + 1)

                if String.IsNullOrEmpty name || name.Contains('/') || not (names.Add name) then
                    invalidOp "tree-entry-name"

                let oid =
                    bytes[(nul + 1) .. (nul + 20)]
                    |> Convert.ToHexString
                    |> _.ToLowerInvariant()
                    |> GitObjectId

                entries.Add(name, oid)
                offset <- nul + 21

            Ok(Map.ofSeq entries)
        with _ ->
            Error "tree-bytes"

    let private parseCommit (bytes: byte array) =
        try
            let text = UTF8Encoding(false, true).GetString bytes
            let separator = text.IndexOf("\n\n", StringComparison.Ordinal)

            if separator < 0 then
                invalidOp "commit-header-boundary"

            let headers = text.Substring(0, separator).Split('\n') |> Array.toList

            let values prefix =
                headers
                |> List.choose (fun line ->
                    if line.StartsWith(prefix, StringComparison.Ordinal) then
                        Some(line.Substring(prefix.Length))
                    else
                        None)

            match values "tree ", values "parent " with
            | [ tree ], [] -> gitObjectId tree |> Result.map (fun treeOid -> treeOid, None)
            | [ tree ], [ parent ] ->
                match gitObjectId tree, gitObjectId parent with
                | Ok treeOid, Ok parentOid -> Ok(treeOid, Some parentOid)
                | _ -> Error "commit-object-reference"
            | _ -> Error "commit-header-shape"
        with
        | :? DecoderFallbackException -> Error "commit-utf8"

    let readVerified (port: AuthorityGitPort) =
        match port.ReadObjects() with
        | Error reason -> Error [ "authority-read:" + reason ]
        | Ok read ->
            let eventOid, eventBytes = read.EventBlob
            let headOid, headBytes = read.HeadBlob
            let errors = ResizeArray<string>()

            let claims =
                read.ClaimJournals
                |> Map.toList
                |> List.choose (fun (claimId, observation) ->
                    match ShardedJournalAdapter.address Claim claimId with
                    | Error _ ->
                        errors.Add("claim-address:" + claimId)
                        None
                    | Ok address ->
                        match ShardedJournalAdapter.validate address observation with
                        | Ok snapshot when
                            snapshot.Commits
                            |> List.forall (fun commit ->
                                lowerHex 40 commit.CommitOid
                                && lowerHex 40 commit.TreeOid
                                && (commit.ParentOid |> Option.forall (lowerHex 40)))
                            ->
                            Some(claimId, snapshot.Current.Head.Generation)
                        | Ok _ ->
                            errors.Add("claim-git-object-id:" + claimId)
                            None
                        | Error failure ->
                            errors.Add($"claim-journal:{claimId}:{failure}")
                            None)
                |> Map.ofList

            if
                read.Repository <> "FS-GG/FS.GG.Coordination.Authority"
                || read.RepositoryId <> 1351660651L
            then
                errors.Add "authority-repository"

            if read.Ref <> "refs/heads/fsgg/v2/journal/cutover/d5" then
                errors.Add "authority-ref"

            match port.RereadHead() with
            | Ok secondHead when read.FirstHead = secondHead && read.Commit = secondHead -> ()
            | Ok _ -> errors.Add "authority-head-moved"
            | Error reason -> errors.Add("authority-reread:" + reason)

            if read.TagTarget <> read.Commit then
                errors.Add "authority-tag"

            if
                read.Ancestry.IsEmpty
                || read.Ancestry.Head <> read.Commit
                || (read.Ancestry |> List.last) <> read.GenesisCommit
            then
                errors.Add "authority-ancestry"

            if (read.Ancestry |> List.distinct |> List.length) <> read.Ancestry.Length then
                errors.Add "authority-ancestry-cycle"

            match read.Parent, read.Ancestry with
            | None, [ only ] when only = read.GenesisCommit -> ()
            | Some parent, _ :: second :: _ when parent = second -> ()
            | _ -> errors.Add "authority-parent-ancestry"

            if gitOid "blob" eventBytes <> eventOid then
                errors.Add "event-object-id"

            if gitOid "blob" headBytes <> headOid then
                errors.Add "head-object-id"

            if gitOid "tree" read.TreeBytes <> read.CommitTree then
                errors.Add "tree-object-id"

            if gitOid "commit" read.CommitBytes <> read.Commit then
                errors.Add "commit-object-id"

            match read.Parent with
            | Some parent when parent = read.Commit -> errors.Add "ancestry-cycle"
            | _ -> ()

            match
                parseTree read.TreeBytes,
                parseCommit read.CommitBytes,
                parseEvent eventBytes,
                parseHead eventBytes headBytes
            with
            | Ok actualEntries, Ok(commitTree, commitParent), Ok(phase, manifest, trust, admissionSeal), Ok generation ->
                if actualEntries <> read.TreeEntries then
                    errors.Add "tree-entry-readback"

                if commitTree <> read.CommitTree || commitParent <> read.Parent then
                    errors.Add "commit-readback"

                if Map.tryFind "event.json" actualEntries <> Some eventOid then
                    errors.Add "event-tree-entry"

                if Map.tryFind "head.json" actualEntries <> Some headOid then
                    errors.Add "head-tree-entry"

                if actualEntries.Count <> 2 then
                    errors.Add "tree-shape"

                if manifest <> read.ManifestSha256 then
                    errors.Add "manifest-binding"

                if trust <> read.TrustAnchorSha256 then
                    errors.Add "trust-binding"

                if errors.Count = 0 then
                    Ok(
                        VerifiedAuthoritySnapshot
                            {
                                Commit = read.Commit
                                Generation = generation
                                Phase = phase
                                Manifest = manifest
                                Trust = trust
                                Claims = claims
                                AdmissionSeal = admissionSeal
                            }
                    )
                else
                    Error(List.ofSeq errors)
            | Error error, _, _, _
            | _, Error error, _, _
            | _, _, Error error, _
            | _, _, _, Error error -> Error(List.ofSeq errors @ [ error ])

    let private registryAddress () =
        ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production"
        |> Result.defaultWith (string >> invalidOp)

    let private canonicalReadIdentity (read: RegistryJournalRead) =
        let address = registryAddress ()
        read.Repository = "FS-GG/FS.GG.Coordination.Authority"
        && read.RepositoryId = 1351660651L
        && read.Ref = address.Ref
        && read.FirstHead = read.SecondHead

    let aggregateAddress (AdmissionRegistry registry) = registry.Address
    let head (AdmissionRegistry registry) = registry.Head
    let generation (AdmissionRegistry registry) = registry.Generation
    let phase (AdmissionRegistry registry) = registry.Phase

    let persistedHead (AdmissionRegistry registry) =
        if registry.Persisted then
            Ok registry.Head
        else
            Error [ "registry-state-not-durably-observed" ]

    let private bindingDigest (context: MutationContext) =
        let claim = JsonObject()

        match context.Claim with
        | TypedClaim(claimId, generation) ->
            claim.Add("claimId", claimId)
            claim.Add("generation", generation)
            claim.Add("kind", "typed")
        | NoClaimRequired -> claim.Add("kind", "none")

        let value = JsonObject()
        value.Add("actor", context.Actor)
        value.Add("canonicalTarget", context.CanonicalTarget)
        value.Add("claim", claim)
        value.Add("intentSha256", sha256Value context.IntentDigest)
        value.Add("kind", context.Kind)
        value.Add("manifestSha256", sha256Value context.Manifest)
        value.Add("operationGeneration", context.OperationGeneration)
        value.Add("operationId", context.OperationId)
        value.Add("originatingEpochCommit", gitObjectIdValue context.OriginatingEpochCommit)
        value.Add("originatingEpochGeneration", context.OriginatingEpochGeneration)
        value.Add("receiver", context.Receiver)
        value.Add("round", context.Round)
        value.Add("touchSetSha256", sha256Value context.TouchSetDigest)

        value.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        |> ShardedJournalAdapter.canonicalJson
        |> Result.defaultWith invalidOp
        |> bytesSha256

    let private phaseName =
        function
        | AdmissionsOpen -> "open"
        | AdmissionsClosing -> "closing"
        | AdmissionsSealed -> "sealed"

    let private phaseValue =
        function
        | "open" -> AdmissionsOpen
        | "closing" -> AdmissionsClosing
        | "sealed" -> AdmissionsSealed
        | _ -> invalidOp "admission-event-phase"

    let private contextJson (context: MutationContext) =
        let claim = JsonObject()

        match context.Claim with
        | TypedClaim(claimId, generation) ->
            claim.Add("claimId", claimId)
            claim.Add("generation", generation)
            claim.Add("kind", "typed")
        | NoClaimRequired -> claim.Add("kind", "none")

        let value = JsonObject()
        value.Add("actor", context.Actor)
        value.Add("canonicalTarget", context.CanonicalTarget)
        value.Add("claim", claim)
        value.Add("intentSha256", sha256Value context.IntentDigest)
        value.Add("kind", context.Kind)
        value.Add("manifestSha256", sha256Value context.Manifest)
        value.Add("operationGeneration", context.OperationGeneration)
        value.Add("operationId", context.OperationId)
        value.Add("originatingEpochCommit", gitObjectIdValue context.OriginatingEpochCommit)
        value.Add("originatingEpochGeneration", context.OriginatingEpochGeneration)
        value.Add("receiver", context.Receiver)
        value.Add("round", context.Round)
        value.Add("touchSetSha256", sha256Value context.TouchSetDigest)
        value

    let private preconditionsJson (preconditions: EffectPreconditions) =
        let value = JsonObject()

        match preconditions.ExpectedClaimGeneration with
        | Some generation -> value.Add("expectedClaimGeneration", generation)
        | None -> value.Add("expectedClaimGeneration", null)

        value.Add("expectedEpochCommit", gitObjectIdValue preconditions.ExpectedEpochCommit)
        value.Add("expectedEpochGeneration", preconditions.ExpectedEpochGeneration)
        value.Add("expectedOperationGeneration", preconditions.ExpectedOperationGeneration)
        value

    let private settlementJson settlement =
        let value = JsonObject()

        match settlement with
        | None -> value.Add("status", "in-flight")
        | Some(EffectSettlementApplied digest) ->
            value.Add("responseSha256", sha256Value digest)
            value.Add("status", "applied")
        | Some(EffectSettlementProvenAbsent evidence) ->
            let mechanism, binding, proof =
                match evidence with
                | ProviderIdempotencyExclusion(requestKey, proof) -> "provider-idempotency", requestKey, proof
                | ConditionalFenceExclusion(fence, proof) -> "conditional-fence", fence, proof
                | OriginalRequestRetirement(retirement, proof) -> "request-retirement", retirement, proof

            value.Add("bindingSha256", sha256Value binding)
            value.Add("evidenceSha256", sha256Value proof)
            value.Add("mechanism", mechanism)
            value.Add("status", "proven-absent")
        | Some(EffectSettlementPartial reason) ->
            value.Add("reason", reason)
            value.Add("status", "partial")
        | Some(EffectSettlementIndeterminate reason) ->
            value.Add("reason", reason)
            value.Add("status", "indeterminate")

        value

    let private effectJson (effectId: string) (effect: EffectRecord) =
        let value = JsonObject()
        value.Add("attempt", effect.Attempt)
        let requestBase64 = Convert.ToBase64String effect.RequestBytes
        value.Add("canonicalRequestBase64", requestBase64)
        value.Add("effectId", effectId)
        value.Add("operationGeneration", effect.OperationGeneration)
        value.Add("operationId", effect.OperationId)
        value.Add("owner", effect.Owner)
        value.Add("preconditions", preconditionsJson effect.Preconditions)
        value.Add("requestSha256", sha256Value effect.RequestDigest)
        value

    let private canonicalEventBytes (commandId: string) (previous: Registry option) (registry: Registry) =
        let command = registry.PendingCommand |> Option.defaultWith (fun () -> invalidOp "registry-command-missing")
        let payload = JsonObject()

        let kind =
            if command = "initialize" then
                "initialize"
            elif command.StartsWith("admit:", StringComparison.Ordinal) then
                let prior = previous |> Option.map _.Admissions |> Option.defaultValue Map.empty

                let _, admission =
                    registry.Admissions
                    |> Map.toList
                    |> List.filter (fun (id, value) -> Map.tryFind id prior <> Some value)
                    |> List.exactlyOne

                payload.Add("bindingSha256", sha256Value admission.BindingDigest)
                payload.Add("context", contextJson admission.Context)
                "admit"
            elif command = "close-admissions" then
                "close"
            elif command.StartsWith("seal:", StringComparison.Ordinal) then
                let cohort = JsonArray()

                registry.Admissions
                |> Map.toSeq
                |> Seq.iter (fun (operationId, admission) ->
                    let memberValue = JsonObject()
                    memberValue.Add("bindingSha256", sha256Value admission.BindingDigest)
                    memberValue.Add("operationGeneration", admission.Context.OperationGeneration)
                    memberValue.Add("operationId", operationId)
                    cohort.Add memberValue)

                payload.Add("cohort", cohort)
                let cohortDigestValue =
                    registry.SealDigest
                    |> Option.map sha256Value
                    |> Option.defaultWith (fun () -> invalidOp "seal-digest")

                payload.Add("cohortSha256", cohortDigestValue)
                "seal"
            elif command.StartsWith("reopen:", StringComparison.Ordinal) then
                "reopen"
            elif command.StartsWith("effect-in-flight:", StringComparison.Ordinal) then
                let effectId = command.Substring("effect-in-flight:".Length)
                let effect = Map.find effectId registry.Effects
                let intentNode: JsonNode = upcast effectJson effectId effect
                payload.Add("intent", intentNode)
                "intent"
            elif command.StartsWith("settle:", StringComparison.Ordinal) then
                let effectId = command.Substring("settle:".Length)
                let effect = registry.Effects[effectId]
                payload.Add("attempt", effect.Attempt)
                payload.Add("effectId", effectId)
                payload.Add("owner", effect.Owner)
                payload.Add("settlement", settlementJson effect.Settlement)
                "settle"
            elif command.StartsWith("retry:", StringComparison.Ordinal) then
                let effectId = command.Substring("retry:".Length)
                let effect = Map.find effectId registry.Effects
                let intentNode: JsonNode = upcast effectJson effectId effect
                payload.Add("intent", intentNode)
                "retry"
            else
                invalidOp "unknown-registry-command"

        let root = JsonObject()
        root.Add("commandId", commandId)
        root.Add("kind", kind)
        root.Add("manifestSha256", sha256Value registry.Manifest)
        root.Add("payload", payload)
        root.Add("round", registry.Round)
        root.Add("schema", "fsgg.github-substrate.admission-event/1")

        root.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        |> ShardedJournalAdapter.canonicalJson
        |> Result.defaultWith invalidOp

    let private requiredString (name: string) (value: JsonElement) =
        let result = value.GetProperty(name).GetString()

        if String.IsNullOrWhiteSpace result then
            invalidOp ("admission-event-" + name)

        result

    let private parseContext (value: JsonElement) =
        let digest name =
            requiredString name value |> sha256Digest |> Result.defaultWith invalidOp

        let claimValue = value.GetProperty("claim")

        let claim =
            match requiredString "kind" claimValue with
            | "none" -> NoClaimRequired
            | "typed" -> TypedClaim(requiredString "claimId" claimValue, claimValue.GetProperty("generation").GetInt64())
            | _ -> invalidOp "admission-event-claim"

        {
            Round = value.GetProperty("round").GetInt64()
            Manifest = digest "manifestSha256"
            OperationId = requiredString "operationId" value
            OperationGeneration = value.GetProperty("operationGeneration").GetInt64()
            Actor = requiredString "actor" value
            Receiver = requiredString "receiver" value
            Kind = requiredString "kind" value
            CanonicalTarget = requiredString "canonicalTarget" value
            Claim = claim
            IntentDigest = digest "intentSha256"
            TouchSetDigest = digest "touchSetSha256"
            OriginatingEpochCommit =
                requiredString "originatingEpochCommit" value
                |> gitObjectId
                |> Result.defaultWith invalidOp
            OriginatingEpochGeneration = value.GetProperty("originatingEpochGeneration").GetInt64()
        }

    let private parsePreconditions (value: JsonElement) =
        let claim = value.GetProperty("expectedClaimGeneration")

        {
            ExpectedEpochCommit =
                requiredString "expectedEpochCommit" value
                |> gitObjectId
                |> Result.defaultWith invalidOp
            ExpectedEpochGeneration = value.GetProperty("expectedEpochGeneration").GetInt64()
            ExpectedClaimGeneration =
                if claim.ValueKind = JsonValueKind.Null then
                    None
                else
                    Some(claim.GetInt64())
            ExpectedOperationGeneration = value.GetProperty("expectedOperationGeneration").GetInt64()
        }

    let private exactObject (expected: string list) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            invalidOp "admission-event-object"

        let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList

        if names.Length <> (names |> List.distinct |> List.length) || Set.ofList names <> Set.ofList expected then
            invalidOp "admission-event-fields"

    let private parseSettlement (value: JsonElement) =
        match requiredString "status" value with
        | "in-flight" ->
            exactObject [ "status" ] value
            None
        | "applied" ->
            exactObject [ "responseSha256"; "status" ] value
            requiredString "responseSha256" value
            |> sha256Digest
            |> Result.defaultWith invalidOp
            |> EffectSettlementApplied
            |> Some
        | "proven-absent" ->
            exactObject [ "bindingSha256"; "evidenceSha256"; "mechanism"; "status" ] value
            let binding = requiredString "bindingSha256" value |> sha256Digest |> Result.defaultWith invalidOp
            let proof = requiredString "evidenceSha256" value |> sha256Digest |> Result.defaultWith invalidOp

            match requiredString "mechanism" value with
            | "provider-idempotency" -> ProviderIdempotencyExclusion(binding, proof)
            | "conditional-fence" -> ConditionalFenceExclusion(binding, proof)
            | "request-retirement" -> OriginalRequestRetirement(binding, proof)
            | _ -> invalidOp "admission-event-absence-mechanism"
            |> EffectSettlementProvenAbsent
            |> Some
        | "partial" ->
            exactObject [ "reason"; "status" ] value
            Some(EffectSettlementPartial(requiredString "reason" value))
        | "indeterminate" ->
            exactObject [ "reason"; "status" ] value
            Some(EffectSettlementIndeterminate(requiredString "reason" value))
        | _ -> invalidOp "admission-event-settlement"

    let private validateContextShape (value: JsonElement) =
        exactObject
            [
                "actor"
                "canonicalTarget"
                "claim"
                "intentSha256"
                "kind"
                "manifestSha256"
                "operationGeneration"
                "operationId"
                "originatingEpochCommit"
                "originatingEpochGeneration"
                "receiver"
                "round"
                "touchSetSha256"
            ]
            value

        let claim = value.GetProperty("claim")

        match requiredString "kind" claim with
        | "none" -> exactObject [ "kind" ] claim
        | "typed" -> exactObject [ "claimId"; "generation"; "kind" ] claim
        | _ -> invalidOp "admission-event-claim"

    let private parseIntent (value: JsonElement) =
        exactObject
            [
                "attempt"
                "canonicalRequestBase64"
                "effectId"
                "operationGeneration"
                "operationId"
                "owner"
                "preconditions"
                "requestSha256"
            ]
            value

        let preconditionsValue = value.GetProperty("preconditions")

        exactObject
            [
                "expectedClaimGeneration"
                "expectedEpochCommit"
                "expectedEpochGeneration"
                "expectedOperationGeneration"
            ]
            preconditionsValue

        let requestBytes = requiredString "canonicalRequestBase64" value |> Convert.FromBase64String

        if requestBytes.Length = 0 then
            invalidOp "admission-event-empty-request"

        let requestDigest =
            requiredString "requestSha256" value
            |> sha256Digest
            |> Result.defaultWith invalidOp

        if bytesSha256 requestBytes <> requestDigest then
            invalidOp "admission-event-request-digest"

        requiredString "effectId" value,
        {
            OperationId = requiredString "operationId" value
            OperationGeneration = value.GetProperty("operationGeneration").GetInt64()
            RequestDigest = requestDigest
            RequestBytes = requestBytes
            Preconditions = parsePreconditions preconditionsValue
            Owner = requiredString "owner" value
            Attempt = value.GetProperty("attempt").GetInt64()
            Settlement = None
        }

    let decodeEvent (bytes: byte array) =
        try
            if obj.ReferenceEquals(bytes, null) || bytes.Length = 0 then
                invalidOp "admission-event-empty"

            let text = UTF8Encoding(false, true).GetString bytes
            let canonical = ShardedJournalAdapter.canonicalJson text |> Result.defaultWith invalidOp

            if canonical <> bytes then
                invalidOp "admission-event-not-canonical"

            use document = JsonDocument.Parse bytes
            let root = document.RootElement

            exactObject [ "commandId"; "kind"; "manifestSha256"; "payload"; "round"; "schema" ] root

            if requiredString "schema" root <> "fsgg.github-substrate.admission-event/1" then
                invalidOp "admission-event-schema"

            let commandId = requiredString "commandId" root
            let round = root.GetProperty("round").GetInt64()

            if round < 1L then
                invalidOp "admission-event-round"

            let manifest =
                requiredString "manifestSha256" root
                |> sha256Digest
                |> Result.defaultWith invalidOp

            let payload = root.GetProperty("payload")

            let command =
                match requiredString "kind" root with
                | "initialize" ->
                    exactObject [] payload
                    InitializeCommand
                | "admit" ->
                    exactObject [ "bindingSha256"; "context" ] payload
                    let contextValue = payload.GetProperty("context")
                    validateContextShape contextValue
                    let context = parseContext contextValue
                    let binding = requiredString "bindingSha256" payload |> sha256Digest |> Result.defaultWith invalidOp

                    if binding <> bindingDigest context then
                        invalidOp "admission-event-binding"

                    AdmitCommand(context, binding)
                | "close" ->
                    exactObject [] payload
                    CloseCommand
                | "seal" ->
                    exactObject [ "cohort"; "cohortSha256" ] payload
                    let members =
                        payload.GetProperty("cohort").EnumerateArray()
                        |> Seq.map (fun memberValue ->
                            exactObject [ "bindingSha256"; "operationGeneration"; "operationId" ] memberValue
                            requiredString "operationId" memberValue,
                            memberValue.GetProperty("operationGeneration").GetInt64(),
                            requiredString "bindingSha256" memberValue
                            |> sha256Digest
                            |> Result.defaultWith invalidOp)
                        |> Seq.toList

                    if members <> List.sortBy (fun (id, _, _) -> id) members || members.Length <> (members |> List.distinctBy (fun (id, _, _) -> id) |> List.length) then
                        invalidOp "admission-event-cohort-order"

                    let digest = requiredString "cohortSha256" payload |> sha256Digest |> Result.defaultWith invalidOp
                    SealCommand(members, digest)
                | "reopen" ->
                    exactObject [] payload
                    ReopenCommand
                | "intent" ->
                    exactObject [ "intent" ] payload
                    let effectId, effect = parseIntent (payload.GetProperty("intent"))
                    IntentCommand(effectId, effect)
                | "settle" ->
                    exactObject [ "attempt"; "effectId"; "owner"; "settlement" ] payload
                    let settlementValue = payload.GetProperty("settlement")
                    let settlement = parseSettlement settlementValue |> Option.defaultWith (fun () -> invalidOp "settlement-in-flight")
                    SettleCommand(
                        requiredString "effectId" payload,
                        requiredString "owner" payload,
                        payload.GetProperty("attempt").GetInt64(),
                        settlement
                    )
                | "retry" ->
                    exactObject [ "intent" ] payload
                    let effectId, effect = parseIntent (payload.GetProperty("intent"))
                    RetryCommand(effectId, effect)
                | _ -> invalidOp "admission-event-kind"

            Ok(RegistryCommand(commandId, round, manifest, command))
        with ex ->
            Error [ "admission-event-decode:" + ex.Message ]

    let private rawCommitErrors (read: RegistryJournalRead) (commit: JournalCommit) =
        let errors = ResizeArray<string>()

        match Map.tryFind commit.CommitOid read.CommitBytes with
        | None -> errors.Add("registry-commit-bytes-missing:" + commit.CommitOid)
        | Some bytes ->
            if gitOid "commit" bytes |> gitObjectIdValue <> commit.CommitOid then
                errors.Add("registry-commit-object-id:" + commit.CommitOid)

            match parseCommit bytes with
            | Ok(tree, parent) when gitObjectIdValue tree = commit.TreeOid && parent |> Option.map gitObjectIdValue = commit.ParentOid -> ()
            | _ -> errors.Add("registry-commit-content:" + commit.CommitOid)

        match Map.tryFind commit.TreeOid read.TreeBytes with
        | None -> errors.Add("registry-tree-bytes-missing:" + commit.TreeOid)
        | Some bytes ->
            if gitOid "tree" bytes |> gitObjectIdValue <> commit.TreeOid then
                errors.Add("registry-tree-object-id:" + commit.TreeOid)

            match parseTree bytes with
            | Ok entries when
                entries
                = Map.ofList
                    [
                        "event.json", gitOid "blob" commit.Event.Bytes
                        "head.json", gitOid "blob" commit.HeadBytes
                    ]
                ->
                ()
            | _ -> errors.Add("registry-tree-content:" + commit.TreeOid)

        List.ofSeq errors

    let private cohortForAdmissions (admissions: Map<string, Admission>) =
        let members = JsonArray()

        admissions
        |> Map.toSeq
        |> Seq.iter (fun (operationId, admission) ->
            let memberValue = JsonObject()
            memberValue.Add("bindingSha256", sha256Value admission.BindingDigest)
            memberValue.Add("operationGeneration", admission.Context.OperationGeneration)
            memberValue.Add("operationId", operationId)
            members.Add memberValue)

        members.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        |> ShardedJournalAdapter.canonicalJson
        |> Result.defaultWith invalidOp
        |> bytesSha256

    let private unresolvedFor operationId (effects: Map<string, EffectRecord>) =
        effects
        |> Map.exists (fun _ effect ->
            effect.OperationId = operationId
            && (match effect.Settlement with
                | None
                | Some(EffectSettlementPartial _)
                | Some(EffectSettlementIndeterminate _) -> true
                | _ -> false))

    let private replayContextValid (context: MutationContext) =
        context.Round > 0L
        && context.OperationGeneration > 0L
        && context.OriginatingEpochGeneration > 0L
        && not (String.IsNullOrWhiteSpace context.OperationId)
        && not (String.IsNullOrWhiteSpace context.Actor)
        && not (String.IsNullOrWhiteSpace context.Receiver)
        && not (String.IsNullOrWhiteSpace context.Kind)
        && not (String.IsNullOrWhiteSpace context.CanonicalTarget)
        && (match context.Claim with
            | NoClaimRequired -> true
            | TypedClaim(id, generation) -> not (String.IsNullOrWhiteSpace id) && generation > 0L)

    let private replayEffectValid effectId (effect: EffectRecord) =
        not (String.IsNullOrWhiteSpace effectId)
        && not (String.IsNullOrWhiteSpace effect.OperationId)
        && not (String.IsNullOrWhiteSpace effect.Owner)
        && effect.OperationGeneration > 0L
        && effect.Attempt > 0L
        && effect.Preconditions.ExpectedOperationGeneration = effect.OperationGeneration
        && effect.Preconditions.ExpectedEpochGeneration > 0L
        && effect.RequestBytes.Length > 0
        && bytesSha256 effect.RequestBytes = effect.RequestDigest

    let private settlementBindsRequest requestDigest settlement =
        match settlement with
        | EffectSettlementProvenAbsent evidence ->
            match evidence with
            | ProviderIdempotencyExclusion(original, _)
            | ConditionalFenceExclusion(original, _)
            | OriginalRequestRetirement(original, _) -> original = requestDigest
        | _ -> true

    let private replayCommand commit previous (RegistryCommand(_, round, manifest, command)) =
        let address = registryAddress ()

        let persisted registry =
            AdmissionRegistry
                { registry with
                    Head = gitObjectId commit.CommitOid |> Result.defaultWith invalidOp
                    Generation = commit.Head.Generation
                    ExpectedParent = None
                    Persisted = true
                    PendingCommand = None
                    ObservedCommits = Set.add (GitObjectId commit.CommitOid) registry.ObservedCommits
                }

        match previous, command with
        | None, InitializeCommand when commit.ParentOid.IsNone && commit.Head.Generation = 1L ->
            Ok(
                persisted
                    {
                        Address = address
                        Head = GitObjectId commit.CommitOid
                        Generation = 1L
                        ExpectedParent = None
                        Persisted = true
                        PendingCommand = None
                        ObservedCommits = Set.singleton (GitObjectId commit.CommitOid)
                        Round = round
                        Manifest = manifest
                        Phase = AdmissionsOpen
                        Admissions = Map.empty
                        Effects = Map.empty
                        SealCommit = None
                        SealGeneration = None
                        SealDigest = None
                    }
            )
        | None, _ -> Error [ "registry-replay-initialize-required" ]
        | Some(AdmissionRegistry prior), InitializeCommand -> Error [ "registry-replay-duplicate-initialize" ]
        | Some(AdmissionRegistry prior), AdmitCommand(context, binding) ->
            if prior.Phase <> AdmissionsOpen || round <> prior.Round || manifest <> prior.Manifest then
                Error [ "registry-replay-admit-state" ]
            elif
                context.Round <> round
                || context.Manifest <> manifest
                || not (replayContextValid context)
                || binding <> bindingDigest context
            then
                Error [ "registry-replay-admit-binding" ]
            else
                match Map.tryFind context.OperationId prior.Admissions with
                | Some existing when context.OperationGeneration <= existing.Context.OperationGeneration ->
                    Error [ "registry-replay-operation-generation" ]
                | Some _ when unresolvedFor context.OperationId prior.Effects ->
                    Error [ "registry-replay-unresolved-replacement" ]
                | _ ->
                    let admission: Admission =
                        {
                            Context = context
                            BindingDigest = binding
                        }

                    Ok(persisted { prior with Admissions = Map.add context.OperationId admission prior.Admissions })
        | Some(AdmissionRegistry prior), CloseCommand ->
            if prior.Phase <> AdmissionsOpen || round <> prior.Round || manifest <> prior.Manifest then
                Error [ "registry-replay-close-state" ]
            else
                Ok(persisted { prior with Phase = AdmissionsClosing })
        | Some(AdmissionRegistry prior), SealCommand(members, digest) ->
            let expectedMembers =
                prior.Admissions
                |> Map.toList
                |> List.map (fun (id, admission) -> id, admission.Context.OperationGeneration, admission.BindingDigest)

            if
                prior.Phase <> AdmissionsClosing
                || round <> prior.Round
                || manifest <> prior.Manifest
                || members <> expectedMembers
                || digest <> cohortForAdmissions prior.Admissions
                || prior.Effects
                   |> Map.exists (fun _ effect ->
                       match effect.Settlement with
                       | None
                       | Some(EffectSettlementPartial _)
                       | Some(EffectSettlementIndeterminate _) -> true
                       | _ -> false)
            then
                Error [ "registry-replay-seal-state" ]
            else
                Ok(
                    persisted
                        { prior with
                            Phase = AdmissionsSealed
                            SealCommit = Some(GitObjectId commit.CommitOid)
                            SealGeneration = Some commit.Head.Generation
                            SealDigest = Some digest
                        }
                )
        | Some(AdmissionRegistry prior), ReopenCommand ->
            if
                prior.Phase <> AdmissionsSealed
                || round <= prior.Round
                || prior.Effects
                   |> Map.exists (fun _ effect ->
                       match effect.Settlement with
                       | None
                       | Some(EffectSettlementPartial _)
                       | Some(EffectSettlementIndeterminate _) -> true
                       | _ -> false)
            then
                Error [ "registry-replay-reopen-state" ]
            else
                Ok(
                    persisted
                        { prior with
                            Round = round
                            Manifest = manifest
                            Phase = AdmissionsOpen
                            Admissions = Map.empty
                            SealCommit = None
                            SealGeneration = None
                            SealDigest = None
                        }
                )
        | Some(AdmissionRegistry prior), IntentCommand(effectId, effect) ->
            match Map.tryFind effect.OperationId prior.Admissions with
            | Some admission when
                round = prior.Round
                && manifest = prior.Manifest
                && effect.OperationGeneration = admission.Context.OperationGeneration
                && replayEffectValid effectId effect
                && effect.Attempt = 1L
                && not (Map.containsKey effectId prior.Effects)
                ->
                Ok(persisted { prior with Effects = Map.add effectId effect prior.Effects })
            | _ -> Error [ "registry-replay-intent-state" ]
        | Some(AdmissionRegistry prior), SettleCommand(effectId, owner, attempt, settlement) ->
            match Map.tryFind effectId prior.Effects with
            | Some effect when
                round = prior.Round
                && manifest = prior.Manifest
                && effect.Owner = owner
                && effect.Attempt = attempt
                && settlementBindsRequest effect.RequestDigest settlement
                && (match effect.Settlement with
                    | None
                    | Some(EffectSettlementPartial _)
                    | Some(EffectSettlementIndeterminate _) -> true
                    | _ -> false)
                ->
                let settled = { effect with Settlement = Some settlement }
                Ok(persisted { prior with Effects = Map.add effectId settled prior.Effects })
            | _ -> Error [ "registry-replay-settlement-state" ]
        | Some(AdmissionRegistry prior), RetryCommand(effectId, replacement) ->
            match Map.tryFind effectId prior.Effects with
            | Some effect when
                round = prior.Round
                && manifest = prior.Manifest
                && (match effect.Settlement with
                    | Some(EffectSettlementProvenAbsent _) -> true
                    | _ -> false)
                && replacement.OperationId = effect.OperationId
                && replacement.OperationGeneration = effect.OperationGeneration
                && replacement.RequestDigest = effect.RequestDigest
                && replacement.RequestBytes = effect.RequestBytes
                && replayEffectValid effectId replacement
                && replacement.Attempt = effect.Attempt + 1L
                && replacement.Settlement.IsNone
                ->
                Ok(persisted { prior with Effects = Map.add effectId replacement prior.Effects })
            | _ -> Error [ "registry-replay-retry-state" ]

    let restore (read: RegistryJournalRead) =
        let address =
            ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production"
            |> Result.defaultWith (string >> invalidOp)

        let identityValid =
            canonicalReadIdentity read && read.FirstHead.IsSome

        if not identityValid then
            Error [ "registry-journal-identity-or-head" ]
        else
            match ShardedJournalAdapter.validate address read.Observation with
            | Error failure -> Error [ "registry-journal:" + string failure ]
            | Ok snapshot ->
                let errors =
                    [
                        if read.FirstHead <> (gitObjectId snapshot.Current.CommitOid |> Result.toOption) then
                            "registry-journal-root"
                        yield! snapshot.Commits |> List.collect (rawCommitErrors read)
                    ]

                if not errors.IsEmpty then
                    Error errors
                else
                    let folder state commit =
                        state
                        |> Result.bind (fun (previous, commandIds) ->
                            decodeEvent commit.Event.Bytes
                            |> Result.bind (fun (RegistryCommand(commandId, _, _, _) as command) ->
                                if Set.contains commandId commandIds then
                                    Error [ "registry-replay-duplicate-command-id" ]
                                else
                                    replayCommand commit previous command
                                    |> Result.map (fun registry -> Some registry, Set.add commandId commandIds)))

                    match snapshot.Commits |> List.fold folder (Ok(None, Set.empty)) with
                    | Ok(Some registry, _) -> Ok registry
                    | Ok(None, _) -> Error [ "registry-replay-empty" ]
                    | Error replayErrors -> Error replayErrors

    let recoverCommand commandId (expectedEventBytes: byte array) read =
        if String.IsNullOrWhiteSpace commandId || obj.ReferenceEquals(expectedEventBytes, null) then
            Error [ "registry-command-recovery-input" ]
        else
            restore read
            |> Result.bind (fun registry ->
                match ShardedJournalAdapter.validate (registryAddress ()) read.Observation with
                | Error failure -> Error [ "registry-journal:" + string failure ]
                | Ok snapshot ->
                    let matches =
                        snapshot.Commits
                        |> List.choose (fun commit ->
                            match decodeEvent commit.Event.Bytes with
                            | Ok(RegistryCommand(existingId, _, _, _)) when existingId = commandId ->
                                Some commit.Event.Bytes
                            | _ -> None)

                    match matches with
                    | [ bytes ] when bytes = expectedEventBytes -> Ok registry
                    | [ _ ] -> Error [ "registry-command-id-conflict" ]
                    | [] -> Error [ "registry-command-not-found" ]
                    | _ -> Error [ "registry-duplicate-command-id" ])

    let recoverOperation operationId (AdmissionRegistry registry) =
        if not registry.Persisted then
            Error [ "registry-state-not-durably-observed" ]
        else
            match Map.tryFind operationId registry.Admissions with
            | None -> Error [ "admission-missing" ]
            | Some admission ->
                Ok(
                    OperationHandle
                        {
                            Context = admission.Context
                            BindingDigest = admission.BindingDigest
                        }
                )

    let private treeBytes eventOid headOid =
        use stream = new IO.MemoryStream()

        for name, oid in [ "event.json", eventOid; "head.json", headOid ] do
            let header = Encoding.UTF8.GetBytes("100644 " + name + "\u0000")
            stream.Write(header, 0, header.Length)
            let raw = Convert.FromHexString(gitObjectIdValue oid)
            stream.Write(raw, 0, raw.Length)

        stream.ToArray()

    let private commitBytes (tree: GitObjectId) (parent: GitObjectId option) (operationId: string) =
        let parentLine = parent |> Option.map (fun value -> "parent " + gitObjectIdValue value) |> Option.toList

        Encoding.UTF8.GetBytes(
            String.concat
                "\n"
                [
                    "tree " + gitObjectIdValue tree
                    yield! parentLine
                    "author FS.GG Coordination <coordination@fs.gg> 0 +0000"
                    "committer FS.GG Coordination <coordination@fs.gg> 0 +0000"
                    ""
                    "fsgg admission " + operationId
                    ""
                ]
        )

    let private sameSemanticState (expected: Registry) (AdmissionRegistry actual) =
        expected.Round = actual.Round
        && expected.Manifest = actual.Manifest
        && expected.Phase = actual.Phase
        && expected.Admissions = actual.Admissions
        && expected.Effects = actual.Effects
        && expected.SealDigest = actual.SealDigest

    let planAppend (operationId: string) (observed: RegistryJournalRead) (AdmissionRegistry candidate) =
        let invalid reason = Error [ reason ]

        if
            String.IsNullOrWhiteSpace operationId
            || operationId.Contains('\n')
            || operationId.Contains('\r')
        then
            invalid "registry-operation-id"
        else
            let prior =
                match ShardedJournalAdapter.validate candidate.Address observed.Observation, restore observed with
                | Ok snapshot, Ok currentState when
                    not candidate.Persisted
                    && candidate.ExpectedParent = (gitObjectId snapshot.Current.CommitOid |> Result.toOption)
                    && candidate.Generation = snapshot.Current.Head.Generation + 1L
                    ->
                    Ok(Some currentState, Some snapshot)
                | Error failure, _ -> invalid ("registry-journal:" + string failure)
                | _, Error reasons -> Error reasons
                | _ -> invalid "registry-candidate-parent"

            prior
            |> Result.bind (fun (previous, snapshot) ->
                    let previousState = previous |> Option.map (fun (AdmissionRegistry state) -> state)
                    let eventBytes = canonicalEventBytes operationId previousState candidate
                    let eventDigest = ShardedJournalAdapter.sha256 eventBytes
                    let eventOid = gitOid "blob" eventBytes

                    let currentHead = snapshot |> Option.map _.Current.Head

                    let provisionalHead =
                        {
                            SchemaVersion = 1
                            Address = candidate.Address
                            Generation = candidate.Generation
                            EventDigest = eventDigest
                            SnapshotDigest = None
                            Terminal = false
                            PriorHeadDigest = currentHead |> Option.map _.HeadDigest
                            HeadDigest = String.replicate 64 "0"
                        }

                    let provisionalBytes = ShardedJournalAdapter.journalHeadBytes provisionalHead

                    let journalHead =
                        { provisionalHead with
                            HeadDigest = ShardedJournalAdapter.sha256 provisionalBytes
                        }

                    let headBytes = ShardedJournalAdapter.journalHeadBytes journalHead
                    let headOid = gitOid "blob" headBytes
                    let tree = treeBytes eventOid headOid
                    let treeOid = gitOid "tree" tree
                    let parent = snapshot |> Option.map (_.Current.CommitOid >> GitObjectId)
                    let commit = commitBytes treeOid parent operationId
                    let commitOid = gitOid "commit" commit

                    let proposed: JournalCommit =
                        {
                            CommitOid = gitObjectIdValue commitOid
                            ParentOid = parent |> Option.map gitObjectIdValue
                            TreeOid = gitObjectIdValue treeOid
                            OperationId = operationId
                            Head = journalHead
                            HeadBytes = headBytes
                            Event =
                                {
                                    Bytes = eventBytes
                                    Digest = eventDigest
                                }
                            Checkpoint = None
                        }

                    let duplicateCommand =
                        snapshot
                        |> Option.exists (fun current ->
                            current.Commits
                            |> List.exists (fun commit ->
                                match decodeEvent commit.Event.Bytes with
                                | Ok(RegistryCommand(existingId, _, _, _)) -> existingId = operationId
                                | Error _ -> true))

                    let transitionValid =
                        decodeEvent eventBytes
                        |> Result.bind (replayCommand proposed previous)
                        |> Result.exists (sameSemanticState candidate)

                    let cas =
                        if duplicateCommand then
                            invalid "registry-duplicate-command-id"
                        elif not transitionValid then
                            invalid "registry-illegal-transition"
                        else
                            match snapshot with
                            | Some current ->
                                ShardedJournalAdapter.planCas operationId current proposed
                                |> Result.mapError (fun failure -> [ "registry-cas:" + string failure ])
                            | None -> invalid "registry-initializer-not-public"

                    cas
                    |> Result.map (fun cas ->
                        let initialEffect =
                            match candidate.PendingCommand with
                            | Some command when command.StartsWith("effect-in-flight:", StringComparison.Ordinal) ->
                                let id = command.Substring("effect-in-flight:".Length)
                                let effect = candidate.Effects[id]
                                Some(id, effect.Owner, effect.Attempt, effect.RequestDigest)
                            | Some command when command.StartsWith("retry:", StringComparison.Ordinal) ->
                                let id = command.Substring("retry:".Length)
                                let effect = candidate.Effects[id]
                                Some(id, effect.Owner, effect.Attempt, effect.RequestDigest)
                            | _ -> None

                        RegistryAppendProposal
                            {
                                Cas = cas
                                Objects =
                                    {
                                        EventObjectId = eventOid
                                        EventBytes = Array.copy eventBytes
                                        HeadObjectId = headOid
                                        HeadBytes = Array.copy headBytes
                                        TreeObjectId = treeOid
                                        TreeBytes = Array.copy tree
                                        CommitObjectId = commitOid
                                        CommitBytes = Array.copy commit
                                    }
                                InitialEffect = initialEffect
                                PermitIssued = ref 0
                            })
                )

    let planGenesis operationId (VerifiedAuthoritySnapshot authority) (observed: RegistryJournalRead) =
        let address = registryAddress ()

        let errors =
            [
                if String.IsNullOrWhiteSpace operationId
                   || operationId.Length > 128
                   || (operationId |> Seq.exists Char.IsControl) then
                    "registry-genesis-operation-id"
                if authority.Phase <> V1OperatingV1 || authority.AdmissionSeal.IsSome then
                    "registry-genesis-authority-phase"
                if observed.Repository <> "FS-GG/FS.GG.Coordination.Authority"
                   || observed.RepositoryId <> 1351660651L
                   || observed.Ref <> address.Ref then
                    "registry-genesis-journal-identity"
                if observed.FirstHead.IsSome || observed.SecondHead.IsSome
                   || observed.Observation <> JournalDeleted
                   || not observed.CommitBytes.IsEmpty
                   || not observed.TreeBytes.IsEmpty then
                    "registry-genesis-journal-not-proven-absent"
            ]

        match errors with
        | _ :: _ -> Error errors
        | [] ->
            let candidate =
                {
                    Address = address
                    Head = authority.Commit
                    Generation = 1L
                    ExpectedParent = None
                    Persisted = false
                    PendingCommand = Some "initialize"
                    ObservedCommits = Set.empty
                    Round = 1L
                    Manifest = authority.Manifest
                    Phase = AdmissionsOpen
                    Admissions = Map.empty
                    Effects = Map.empty
                    SealCommit = None
                    SealGeneration = None
                    SealDigest = None
                }

            let eventBytes = canonicalEventBytes operationId None candidate
            let eventDigest = ShardedJournalAdapter.sha256 eventBytes
            let eventOid = gitOid "blob" eventBytes
            let provisionalHead =
                {
                    SchemaVersion = 1
                    Address = address
                    Generation = 1L
                    EventDigest = eventDigest
                    SnapshotDigest = None
                    Terminal = false
                    PriorHeadDigest = None
                    HeadDigest = String.replicate 64 "0"
                }
            let head =
                { provisionalHead with
                    HeadDigest = ShardedJournalAdapter.journalHeadBytes provisionalHead |> ShardedJournalAdapter.sha256 }
            let headBytes = ShardedJournalAdapter.journalHeadBytes head
            let headOid = gitOid "blob" headBytes
            let tree = treeBytes eventOid headOid
            let treeOid = gitOid "tree" tree
            let commitBytesValue = commitBytes treeOid None operationId
            let commitOid = gitOid "commit" commitBytesValue
            let commit =
                {
                    CommitOid = gitObjectIdValue commitOid
                    ParentOid = None
                    TreeOid = gitObjectIdValue treeOid
                    OperationId = operationId
                    Head = head
                    HeadBytes = Array.copy headBytes
                    Event = { Bytes = Array.copy eventBytes; Digest = eventDigest }
                    Checkpoint = None
                }
            let objects =
                {
                    EventObjectId = eventOid
                    EventBytes = Array.copy eventBytes
                    HeadObjectId = headOid
                    HeadBytes = Array.copy headBytes
                    TreeObjectId = treeOid
                    TreeBytes = Array.copy tree
                    CommitObjectId = commitOid
                    CommitBytes = Array.copy commitBytesValue
                }

            Ok(
                RegistryGenesisPlan
                    {
                        Address = address
                        AuthorityCommit = authority.Commit
                        Manifest = authority.Manifest
                        TrustDigest = authority.Trust
                        Commit = commit
                        Objects = objects
                    }
            )

    let genesisAddress (RegistryGenesisPlan plan) = plan.Address
    let genesisAuthorityCommit (RegistryGenesisPlan plan) = plan.AuthorityCommit
    let genesisManifest (RegistryGenesisPlan plan) = plan.Manifest
    let genesisTrustDigest (RegistryGenesisPlan plan) = plan.TrustDigest
    let genesisCommit (RegistryGenesisPlan plan) =
        { plan.Commit with
            HeadBytes = Array.copy plan.Commit.HeadBytes
            Event = { plan.Commit.Event with Bytes = Array.copy plan.Commit.Event.Bytes } }

    let private exactProposal (RegistryAppendProposal proposal) (read: RegistryJournalRead) =
        match restore read with
        | Error _ -> false
        | Ok _ ->
            match ShardedJournalAdapter.validate proposal.Cas.Address read.Observation with
            | Error _ -> false
            | Ok snapshot ->
                snapshot.Commits
                |> List.exists (fun commit ->
                    commit.CommitOid = proposal.Cas.ProposedCommit.CommitOid
                    && commit.TreeOid = proposal.Cas.ProposedCommit.TreeOid
                    && commit.OperationId = proposal.Cas.OperationId
                    && commit.Head.HeadDigest = proposal.Cas.ProposedCommit.Head.HeadDigest
                    && commit.Event.Digest = proposal.Cas.ProposedCommit.Event.Digest)

    let private copyObjects objects =
        {
            objects with
                EventBytes = Array.copy objects.EventBytes
                HeadBytes = Array.copy objects.HeadBytes
                TreeBytes = Array.copy objects.TreeBytes
                CommitBytes = Array.copy objects.CommitBytes
        }

    let proposalCas (RegistryAppendProposal proposal) = proposal.Cas
    let proposalObjects (RegistryAppendProposal proposal) = copyObjects proposal.Objects
    let genesisObjects (RegistryGenesisPlan plan) = copyObjects plan.Objects

    let verifyGenesisReadback (RegistryGenesisPlan plan) (read: RegistryJournalRead) =
        let objects = plan.Objects

        let exactRoot =
            match read.Observation with
            | JournalComplete(_, [ commit ]) -> commit = plan.Commit
            | _ -> false

        if not exactRoot
           || read.FirstHead <> Some objects.CommitObjectId
           || read.SecondHead <> Some objects.CommitObjectId
           || read.CommitBytes <> Map.ofList [ plan.Commit.CommitOid, objects.CommitBytes ]
           || read.TreeBytes <> Map.ofList [ plan.Commit.TreeOid, objects.TreeBytes ] then
            Error [ "registry-genesis-readback-not-exact" ]
        else
            restore read
            |> Result.bind (fun registry ->
                if head registry = objects.CommitObjectId
                   && generation registry = 1L
                   && phase registry = AdmissionsOpen then
                    Ok registry
                else
                    Error [ "registry-genesis-restored-state" ])

    let appendAndReconcile (port: RegistryJournalPort) (RegistryAppendProposal proposal as opaqueProposal) =
        let before = port.Read proposal.Cas.Address
        let alreadyObserved = exactProposal opaqueProposal before
        let leaseMatches =
            before.FirstHead = before.SecondHead
            && (before.FirstHead |> Option.map gitObjectIdValue) = Some proposal.Cas.ObservedObjectId
            && Result.isOk (restore before)

        let outcome =
            if alreadyObserved then
                ReceiveParentConflict
            elif leaseMatches then
                port.Write opaqueProposal
            else
                ReceiveParentConflict

        let reread = port.Read proposal.Cas.Address

        let restored = restore reread |> Result.toOption

        if exactProposal opaqueProposal reread then
            match restored with
            | Some registry ->
                let permit =
                    match outcome with
                    | ReceiveAccepted when
                        not alreadyObserved
                        && Threading.Interlocked.CompareExchange(proposal.PermitIssued, 1, 0) = 0
                        ->
                        proposal.InitialEffect
                        |> Option.map (fun (effectId, owner, attempt, requestDigest) ->
                            InitialDispatchPermit
                                {
                                    ConfirmedCommit = GitObjectId proposal.Cas.ProposedCommit.CommitOid
                                    EffectId = effectId
                                    Owner = owner
                                    Attempt = attempt
                                    RequestDigest = requestDigest
                                    Consumed = ref 0
                                })
                    | ReceiveParentConflict
                    | ReceiveDefiniteRefusal _
                    | ReceiveResponseUnknown
                    | ReceiveAccepted -> None

                DurableAppendAccepted(registry, permit)
            | None -> DurableAppendIndeterminate([ "registry-replay-failed" ], None)
        else
            match outcome with
            | ReceiveParentConflict -> DurableAppendParentConflict restored
            | ReceiveDefiniteRefusal reason -> DurableAppendRefused(reason, restored)
            | ReceiveAccepted -> DurableAppendIndeterminate([ "accepted-proposal-not-observed" ], restored)
            | ReceiveResponseUnknown -> DurableAppendIndeterminate([ "append-response-unknown" ], restored)

    let private contextErrors (snapshot: Snapshot) (context: MutationContext) (registry: Registry) =
        [
            if registry.Phase <> AdmissionsOpen then
                "admissions-not-open"
            if context.Round <> registry.Round then
                "round-mismatch"
            if context.Manifest <> registry.Manifest || context.Manifest <> snapshot.Manifest then
                "manifest-mismatch"
            if
                String.IsNullOrWhiteSpace context.OperationId
                || context.OperationGeneration < 1L
            then
                "operation-binding"
            if
                [ context.Actor; context.Receiver; context.Kind; context.CanonicalTarget ]
                |> List.exists String.IsNullOrWhiteSpace
            then
                "mutation-identity"
            if
                context.OriginatingEpochCommit <> snapshot.Commit
                || context.OriginatingEpochGeneration <> snapshot.Generation
            then
                "originating-epoch"
            if snapshot.Phase <> V1OperatingV1 then
                "phase-refuses-admission"
            match context.Claim with
            | TypedClaim(id, generation) when Map.tryFind id snapshot.Claims <> Some generation -> "claim-generation"
            | TypedClaim(id, generation) when String.IsNullOrWhiteSpace id || generation < 1L -> "claim-binding"
            | _ -> ()
        ]

    let private append (discriminator: string) (registry: Registry) =
        let generation = registry.Generation + 1L

        { registry with
            Head = nextHead registry.Head generation discriminator
            Generation = generation
            ExpectedParent = Some registry.Head
            Persisted = false
            PendingCommand = Some discriminator
        }

    let admit expectedParent (VerifiedAuthoritySnapshot snapshot) context (AdmissionRegistry registry) =
        if not registry.Persisted then
            RegistryAdmissionRefused [ "registry-state-not-durably-observed" ]
        elif expectedParent <> registry.Head then
            RegistryAdmissionRefused [ "parent-conflict" ]
        else
            let digest = bindingDigest context

            match Map.tryFind context.OperationId registry.Admissions with
            | Some existing when existing.BindingDigest = digest ->
                RegistryAdmissionAlreadyPresent(
                    OperationHandle
                        {
                            Context = existing.Context
                            BindingDigest = digest
                        }
                )
            | Some existing when context.OperationGeneration <= existing.Context.OperationGeneration ->
                RegistryAdmissionRefused [ "operation-generation-not-replaced" ]
            | Some existing when
                registry.Effects
                |> Map.exists (fun _ effect ->
                    effect.OperationId = existing.Context.OperationId
                    && (match effect.Settlement with
                        | None
                        | Some(EffectSettlementPartial _)
                        | Some(EffectSettlementIndeterminate _) -> true
                        | _ -> false))
                ->
                RegistryAdmissionRefused [ "operation-has-unresolved-effects" ]
            | _ ->
                match contextErrors snapshot context registry with
                | _ :: _ as errors -> RegistryAdmissionRefused errors
                | [] ->
                    let admission: Admission =
                        {
                            Context = context
                            BindingDigest = digest
                        }

                    let updated =
                        { registry with
                            Admissions = Map.add context.OperationId admission registry.Admissions
                        }
                        |> append ("admit:" + sha256Value digest)

                    RegistryAdmissionAppended(AdmissionRegistry updated)

    let closeAdmissions expectedParent (AdmissionRegistry registry) =
        if not registry.Persisted then
            RegistryRefused [ "registry-state-not-durably-observed" ]
        elif expectedParent <> registry.Head then
            RegistryRefused [ "parent-conflict" ]
        elif registry.Phase <> AdmissionsOpen then
            RegistryRefused [ "admissions-not-open" ]
        else
            RegistryAppended(
                AdmissionRegistry(
                    { registry with
                        Phase = AdmissionsClosing
                    }
                    |> append "close-admissions"
                )
            )

    let unresolvedEffects (AdmissionRegistry registry) =
        registry.Effects
        |> Map.toList
        |> List.choose (fun (id, effect) ->
            match effect.Settlement with
            | None
            | Some(EffectSettlementPartial _)
            | Some(EffectSettlementIndeterminate _) -> Some id
            | _ -> None)

    let private cohort (registry: Registry) =
        cohortForAdmissions registry.Admissions

    let sealAdmissions expectedParent (VerifiedAuthoritySnapshot snapshot) (AdmissionRegistry registry) =
        let unresolved = unresolvedEffects (AdmissionRegistry registry)

        if not registry.Persisted then
            RegistryRefused [ "registry-state-not-durably-observed" ]
        elif expectedParent <> registry.Head then
            RegistryRefused [ "parent-conflict" ]
        elif registry.Phase <> AdmissionsClosing then
            RegistryRefused [ "admissions-not-closing" ]
        elif snapshot.Phase <> V1OperatingV1 || snapshot.Manifest <> registry.Manifest then
            RegistryRefused [ "epoch-or-manifest-mismatch" ]
        elif not unresolved.IsEmpty then
            RegistryRefused("unsettled-effects" :: unresolved)
        else
            let digest = cohort registry

            let sealedRound =
                { registry with
                    Phase = AdmissionsSealed
                    SealDigest = Some digest
                }
                |> append ("seal:" + sha256Value digest)

            RegistryAppended(
                AdmissionRegistry
                    { sealedRound with
                        SealCommit = Some sealedRound.Head
                        SealGeneration = Some sealedRound.Generation
                    }
            )

    let reopenAdmissions
        expectedParent
        newRound
        manifest
        (VerifiedAuthoritySnapshot snapshot)
        (AdmissionRegistry registry)
        =
        if not registry.Persisted then
            RegistryRefused [ "registry-state-not-durably-observed" ]
        elif expectedParent <> registry.Head then
            RegistryRefused [ "parent-conflict" ]
        elif registry.Phase <> AdmissionsSealed then
            RegistryRefused [ "prior-round-not-sealed" ]
        elif newRound <= registry.Round then
            RegistryRefused [ "round-not-advanced" ]
        elif snapshot.Phase <> V1OperatingV1 || snapshot.Manifest <> manifest then
            RegistryRefused [ "epoch-or-manifest-mismatch" ]
        else
            RegistryAppended(
                AdmissionRegistry(
                    { registry with
                        Round = newRound
                        Manifest = manifest
                        Phase = AdmissionsOpen
                        Admissions = Map.empty
                        SealCommit = None
                        SealGeneration = None
                        SealDigest = None
                    }
                    |> append ("reopen:" + string newRound)
                )
            )

    let cohortDigest (AdmissionRegistry registry) = registry.SealDigest

    let private currentHandle (registry: Registry) (handle: Handle) =
        Map.tryFind handle.Context.OperationId registry.Admissions
        |> Option.exists (fun admission -> admission.BindingDigest = handle.BindingDigest)

    let private claimMatches (snapshot: Snapshot) (handle: Handle) expected =
        match handle.Context.Claim, expected with
        | NoClaimRequired, None -> true
        | TypedClaim(id, generation), Some expectedGeneration ->
            generation = expectedGeneration
            && Map.tryFind id snapshot.Claims = Some generation
        | _ -> false

    let private fenceErrors (snapshot: Snapshot) (handle: Handle) (request: MutationRequest) (registry: Registry) =
        let errors = ResizeArray<string>()

        if not (currentHandle registry handle) then
            errors.Add "admission-replaced-or-missing"

        if handle.Context.Manifest <> snapshot.Manifest then
            errors.Add "manifest-mismatch"

        if
            request.Preconditions.ExpectedEpochCommit <> snapshot.Commit
            || request.Preconditions.ExpectedEpochGeneration <> snapshot.Generation
        then
            errors.Add "epoch-fence"

        if
            request.Preconditions.ExpectedOperationGeneration
            <> handle.Context.OperationGeneration
        then
            errors.Add "operation-generation"

        if not (claimMatches snapshot handle request.Preconditions.ExpectedClaimGeneration) then
            errors.Add "claim-generation"

        if snapshot.Phase <> V1OperatingV1 && snapshot.Phase <> V1Preparing then
            errors.Add "phase-refuses-effect"

        if snapshot.Phase = V1Preparing then
            match
                registry.Phase,
                registry.SealCommit,
                registry.SealGeneration,
                registry.SealDigest,
                snapshot.AdmissionSeal
            with
            | AdmissionsSealed, Some sealCommit, Some sealGeneration, Some digest, Some(commit, generation, cohort) when
                commit = sealCommit && generation = sealGeneration && cohort = digest
                ->
                ()
            | _ -> errors.Add "preparing-requires-exact-seal"

        List.ofSeq errors

    let prepareEffect
        expectedParent
        (VerifiedAuthoritySnapshot snapshot)
        (OperationHandle handle)
        owner
        outbound
        (AdmissionRegistry registry)
        =
        if not registry.Persisted then
            EffectRefused [ "registry-state-not-durably-observed" ]
        elif expectedParent <> registry.Head then
            EffectRefused [ "parent-conflict" ]
        elif String.IsNullOrWhiteSpace owner then
            EffectRefused [ "dispatch-owner" ]
        elif not (currentHandle registry handle) then
            EffectRefused [ "admission-replaced-or-missing" ]
        elif
            snapshot.Phase = V1Preparing
            && (match
                    registry.Phase,
                    registry.SealCommit,
                    registry.SealGeneration,
                    registry.SealDigest,
                    snapshot.AdmissionSeal
                with
                | AdmissionsSealed, Some sealCommit, Some sealGeneration, Some digest, Some(commit, generation, cohort) ->
                    commit <> sealCommit || generation <> sealGeneration || cohort <> digest
                | _ -> true)
        then
            EffectRefused [ "preparing-requires-exact-seal" ]
        else
            match outbound with
            | ReadRequest _ -> EffectRefused [ "read-request-cannot-dispatch-mutation" ]
            | MutationRequest request ->
                if
                    request.Preconditions.ExpectedOperationGeneration
                    <> handle.Context.OperationGeneration
                then
                    EffectRefused [ "operation-generation" ]
                elif
                    request.Preconditions.ExpectedEpochCommit <> snapshot.Commit
                    || request.Preconditions.ExpectedEpochGeneration <> snapshot.Generation
                then
                    EffectRefused [ "epoch-fence" ]
                elif snapshot.Phase <> V1OperatingV1 && snapshot.Phase <> V1Preparing then
                    EffectRefused [ "phase-refuses-effect" ]
                elif not (claimMatches snapshot handle request.Preconditions.ExpectedClaimGeneration) then
                    EffectRefused [ "claim-generation" ]
                else
                    match Map.tryFind request.EffectId registry.Effects with
                    | Some effect when
                        effect.RequestDigest <> request.RequestDigest
                        || effect.RequestBytes <> request.CanonicalRequestBytes
                        || effect.Preconditions <> request.Preconditions
                        ->
                        EffectRefused [ "effect-id-request-conflict" ]
                    | Some effect when effect.Settlement.IsNone -> EffectAlreadyInFlight effect.Owner
                    | Some effect -> EffectAlreadySettled(Option.get effect.Settlement)
                    | None ->
                        let errors =
                            [
                                if String.IsNullOrWhiteSpace request.EffectId then
                                    "effect-id"
                                if
                                    obj.ReferenceEquals(request.CanonicalRequestBytes, null)
                                    || request.CanonicalRequestBytes.Length = 0
                                    || bytesSha256 request.CanonicalRequestBytes <> request.RequestDigest
                                then
                                    "request-bytes-or-digest"

                                yield! fenceErrors snapshot handle request registry
                            ]

                        if not errors.IsEmpty then
                            EffectRefused errors
                        else
                            let effect: EffectRecord =
                                {
                                    OperationId = handle.Context.OperationId
                                    OperationGeneration = handle.Context.OperationGeneration
                                    RequestDigest = request.RequestDigest
                                    RequestBytes = Array.copy request.CanonicalRequestBytes
                                    Preconditions = request.Preconditions
                                    Owner = owner
                                    Attempt = 1L
                                    Settlement = None
                                }

                            let updated =
                                { registry with
                                    Effects = Map.add request.EffectId effect registry.Effects
                                }
                                |> append ("effect-in-flight:" + request.EffectId)

                            EffectIntentAppended(AdmissionRegistry updated)

    let authorizeRead outbound =
        match outbound with
        | ReadRequest request when not (String.IsNullOrWhiteSpace request.Resource) -> Ok()
        | ReadRequest _ -> Error [ "read-resource" ]
        | MutationRequest _ -> Error [ "mutation-request-is-not-read" ]

    let private authorizeDispatchSnapshot
        (VerifiedAuthoritySnapshot snapshot)
        (OperationHandle handle)
        owner
        effectId
        (AdmissionRegistry registry)
        =
        match Map.tryFind effectId registry.Effects with
        | None -> DispatchRefused [ "effect-intent-missing" ]
        | Some effect when not (claimMatches snapshot handle effect.Preconditions.ExpectedClaimGeneration) ->
            DispatchRefused [ "claim-generation" ]
        | Some effect ->
            let request: MutationRequest =
                {
                    EffectId = effectId
                    RequestDigest = effect.RequestDigest
                    CanonicalRequestBytes = Array.copy effect.RequestBytes
                    Preconditions = effect.Preconditions
                }

            let errors =
                [
                    if
                        effect.OperationId <> handle.Context.OperationId
                        || effect.OperationGeneration <> handle.Context.OperationGeneration
                    then
                        "effect-operation-binding"

                    if effect.Owner <> owner then
                        "dispatch-owner"

                    if effect.Settlement.IsSome then
                        "effect-settled"

                    if not (claimMatches snapshot handle effect.Preconditions.ExpectedClaimGeneration) then
                        "claim-generation"

                    yield! fenceErrors snapshot handle request registry
                ]

            if errors.IsEmpty then
                DispatchAuthorized
            else
                DispatchRefused errors

    let refreshDispatch
        registryPort
        authorityPort
        (InitialDispatchPermit permit)
        (OperationHandle handle)
        owner
        effectId
        (AdmissionRegistry registry)
        =
        let permitMatches =
            registry.Persisted
            && Set.contains permit.ConfirmedCommit registry.ObservedCommits
            && permit.EffectId = effectId
            && permit.Owner = owner
            && (match Map.tryFind effectId registry.Effects with
                | Some effect -> effect.Attempt = permit.Attempt && effect.RequestDigest = permit.RequestDigest
                | None -> false)

        if not permitMatches then
            Error [ "initial-dispatch-permit-mismatch" ]
        elif Threading.Interlocked.CompareExchange(permit.Consumed, 1, 0) <> 0 then
            Error [ "initial-dispatch-permit-consumed" ]
        else
            match restore (registryPort.Read registry.Address), readVerified authorityPort with
            | Error reasons, _ -> Error reasons
            | _, Error reasons -> Error reasons
            | Ok(AdmissionRegistry freshRegistry as fresh), Ok snapshot ->
                if freshRegistry.Head <> registry.Head || freshRegistry.Generation <> registry.Generation then
                    Error [ "registry-head-moved" ]
                else
                    match authorizeDispatchSnapshot snapshot (OperationHandle handle) owner effectId fresh with
                    | DispatchRefused reasons -> Error reasons
                    | DispatchAuthorized ->
                        Ok(
                            DispatchFence
                                {
                                    Snapshot =
                                        let (VerifiedAuthoritySnapshot value) = snapshot
                                        value
                                    RegistryHead = registry.Head
                                    RegistryGeneration = registry.Generation
                                    HandleDigest = handle.BindingDigest
                                    Owner = owner
                                    EffectId = effectId
                                    Consumed = ref 0
                                }
                        )

    let authorizeDispatch
        registryPort
        authorityPort
        (DispatchFence fence)
        (OperationHandle handle)
        owner
        effectId
        (AdmissionRegistry registry)
        =
        if Threading.Interlocked.CompareExchange(fence.Consumed, 1, 0) <> 0 then
            DispatchRefused [ "dispatch-fence-consumed" ]
        elif
            fence.RegistryHead <> registry.Head
            || fence.RegistryGeneration <> registry.Generation
            || fence.HandleDigest <> handle.BindingDigest
            || fence.Owner <> owner
            || fence.EffectId <> effectId
        then
            DispatchRefused [ "dispatch-fence-stale" ]
        else
            match restore (registryPort.Read registry.Address), readVerified authorityPort with
            | Error reasons, _ -> DispatchRefused reasons
            | _, Error reasons -> DispatchRefused reasons
            | Ok(AdmissionRegistry freshRegistry as fresh), Ok snapshot when
                freshRegistry.Head = registry.Head
                && freshRegistry.Generation = registry.Generation
                ->
                authorizeDispatchSnapshot snapshot (OperationHandle handle) owner effectId fresh
            | Ok _, Ok _ -> DispatchRefused [ "registry-head-moved" ]

    let reconcileEffect
        (port: ProviderReconciliationPort)
        (OperationHandle handle)
        effectId
        (AdmissionRegistry registry)
        =
        if not registry.Persisted || not (currentHandle registry handle) then
            Error [ "admission-replaced-or-registry-not-durable" ]
        else
            match Map.tryFind effectId registry.Effects with
            | None -> Error [ "effect-intent-missing" ]
            | Some effect when
                effect.OperationId <> handle.Context.OperationId
                || effect.OperationGeneration <> handle.Context.OperationGeneration
                ->
                Error [ "effect-operation-binding" ]
            | Some effect ->
                match port.Read effect.OperationId effectId effect.Attempt (Array.copy effect.RequestBytes) with
                | Error reason -> Error [ "provider-reconciliation:" + reason ]
                | Ok observation ->
                    let settlement, binding =
                        match observation with
                        | ProviderApplied digest -> EffectSettlementApplied digest, None
                        | ProviderStronglyAbsent evidence ->
                            let original =
                                match evidence with
                                | ProviderIdempotencyExclusion(request, _)
                                | ConditionalFenceExclusion(request, _)
                                | OriginalRequestRetirement(request, _) -> request

                            EffectSettlementProvenAbsent evidence, Some original
                        | ProviderPartial reason -> EffectSettlementPartial reason, None
                        | ProviderIndeterminate reason -> EffectSettlementIndeterminate reason, None

                    if binding |> Option.exists ((<>) effect.RequestDigest) then
                        Error [ "provider-absence-request-binding" ]
                    else
                        Ok(
                            VerifiedProviderObservation
                                {
                                    OperationId = effect.OperationId
                                    EffectId = effectId
                                    Attempt = effect.Attempt
                                    RequestDigest = effect.RequestDigest
                                    Settlement = settlement
                                }
                        )

    let settleEffect
        expectedParent
        owner
        effectId
        (VerifiedProviderObservation proof)
        (AdmissionRegistry registry)
        =
        if not registry.Persisted then
            RegistryRefused [ "registry-state-not-durably-observed" ]
        elif expectedParent <> registry.Head then
            RegistryRefused [ "parent-conflict" ]
        else
            match Map.tryFind effectId registry.Effects with
            | None -> RegistryRefused [ "effect-intent-missing" ]
            | Some effect when effect.Owner <> owner -> RegistryRefused [ "dispatch-owner" ]
            | Some effect when
                proof.OperationId <> effect.OperationId
                || proof.EffectId <> effectId
                || proof.Attempt <> effect.Attempt
                || proof.RequestDigest <> effect.RequestDigest
                ->
                RegistryRefused [ "provider-proof-binding" ]
            | Some effect when
                match effect.Settlement with
                | Some(EffectSettlementApplied _)
                | Some(EffectSettlementProvenAbsent _) -> true
                | _ -> false
                ->
                RegistryRefused [ "effect-already-terminal" ]
            | Some effect ->
                let updated =
                    { registry with
                        Effects =
                            Map.add
                                effectId
                                { effect with
                                    Settlement = Some proof.Settlement
                                }
                                registry.Effects
                    }
                    |> append ("settle:" + effectId)

                RegistryAppended(AdmissionRegistry updated)

    let retryAfterProvenAbsence
        expectedParent
        port
        (OperationHandle handle)
        newOwner
        effectId
        (AdmissionRegistry registry)
        =
        if not registry.Persisted then
            EffectRefused [ "registry-state-not-durably-observed" ]
        elif expectedParent <> registry.Head then
            EffectRefused [ "parent-conflict" ]
        elif String.IsNullOrWhiteSpace newOwner then
            EffectRefused [ "dispatch-owner" ]
        else
            match readVerified port with
            | Error reasons -> EffectRefused reasons
            | Ok(VerifiedAuthoritySnapshot snapshot) ->
                match Map.tryFind effectId registry.Effects with
                | Some effect when
                    match effect.Settlement with
                    | Some(EffectSettlementProvenAbsent _) -> true
                    | _ -> false
                    ->
                    let request: MutationRequest =
                        {
                            EffectId = effectId
                            RequestDigest = effect.RequestDigest
                            CanonicalRequestBytes = Array.copy effect.RequestBytes
                            Preconditions =
                                { effect.Preconditions with
                                    ExpectedEpochCommit = snapshot.Commit
                                    ExpectedEpochGeneration = snapshot.Generation
                                }
                        }

                    let errors =
                        [
                            if
                                effect.OperationId <> handle.Context.OperationId
                                || effect.OperationGeneration <> handle.Context.OperationGeneration
                            then
                                "effect-operation-binding"

                            yield! fenceErrors snapshot handle request registry
                        ]

                    if not errors.IsEmpty then
                        EffectRefused errors
                    else
                        let replacement =
                            { effect with
                                Preconditions = request.Preconditions
                                Owner = newOwner
                                Attempt = effect.Attempt + 1L
                                Settlement = None
                            }

                        let updated =
                            { registry with
                                Effects = Map.add effectId replacement registry.Effects
                            }
                            |> append ("retry:" + effectId)

                        EffectIntentAppended(AdmissionRegistry updated)
                | Some _ -> EffectRefused [ "retry-requires-proven-absence" ]
                | None -> EffectRefused [ "effect-intent-missing" ]

    let preparingReference (AdmissionRegistry registry) =
        match registry.Persisted, registry.Phase, registry.SealCommit, registry.SealGeneration, registry.SealDigest with
        | true, AdmissionsSealed, Some commit, Some generation, Some digest -> Ok(commit, generation, digest)
        | _ -> Error [ "admissions-not-sealed" ]
