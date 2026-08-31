namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type RelationKind = ParentChild | Blocks

type RelationEdge =
    { Kind: RelationKind
      Source: LiveId
      Target: LiveId }

type RelationPage =
    { Number: int
      Edges: RelationEdge list
      TerminalPage: bool }

type NativeRelationObservation =
    | RelationsComplete of revision: string * scope: RelationKind * pages: RelationPage list
    | RelationsIncomplete of reason: string * cursor: string option
    | RelationsUnsupported of reason: string
    | RelationsUnauthorized of reason: string
    | RelationsIndeterminate of reason: string

type RelationSnapshot =
    { Revision: string
      Scope: RelationKind
      PageCount: int
      NodeCount: int
      Edges: RelationEdge list }

type RelationReadFailure =
    | RelationObservationRefused of ObservationRefusal
    | InvalidRelationPageChain
    | RelationKindMismatch of expected: RelationKind * observed: RelationKind
    | InvalidRelationEdge of RelationEdge
    | DuplicateRelationEdge of RelationEdge

type RelationIntent = AddEdge of RelationEdge | RemoveEdge of RelationEdge
type RelationOperation = AddEdgeOperation of RelationEdge | RemoveEdgeOperation of RelationEdge

type RelationMutationPlan =
    { Before: RelationSnapshot
      CausationIdentity: string
      IdempotencyIdentity: string
      Operation: RelationOperation }

type RelationNoOpReceipt =
    { ObservedRevision: string
      IdempotencyIdentity: string
      Intent: RelationIntent }

type RelationPlanDecision = RelationPlanned of RelationMutationPlan | RelationNoOp of RelationNoOpReceipt

type RelationPlanRefusal =
    | InvalidRelationExpectedRevision
    | RelationStaleExpectedRevision of observed: string
    | InvalidRelationCausationIdentity
    | InvalidRelationIntent
    | RelationIntentOutsideScope of expected: RelationKind * observed: RelationKind

type RelationPreStateRefusal =
    | PreStateReadRefused of RelationReadFailure
    | ReReadRequired of plannedRevision: string * observedRevision: string
    | ConcurrentPreStateChange of planned: RelationEdge list * observed: RelationEdge list

type RelationPostStateRefusal =
    | PostStateReadRefused of RelationReadFailure
    | InvalidResultRevision
    | ResultRevisionDidNotAdvance of revision: string
    | ResultRevisionMismatch of expected: string * observed: string
    | PostStateMismatch of expected: RelationEdge list * observed: RelationEdge list

