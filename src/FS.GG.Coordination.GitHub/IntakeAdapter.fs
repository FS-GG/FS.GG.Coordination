namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type IntakeSurface = IssueIdentity | NativeIssueType | OrganizationFields | ProjectMembership | Hierarchy | Dependencies | RepositoryScope | InitialJournal | SchedulingIntent | Contract | TouchSet | Projections
type IntakeOutcome = Observed of string | Missing | Redacted | Unauthorized of string | Archived | External | Draft | Unknown of string | Duplicate of string | Cycle of string | Partial of string | Stale of observed: string * expected: string | Unsupported of string | Unreadable of string | Indeterminate of string
type IntakeFact = { Surface: IntakeSurface; Outcome: IntakeOutcome }
type IntakePage = { Number: int; Cursor: string option; NextCursor: string option; Facts: IntakeFact list; TerminalPage: bool }
type IntakeObservation = { Identity: string; Revision: string; Pages: IntakePage list }
type IntakeSnapshot = { Identity: string; Revision: string; Facts: IntakeFact list; Digest: string }
type IntakeDiagnostic = { Code: string; Surface: IntakeSurface option; Message: string }
type ProtocolInitializationIntent = InitializeJournal of string | InitializeSchedulingIntent of string | InitializeContract of string | InitializeTouchSet of string list | InitializeProjections of string list
type IntakeRequest = { Identity: string; Repository: string; Causation: string; Initializations: ProtocolInitializationIntent list }
type CanonicalIntakeIntent = { Identity: string; Repository: string; Causation: string; Initializations: ProtocolInitializationIntent list; Digest: string }
type IntakeEffect = { Ordinal: int; OperationIdentity: string; Dependencies: string list; ExpectedRevision: string; Precondition: IntakeFact; Postcondition: IntakeFact; Compensation: IntakeFact }
type IntakePlan = { Identity: string; Repository: string; Causation: string; Before: IntakeSnapshot; Effects: IntakeEffect list; IntendedPostState: IntakeFact list; Digest: string }
type IntakeNoOp = { Identity: string; Revision: string; Digest: string }
type IntakePlanDecision = IntakePlanned of IntakePlan | IntakeNoOp of IntakeNoOp
type DurableEffect = { PlanDigest: string; Ordinal: int; OperationIdentity: string; ResultRevision: string; PostStateDigest: string }
type IntakeApplyFailure = InvalidSealedPlan | PreStateRefused of IntakeDiagnostic list | FullFenceChanged | ScriptLengthMismatch | EffectRejected of ordinal: int * reason: string * accepted: DurableEffect list | EffectPostStateRefused of ordinal: int * IntakeDiagnostic list * accepted: DurableEffect list | EffectPreconditionChanged of ordinal: int * accepted: DurableEffect list | EffectIdentityMismatch of ordinal: int * accepted: DurableEffect list | EffectPostStateMismatch of ordinal: int * accepted: DurableEffect list | DurableResultMismatch of ordinal: int | FinalPostStateMismatch of accepted: DurableEffect list
type ScriptedEffectResult = { Ordinal: int; OperationIdentity: string; Accepted: bool; Reason: string option; After: IntakeObservation }
type IntakeApplyReceipt = { PlanDigest: string; FinalRevision: string; AcceptedEffects: DurableEffect list; CompensatedOrdinals: int list }
type IntakeApplyMode = Execute | Resume of DurableEffect list | RollForward of DurableEffect list | Compensate of DurableEffect list

[<RequireQualifiedAccess>]
type DiscoveryDetail = Known of string | ExplicitlyUnknown of reason: string | Deferred of reason: string

[<RequireQualifiedAccess>]
type CaptureIdentityMode = CreateOrReuse

[<RequireQualifiedAccess>]
type CaptureAuthorityRead = IssueIdentity | NativeTypeAndFields | ProjectMembership | Relations | RepositoryScope | ProtocolState

type StagedCaptureRequest =
    { Identity: string
      IdentityMode: CaptureIdentityMode
      Repository: string
      Causation: string
      RootCause: DiscoveryDetail
      Verification: DiscoveryDetail
      TouchSet: string list option }

