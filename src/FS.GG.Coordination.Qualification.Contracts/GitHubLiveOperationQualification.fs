namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type GitHubLiveOperationFamily = Claim | QueuedWrite | Review | Delivery | Release | CutoverAdjacent
type GitHubLiveOperationObligation = { OperationIdentity: string; Family: GitHubLiveOperationFamily }
type GitHubDrainDisposition = { CompletionReceiptSha256: string; DrainFence: string }
type GitHubMigrateDisposition = { TargetOperationIdentity: string; GlobalId: string; TargetSchema: string; PayloadSha256: string; MappingSha256: string }
type GitHubParkDisposition = { ParkingIdentity: string; ResumeCondition: string; PayloadSha256: string; EvidenceSha256: string }
type GitHubInvalidDisposition = { Code: string; Reason: string; EvidenceSha256: string }

[<RequireQualifiedAccess>]
type GitHubLiveOperationDisposition =
    | Drain of GitHubDrainDisposition
    | Migrate of GitHubMigrateDisposition
    | Park of GitHubParkDisposition
    | Invalid of GitHubInvalidDisposition

type GitHubLiveOperationDecision =
    { OperationIdentity: string; GlobalId: string; Family: GitHubLiveOperationFamily; SourceState: string
      SourceBytesSha256: string; DependencySetSha256: string; Disposition: GitHubLiveOperationDisposition }

type GitHubLiveOperationQualification =
    { SchemaVersion: int; QualificationId: string; RoadmapRevision: string; RoadmapSha256: string
      UnitContractSha256: string; PredecessorReceiptDigest: string; ManifestNormalizedDigest: string
      ManifestSeal: string; TransformNormalizedDigest: string; TransformSeal: string
      Planner: GitHubManifestFingerprint; Obligations: GitHubLiveOperationObligation list
      Decisions: GitHubLiveOperationDecision list; CreatedAt: DateTimeOffset; NormalizedDigest: string; Seal: string }

[<RequireQualifiedAccess>]
type GitHubLiveOperationFinding =
    | InvalidLiveOperationField of string
    | InvalidPlannerFingerprint
    | InvalidObligationPopulation
    | InvalidDecisionPopulation
    | InvalidDrainDisposition of string
    | InvalidMigrateDisposition of string
    | InvalidParkDisposition of string
    | InvalidExplicitInvalidDisposition of string
    | AlteredLiveOperationDigest
    | AlteredLiveOperationSeal

[<RequireQualifiedAccess>]
type GitHubLiveOperationControl =
    | LiveOperationPrerequisiteReceipt | LiveOperationManifestBinding | LiveOperationTransformBinding
    | LiveOperationClaims | LiveOperationQueuedWrites | LiveOperationReviews | LiveOperationDeliveries
    | LiveOperationReleases | LiveOperationCutoverAdjacent | LiveOperationCompleteCoverage
    | LiveOperationTypedDispositions | LiveOperationTamperRefusal | LiveOperationReplay | LiveOperationNoMutation