[<RequireQualifiedAccess>]
module NativeRelations =
    let private kindText = function ParentChild -> "parent-child" | Blocks -> "blocks"
    let private edgeKey edge = kindText edge.Kind, LiveId.value edge.Source, LiveId.value edge.Target
    let private sortEdges edges = List.sortBy edgeKey edges
    let private validEdge edge =
        not (obj.ReferenceEquals(edge, null)) && edge.Source <> edge.Target

    let read observation =
        if obj.ReferenceEquals(observation, null) then Error(RelationObservationRefused InvalidCompletenessEvidence)
        else
            match observation with
            | RelationsIncomplete(reason, cursor) -> Error(RelationObservationRefused(ObservationIncomplete(reason, cursor)))
            | RelationsUnsupported reason -> Error(RelationObservationRefused(ObservationUnsupported reason))
            | RelationsUnauthorized reason -> Error(RelationObservationRefused(ObservationUnauthorized reason))
            | RelationsIndeterminate reason -> Error(RelationObservationRefused(ObservationIndeterminate reason))
            | RelationsComplete(revision, _, _) when String.IsNullOrWhiteSpace revision -> Error(RelationObservationRefused MissingObservationRevision)
            | RelationsComplete(revision, _, _) when revision <> revision.Trim() -> Error(RelationObservationRefused InvalidCompletenessEvidence)
            | RelationsComplete(_, _, pages) when obj.ReferenceEquals(pages, null) || List.isEmpty pages -> Error InvalidRelationPageChain
            | RelationsComplete(revision, scope, pages) ->
                let validChain =
                    pages
                    |> List.mapi (fun index page ->
                        not (obj.ReferenceEquals(page, null))
                        && not (obj.ReferenceEquals(page.Edges, null))
                        && page.Number = index + 1
                        && page.TerminalPage = (index = pages.Length - 1))
                    |> List.forall id
                if not validChain then Error InvalidRelationPageChain
                else
                    let edges = pages |> List.collect _.Edges
                    match edges |> List.tryFind (validEdge >> not) with
                    | Some edge -> Error(InvalidRelationEdge edge)
                    | None ->
                        match edges |> List.tryFind (fun edge -> edge.Kind <> scope) with
                        | Some edge -> Error(RelationKindMismatch(scope, edge.Kind))
                        | None ->
                            match edges |> List.groupBy edgeKey |> List.tryPick (fun (_, values) -> if values.Length > 1 then Some values.Head else None) with
                            | Some edge -> Error(DuplicateRelationEdge edge)
                            | None ->
                                Ok { Revision = revision; Scope = scope; PageCount = pages.Length; NodeCount = edges.Length; Edges = sortEdges edges }

    let private canonicalPart (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private intentEdge = function AddEdge edge | RemoveEdge edge -> edge
    let private intentText intent =
        let edge = intentEdge intent
        let verb = match intent with AddEdge _ -> "add" | RemoveEdge _ -> "remove"
        [ verb; kindText edge.Kind; LiveId.value edge.Source; LiveId.value edge.Target ]
        |> List.map canonicalPart
        |> String.concat ""
    let private identity revision causation intent =
        [ revision; causation; intentText intent ]
        |> List.map canonicalPart
        |> String.concat ""
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let plan expectedRevision causationIdentity intent snapshot =
        if String.IsNullOrWhiteSpace expectedRevision || expectedRevision <> expectedRevision.Trim() then Error InvalidRelationExpectedRevision
        elif String.IsNullOrWhiteSpace causationIdentity || causationIdentity <> causationIdentity.Trim() then Error InvalidRelationCausationIdentity
        elif obj.ReferenceEquals(intent, null) || obj.ReferenceEquals(snapshot, null) then Error InvalidRelationIntent
        else
            let edge = intentEdge intent
            if not (validEdge edge) then Error InvalidRelationIntent
            elif edge.Kind <> snapshot.Scope then Error(RelationIntentOutsideScope(snapshot.Scope, edge.Kind))
            elif expectedRevision <> snapshot.Revision then Error(RelationStaleExpectedRevision snapshot.Revision)
            else
                let key = identity snapshot.Revision causationIdentity intent
                let exists = List.contains edge snapshot.Edges
                match intent, exists with
                | AddEdge _, true
                | RemoveEdge _, false -> Ok(RelationNoOp { ObservedRevision = snapshot.Revision; IdempotencyIdentity = key; Intent = intent })
                | AddEdge edge, false -> Ok(RelationPlanned { Before = snapshot; CausationIdentity = causationIdentity; IdempotencyIdentity = key; Operation = AddEdgeOperation edge })
                | RemoveEdge edge, true -> Ok(RelationPlanned { Before = snapshot; CausationIdentity = causationIdentity; IdempotencyIdentity = key; Operation = RemoveEdgeOperation edge })

    let checkPreState mutationPlan observation =
        match read observation with
        | Error failure -> Error(PreStateReadRefused failure)
        | Ok observed when observed.Revision <> mutationPlan.Before.Revision -> Error(ReReadRequired(mutationPlan.Before.Revision, observed.Revision))
        | Ok observed when observed.Scope <> mutationPlan.Before.Scope || observed.Edges <> mutationPlan.Before.Edges -> Error(ConcurrentPreStateChange(mutationPlan.Before.Edges, observed.Edges))
        | Ok observed -> Ok observed

    let verifyPostState expectedResultRevision mutationPlan observation =
        if String.IsNullOrWhiteSpace expectedResultRevision || expectedResultRevision <> expectedResultRevision.Trim() then Error InvalidResultRevision
        elif expectedResultRevision = mutationPlan.Before.Revision then Error(ResultRevisionDidNotAdvance expectedResultRevision)
        else
            match read observation with
            | Error failure -> Error(PostStateReadRefused failure)
            | Ok observed when observed.Revision <> expectedResultRevision -> Error(ResultRevisionMismatch(expectedResultRevision, observed.Revision))
            | Ok observed ->
                let expected =
                    match mutationPlan.Operation with
                    | AddEdgeOperation edge -> edge :: mutationPlan.Before.Edges |> Set.ofList |> Set.toList |> sortEdges
                    | RemoveEdgeOperation edge -> mutationPlan.Before.Edges |> List.filter ((<>) edge) |> sortEdges
                if observed.Scope <> mutationPlan.Before.Scope || observed.Edges <> expected then Error(PostStateMismatch(expected, observed.Edges))
                else Ok observed
