namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type GitHubManifestFingerprint =
    { Name: string; Version: string; Sha256: string; Bytes: int64 }

type GitHubManifestOldSubject =
    { Identity: string; GlobalId: string; SourceRevision: string; Schema: string; BytesSha256: string; ValueSha256: string }

type GitHubManifestV2Result =
    { Identity: string; GlobalId: string; Revision: string; PayloadSha256: string; Outcome: string }

type GitHubManifestSubjectBinding =
    { Old: GitHubManifestOldSubject; Result: GitHubManifestV2Result; Disposition: string }

type GitHubManifestLiveOperation =
    { Identity: string; Generation: int64; Kind: string; Target: string; State: string; PayloadSha256: string }

type GitHubManifestReceiverHead =
    { Receiver: string; RepositoryId: string; CommitSha: string; TreeSha: string; PinsSha256: string }

type GitHubManifestSettingsPlan =
    { Repository: string; PrestateSha256: string; DesiredSha256: string; PlanSha256: string }

type GitHubManifestArchiveBinding =
    {
        Authority: string
        SourceSchema: string
        SourceBytesSha256: string
        ArchiveSha256: string
        LookupIndexSha256: string
        VerifierSha256: string
    }

type GitHubManifestPhasePlan =
    {
        Order: int
        PhaseId: string
        InputSha256: string
        OperationsSha256: string
        ReceiptSha256: string
        RollbackInputIds: string list
    }

type GitHubManifestReviewer =
    { Login: string; GlobalId: string; DecisionSha256: string }

type GitHubManifestRollbackInput =
    { Identity: string; Kind: string; Revision: string; PayloadSha256: string }

type GitHubImmutableManifest =
    {
        SchemaVersion: int
        ManifestId: string
        RoadmapRevision: string
        RoadmapSha256: string
        UnitContractSha256: string
        PredecessorReceiptDigest: string
        DiscoverySourceRevision: string
        DiscoveryNormalizedDigest: string
        DiscoverySeal: string
        OldModel: GitHubManifestFingerprint
        NewModel: GitHubManifestFingerprint
        ArtifactFingerprints: GitHubManifestFingerprint list
        Subjects: GitHubManifestSubjectBinding list
        LiveOperations: GitHubManifestLiveOperation list
        ReceiverHeads: GitHubManifestReceiverHead list
        SettingsPlans: GitHubManifestSettingsPlan list
        Archives: GitHubManifestArchiveBinding list
        PhasePlans: GitHubManifestPhasePlan list
        Reviewers: GitHubManifestReviewer list
        RollbackInputs: GitHubManifestRollbackInput list
        CreatedAt: DateTimeOffset
        NormalizedDigest: string
        Seal: string
    }

[<RequireQualifiedAccess>]
type GitHubImmutableManifestFinding =
    | InvalidManifestField of string
    | InvalidManifestFingerprint of string
    | InvalidManifestPopulation of string
    | InvalidManifestSubject of string
    | InvalidManifestPhasePlan of string
    | MissingManifestRollbackInput of string
    | AlteredManifestDigest
    | AlteredManifestSeal

type GitHubImmutableManifestControl =
    | ManifestPrerequisiteReceipt
    | ManifestDiscoveryBinding
    | ManifestOldAndNewModels
    | ManifestArtifactFingerprints
    | ManifestGlobalIds
    | ManifestOldBytesAndValues
    | ManifestV2Results
    | ManifestLiveOperations
    | ManifestReceiverHeads
    | ManifestSettingsPlans
    | ManifestArchiveDigests
    | ManifestDispositions
    | ManifestPhasePlans
    | ManifestReviewers
    | ManifestRollbackInputs
    | ManifestNoOmission
    | ManifestTamperRefusal
    | ManifestReplay
    | ManifestNoMutation

type GitHubImmutableManifestControlResult =
    { Control: GitHubImmutableManifestControl; ControlPassed: bool; BaselineGreen: bool }

type GitHubImmutableManifestQualificationFinding =
    { Code: string; ControlId: string; Message: string }

