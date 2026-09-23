namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type GitHubVerifiedHistoryOutcome = { OutputSchema: string; OutputSha256: string }
type GitHubRejectedHistoryOutcome = { Code: string; Reason: string; EvidenceSha256: string }
[<RequireQualifiedAccess>]
type GitHubHistoryExpectedOutcome = Verified of GitHubVerifiedHistoryOutcome | Rejected of GitHubRejectedHistoryOutcome
type GitHubSealedHistoryRecord =
    { ArchiveIdentity: string; SourceIdentity: string; SourceSchema: string; SourceBytesBase64: string
      SourceBytesSha256: string; SourceValueSha256: string; ExpectedOutcome: GitHubHistoryExpectedOutcome }
type GitHubSealedHistoryLookup = { LookupKey: string; ArchiveIdentity: string; RecordSha256: string }
type GitHubSealedHistoryProductionClosure =
    { Artifact: GitHubManifestFingerprint; V1UpcasterCount: int; ArchiveVerifierOnly: bool; LookupReadOnly: bool }
type GitHubSealedHistoryQualification =
    { SchemaVersion: int; QualificationId: string; RoadmapRevision: string; RoadmapSha256: string
      UnitContractSha256: string; PredecessorReceiptDigest: string; ManifestNormalizedDigest: string; ManifestSeal: string
      TransformNormalizedDigest: string; TransformSeal: string; LiveOperationNormalizedDigest: string; LiveOperationSeal: string
      Verifier: GitHubManifestFingerprint; ProductionClosure: GitHubSealedHistoryProductionClosure
      Records: GitHubSealedHistoryRecord list; LookupIndex: GitHubSealedHistoryLookup list; CreatedAt: DateTimeOffset
      ArchiveDigest: string; LookupDigest: string; NormalizedDigest: string; Seal: string }

[<RequireQualifiedAccess>]
type GitHubSealedHistoryFinding =
    | InvalidHistoryField of string | InvalidVerifierArtifact | InvalidProductionClosure | InvalidHistoryPopulation
    | InvalidSourceBytes of string | InvalidExpectedOutcome of string | InvalidLookupIndex
    | AlteredArchiveDigest | AlteredLookupDigest | AlteredHistoryDigest | AlteredHistorySeal

[<RequireQualifiedAccess>]
type GitHubSealedHistoryControl =
    | HistoryPrerequisiteReceipt | HistoryManifestBinding | HistoryTransformBinding | HistoryLiveOperationBinding
    | HistorySourceSchema | HistoryExactBytes | HistorySourceDigests | HistoryVerifierArtifact
    | HistoryExpectedOutcomes | HistoryLookupIndex | HistoryNoProductionUpcasters | HistoryCompleteCoverage
    | HistoryTamperRefusal | HistoryReplay | HistoryNoMutation