type GitHubLiveOperationControlResult = { Control: GitHubLiveOperationControl; ControlPassed: bool; BaselineGreen: bool }
type GitHubLiveOperationQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubLiveOperationQualification =
    let requiredFamilies =
        [ GitHubLiveOperationFamily.Claim; GitHubLiveOperationFamily.CutoverAdjacent
          GitHubLiveOperationFamily.Delivery; GitHubLiveOperationFamily.QueuedWrite
          GitHubLiveOperationFamily.Release; GitHubLiveOperationFamily.Review ]

    let familyId = function
        | GitHubLiveOperationFamily.Claim -> "claim"
        | GitHubLiveOperationFamily.QueuedWrite -> "queued-write"
        | GitHubLiveOperationFamily.Review -> "review"
        | GitHubLiveOperationFamily.Delivery -> "delivery"
        | GitHubLiveOperationFamily.Release -> "release"
        | GitHubLiveOperationFamily.CutoverAdjacent -> "cutover-adjacent"

    let requiredControls =
        [ GitHubLiveOperationControl.LiveOperationPrerequisiteReceipt
          GitHubLiveOperationControl.LiveOperationManifestBinding
          GitHubLiveOperationControl.LiveOperationTransformBinding
          GitHubLiveOperationControl.LiveOperationClaims
          GitHubLiveOperationControl.LiveOperationQueuedWrites
          GitHubLiveOperationControl.LiveOperationReviews
          GitHubLiveOperationControl.LiveOperationDeliveries
          GitHubLiveOperationControl.LiveOperationReleases
          GitHubLiveOperationControl.LiveOperationCutoverAdjacent
          GitHubLiveOperationControl.LiveOperationCompleteCoverage
          GitHubLiveOperationControl.LiveOperationTypedDispositions
          GitHubLiveOperationControl.LiveOperationTamperRefusal
          GitHubLiveOperationControl.LiveOperationReplay
          GitHubLiveOperationControl.LiveOperationNoMutation ]

    let controlId = function
        | GitHubLiveOperationControl.LiveOperationPrerequisiteReceipt -> "live-operation-prerequisite-receipt"
        | GitHubLiveOperationControl.LiveOperationManifestBinding -> "live-operation-manifest-binding"
        | GitHubLiveOperationControl.LiveOperationTransformBinding -> "live-operation-transform-binding"
        | GitHubLiveOperationControl.LiveOperationClaims -> "live-operation-claims"
        | GitHubLiveOperationControl.LiveOperationQueuedWrites -> "live-operation-queued-writes"
        | GitHubLiveOperationControl.LiveOperationReviews -> "live-operation-reviews"
        | GitHubLiveOperationControl.LiveOperationDeliveries -> "live-operation-deliveries"
        | GitHubLiveOperationControl.LiveOperationReleases -> "live-operation-releases"
        | GitHubLiveOperationControl.LiveOperationCutoverAdjacent -> "live-operation-cutover-adjacent"
        | GitHubLiveOperationControl.LiveOperationCompleteCoverage -> "live-operation-complete-coverage"
        | GitHubLiveOperationControl.LiveOperationTypedDispositions -> "live-operation-typed-dispositions"
        | GitHubLiveOperationControl.LiveOperationTamperRefusal -> "live-operation-tamper-refusal"
        | GitHubLiveOperationControl.LiveOperationReplay -> "live-operation-replay"
        | GitHubLiveOperationControl.LiveOperationNoMutation -> "live-operation-no-mutation"

    let private validText value = not (String.IsNullOrWhiteSpace value)
    let private isSha length value = validText value && value.Length = length && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant)
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private framed values = values |> List.map frame |> String.concat ""
    let private hash (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private unique values = List.length values = (values |> Set.ofList |> Set.count)
    let private key identity family = identity, familyId family
    let private obligationKey (value: GitHubLiveOperationObligation) = key value.OperationIdentity value.Family
    let private decisionKey (value: GitHubLiveOperationDecision) = key value.OperationIdentity value.Family

    let private dispositionParts = function
        | GitHubLiveOperationDisposition.Drain value -> [ "drain"; value.CompletionReceiptSha256; value.DrainFence ]
        | GitHubLiveOperationDisposition.Migrate value ->
            [ "migrate"; value.TargetOperationIdentity; value.GlobalId; value.TargetSchema; value.PayloadSha256; value.MappingSha256 ]
        | GitHubLiveOperationDisposition.Park value ->
            [ "park"; value.ParkingIdentity; value.ResumeCondition; value.PayloadSha256; value.EvidenceSha256 ]
        | GitHubLiveOperationDisposition.Invalid value -> [ "invalid"; value.Code; value.Reason; value.EvidenceSha256 ]

    let private payloadParts (value: GitHubLiveOperationQualification) =
        [ yield "fsgg.coordination.github-live-operation-qualification/1"
          yield value.QualificationId; yield value.RoadmapRevision; yield value.RoadmapSha256
          yield value.UnitContractSha256; yield value.PredecessorReceiptDigest
          yield value.ManifestNormalizedDigest; yield value.ManifestSeal
          yield value.TransformNormalizedDigest; yield value.TransformSeal
          yield value.Planner.Name; yield value.Planner.Version; yield value.Planner.Sha256; yield string value.Planner.Bytes
          for obligation in value.Obligations do yield obligation.OperationIdentity; yield familyId obligation.Family
          for decision in value.Decisions do
              yield decision.OperationIdentity; yield decision.GlobalId; yield familyId decision.Family
              yield decision.SourceState; yield decision.SourceBytesSha256; yield decision.DependencySetSha256
              yield! dispositionParts decision.Disposition
          yield value.CreatedAt.ToUniversalTime().ToString("O") ]

    let private makeSeal digest = [ "fsgg.coordination.github-live-operation-seal/1"; digest ] |> framed |> hash

    let private validate expectedObligations (qualification: GitHubLiveOperationQualification) =
        let findings = ResizeArray<GitHubLiveOperationFinding>()
        let require condition field = if not condition then findings.Add(GitHubLiveOperationFinding.InvalidLiveOperationField field)
        require (qualification.SchemaVersion = 1) "schemaVersion"
        require (validText qualification.QualificationId) "qualificationId"
        require (isSha 40 qualification.RoadmapRevision) "roadmapRevision"
        for field, value in
            [ "roadmapSha256", qualification.RoadmapSha256; "unitContractSha256", qualification.UnitContractSha256
              "predecessorReceiptDigest", qualification.PredecessorReceiptDigest; "manifestNormalizedDigest", qualification.ManifestNormalizedDigest
              "manifestSeal", qualification.ManifestSeal; "transformNormalizedDigest", qualification.TransformNormalizedDigest
              "transformSeal", qualification.TransformSeal ] do require (isSha 64 value) field
        if not (validText qualification.Planner.Name && validText qualification.Planner.Version && isSha 64 qualification.Planner.Sha256 && qualification.Planner.Bytes > 0L) then
            findings.Add GitHubLiveOperationFinding.InvalidPlannerFingerprint

        let obligationKeys = qualification.Obligations |> List.map obligationKey
        let expectedKeys = expectedObligations |> List.map obligationKey
        if List.isEmpty expectedKeys || expectedKeys <> List.sort expectedKeys || not (unique expectedKeys)
           || qualification.Obligations <> expectedObligations
           || qualification.Obligations |> List.exists (fun value -> not (validText value.OperationIdentity)) then
            findings.Add GitHubLiveOperationFinding.InvalidObligationPopulation

        let decisionKeys = qualification.Decisions |> List.map decisionKey
        if decisionKeys <> obligationKeys || not (unique decisionKeys) then findings.Add GitHubLiveOperationFinding.InvalidDecisionPopulation
        let represented = qualification.Decisions |> List.map _.Family |> Set.ofList
        if requiredFamilies |> List.exists (fun family -> not (Set.contains family represented)) then findings.Add GitHubLiveOperationFinding.InvalidDecisionPopulation

        for decision in qualification.Decisions do
            let identity = $"{decision.OperationIdentity}:{familyId decision.Family}"
            if not (validText decision.OperationIdentity && validText decision.GlobalId && validText decision.SourceState
                    && isSha 64 decision.SourceBytesSha256 && isSha 64 decision.DependencySetSha256) then
                findings.Add(GitHubLiveOperationFinding.InvalidLiveOperationField identity)
            match decision.Disposition with
            | GitHubLiveOperationDisposition.Drain value ->
                if not (isSha 64 value.CompletionReceiptSha256 && validText value.DrainFence) then findings.Add(GitHubLiveOperationFinding.InvalidDrainDisposition identity)
            | GitHubLiveOperationDisposition.Migrate value ->
                if not (validText value.TargetOperationIdentity && value.GlobalId = decision.GlobalId && validText value.TargetSchema
                        && isSha 64 value.PayloadSha256 && isSha 64 value.MappingSha256) then findings.Add(GitHubLiveOperationFinding.InvalidMigrateDisposition identity)
            | GitHubLiveOperationDisposition.Park value ->
                if not (validText value.ParkingIdentity && validText value.ResumeCondition && isSha 64 value.PayloadSha256 && isSha 64 value.EvidenceSha256) then
                    findings.Add(GitHubLiveOperationFinding.InvalidParkDisposition identity)
            | GitHubLiveOperationDisposition.Invalid value ->
                if not (validText value.Code && validText value.Reason && isSha 64 value.EvidenceSha256) then
                    findings.Add(GitHubLiveOperationFinding.InvalidExplicitInvalidDisposition identity)
        findings |> Seq.distinct |> Seq.toList

    let qualify qualificationId roadmapRevision roadmapSha256 unitContractSha256 predecessorReceiptDigest manifestNormalizedDigest manifestSeal transformNormalizedDigest transformSeal planner obligations decisions createdAt =
        let draft =
            { SchemaVersion = 1; QualificationId = qualificationId; RoadmapRevision = roadmapRevision; RoadmapSha256 = roadmapSha256
              UnitContractSha256 = unitContractSha256; PredecessorReceiptDigest = predecessorReceiptDigest
              ManifestNormalizedDigest = manifestNormalizedDigest; ManifestSeal = manifestSeal
              TransformNormalizedDigest = transformNormalizedDigest; TransformSeal = transformSeal; Planner = planner
              Obligations = obligations; Decisions = decisions; CreatedAt = createdAt; NormalizedDigest = ""; Seal = "" }
        match validate obligations draft with
        | [] ->
            let digest = payloadParts draft |> framed |> hash
            Ok { draft with NormalizedDigest = digest; Seal = makeSeal digest }
        | findings -> Error findings

    let verify expectedObligations expectedSeal qualification =
        match validate expectedObligations qualification with
        | findings when not (List.isEmpty findings) -> Error findings
        | _ ->
            let digest = payloadParts qualification |> framed |> hash
            if digest <> qualification.NormalizedDigest then Error [ GitHubLiveOperationFinding.AlteredLiveOperationDigest ]
            elif makeSeal digest <> qualification.Seal || expectedSeal <> qualification.Seal then Error [ GitHubLiveOperationFinding.AlteredLiveOperationSeal ]
            else Ok qualification

    let validateControls generated independent =
        let validateLane lane rows =
            let counts = rows |> List.countBy _.Control |> Map.ofList
            [ for control in requiredControls do
                  match Map.tryFind control counts with
                  | Some 1 -> ()
                  | _ -> yield { Code = "LIVE-OPERATION-CONTROL-POPULATION"; ControlId = controlId control; Message = $"{lane} control population differs" }
              for row in rows do
                  if not (row.ControlPassed && row.BaselineGreen) then
                      yield { Code = "LIVE-OPERATION-CONTROL-RED"; ControlId = controlId row.Control; Message = $"{lane} control did not pass" } ]
        match validateLane "generated" generated @ validateLane "independent" independent with
        | [] -> Ok()
        | findings -> Error findings
