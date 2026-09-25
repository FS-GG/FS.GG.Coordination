namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type GitHubRollbackDomain =
    | Settings
    | ReceiverPin
    | V1Projection
    | Schedule
    | AuthoritySnapshot

type GitHubRollbackStep =
    { Order: int
      StepId: string
      Domain: GitHubRollbackDomain
      TargetIdentity: string
      CapturedStateSha256: string
      RestorePayloadSha256: string }

type GitHubRollbackReceipt =
    { Order: int
      StepId: string
      PlanSeal: string
      PreviousReceiptSha256: string option
      ResultSha256: string
      ReceiptSha256: string }

type GitHubRollbackPlan =
    { Identity: string
      RoadmapRevision: string
      RoadmapSha256: string
      UnitContractSha256: string
      PredecessorReceiptDigest: string
      ManifestNormalizedDigest: string
      ManifestSeal: string
      HistoryNormalizedDigest: string
      HistorySeal: string
      StartEpoch: string
      TerminalEpoch: string
      Steps: GitHubRollbackStep list
      NormalizedDigest: string
      Seal: string
      CreatedAt: DateTimeOffset }

type GitHubRollbackPlanFinding =
    | InvalidIdentity
    | InvalidAuthority
    | InvalidEpochBoundary
    | InvalidStepPopulation
    | InvalidStep of string
    | MissingDomain of GitHubRollbackDomain
    | AlteredNormalizedDigest
    | AlteredSeal
    | ReceiptPopulationMismatch
    | InvalidReceipt of string
    | ReceiptChainMismatch of string

type GitHubRollbackPlanControlResult =
    { Control: string
      ControlPassed: bool
      BaselineGreen: bool }