type StagedCaptureObservation =
    { Intake: IntakeObservation
      AuthorityReads: CaptureAuthorityRead list
      UnrelatedProjectItems: int
      UnrelatedBacklogItems: int }

type IntakeOperationBudget = { AuthorityReads: int; Mutations: int }
type StagedCapturePlan = { ContractSchema: string; Intent: CanonicalIntakeIntent; Decision: IntakePlanDecision; Budget: IntakeOperationBudget }

[<RequireQualifiedAccess>]
type ReadyPromotionSurface = RootCause | TouchSet | VerificationContract | Dependencies | RouteDecision | NativeIssueType | OrganizationFields | RepositoryScope | WorkClassification

type ReadyPromotionFact = { Surface: ReadyPromotionSurface; Value: string }

[<RequireQualifiedAccess>]
module IntakeAdapter =
    let stagedContractSchema = "fsgg.coord.intake/v1"
    let requiredCaptureReads =
        [ CaptureAuthorityRead.IssueIdentity
          CaptureAuthorityRead.NativeTypeAndFields
          CaptureAuthorityRead.ProjectMembership
          CaptureAuthorityRead.Relations
          CaptureAuthorityRead.RepositoryScope
          CaptureAuthorityRead.ProtocolState ]
    let requiredReadyPromotionSurfaces =
        [ ReadyPromotionSurface.RootCause
          ReadyPromotionSurface.TouchSet
          ReadyPromotionSurface.VerificationContract
          ReadyPromotionSurface.Dependencies
          ReadyPromotionSurface.RouteDecision
          ReadyPromotionSurface.NativeIssueType
          ReadyPromotionSurface.OrganizationFields
          ReadyPromotionSurface.RepositoryScope
          ReadyPromotionSurface.WorkClassification ]
    let private surfaces = [ IssueIdentity; NativeIssueType; OrganizationFields; ProjectMembership; Hierarchy; Dependencies; RepositoryScope; InitialJournal; SchedulingIntent; Contract; TouchSet; Projections ]
    let private surfaceText = function IssueIdentity -> "issue-identity" | NativeIssueType -> "native-issue-type" | OrganizationFields -> "organization-fields" | ProjectMembership -> "project-membership" | Hierarchy -> "hierarchy" | Dependencies -> "dependencies" | RepositoryScope -> "repository-scope" | InitialJournal -> "initial-journal" | SchedulingIntent -> "scheduling-intent" | Contract -> "contract" | TouchSet -> "touch-set" | Projections -> "projections"
    let private outcomeText = function Observed v -> "observed:" + v | Missing -> "missing" | Redacted -> "redacted" | Unauthorized v -> "unauthorized:" + v | Archived -> "archived" | External -> "external" | Draft -> "draft" | Unknown v -> "unknown:" + v | Duplicate v -> "duplicate:" + v | Cycle v -> "cycle:" + v | Partial v -> "partial:" + v | Stale(o, e) -> "stale:" + o + ":" + e | Unsupported v -> "unsupported:" + v | Unreadable v -> "unreadable:" + v | Indeterminate v -> "indeterminate:" + v
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private digest values = values |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private canonicalValues (values: string list) = if obj.ReferenceEquals(values, null) then [] else values |> List.map (fun value -> if isNull value then "" else value.Trim()) |> List.distinct |> List.sort
    let private factText (fact: IntakeFact) = surfaceText fact.Surface + "=" + outcomeText fact.Outcome
    let private diag code surface message : IntakeDiagnostic = { Code = code; Surface = surface; Message = message }
    let private intentSurface = function InitializeJournal _ -> InitialJournal | InitializeSchedulingIntent _ -> SchedulingIntent | InitializeContract _ -> Contract | InitializeTouchSet _ -> TouchSet | InitializeProjections _ -> Projections
    let private canonicalText (value: string) = if isNull value then "" else value.Trim()
    let private normalizeIntent = function InitializeJournal v -> InitializeJournal(canonicalText v) | InitializeSchedulingIntent v -> InitializeSchedulingIntent(canonicalText v) | InitializeContract v -> InitializeContract(canonicalText v) | InitializeTouchSet v -> InitializeTouchSet(canonicalValues v) | InitializeProjections v -> InitializeProjections(canonicalValues v)
    let private intentValue = function InitializeJournal v | InitializeSchedulingIntent v | InitializeContract v -> v | InitializeTouchSet values | InitializeProjections values -> String.concat "," values
    let private intentText intent = surfaceText (intentSurface intent) + "=" + intentValue intent
    let private intentDigest identity repository causation initializations = digest ([ identity; repository; causation ] @ (initializations |> List.map intentText))

    let validate (request: IntakeRequest) =
        if obj.ReferenceEquals(request, null) then Error [ diag "INTAKE-REQUEST-NULL" None "request is required" ] else
        let hasNullIntent = not (obj.ReferenceEquals(request.Initializations, null)) && request.Initializations |> List.exists (fun value -> obj.ReferenceEquals(value, null))
        let normalized = if obj.ReferenceEquals(request.Initializations, null) || hasNullIntent then [] else request.Initializations |> List.map normalizeIntent |> List.sortBy (intentSurface >> surfaceText)
        let findings =
            [ if not (validText request.Identity) then yield diag "INTAKE-IDENTITY" None "identity must be canonical non-empty text"
              if not (validText request.Repository) then yield diag "INTAKE-REPOSITORY" (Some RepositoryScope) "repository must be canonical non-empty text"
              if not (validText request.Causation) then yield diag "INTAKE-CAUSATION" None "causation must be canonical non-empty text"
              if hasNullIntent then yield diag "INTAKE-INTENT-NULL" None "initialization intents must be typed values"
              if List.isEmpty normalized then yield diag "INTAKE-INTENTS" None "at least one initialization intent is required"
              for surface, values in normalized |> List.groupBy intentSurface do if values.Length > 1 then yield diag "INTAKE-DUPLICATE-INTENT" (Some surface) "initialization family occurred more than once"
              for intent in normalized do if not (validText (intentValue intent)) then yield diag "INTAKE-INTENT-VALUE" (Some(intentSurface intent)) "initialization value must be non-empty canonical text" ]
        if not (List.isEmpty findings) then Error findings else
        let identity, repository, causation = request.Identity.Trim(), request.Repository.Trim(), request.Causation.Trim()
        Ok { Identity = identity; Repository = repository; Causation = causation; Initializations = normalized; Digest = intentDigest identity repository causation normalized }

    let inspect (observation: IntakeObservation) =
        if obj.ReferenceEquals(observation, null) then Error [ diag "INTAKE-OBSERVATION-NULL" None "observation is required" ]
        elif not (validText observation.Identity) then Error [ diag "INTAKE-IDENTITY" None "observation identity is invalid" ]
        elif not (validText observation.Revision) then Error [ diag "INTAKE-REVISION" None "observation revision is invalid" ]
        elif obj.ReferenceEquals(observation.Pages, null) || List.isEmpty observation.Pages then Error [ diag "INTAKE-PAGES" None "a complete page chain is required" ]
        else
            let nonNullPages = observation.Pages |> List.forall (fun page -> not (obj.ReferenceEquals(page, null)) && not (obj.ReferenceEquals(page.Facts, null)) && page.Facts |> List.forall (fun fact -> not (obj.ReferenceEquals(fact, null))))
            if not nonNullPages then Error [ diag "INTAKE-PAGE-NULL" None "pages and page facts are required" ] else
            let pageShape = observation.Pages |> List.mapi (fun index (page: IntakePage) -> page.Number = index + 1 && page.TerminalPage = (index = observation.Pages.Length - 1) && page.TerminalPage = page.NextCursor.IsNone && (if index = 0 then page.Cursor.IsNone else page.Cursor = observation.Pages[index - 1].NextCursor)) |> List.forall id
            let cursors = (observation.Pages |> List.choose _.Cursor) @ (observation.Pages |> List.choose _.NextCursor)
            let cursorCycle = cursors |> List.groupBy id |> List.exists (fun (_, values) -> values.Length > 2)
            if not pageShape then Error [ diag "INTAKE-PAGE-CHAIN" None "page, cursor, and terminal evidence are inconsistent" ]
            elif cursorCycle then Error [ diag "INTAKE-CURSOR-CYCLE" None "cursor chain repeated or cycled" ]
            else
                let facts : IntakeFact list = observation.Pages |> List.collect _.Facts |> List.sortBy (fun fact -> surfaceText fact.Surface)
                let duplicates = facts |> List.groupBy (fun fact -> fact.Surface) |> List.choose (fun (surface, values) -> if values.Length > 1 then Some surface else None)
                let missing = surfaces |> List.filter (fun surface -> facts |> List.exists (fun (fact: IntakeFact) -> fact.Surface = surface) |> not)
                let invalid = facts |> List.choose (fun (fact: IntakeFact) -> match fact.Outcome with Observed v | Unauthorized v | Unknown v | Duplicate v | Cycle v | Partial v | Unsupported v | Unreadable v | Indeterminate v when not (validText v) -> Some(diag "INTAKE-OUTCOME-VALUE" (Some fact.Surface) "outcome evidence must be canonical non-empty text") | Stale(o, e) when not (validText o && validText e) -> Some(diag "INTAKE-STALE-EVIDENCE" (Some fact.Surface) "stale outcome requires observed and expected revisions") | _ -> None)
                let findings = duplicates |> List.map (fun surface -> diag "INTAKE-DUPLICATE-SURFACE" (Some surface) "surface occurred more than once") |> fun values -> values @ (missing |> List.map (fun surface -> diag "INTAKE-MISSING-SURFACE" (Some surface) "surface was not observed")) @ invalid
                if not (List.isEmpty findings) then Error findings else
                let snapshotDigest = digest ([ observation.Identity; observation.Revision ] @ (facts |> List.map factText))
                Ok { Identity = observation.Identity; Revision = observation.Revision; Facts = facts; Digest = snapshotDigest }

    let private refusal (fact: IntakeFact) =
        match fact.Outcome with
        | Observed _ -> None
        | Missing -> Some("INTAKE-ABSENT", "required state is absent") | Unauthorized _ -> Some("INTAKE-UNAUTHORIZED", "state is unauthorized") | Unsupported _ -> Some("INTAKE-UNSUPPORTED", "state is unsupported") | Partial _ -> Some("INTAKE-PARTIAL", "state is partial") | Stale _ -> Some("INTAKE-STALE", "state is stale") | Unknown _ -> Some("INTAKE-UNKNOWN-TYPE", "state type is unknown") | Duplicate _ -> Some("INTAKE-DUPLICATE-MEMBERSHIP", "state is duplicated") | Cycle _ -> Some("INTAKE-RELATION-CYCLE", "relation contains a cycle") | Redacted -> Some("INTAKE-REDACTED", "state is redacted") | Archived -> Some("INTAKE-ARCHIVED", "state is archived") | External -> Some("INTAKE-EXTERNAL", "state is external") | Draft -> Some("INTAKE-DRAFT", "state is a draft") | Unreadable _ -> Some("INTAKE-UNREADABLE", "state is unreadable") | Indeterminate _ -> Some("INTAKE-INDETERMINATE", "state is indeterminate")

    let private operationIdentity intent (before: IntakeSnapshot) = digest [ before.Digest; intentText intent ]
    let private sealPlan identity repository causation (before: IntakeSnapshot) (effects: IntakeEffect list) (intended: IntakeFact list) = digest ([ identity; repository; causation; before.Digest ] @ (effects |> List.collect (fun effect -> [ string effect.Ordinal; effect.OperationIdentity; String.concat "," effect.Dependencies; effect.ExpectedRevision; factText effect.Precondition; factText effect.Postcondition; factText effect.Compensation ])) @ (intended |> List.map factText))

    let private validIntent (intent: CanonicalIntakeIntent) =
        if obj.ReferenceEquals(intent, null) || obj.ReferenceEquals(intent.Initializations, null) || intent.Initializations |> List.exists (fun value -> obj.ReferenceEquals(value, null)) then false else
        let normalized = intent.Initializations |> List.map normalizeIntent |> List.sortBy (intentSurface >> surfaceText)
        validText intent.Identity && validText intent.Repository && validText intent.Causation && not (List.isEmpty normalized) && normalized = intent.Initializations && (normalized |> List.groupBy intentSurface |> List.forall (fun (_, values) -> values.Length = 1)) && (normalized |> List.forall (intentValue >> validText)) && intent.Digest = intentDigest intent.Identity intent.Repository intent.Causation normalized

    let plan (intent: CanonicalIntakeIntent) observation =
        if not (validIntent intent) then Error [ diag "INTAKE-INTENT-SEAL" None "canonical intake intent is invalid or altered" ] else
        match inspect observation with
        | Error findings -> Error findings
        | Ok before when before.Identity <> intent.Identity -> Error [ diag "INTAKE-IDENTITY-DRIFT" None "request and observation identities differ" ]
        | Ok before ->
            let repository = before.Facts |> List.find (fun fact -> fact.Surface = RepositoryScope)
            let refusals = before.Facts |> List.choose (fun fact -> refusal fact |> Option.map (fun (code, message) -> diag code (Some fact.Surface) message))
            let repositoryMismatch = match repository.Outcome with Observed value when value <> intent.Repository -> [ diag "INTAKE-REPOSITORY-DRIFT" (Some RepositoryScope) "request and observed repository differ" ] | _ -> []
            if not (List.isEmpty (refusals @ repositoryMismatch)) then Error(refusals @ repositoryMismatch) else
            let wanted = intent.Initializations |> List.map (fun value -> intentSurface value, intentValue value) |> Map.ofList
            let mutable previous : string option = None
            let effects : IntakeEffect list = before.Facts |> List.choose (fun (fact: IntakeFact) -> wanted |> Map.tryFind fact.Surface |> Option.bind (fun desired -> if fact.Outcome = Observed desired then None else let identity = operationIdentity (intent.Initializations |> List.find (fun value -> intentSurface value = fact.Surface)) before in let effect : IntakeEffect = { Ordinal = 0; OperationIdentity = identity; Dependencies = previous |> Option.toList; ExpectedRevision = before.Revision; Precondition = fact; Postcondition = { fact with Outcome = Observed desired }; Compensation = fact } in previous <- Some identity; Some effect)) |> List.mapi (fun index effect -> { effect with Ordinal = index + 1 })
            let intended : IntakeFact list = before.Facts |> List.map (fun (fact: IntakeFact) -> wanted |> Map.tryFind fact.Surface |> Option.map (fun value -> { fact with Outcome = Observed value }) |> Option.defaultValue fact)
            let planDigest = sealPlan intent.Identity intent.Repository intent.Causation before effects intended
            if List.isEmpty effects then Ok(IntakeNoOp { Identity = intent.Identity; Revision = before.Revision; Digest = planDigest }) else Ok(IntakePlanned { Identity = intent.Identity; Repository = intent.Repository; Causation = intent.Causation; Before = before; Effects = effects; IntendedPostState = intended; Digest = planDigest })

    let private validSeal (plan: IntakePlan) = plan.Digest = sealPlan plan.Identity plan.Repository plan.Causation plan.Before plan.Effects plan.IntendedPostState && plan.Effects |> List.mapi (fun index (effect: IntakeEffect) -> effect.Ordinal = index + 1 && effect.ExpectedRevision = plan.Before.Revision && effect.Dependencies = (if index = 0 then [] else [ plan.Effects[index - 1].OperationIdentity ])) |> List.forall id
    let private stateAfter (effects: IntakeEffect list) (facts: IntakeFact list) = facts |> List.map (fun fact -> effects |> List.tryFind (fun effect -> effect.Postcondition.Surface = fact.Surface) |> Option.map _.Postcondition |> Option.defaultValue fact)
    let private stateCompensated (effects: IntakeEffect list) (facts: IntakeFact list) = facts |> List.map (fun fact -> effects |> List.tryFind (fun effect -> effect.Compensation.Surface = fact.Surface) |> Option.map _.Compensation |> Option.defaultValue fact)

    let applyControlled (plan: IntakePlan) (reobserved: IntakeObservation) (mode: IntakeApplyMode) (scripted: ScriptedEffectResult list) : Result<IntakeApplyReceipt, IntakeApplyFailure> =
        if obj.ReferenceEquals(plan, null) || not (validSeal plan) then Error InvalidSealedPlan else
        match inspect reobserved with
        | Error findings -> Error(PreStateRefused findings)
        | Ok current ->
            let durable, compensate : DurableEffect list * bool = match mode with Execute -> [], false | Resume values | RollForward values -> values, false | Compensate values -> values, true
            if mode = Execute && current <> plan.Before then Error FullFenceChanged else
            let durableOrdinals = durable |> List.map _.Ordinal
            let durableEffects = plan.Effects |> List.filter (fun (effect: IntakeEffect) -> durableOrdinals |> List.contains effect.Ordinal)
            let durableValid = durableOrdinals = [ 1 .. durable.Length ] && durable |> List.forall (fun value -> value.PlanDigest = plan.Digest && plan.Effects[value.Ordinal - 1].OperationIdentity = value.OperationIdentity) && (match List.tryLast durable with None -> current = plan.Before | Some value -> current.Revision = value.ResultRevision && current.Digest = value.PostStateDigest && current.Facts = stateAfter durableEffects plan.Before.Facts)
            if not durableValid then Error(DurableResultMismatch(defaultArg (durable |> List.tryLast |> Option.map _.Ordinal) 0)) else
            let targets = if compensate then List.rev durableEffects else plan.Effects |> List.filter (fun effect -> durableOrdinals |> List.contains effect.Ordinal |> not)
            if targets.Length <> scripted.Length then Error ScriptLengthMismatch else
            let rec run (accepted: DurableEffect list) (state: IntakeSnapshot) (pairs: (IntakeEffect * ScriptedEffectResult) list) : Result<DurableEffect list * IntakeSnapshot, IntakeApplyFailure> =
                match pairs with
                | [] -> Ok(accepted, state)
                | (effect, _) :: _ when state.Facts |> List.tryFind (fun fact -> fact.Surface = effect.Precondition.Surface) <> Some(if compensate then effect.Postcondition else effect.Precondition) -> Error(EffectPreconditionChanged(effect.Ordinal, durable @ accepted))
                | (effect, result) :: _ when result.Ordinal <> effect.Ordinal || result.OperationIdentity <> effect.OperationIdentity -> Error(EffectIdentityMismatch(effect.Ordinal, durable @ accepted))
                | (effect, result) :: _ when not result.Accepted -> Error(EffectRejected(effect.Ordinal, defaultArg result.Reason "controlled effect rejected", durable @ accepted))
                | (effect, result) :: rest ->
                    match inspect result.After with
                    | Error findings -> Error(EffectPostStateRefused(effect.Ordinal, findings, durable @ accepted))
                    | Ok after ->
                        let completed = targets |> List.take (accepted.Length + 1)
                        let expected = if compensate then stateCompensated completed plan.IntendedPostState else stateAfter (durableEffects @ completed) plan.Before.Facts
                        if after.Identity <> plan.Identity || after.Facts <> expected || after.Revision = state.Revision then Error(EffectPostStateMismatch(effect.Ordinal, durable @ accepted)) else let receipt = { PlanDigest = plan.Digest; Ordinal = effect.Ordinal; OperationIdentity = effect.OperationIdentity; ResultRevision = after.Revision; PostStateDigest = after.Digest } in run (accepted @ [ receipt ]) after rest
            match run [] current (List.zip targets scripted) with
            | Error failure -> Error failure
            | Ok(accepted, finalState) -> if not compensate && durable.Length + accepted.Length = plan.Effects.Length && finalState.Facts <> plan.IntendedPostState then Error(FinalPostStateMismatch(durable @ accepted)) else Ok { PlanDigest = plan.Digest; FinalRevision = finalState.Revision; AcceptedEffects = (if compensate then durable else durable @ accepted); CompensatedOrdinals = (if compensate then targets |> List.map _.Ordinal else []) }

    let private detailText = function
        | DiscoveryDetail.Known value -> "known:" + canonicalText value
        | DiscoveryDetail.ExplicitlyUnknown reason -> "unknown:" + canonicalText reason
        | DiscoveryDetail.Deferred reason -> "deferred:" + canonicalText reason

    let private validDetail = function
        | DiscoveryDetail.Known value
        | DiscoveryDetail.ExplicitlyUnknown value
        | DiscoveryDetail.Deferred value -> validText value

    let private canonicalTouchSet = function
        | None -> Ok None
        | Some values when obj.ReferenceEquals(values, null) -> Error "touch set must be declared values or explicitly unspecified"
        | Some values ->
            let normalized = canonicalValues values
            if List.isEmpty normalized || normalized |> List.exists (validText >> not) then
                Error "declared touch set must contain canonical non-empty paths"
            else
                Ok(Some normalized)

    let validateCapture (request: StagedCaptureRequest) =
        if obj.ReferenceEquals(request, null) then
            Error [ diag "INTAKE-CAPTURE-REQUEST-NULL" None "staged capture request is required" ]
        else
            let touchSet = canonicalTouchSet request.TouchSet
            let findings =
                [ if not (validDetail request.RootCause) then
                      yield diag "INTAKE-CAPTURE-ROOT-CAUSE" None "root-cause evidence must carry canonical non-empty text"
                  match request.RootCause with
                  | DiscoveryDetail.Deferred _ ->
                      yield diag "INTAKE-CAPTURE-ROOT-CAUSE-KIND" None "root cause must be known or explicitly unknown"
                  | _ -> ()
                  if not (validDetail request.Verification) then
                      yield diag "INTAKE-CAPTURE-VERIFICATION" None "verification evidence must carry canonical non-empty text"
                  match request.Verification with
                  | DiscoveryDetail.ExplicitlyUnknown _ ->
                      yield diag "INTAKE-CAPTURE-VERIFICATION-KIND" None "verification must be known or explicitly deferred"
                  | _ -> ()
                  match touchSet with
                  | Error message -> yield diag "INTAKE-CAPTURE-TOUCH-SET" (Some TouchSet) message
                  | Ok _ -> () ]
            if not (List.isEmpty findings) then Error findings else
            let canonicalTouchSet = touchSet |> Result.defaultValue None
            let contract =
                String.concat ";"
                    [ "schema=" + stagedContractSchema
                      "identity=create-or-reuse"
                      "root-cause=" + detailText request.RootCause
                      "verification=" + detailText request.Verification
                      "touch-set=" + (canonicalTouchSet |> Option.map (String.concat ",") |> Option.defaultValue "unspecified") ]
            let initializations =
                [ InitializeJournal(canonicalText request.Causation)
                  InitializeSchedulingIntent "Backlog"
                  InitializeContract contract
                  InitializeProjections [ "status"; "type" ] ]
                @ (canonicalTouchSet |> Option.map (fun values -> [ InitializeTouchSet values ]) |> Option.defaultValue [])
            validate
                { Identity = request.Identity
                  Repository = request.Repository
                  Causation = request.Causation
                  Initializations = initializations }

    let private captureReadText = function
        | CaptureAuthorityRead.IssueIdentity -> "issue-identity"
        | CaptureAuthorityRead.NativeTypeAndFields -> "native-type-and-fields"
        | CaptureAuthorityRead.ProjectMembership -> "project-membership"
        | CaptureAuthorityRead.Relations -> "relations"
        | CaptureAuthorityRead.RepositoryScope -> "repository-scope"
        | CaptureAuthorityRead.ProtocolState -> "protocol-state"

    let planCapture request (observation: StagedCaptureObservation) =
        if obj.ReferenceEquals(observation, null) then
            Error [ diag "INTAKE-CAPTURE-OBSERVATION-NULL" None "staged capture observation is required" ]
        elif obj.ReferenceEquals(observation.AuthorityReads, null) then
            Error [ diag "INTAKE-CAPTURE-READS" None "the complete item-local authority-read inventory is required" ]
        else
            let reads = observation.AuthorityReads |> List.sortBy captureReadText
            let duplicates = reads |> List.groupBy id |> List.choose (fun (read, values) -> if values.Length > 1 then Some read else None)
            let missing = requiredCaptureReads |> List.filter (fun required -> reads |> List.contains required |> not)
            let readFindings =
                [ for read in duplicates do
                      yield diag "INTAKE-CAPTURE-READ-DUPLICATE" None $"authority read occurred more than once: %s{captureReadText read}"
                  for read in missing do
                      yield diag "INTAKE-CAPTURE-READ-MISSING" None $"required item-local authority read is missing: %s{captureReadText read}"
                  if reads.Length > 6 then
                      yield diag "INTAKE-CAPTURE-READ-BUDGET" None $"capture declared %d{reads.Length} authority reads; maximum is 6"
                  if observation.UnrelatedProjectItems < 0 || observation.UnrelatedBacklogItems < 0 then
                      yield diag "INTAKE-CAPTURE-CARDINALITY" None "unrelated Project and Backlog cardinalities cannot be negative" ]
            if not (List.isEmpty readFindings) then Error readFindings else
            match validateCapture request with
            | Error findings -> Error findings
            | Ok intent ->
                match plan intent observation.Intake with
                | Error findings -> Error findings
                | Ok decision ->
                    let mutations = match decision with IntakePlanned value -> value.Effects.Length | IntakeNoOp _ -> 0
                    if mutations > 6 then
                        Error [ diag "INTAKE-CAPTURE-MUTATION-BUDGET" None $"capture planned %d{mutations} mutations; maximum is 6" ]
                    else
                        Ok
                            { ContractSchema = stagedContractSchema
                              Intent = intent
                              Decision = decision
                              Budget = { AuthorityReads = reads.Length; Mutations = mutations } }

    let private promotionSurfaceText = function
        | ReadyPromotionSurface.RootCause -> "root-cause"
        | ReadyPromotionSurface.TouchSet -> "touch-set"
        | ReadyPromotionSurface.VerificationContract -> "verification-contract"
        | ReadyPromotionSurface.Dependencies -> "dependencies"
        | ReadyPromotionSurface.RouteDecision -> "route-decision"
        | ReadyPromotionSurface.NativeIssueType -> "native-issue-type"
        | ReadyPromotionSurface.OrganizationFields -> "organization-fields"
        | ReadyPromotionSurface.RepositoryScope -> "repository-scope"
        | ReadyPromotionSurface.WorkClassification -> "work-classification"

    let private promotionCode surface = (promotionSurfaceText surface).ToUpperInvariant().Replace('-', '_')

    let prepareReadyPromotion identity repository causation (facts: ReadyPromotionFact list) =
        if obj.ReferenceEquals(facts, null) || facts |> List.exists (fun fact -> obj.ReferenceEquals(fact, null)) then
            Error [ diag "INTAKE-PROMOTION-FACTS" None "typed Ready-promotion facts are required" ]
        else
            let canonical = facts |> List.map (fun fact -> { fact with Value = canonicalText fact.Value }) |> List.sortBy (fun fact -> promotionSurfaceText fact.Surface)
            let duplicates = canonical |> List.groupBy _.Surface |> List.choose (fun (surface, values) -> if values.Length > 1 then Some surface else None)
            let missing = requiredReadyPromotionSurfaces |> List.filter (fun required -> canonical |> List.exists (fun fact -> fact.Surface = required) |> not)
            let findings =
                [ for surface in duplicates do
                      yield diag ($"INTAKE-PROMOTION-DUPLICATE-%s{promotionCode surface}") None $"Ready-promotion fact occurred more than once: %s{promotionSurfaceText surface}"
                  for surface in missing do
                      yield diag ($"INTAKE-PROMOTION-MISSING-%s{promotionCode surface}") None $"Ready-promotion fact is missing: %s{promotionSurfaceText surface}"
                  for fact in canonical do
                      if not (validText fact.Value) then
                          yield diag ($"INTAKE-PROMOTION-VALUE-%s{promotionCode fact.Surface}") None $"Ready-promotion fact must be canonical non-empty text: %s{promotionSurfaceText fact.Surface}" ]
            if not (List.isEmpty findings) then Error findings else
            let values = canonical |> List.map (fun fact -> fact.Surface, fact.Value) |> Map.ofList
            let touchSet =
                values[ReadyPromotionSurface.TouchSet].Split(',', StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
                |> canonicalValues
            if List.isEmpty touchSet || touchSet |> List.exists (validText >> not) then
                Error [ diag "INTAKE-PROMOTION-VALUE-TOUCH_SET" (Some TouchSet) "Ready-promotion touch set must contain canonical comma-separated paths" ]
            else
                let contract =
                    canonical
                    |> List.map (fun fact -> promotionSurfaceText fact.Surface + "=" + fact.Value)
                    |> fun values -> String.concat ";" ("schema=" + stagedContractSchema :: values)
                validate
                    { Identity = identity
                      Repository = repository
                      Causation = causation
                      Initializations = [ InitializeSchedulingIntent "Ready"; InitializeContract contract; InitializeTouchSet touchSet ] }