module GitHubImmutableManifestQualification =
    let requiredControls =
        [
            ManifestPrerequisiteReceipt
            ManifestDiscoveryBinding
            ManifestOldAndNewModels
            ManifestArtifactFingerprints
            ManifestGlobalIds
            ManifestOldBytesAndValues
            ManifestV2Results
            ManifestLiveOperations
            ManifestReceiverHeads
            ManifestSettingsPlans
            ManifestArchiveDigests
            ManifestDispositions
            ManifestPhasePlans
            ManifestReviewers
            ManifestRollbackInputs
            ManifestNoOmission
            ManifestTamperRefusal
            ManifestReplay
            ManifestNoMutation
        ]

    let controlId =
        function
        | ManifestPrerequisiteReceipt -> "manifest-prerequisite-receipt"
        | ManifestDiscoveryBinding -> "manifest-discovery-binding"
        | ManifestOldAndNewModels -> "manifest-old-and-new-models"
        | ManifestArtifactFingerprints -> "manifest-artifact-fingerprints"
        | ManifestGlobalIds -> "manifest-global-ids"
        | ManifestOldBytesAndValues -> "manifest-old-bytes-and-values"
        | ManifestV2Results -> "manifest-v2-results"
        | ManifestLiveOperations -> "manifest-live-operations"
        | ManifestReceiverHeads -> "manifest-receiver-heads"
        | ManifestSettingsPlans -> "manifest-settings-plans"
        | ManifestArchiveDigests -> "manifest-archive-digests"
        | ManifestDispositions -> "manifest-dispositions"
        | ManifestPhasePlans -> "manifest-phase-plans"
        | ManifestReviewers -> "manifest-reviewers"
        | ManifestRollbackInputs -> "manifest-rollback-inputs"
        | ManifestNoOmission -> "manifest-no-omission"
        | ManifestTamperRefusal -> "manifest-tamper-refusal"
        | ManifestReplay -> "manifest-replay"
        | ManifestNoMutation -> "manifest-no-mutation"

    let private validText value = not (String.IsNullOrWhiteSpace value)

    let private isSha length value =
        validText value
        && value.Length = length
        && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private framed values = values |> List.map frame |> String.concat ""

    let private hash (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private unique values = List.length values = (values |> Set.ofList |> Set.count)

    let private sortedUnique selector rows =
        let values = rows |> List.map selector
        values = List.sort values && unique values

    let private fingerprintParts (value: GitHubManifestFingerprint) =
        [ value.Name; value.Version; value.Sha256; string value.Bytes ]

    let private oldSubjectParts (value: GitHubManifestOldSubject) =
        [ value.Identity; value.GlobalId; value.SourceRevision; value.Schema; value.BytesSha256; value.ValueSha256 ]

    let private resultParts (value: GitHubManifestV2Result) =
        [ value.Identity; value.GlobalId; value.Revision; value.PayloadSha256; value.Outcome ]

    let private payloadParts (manifest: GitHubImmutableManifest) =
        [
            yield "fsgg.coordination.github-immutable-manifest/1"
            yield manifest.ManifestId
            yield manifest.RoadmapRevision
            yield manifest.RoadmapSha256
            yield manifest.UnitContractSha256
            yield manifest.PredecessorReceiptDigest
            yield manifest.DiscoverySourceRevision
            yield manifest.DiscoveryNormalizedDigest
            yield manifest.DiscoverySeal
            yield! fingerprintParts manifest.OldModel
            yield! fingerprintParts manifest.NewModel
            for artifact in manifest.ArtifactFingerprints do
                yield! fingerprintParts artifact
            for subject in manifest.Subjects do
                yield! oldSubjectParts subject.Old
                yield! resultParts subject.Result
                yield subject.Disposition
            for operation in manifest.LiveOperations do
                yield operation.Identity
                yield string operation.Generation
                yield operation.Kind
                yield operation.Target
                yield operation.State
                yield operation.PayloadSha256
            for receiver in manifest.ReceiverHeads do
                yield receiver.Receiver
                yield receiver.RepositoryId
                yield receiver.CommitSha
                yield receiver.TreeSha
                yield receiver.PinsSha256
            for settings in manifest.SettingsPlans do
                yield settings.Repository
                yield settings.PrestateSha256
                yield settings.DesiredSha256
                yield settings.PlanSha256
            for archive in manifest.Archives do
                yield archive.Authority
                yield archive.SourceSchema
                yield archive.SourceBytesSha256
                yield archive.ArchiveSha256
                yield archive.LookupIndexSha256
                yield archive.VerifierSha256
            for phase in manifest.PhasePlans do
                yield string phase.Order
                yield phase.PhaseId
                yield phase.InputSha256
                yield phase.OperationsSha256
                yield phase.ReceiptSha256
                yield framed phase.RollbackInputIds
            for reviewer in manifest.Reviewers do
                yield reviewer.Login
                yield reviewer.GlobalId
                yield reviewer.DecisionSha256
            for rollback in manifest.RollbackInputs do
                yield rollback.Identity
                yield rollback.Kind
                yield rollback.Revision
                yield rollback.PayloadSha256
            yield manifest.CreatedAt.ToUniversalTime().ToString("O")
        ]

    let private seal digest =
        [ "fsgg.coordination.github-immutable-manifest-seal/1"; digest ] |> framed |> hash

    let private validateFingerprint name value =
        if validText value.Name && validText value.Version && isSha 64 value.Sha256 && value.Bytes > 0L then
            []
        else
            [ GitHubImmutableManifestFinding.InvalidManifestFingerprint name ]

    let private validate discoveredSubjects (manifest: GitHubImmutableManifest) =
        let findings = ResizeArray<GitHubImmutableManifestFinding>()

        let require condition field =
            if not condition then findings.Add(GitHubImmutableManifestFinding.InvalidManifestField field)

        require (manifest.SchemaVersion = 1) "schemaVersion"
        require (validText manifest.ManifestId) "manifestId"
        require (isSha 40 manifest.RoadmapRevision) "roadmapRevision"
        require (isSha 64 manifest.RoadmapSha256) "roadmapSha256"
        require (isSha 64 manifest.UnitContractSha256) "unitContractSha256"
        require (isSha 64 manifest.PredecessorReceiptDigest) "predecessorReceiptDigest"
        require (isSha 40 manifest.DiscoverySourceRevision) "discoverySourceRevision"
        require (isSha 64 manifest.DiscoveryNormalizedDigest) "discoveryNormalizedDigest"
        require (isSha 64 manifest.DiscoverySeal) "discoverySeal"

        validateFingerprint "oldModel" manifest.OldModel |> List.iter findings.Add
        validateFingerprint "newModel" manifest.NewModel |> List.iter findings.Add
        require (manifest.OldModel.Sha256 <> manifest.NewModel.Sha256) "modelTransition"

        if List.isEmpty manifest.ArtifactFingerprints || not (sortedUnique (fun (value: GitHubManifestFingerprint) -> value.Name) manifest.ArtifactFingerprints) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "artifactFingerprints")

        manifest.ArtifactFingerprints
        |> List.collect (fun fingerprint -> validateFingerprint fingerprint.Name fingerprint)
        |> List.iter findings.Add

        if List.isEmpty discoveredSubjects || discoveredSubjects <> List.sort discoveredSubjects || not (unique discoveredSubjects) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "discoveredSubjects")

        let subjectIds = manifest.Subjects |> List.map _.Old.Identity
        if subjectIds <> discoveredSubjects || not (unique subjectIds) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "subjects")

        let oldGlobalIds = manifest.Subjects |> List.map _.Old.GlobalId
        let resultIdentities = manifest.Subjects |> List.map _.Result.Identity
        let resultGlobalIds = manifest.Subjects |> List.map _.Result.GlobalId
        if not (unique resultIdentities) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "resultIdentities")
        if not (unique oldGlobalIds && unique resultGlobalIds) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "globalIds")

        for subject in manifest.Subjects do
            if not (
                validText subject.Old.Identity
                && validText subject.Old.GlobalId
                && validText subject.Old.SourceRevision
                && validText subject.Old.Schema
                && isSha 64 subject.Old.BytesSha256
                && isSha 64 subject.Old.ValueSha256
                && validText subject.Result.Identity
                && validText subject.Result.GlobalId
                && validText subject.Result.Revision
                && isSha 64 subject.Result.PayloadSha256
                && validText subject.Result.Outcome
                && validText subject.Disposition
            ) then
                findings.Add(GitHubImmutableManifestFinding.InvalidManifestSubject subject.Old.Identity)

        if List.isEmpty manifest.LiveOperations || not (sortedUnique (fun (value: GitHubManifestLiveOperation) -> value.Identity) manifest.LiveOperations) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "liveOperations")

        for operation in manifest.LiveOperations do
            if not (validText operation.Identity && operation.Generation > 0L && validText operation.Kind && validText operation.Target && validText operation.State && isSha 64 operation.PayloadSha256) then
                findings.Add(GitHubImmutableManifestFinding.InvalidManifestField $"liveOperation:{operation.Identity}")

        if List.isEmpty manifest.ReceiverHeads || not (sortedUnique (fun (value: GitHubManifestReceiverHead) -> value.Receiver) manifest.ReceiverHeads) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "receiverHeads")

        for receiver in manifest.ReceiverHeads do
            if not (validText receiver.Receiver && validText receiver.RepositoryId && isSha 40 receiver.CommitSha && isSha 40 receiver.TreeSha && isSha 64 receiver.PinsSha256) then
                findings.Add(GitHubImmutableManifestFinding.InvalidManifestField $"receiver:{receiver.Receiver}")

        if List.isEmpty manifest.SettingsPlans || not (sortedUnique (fun (value: GitHubManifestSettingsPlan) -> value.Repository) manifest.SettingsPlans) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "settingsPlans")

        for settings in manifest.SettingsPlans do
            if not (validText settings.Repository && isSha 64 settings.PrestateSha256 && isSha 64 settings.DesiredSha256 && isSha 64 settings.PlanSha256) then
                findings.Add(GitHubImmutableManifestFinding.InvalidManifestField $"settings:{settings.Repository}")

        let archiveAuthorities = manifest.Archives |> List.map _.Authority
        if archiveAuthorities <> GitHubCompleteDiscoveryQualification.expectedAuthorities || not (unique archiveAuthorities) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "archives")

        for archive in manifest.Archives do
            if not (validText archive.SourceSchema && [ archive.SourceBytesSha256; archive.ArchiveSha256; archive.LookupIndexSha256; archive.VerifierSha256 ] |> List.forall (isSha 64)) then
                findings.Add(GitHubImmutableManifestFinding.InvalidManifestField $"archive:{archive.Authority}")

        if List.isEmpty manifest.RollbackInputs || not (sortedUnique (fun (value: GitHubManifestRollbackInput) -> value.Identity) manifest.RollbackInputs) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "rollbackInputs")

        for rollback in manifest.RollbackInputs do
            if not (validText rollback.Identity && validText rollback.Kind && validText rollback.Revision && isSha 64 rollback.PayloadSha256) then
                findings.Add(GitHubImmutableManifestFinding.InvalidManifestField $"rollback:{rollback.Identity}")

        let rollbackIds = manifest.RollbackInputs |> List.map _.Identity |> Set.ofList
        let expectedOrders = [ 1 .. manifest.PhasePlans.Length ]
        let actualOrders = manifest.PhasePlans |> List.map _.Order

        if List.isEmpty manifest.PhasePlans || actualOrders <> expectedOrders || not (sortedUnique (fun (value: GitHubManifestPhasePlan) -> value.PhaseId) manifest.PhasePlans) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "phasePlans")

        for phase in manifest.PhasePlans do
            if not (validText phase.PhaseId && [ phase.InputSha256; phase.OperationsSha256; phase.ReceiptSha256 ] |> List.forall (isSha 64) && phase.RollbackInputIds = List.sort phase.RollbackInputIds && unique phase.RollbackInputIds && not (List.isEmpty phase.RollbackInputIds)) then
                findings.Add(GitHubImmutableManifestFinding.InvalidManifestPhasePlan phase.PhaseId)

            for rollbackId in phase.RollbackInputIds do
                if not (Set.contains rollbackId rollbackIds) then
                    findings.Add(GitHubImmutableManifestFinding.MissingManifestRollbackInput rollbackId)

        if manifest.Reviewers.Length < 2 || not (sortedUnique (fun (value: GitHubManifestReviewer) -> value.Login) manifest.Reviewers) || not (manifest.Reviewers |> List.map _.GlobalId |> unique) then
            findings.Add(GitHubImmutableManifestFinding.InvalidManifestPopulation "reviewers")

        for reviewer in manifest.Reviewers do
            if not (validText reviewer.Login && validText reviewer.GlobalId && isSha 64 reviewer.DecisionSha256) then
                findings.Add(GitHubImmutableManifestFinding.InvalidManifestField $"reviewer:{reviewer.Login}")

        findings |> Seq.distinct |> Seq.toList

    let qualify manifestId roadmapRevision roadmapSha256 unitContractSha256 predecessorReceiptDigest discoverySourceRevision discoveryNormalizedDigest discoverySeal discoveredSubjects oldModel newModel artifactFingerprints subjects liveOperations receiverHeads settingsPlans archives phasePlans reviewers rollbackInputs createdAt =
        let draft =
            {
                SchemaVersion = 1
                ManifestId = manifestId
                RoadmapRevision = roadmapRevision
                RoadmapSha256 = roadmapSha256
                UnitContractSha256 = unitContractSha256
                PredecessorReceiptDigest = predecessorReceiptDigest
                DiscoverySourceRevision = discoverySourceRevision
                DiscoveryNormalizedDigest = discoveryNormalizedDigest
                DiscoverySeal = discoverySeal
                OldModel = oldModel
                NewModel = newModel
                ArtifactFingerprints = artifactFingerprints
                Subjects = subjects
                LiveOperations = liveOperations
                ReceiverHeads = receiverHeads
                SettingsPlans = settingsPlans
                Archives = archives
                PhasePlans = phasePlans
                Reviewers = reviewers
                RollbackInputs = rollbackInputs
                CreatedAt = createdAt
                NormalizedDigest = ""
                Seal = ""
            }

        match validate discoveredSubjects draft with
        | _ :: _ as findings -> Error findings
        | [] ->
            let digest = payloadParts draft |> framed |> hash
            Ok { draft with NormalizedDigest = digest; Seal = seal digest }

    let verify discoveredSubjects expectedSeal manifest =
        match validate discoveredSubjects manifest with
        | _ :: _ as findings -> Error findings
        | [] ->
            let digest = payloadParts manifest |> framed |> hash
            if digest <> manifest.NormalizedDigest then
                Error [ GitHubImmutableManifestFinding.AlteredManifestDigest ]
            elif not (isSha 64 expectedSeal) || manifest.Seal <> seal digest || manifest.Seal <> expectedSeal then
                Error [ GitHubImmutableManifestFinding.AlteredManifestSeal ]
            else
                Ok manifest

    let validateControls generated independent =
        let validateLane lane rows =
            let controls = rows |> List.map _.Control
            [
                if controls <> requiredControls || not (unique controls) then
                    yield { Code = "MANIFEST-CONTROLS"; ControlId = lane; Message = "control inventory differs" }

                for row in rows do
                    if not (row.ControlPassed && row.BaselineGreen) then
                        yield { Code = "MANIFEST-CONTROL-RED"; ControlId = controlId row.Control; Message = $"{lane} control did not pass" }
            ]

        match validateLane "generated" generated @ validateLane "independent" independent with
        | [] -> Ok()
        | findings -> Error findings