module GitHubRollbackPlanQualification =
    let requiredDomains = [ Settings; ReceiverPin; V1Projection; Schedule; AuthoritySnapshot ]

    let requiredControls =
        [ "accepted-predecessor"; "roadmap-authority"; "manifest-binding"; "history-binding"
          "verified-v2-boundary"; "complete-restoration-domains"; "reverse-contiguous-order"
          "captured-prestate"; "restore-payload"; "receipt-prefix"; "receipt-plan-binding"
          "receipt-chain"; "deterministic-resume"; "seal-replay"; "no-provider-mutation"
          "no-rollback-execution"; "no-successor-authority" ]

    let private sha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private isSha value =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private isRevision value =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)

    // These atoms enter pipe- and newline-delimited seal material. Reject the
    // delimiters rather than changing the encoding of previously valid plans.
    let private isSealAtom (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && (value |> Seq.forall (fun ch -> ch <> '|' && not (Char.IsControl ch)))

    let private domainName (domain: GitHubRollbackDomain) =
        match domain with
        | Settings -> "settings"
        | ReceiverPin -> "receiver-pin"
        | V1Projection -> "v1-projection"
        | Schedule -> "schedule"
        | AuthoritySnapshot -> "authority-snapshot"

    let private stepLine (step: GitHubRollbackStep) =
        String.concat "|"
            [ string step.Order; step.StepId; domainName step.Domain; step.TargetIdentity
              step.CapturedStateSha256; step.RestorePayloadSha256 ]

    let private normalized (plan: GitHubRollbackPlan) =
        [ plan.Identity; plan.RoadmapRevision; plan.RoadmapSha256; plan.UnitContractSha256
          plan.PredecessorReceiptDigest; plan.ManifestNormalizedDigest; plan.ManifestSeal
          plan.HistoryNormalizedDigest; plan.HistorySeal; plan.StartEpoch; plan.TerminalEpoch
          plan.CreatedAt.ToUniversalTime().ToString("O")
          plan.Steps |> List.map stepLine |> String.concat "\n" ]
        |> String.concat "\n"
        |> sha256

    let private seal normalizedDigest = sha256 $"fsgg.github-rollback-plan/1\n{normalizedDigest}"

    let private findings (plan: GitHubRollbackPlan) =
        [ if not (isSealAtom plan.Identity) then InvalidIdentity
          if not (isRevision plan.RoadmapRevision)
             || [ plan.RoadmapSha256; plan.UnitContractSha256; plan.PredecessorReceiptDigest
                  plan.ManifestNormalizedDigest; plan.ManifestSeal; plan.HistoryNormalizedDigest; plan.HistorySeal ]
                |> List.exists (isSha >> not) then InvalidAuthority
          if plan.StartEpoch <> "VerifiedV2" || plan.TerminalEpoch <> "OperatingV1" then InvalidEpochBoundary
          if plan.Steps.IsEmpty
             || (plan.Steps |> List.map _.Order) <> [ plan.Steps.Length .. -1 .. 1 ]
             || (plan.Steps |> List.map _.Domain) <> List.rev requiredDomains
             || (plan.Steps |> List.map _.StepId |> Set.ofList |> Set.count) <> plan.Steps.Length then InvalidStepPopulation
          for step in plan.Steps do
              if not (isSealAtom step.StepId) || not (isSealAtom step.TargetIdentity)
                 || not (isSha step.CapturedStateSha256) || not (isSha step.RestorePayloadSha256) then
                  InvalidStep step.StepId
          let domains = plan.Steps |> List.map _.Domain |> Set.ofList
          for domain in requiredDomains do if not (Set.contains domain domains) then MissingDomain domain ]

    let verify expectedSeal (plan: GitHubRollbackPlan) =
        let basic = findings plan
        if not basic.IsEmpty then Error basic
        else
            let expectedNormalized = normalized plan
            if plan.NormalizedDigest <> expectedNormalized then Error [ AlteredNormalizedDigest ]
            elif plan.Seal <> seal expectedNormalized || expectedSeal <> plan.Seal then Error [ AlteredSeal ]
            else Ok plan

    let qualify identity roadmapRevision roadmapSha256 unitContractSha256 predecessorReceiptDigest
        manifestNormalizedDigest manifestSeal historyNormalizedDigest historySeal startEpoch steps createdAt =
        let provisional =
            { Identity=identity; RoadmapRevision=roadmapRevision; RoadmapSha256=roadmapSha256
              UnitContractSha256=unitContractSha256; PredecessorReceiptDigest=predecessorReceiptDigest
              ManifestNormalizedDigest=manifestNormalizedDigest; ManifestSeal=manifestSeal
              HistoryNormalizedDigest=historyNormalizedDigest; HistorySeal=historySeal
              StartEpoch=startEpoch; TerminalEpoch="OperatingV1"; Steps=steps
              NormalizedDigest=String.replicate 64 "0"; Seal=String.replicate 64 "0"; CreatedAt=createdAt }
        let digest = normalized provisional
        let plan = { provisional with NormalizedDigest=digest; Seal=seal digest }
        verify plan.Seal plan

    let private receiptDigest order stepId planSeal previous result =
        sha256 (String.concat "\n" [ string order; stepId; planSeal; defaultArg previous "root"; result ])

    let createReceipt (plan: GitHubRollbackPlan) (previous: GitHubRollbackReceipt option) (step: GitHubRollbackStep) resultSha256 =
        let previousDigest = previous |> Option.map _.ReceiptSha256
        { Order=step.Order; StepId=step.StepId; PlanSeal=plan.Seal; PreviousReceiptSha256=previousDigest
          ResultSha256=resultSha256
          ReceiptSha256=receiptDigest step.Order step.StepId plan.Seal previousDigest resultSha256 }

    let resume (plan: GitHubRollbackPlan) (receipts: GitHubRollbackReceipt list) =
        match verify plan.Seal plan with
        | Error values -> Error values
        | Ok _ when receipts.Length > plan.Steps.Length -> Error [ ReceiptPopulationMismatch ]
        | Ok _ ->
            let expectedSteps = plan.Steps |> List.take receipts.Length
            let mutable previous: string option = None
            let errors =
                (receipts, expectedSteps)
                ||> List.map2 (fun receipt step ->
                    let expected = receiptDigest step.Order step.StepId plan.Seal previous receipt.ResultSha256
                    let result =
                        if receipt.Order <> step.Order || receipt.StepId <> step.StepId || receipt.PlanSeal <> plan.Seal
                           || not (isSha receipt.ResultSha256) || receipt.ReceiptSha256 <> expected then
                            Some(InvalidReceipt receipt.StepId)
                        elif receipt.PreviousReceiptSha256 <> previous then Some(ReceiptChainMismatch receipt.StepId)
                        else None
                    previous <- Some receipt.ReceiptSha256
                    result)
                |> List.choose id
            if not errors.IsEmpty then Error errors
            elif receipts.Length = plan.Steps.Length then Ok None
            else Ok(Some plan.Steps[receipts.Length])

    let validateControls (primary: GitHubRollbackPlanControlResult list) (recovery: GitHubRollbackPlanControlResult list) =
        let validate name (values: GitHubRollbackPlanControlResult list) =
            let expected = Set.ofList requiredControls
            let observed = values |> List.map _.Control |> Set.ofList
            [ if observed <> expected || values.Length <> requiredControls.Length then $"{name}: incomplete control inventory"
              for value in values do
                  if not value.ControlPassed || not value.BaselineGreen then $"{name}: red control {value.Control}" ]
        let errors = validate "primary" primary @ validate "recovery" recovery
        if errors.IsEmpty then Ok() else Error errors