type GitHubSealedHistoryControlResult = { Control: GitHubSealedHistoryControl; ControlPassed: bool; BaselineGreen: bool }
type GitHubSealedHistoryQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubSealedHistoryQualification =
    let requiredControls =
        [ GitHubSealedHistoryControl.HistoryPrerequisiteReceipt; GitHubSealedHistoryControl.HistoryManifestBinding
          GitHubSealedHistoryControl.HistoryTransformBinding; GitHubSealedHistoryControl.HistoryLiveOperationBinding
          GitHubSealedHistoryControl.HistorySourceSchema; GitHubSealedHistoryControl.HistoryExactBytes
          GitHubSealedHistoryControl.HistorySourceDigests; GitHubSealedHistoryControl.HistoryVerifierArtifact
          GitHubSealedHistoryControl.HistoryExpectedOutcomes; GitHubSealedHistoryControl.HistoryLookupIndex
          GitHubSealedHistoryControl.HistoryNoProductionUpcasters; GitHubSealedHistoryControl.HistoryCompleteCoverage
          GitHubSealedHistoryControl.HistoryTamperRefusal; GitHubSealedHistoryControl.HistoryReplay
          GitHubSealedHistoryControl.HistoryNoMutation ]
    let controlId = function
        | GitHubSealedHistoryControl.HistoryPrerequisiteReceipt -> "history-prerequisite-receipt"
        | GitHubSealedHistoryControl.HistoryManifestBinding -> "history-manifest-binding"
        | GitHubSealedHistoryControl.HistoryTransformBinding -> "history-transform-binding"
        | GitHubSealedHistoryControl.HistoryLiveOperationBinding -> "history-live-operation-binding"
        | GitHubSealedHistoryControl.HistorySourceSchema -> "history-source-schema"
        | GitHubSealedHistoryControl.HistoryExactBytes -> "history-exact-bytes"
        | GitHubSealedHistoryControl.HistorySourceDigests -> "history-source-digests"
        | GitHubSealedHistoryControl.HistoryVerifierArtifact -> "history-verifier-artifact"
        | GitHubSealedHistoryControl.HistoryExpectedOutcomes -> "history-expected-outcomes"
        | GitHubSealedHistoryControl.HistoryLookupIndex -> "history-lookup-index"
        | GitHubSealedHistoryControl.HistoryNoProductionUpcasters -> "history-no-production-upcasters"
        | GitHubSealedHistoryControl.HistoryCompleteCoverage -> "history-complete-coverage"
        | GitHubSealedHistoryControl.HistoryTamperRefusal -> "history-tamper-refusal"
        | GitHubSealedHistoryControl.HistoryReplay -> "history-replay"
        | GitHubSealedHistoryControl.HistoryNoMutation -> "history-no-mutation"

    let private validText value = not (String.IsNullOrWhiteSpace value)
    let private isSha length value = validText value && value.Length = length && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant)
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private framed values = values |> List.map frame |> String.concat ""
    let private hashBytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private hash (value: string) = value |> Encoding.UTF8.GetBytes |> hashBytes
    let private unique values = List.length values = (values |> Set.ofList |> Set.count)
    let private fingerprintValid value = validText value.Name && validText value.Version && isSha 64 value.Sha256 && value.Bytes > 0L
    let private outcomeParts = function
        | GitHubHistoryExpectedOutcome.Verified value -> [ "verified"; value.OutputSchema; value.OutputSha256 ]
        | GitHubHistoryExpectedOutcome.Rejected value -> [ "rejected"; value.Code; value.Reason; value.EvidenceSha256 ]
    let private recordParts (value: GitHubSealedHistoryRecord) =
        [ yield value.ArchiveIdentity; yield value.SourceIdentity; yield value.SourceSchema; yield value.SourceBytesBase64
          yield value.SourceBytesSha256; yield value.SourceValueSha256; yield! outcomeParts value.ExpectedOutcome ]
    let recordSha256 value = recordParts value |> framed |> hash
    let private archiveDigest (records: GitHubSealedHistoryRecord list) = records |> List.collect recordParts |> framed |> hash
    let private lookupDigest (lookups: GitHubSealedHistoryLookup list) =
        lookups |> List.collect (fun value -> [ value.LookupKey; value.ArchiveIdentity; value.RecordSha256 ]) |> framed |> hash
    let private payloadParts (value: GitHubSealedHistoryQualification) =
        [ yield "fsgg.coordination.github-sealed-history-qualification/1"
          yield value.QualificationId; yield value.RoadmapRevision; yield value.RoadmapSha256; yield value.UnitContractSha256
          yield value.PredecessorReceiptDigest; yield value.ManifestNormalizedDigest; yield value.ManifestSeal
          yield value.TransformNormalizedDigest; yield value.TransformSeal; yield value.LiveOperationNormalizedDigest; yield value.LiveOperationSeal
          yield value.Verifier.Name; yield value.Verifier.Version; yield value.Verifier.Sha256; yield string value.Verifier.Bytes
          yield value.ProductionClosure.Artifact.Name; yield value.ProductionClosure.Artifact.Version
          yield value.ProductionClosure.Artifact.Sha256; yield string value.ProductionClosure.Artifact.Bytes
          yield string value.ProductionClosure.V1UpcasterCount; yield string value.ProductionClosure.ArchiveVerifierOnly
          yield string value.ProductionClosure.LookupReadOnly
          for record in value.Records do yield! recordParts record
          for lookup in value.LookupIndex do yield lookup.LookupKey; yield lookup.ArchiveIdentity; yield lookup.RecordSha256
          yield value.CreatedAt.ToUniversalTime().ToString("O"); yield value.ArchiveDigest; yield value.LookupDigest ]
    let private makeSeal digest = [ "fsgg.coordination.github-sealed-history-seal/1"; digest ] |> framed |> hash

    let private validate (expectedRecords: GitHubSealedHistoryRecord list) (value: GitHubSealedHistoryQualification) =
        let findings = ResizeArray<GitHubSealedHistoryFinding>()
        let require condition field = if not condition then findings.Add(GitHubSealedHistoryFinding.InvalidHistoryField field)
        require (value.SchemaVersion = 1) "schemaVersion"; require (validText value.QualificationId) "qualificationId"
        require (isSha 40 value.RoadmapRevision) "roadmapRevision"
        for name, digest in [ "roadmapSha256",value.RoadmapSha256; "unitContractSha256",value.UnitContractSha256
                              "predecessorReceiptDigest",value.PredecessorReceiptDigest; "manifestNormalizedDigest",value.ManifestNormalizedDigest
                              "manifestSeal",value.ManifestSeal; "transformNormalizedDigest",value.TransformNormalizedDigest
                              "transformSeal",value.TransformSeal; "liveOperationNormalizedDigest",value.LiveOperationNormalizedDigest
                              "liveOperationSeal",value.LiveOperationSeal ] do require (isSha 64 digest) name
        if not (fingerprintValid value.Verifier) then findings.Add GitHubSealedHistoryFinding.InvalidVerifierArtifact
        if not (fingerprintValid value.ProductionClosure.Artifact && value.ProductionClosure.V1UpcasterCount = 0
                && value.ProductionClosure.ArchiveVerifierOnly && value.ProductionClosure.LookupReadOnly) then
            findings.Add GitHubSealedHistoryFinding.InvalidProductionClosure

        let recordKeys = value.Records |> List.map _.ArchiveIdentity
        let expectedKeys = expectedRecords |> List.map _.ArchiveIdentity
        if List.isEmpty expectedKeys || expectedKeys <> List.sort expectedKeys || not (unique expectedKeys)
           || value.Records <> expectedRecords || recordKeys <> expectedKeys || not (unique recordKeys) then
            findings.Add GitHubSealedHistoryFinding.InvalidHistoryPopulation
        for record in value.Records do
            if not (validText record.ArchiveIdentity && validText record.SourceIdentity && validText record.SourceSchema
                    && isSha 64 record.SourceBytesSha256 && isSha 64 record.SourceValueSha256) then
                findings.Add(GitHubSealedHistoryFinding.InvalidHistoryField record.ArchiveIdentity)
            try
                let bytes = Convert.FromBase64String record.SourceBytesBase64
                if bytes.Length = 0 || hashBytes bytes <> record.SourceBytesSha256 then findings.Add(GitHubSealedHistoryFinding.InvalidSourceBytes record.ArchiveIdentity)
            with :? FormatException -> findings.Add(GitHubSealedHistoryFinding.InvalidSourceBytes record.ArchiveIdentity)
            match record.ExpectedOutcome with
            | GitHubHistoryExpectedOutcome.Verified outcome ->
                if not (validText outcome.OutputSchema && isSha 64 outcome.OutputSha256) then findings.Add(GitHubSealedHistoryFinding.InvalidExpectedOutcome record.ArchiveIdentity)
            | GitHubHistoryExpectedOutcome.Rejected outcome ->
                if not (validText outcome.Code && validText outcome.Reason && isSha 64 outcome.EvidenceSha256) then findings.Add(GitHubSealedHistoryFinding.InvalidExpectedOutcome record.ArchiveIdentity)

        let lookupKeys = value.LookupIndex |> List.map _.LookupKey
        let expectedLookups = value.Records |> List.map (fun record -> record.SourceIdentity, record.ArchiveIdentity, recordSha256 record) |> List.sort
        let actualLookups = value.LookupIndex |> List.map (fun lookup -> lookup.LookupKey, lookup.ArchiveIdentity, lookup.RecordSha256)
        if lookupKeys <> List.sort lookupKeys || not (unique lookupKeys) || actualLookups <> expectedLookups
           || value.LookupIndex |> List.exists (fun lookup -> not (validText lookup.LookupKey && validText lookup.ArchiveIdentity && isSha 64 lookup.RecordSha256)) then
            findings.Add GitHubSealedHistoryFinding.InvalidLookupIndex
        findings |> Seq.distinct |> Seq.toList

    let qualify qualificationId roadmapRevision roadmapSha256 unitContractSha256 predecessorReceiptDigest manifestNormalizedDigest manifestSeal transformNormalizedDigest transformSeal liveOperationNormalizedDigest liveOperationSeal verifier productionClosure (records: GitHubSealedHistoryRecord list) (lookupIndex: GitHubSealedHistoryLookup list) createdAt =
        let archive = archiveDigest records
        let lookup = lookupDigest lookupIndex
        let draft =
            { SchemaVersion=1; QualificationId=qualificationId; RoadmapRevision=roadmapRevision; RoadmapSha256=roadmapSha256
              UnitContractSha256=unitContractSha256; PredecessorReceiptDigest=predecessorReceiptDigest
              ManifestNormalizedDigest=manifestNormalizedDigest; ManifestSeal=manifestSeal; TransformNormalizedDigest=transformNormalizedDigest
              TransformSeal=transformSeal; LiveOperationNormalizedDigest=liveOperationNormalizedDigest; LiveOperationSeal=liveOperationSeal
              Verifier=verifier; ProductionClosure=productionClosure; Records=records; LookupIndex=lookupIndex; CreatedAt=createdAt
              ArchiveDigest=archive; LookupDigest=lookup; NormalizedDigest=""; Seal="" }
        match validate records draft with
        | [] -> let digest = payloadParts draft |> framed |> hash in Ok { draft with NormalizedDigest=digest; Seal=makeSeal digest }
        | findings -> Error findings

    let verify expectedRecords expectedSeal qualification =
        match validate expectedRecords qualification with
        | findings when not (List.isEmpty findings) -> Error findings
        | _ when archiveDigest qualification.Records <> qualification.ArchiveDigest -> Error [ GitHubSealedHistoryFinding.AlteredArchiveDigest ]
        | _ when lookupDigest qualification.LookupIndex <> qualification.LookupDigest -> Error [ GitHubSealedHistoryFinding.AlteredLookupDigest ]
        | _ ->
            let digest = payloadParts qualification |> framed |> hash
            if digest <> qualification.NormalizedDigest then Error [ GitHubSealedHistoryFinding.AlteredHistoryDigest ]
            elif makeSeal digest <> qualification.Seal || expectedSeal <> qualification.Seal then Error [ GitHubSealedHistoryFinding.AlteredHistorySeal ]
            else Ok qualification

    let validateControls generated independent =
        let lane name rows =
            let counts = rows |> List.countBy _.Control |> Map.ofList
            [ for control in requiredControls do match Map.tryFind control counts with Some 1 -> () | _ -> yield { Code="HISTORY-CONTROL-POPULATION"; ControlId=controlId control; Message=$"{name} control population differs" }
              for row in rows do if not (row.ControlPassed && row.BaselineGreen) then yield { Code="HISTORY-CONTROL-RED"; ControlId=controlId row.Control; Message=$"{name} control did not pass" } ]
        match lane "generated" generated @ lane "independent" independent with [] -> Ok() | findings -> Error findings
