namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type IntakeSurface = Issue | IssueFields | ProjectMembership | Hierarchy | Dependencies | ProtocolState
type IntakeOutcome = Observed of string | Missing | Redacted | Unauthorized of string | Archived | External | Draft | Unsupported of string | Unreadable of string | Indeterminate of string
type IntakeFact = { Surface: IntakeSurface; Outcome: IntakeOutcome }
type IntakePage = { Number: int; Facts: IntakeFact list; TerminalPage: bool }
type IntakeObservation = { Identity: string; Revision: string; Pages: IntakePage list }
type IntakeSnapshot = { Identity: string; Revision: string; Facts: IntakeFact list; Digest: string }
type IntakeDiagnostic = { Code: string; Surface: IntakeSurface option; Message: string }
type ProtocolInitializationIntent = InitializeProtocolIssue of string | InitializeProjectMembership of string | InitializeHierarchy of string | InitializeDependencies of string | InitializeRequiredIssueFields of string
type IntakeEffect = { Ordinal: int; Surface: IntakeSurface; Before: string; After: string; Compensation: string }
type IntakePlan = { Identity: string; Causation: string; Before: IntakeSnapshot; Effects: IntakeEffect list; IntendedPostState: IntakeFact list; Digest: string }
type IntakeNoOp = { Identity: string; Revision: string; Digest: string }
type IntakePlanDecision = IntakePlanned of IntakePlan | IntakeNoOp of IntakeNoOp
type IntakeApplyFailure = InvalidSealedPlan | PreStateRefused of IntakeDiagnostic list | FullFenceChanged | ScriptLengthMismatch | EffectRejected of ordinal: int * reason: string | EffectPostStateRefused of ordinal: int * IntakeDiagnostic list | EffectPostStateMismatch of ordinal: int | DurableResultMismatch of ordinal: int | FinalPostStateMismatch
type DurableEffect = { PlanDigest: string; Ordinal: int; ResultRevision: string; PostStateDigest: string }
type ScriptedEffectResult = { Ordinal: int; Accepted: bool; Reason: string option; After: IntakeObservation }
type IntakeApplyReceipt = { PlanDigest: string; FinalRevision: string; AcceptedEffects: DurableEffect list; CompensatedOrdinals: int list }
type IntakeApplyMode = Execute | Resume of DurableEffect list | RollForward of DurableEffect list | Compensate of DurableEffect list

[<RequireQualifiedAccess>]
module IntakeAdapter =
    let private surfaces = [ Issue; IssueFields; ProjectMembership; Hierarchy; Dependencies; ProtocolState ]
    let private surfaceText = function Issue -> "issue" | IssueFields -> "issue-fields" | ProjectMembership -> "project-membership" | Hierarchy -> "hierarchy" | Dependencies -> "dependencies" | ProtocolState -> "protocol-state"
    let private outcomeText = function Observed value -> "observed:" + value | Missing -> "missing" | Redacted -> "redacted" | Unauthorized value -> "unauthorized:" + value | Archived -> "archived" | External -> "external" | Draft -> "draft" | Unsupported value -> "unsupported:" + value | Unreadable value -> "unreadable:" + value | Indeterminate value -> "indeterminate:" + value
    let private outcomeFromText value =
        if value = "missing" then Missing
        elif value = "redacted" then Redacted
        elif value = "archived" then Archived
        elif value = "external" then External
        elif value = "draft" then Draft
        elif value.StartsWith("observed:", StringComparison.Ordinal) then Observed(value.Substring(9))
        elif value.StartsWith("unauthorized:", StringComparison.Ordinal) then Unauthorized(value.Substring(13))
        elif value.StartsWith("unsupported:", StringComparison.Ordinal) then Unsupported(value.Substring(12))
        elif value.StartsWith("unreadable:", StringComparison.Ordinal) then Unreadable(value.Substring(11))
        else Indeterminate(value.Substring(Math.Min(value.Length, 14)))
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private digest values = values |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private factText (fact: IntakeFact) = surfaceText fact.Surface + "=" + outcomeText fact.Outcome
    let private snapshotDigest identity revision (facts: IntakeFact list) = digest ([ identity; revision ] @ (facts |> List.map factText))
    let private diag code surface message : IntakeDiagnostic = { Code = code; Surface = surface; Message = message }

    let observe (observation: IntakeObservation) : Result<IntakeSnapshot, IntakeDiagnostic list> =
        if obj.ReferenceEquals(observation, null) then Error [ diag "INTAKE-OBSERVATION-NULL" None "observation is required" ]
        elif not (validText observation.Identity) then Error [ diag "INTAKE-IDENTITY" None "identity must be canonical non-empty text" ]
        elif not (validText observation.Revision) then Error [ diag "INTAKE-REVISION" None "revision must be canonical non-empty text" ]
        elif obj.ReferenceEquals(observation.Pages, null) || List.isEmpty observation.Pages then Error [ diag "INTAKE-PAGES" None "a complete page chain is required" ]
        else
            let chainValid = observation.Pages |> List.mapi (fun index (page: IntakePage) -> not (obj.ReferenceEquals(page, null)) && not (obj.ReferenceEquals(page.Facts, null)) && page.Number = index + 1 && page.TerminalPage = (index = observation.Pages.Length - 1)) |> List.forall id
            if not chainValid then Error [ diag "INTAKE-PAGE-CHAIN" None "page numbering and terminal evidence must be complete" ]
            else
                let facts : IntakeFact list = observation.Pages |> List.collect (fun page -> page.Facts) |> List.sortBy (fun fact -> surfaceText fact.Surface)
                let duplicates = facts |> List.groupBy (fun fact -> fact.Surface) |> List.choose (fun (surface, values) -> if values.Length > 1 then Some surface else None)
                let missing = surfaces |> List.filter (fun surface -> facts |> List.exists (fun (fact: IntakeFact) -> fact.Surface = surface) |> not)
                let invalidOutcomes = facts |> List.choose (fun (fact: IntakeFact) -> match fact.Outcome with Observed value when not (validText value) -> Some(diag "INTAKE-OBSERVED-VALUE" (Some fact.Surface) "observed value must be canonical non-empty text") | Unauthorized value | Unsupported value | Unreadable value | Indeterminate value when not (validText value) -> Some(diag "INTAKE-OUTCOME-REASON" (Some fact.Surface) "outcome reason must be canonical non-empty text") | _ -> None)
                let findings = (duplicates |> List.map (fun surface -> diag "INTAKE-DUPLICATE-SURFACE" (Some surface) "surface occurred more than once")) @ (missing |> List.map (fun surface -> diag "INTAKE-MISSING-SURFACE" (Some surface) "surface was not exhaustively observed")) @ invalidOutcomes
                if not (List.isEmpty findings) then Error findings else Ok { Identity = observation.Identity; Revision = observation.Revision; Facts = facts; Digest = snapshotDigest observation.Identity observation.Revision facts }

    let private intentParts = function
        | InitializeProtocolIssue value -> Issue, value
        | InitializeProjectMembership value -> ProjectMembership, value
        | InitializeHierarchy value -> Hierarchy, value
        | InitializeDependencies value -> Dependencies, value
        | InitializeRequiredIssueFields value -> IssueFields, value

    let private sealPlan identity causation (before: IntakeSnapshot) (effects: IntakeEffect list) (intended: IntakeFact list) =
        digest ([ identity; causation; before.Digest ] @ (effects |> List.collect (fun (effect: IntakeEffect) -> [ string effect.Ordinal; surfaceText effect.Surface; effect.Before; effect.After; effect.Compensation ])) @ (intended |> List.map factText))

    let plan causation (intents: ProtocolInitializationIntent list) (observation: IntakeObservation) : Result<IntakePlanDecision, IntakeDiagnostic list> =
        match observe observation with
        | Error findings -> Error findings
        | Ok before when not (validText causation) -> Error [ diag "INTAKE-CAUSATION" None "causation must be canonical non-empty text" ]
        | Ok _ when obj.ReferenceEquals(intents, null) -> Error [ diag "INTAKE-INTENTS" None "intent list is required" ]
        | Ok before ->
            let parsed = intents |> List.map intentParts
            let invalid = parsed |> List.choose (fun (surface, value) -> if validText value then None else Some(diag "INTAKE-INTENT-VALUE" (Some surface) "intent value must be canonical non-empty text"))
            let duplicates = parsed |> List.groupBy fst |> List.choose (fun (surface, values) -> if values.Length > 1 then Some(diag "INTAKE-DUPLICATE-INTENT" (Some surface) "surface has more than one intent") else None)
            let findings = invalid @ duplicates
            if not (List.isEmpty findings) then Error findings else
            let wanted = parsed |> Map.ofList
            let effects : IntakeEffect list =
                before.Facts
                |> List.choose (fun (fact: IntakeFact) -> wanted |> Map.tryFind fact.Surface |> Option.bind (fun desired -> let current = outcomeText fact.Outcome in if current = "observed:" + desired then None else Some(fact.Surface, current, desired)))
                |> List.sortBy (fun (surface, _, _) -> surfaces |> List.findIndex ((=) surface))
                |> List.mapi (fun index (surface, current, desired) -> ({ Ordinal = index + 1; Surface = surface; Before = current; After = desired; Compensation = current }: IntakeEffect))
            let intended : IntakeFact list = before.Facts |> List.map (fun (fact: IntakeFact) -> match wanted |> Map.tryFind fact.Surface with Some desired -> { fact with Outcome = Observed desired } | None -> fact)
            let planDigest = sealPlan before.Identity causation before effects intended
            if List.isEmpty effects then Ok(IntakeNoOp { Identity = before.Identity; Revision = before.Revision; Digest = planDigest })
            else Ok(IntakePlanned { Identity = before.Identity; Causation = causation; Before = before; Effects = effects; IntendedPostState = intended; Digest = planDigest })

    let private validSeal (plan: IntakePlan) = plan.Digest = sealPlan plan.Identity plan.Causation plan.Before plan.Effects plan.IntendedPostState && plan.Identity = plan.Before.Identity && (plan.Effects |> List.mapi (fun i (effect: IntakeEffect) -> effect.Ordinal = i + 1) |> List.forall id)
    let private intendedAfter (accepted: IntakeEffect list) (plan: IntakePlan) =
        plan.Before.Facts |> List.map (fun (fact: IntakeFact) -> match accepted |> List.tryFind (fun effect -> effect.Surface = fact.Surface) with Some effect -> { fact with Outcome = Observed effect.After } | None -> fact)
    let private compensationAfter (remaining: IntakeEffect list) (plan: IntakePlan) =
        plan.IntendedPostState |> List.map (fun (fact: IntakeFact) -> match remaining |> List.tryFind (fun effect -> effect.Surface = fact.Surface) with Some effect -> { fact with Outcome = outcomeFromText effect.Before } | None -> fact)

    let applyControlled (plan: IntakePlan) (reobserved: IntakeObservation) (mode: IntakeApplyMode) (scripted: ScriptedEffectResult list) : Result<IntakeApplyReceipt, IntakeApplyFailure> =
        if obj.ReferenceEquals(plan, null) || not (validSeal plan) then Error InvalidSealedPlan else
        match observe reobserved with
        | Error findings -> Error(PreStateRefused findings)
        | Ok current when current <> plan.Before && (match mode with Execute -> true | _ -> false) -> Error FullFenceChanged
        | Ok current ->
            let durable, compensate : DurableEffect list * bool = match mode with Execute -> [], false | Resume values | RollForward values -> values, false | Compensate values -> values, true
            let durableOrdinals = durable |> List.map _.Ordinal
            let expectedOrdinals = [ 1 .. durable.Length ]
            let durableFacts = plan.Effects |> List.filter (fun effect -> durableOrdinals |> List.contains effect.Ordinal) |> fun effects -> intendedAfter effects plan
            let durableValid =
                durableOrdinals = expectedOrdinals
                && durable |> List.forall (fun (value: DurableEffect) -> value.PlanDigest = plan.Digest)
                && (match List.tryLast durable with
                    | None -> current = plan.Before
                    | Some value -> current.Revision = value.ResultRevision && current.Digest = value.PostStateDigest && current.Facts = durableFacts)
            if not durableValid then Error(DurableResultMismatch(defaultArg (durable |> List.tryLast |> Option.map _.Ordinal) 0)) else
            let targets =
                if compensate then plan.Effects |> List.filter (fun (effect: IntakeEffect) -> durable |> List.exists (fun value -> value.Ordinal = effect.Ordinal)) |> List.rev
                else plan.Effects |> List.filter (fun (effect: IntakeEffect) -> durable |> List.exists (fun value -> value.Ordinal = effect.Ordinal) |> not)
            if targets.Length <> scripted.Length then Error ScriptLengthMismatch else
            let rec run (accepted: DurableEffect list) (state: IntakeSnapshot) (pairs: (IntakeEffect * ScriptedEffectResult) list) : Result<DurableEffect list * IntakeSnapshot, IntakeApplyFailure> =
                match pairs with
                | [] -> Ok(accepted, state)
                | (effect, result) :: rest when result.Ordinal <> effect.Ordinal -> Error(DurableResultMismatch effect.Ordinal)
                | (effect, result) :: _ when not result.Accepted -> Error(EffectRejected(effect.Ordinal, defaultArg result.Reason "controlled effect rejected"))
                | (effect, result) :: rest ->
                    match observe result.After with
                    | Error findings -> Error(EffectPostStateRefused(effect.Ordinal, findings))
                    | Ok after ->
                        let completed = if compensate then targets |> List.take (accepted.Length + 1) else (plan.Effects |> List.filter (fun (item: IntakeEffect) -> durable |> List.exists (fun value -> value.Ordinal = item.Ordinal))) @ (targets |> List.take (accepted.Length + 1))
                        let expectedFacts = if compensate then compensationAfter completed plan else intendedAfter completed plan
                        if after.Identity <> plan.Identity || after.Facts <> expectedFacts || after.Revision = state.Revision then Error(EffectPostStateMismatch effect.Ordinal)
                        else let receipt : DurableEffect = { PlanDigest = plan.Digest; Ordinal = effect.Ordinal; ResultRevision = after.Revision; PostStateDigest = after.Digest } in run (accepted @ [ receipt ]) after rest
            match run [] current (List.zip targets scripted) with
            | Error failure -> Error failure
            | Ok(accepted, finalState) ->
                let expectedFinal = if compensate then compensationAfter targets plan else intendedAfter plan.Effects plan
                if (not compensate && targets.Length + durable.Length = plan.Effects.Length && finalState.Facts <> expectedFinal) then Error FinalPostStateMismatch
                else Ok { PlanDigest = plan.Digest; FinalRevision = finalState.Revision; AcceptedEffects = durable @ accepted; CompensatedOrdinals = if compensate then targets |> List.map (fun effect -> effect.Ordinal) else [] }
