namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type RepositoryCoordinates = { Owner: string; Name: string }

type ProjectContent =
    | RepositoryIssue of repository: RepositoryCoordinates * number: int * contentId: LiveId
    | PullRequest of repository: RepositoryCoordinates * number: int * contentId: LiveId
    | DraftIssue of contentId: LiveId
    | RedactedContent of contentId: LiveId
    | UnknownContent of kind: string * contentId: LiveId

type ProjectItem = { ProjectId: LiveId; ItemId: LiveId; Content: ProjectContent; Archived: bool }
type ProjectItemPage = { Number: int; Items: ProjectItem list; TerminalPage: bool }

type ProjectObservation =
    | ProjectComplete of revision: string * pages: ProjectItemPage list
    | ProjectIncomplete of reason: string * cursor: string option
    | ProjectUnsupported of reason: string
    | ProjectUnauthorized of reason: string
    | ProjectUnreadable of reason: string
    | ProjectIndeterminate of reason: string

type ProjectSnapshot = { Revision: string; PageCount: int; NodeCount: int; Items: ProjectItem list }

type ProjectReadFailure =
    | ProjectObservationRefused of ObservationRefusal
    | ProjectObservationUnreadable of string
    | InvalidProjectPageChain
    | InvalidProjectItem of ProjectItem
    | DuplicateProjectItemId of LiveId
    | DuplicateProjectContent of LiveId

type ProjectMembership =
    | ActiveMembership of ProjectItem
    | ArchivedMembership of ProjectItem
    | ExternalRepositoryMembership of ProjectItem
    | DraftMembership of ProjectItem
    | RedactedMembership of ProjectItem
    | UnknownMembership of ProjectItem
    | MissingMembership

type MembershipResolutionFailure = InvalidExpectedRepository | InvalidTargetContentIdentity
type ProjectionNature = ProjectionOnly
type StatusOptionProjection = { Id: LiveId; Name: SemanticName }
type StatusFieldProjection = { ProjectId: LiveId; ItemId: LiveId; FieldId: LiveId; FieldName: SemanticName; Options: StatusOptionProjection list; SelectedOptionId: LiveId option }

type StatusObservation =
    | StatusComplete of revision: string * evidence: PageEvidence * fields: StatusFieldProjection list
    | StatusIncomplete of reason: string * cursor: string option
    | StatusUnsupported of reason: string
    | StatusUnauthorized of reason: string
    | StatusUnreadable of reason: string
    | StatusIndeterminate of reason: string

type StatusSnapshot = { Revision: string; Nature: ProjectionNature; ProjectId: LiveId; ItemId: LiveId; FieldId: LiveId; FieldName: SemanticName; Options: StatusOptionProjection list; SelectedOptionId: LiveId option }

type StatusReadFailure =
    | StatusObservationRefused of ObservationRefusal
    | StatusObservationUnreadable of string
    | InvalidStatusCompletenessEvidence
    | StatusFieldMissing
    | StatusFieldDuplicated
    | InvalidStatusField
    | DuplicateStatusOptionId of LiveId
    | DuplicateStatusOptionName of SemanticName
    | UnknownSelectedStatusOption of LiveId

type MembershipIntent = EnsureMember of projectId: LiveId * contentId: LiveId | EnsureNotMember of itemId: LiveId * contentId: LiveId
type MembershipOperation = AddMembershipOperation of projectId: LiveId * contentId: LiveId | RemoveMembershipOperation of projectId: LiveId * itemId: LiveId * contentId: LiveId
type MembershipPlan = { Before: ProjectSnapshot; Repository: RepositoryCoordinates; CausationIdentity: string; IdempotencyIdentity: string; Operation: MembershipOperation }
type MembershipNoOpReceipt = { ObservedRevision: string; IdempotencyIdentity: string; Intent: MembershipIntent }
type MembershipPlanDecision = MembershipPlanned of MembershipPlan | MembershipNoOp of MembershipNoOpReceipt

type MembershipPlanRefusal =
    | InvalidMembershipExpectedRevision
    | MembershipStaleExpectedRevision of observed: string
    | InvalidMembershipCausationIdentity
    | InvalidMembershipIntent
    | MembershipMutationIneligible of ProjectMembership

type MembershipPreStateRefusal = MembershipPreStateReadRefused of ProjectReadFailure | MembershipReReadRequired of plannedRevision: string * observedRevision: string | ConcurrentMembershipChange
type MembershipPostStateRefusal = MembershipPostStateReadRefused of ProjectReadFailure | InvalidMembershipResultRevision | MembershipResultRevisionDidNotAdvance of string | MembershipResultRevisionMismatch of expected: string * observed: string | InvalidResultingProjectItem | MembershipPostStateMismatch
type StatusIntent = SetStatus of optionId: LiveId | ClearStatus
type StatusOperation = SetStatusOperation of optionId: LiveId | ClearStatusOperation
type StatusPlan = { Before: StatusSnapshot; CausationIdentity: string; IdempotencyIdentity: string; Operation: StatusOperation }
type StatusNoOpReceipt = { ObservedRevision: string; IdempotencyIdentity: string; Intent: StatusIntent }
type StatusPlanDecision = StatusPlanned of StatusPlan | StatusNoOp of StatusNoOpReceipt
type StatusPlanRefusal = InvalidStatusExpectedRevision | StatusStaleExpectedRevision of observed: string | InvalidStatusCausationIdentity | InvalidStatusIntent | RequestedStatusOptionMissing of LiveId
type StatusPreStateRefusal = StatusPreStateReadRefused of StatusReadFailure | StatusReReadRequired of plannedRevision: string * observedRevision: string | ConcurrentStatusChange
type StatusPostStateRefusal = StatusPostStateReadRefused of StatusReadFailure | InvalidStatusResultRevision | StatusResultRevisionDidNotAdvance of string | StatusResultRevisionMismatch of expected: string * observed: string | StatusPostStateMismatch

[<RequireQualifiedAccess>]
module ProjectAdapter =
    let private validText value = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private validRepo repo = not (obj.ReferenceEquals(repo, null)) && validText repo.Owner && validText repo.Name
    let private contentId = function RepositoryIssue(_, _, id) | PullRequest(_, _, id) | DraftIssue id | RedactedContent id | UnknownContent(_, id) -> Some id
    let private contentKey = function
        | RepositoryIssue(repo, number, id) -> "issue", repo.Owner, repo.Name, number, LiveId.value id
        | PullRequest(repo, number, id) -> "pull-request", repo.Owner, repo.Name, number, LiveId.value id
        | DraftIssue id -> "draft", "", "", 0, LiveId.value id
        | RedactedContent id -> "redacted", "", "", 0, LiveId.value id
        | UnknownContent(kind, id) -> "unknown", kind, "", 0, LiveId.value id
    let private itemKey (item: ProjectItem) = LiveId.value item.ProjectId, LiveId.value item.ItemId, contentKey item.Content, item.Archived
    let private validItem (item: ProjectItem) =
        if obj.ReferenceEquals(item, null) || obj.ReferenceEquals(item.Content, null) then false else
        match item.Content with
        | RepositoryIssue(repo, number, _) | PullRequest(repo, number, _) -> validRepo repo && number > 0
        | DraftIssue _ | RedactedContent _ -> true
        | UnknownContent(kind, _) -> validText kind
    let private duplicateBy key values = values |> List.groupBy key |> List.tryPick (fun (_, xs) -> if xs.Length > 1 then Some xs.Head else None)

    let readProject (observation: ProjectObservation) =
        if obj.ReferenceEquals(observation, null) then Error(ProjectObservationRefused InvalidCompletenessEvidence) else
        match observation with
        | ProjectIncomplete(reason, cursor) -> Error(ProjectObservationRefused(ObservationIncomplete(reason, cursor)))
        | ProjectUnsupported reason -> Error(ProjectObservationRefused(ObservationUnsupported reason))
        | ProjectUnauthorized reason -> Error(ProjectObservationRefused(ObservationUnauthorized reason))
        | ProjectUnreadable reason -> Error(ProjectObservationUnreadable reason)
        | ProjectIndeterminate reason -> Error(ProjectObservationRefused(ObservationIndeterminate reason))
        | ProjectComplete(revision, _) when not (validText revision) -> Error(ProjectObservationRefused MissingObservationRevision)
        | ProjectComplete(_, pages) when obj.ReferenceEquals(pages, null) || List.isEmpty pages -> Error InvalidProjectPageChain
        | ProjectComplete(revision, pages) ->
            let validChain = pages |> List.mapi (fun i p -> not (obj.ReferenceEquals(p, null)) && not (obj.ReferenceEquals(p.Items, null)) && p.Number = i + 1 && p.TerminalPage = (i = pages.Length - 1)) |> List.forall id
            if not validChain then Error InvalidProjectPageChain else
            let items = pages |> List.collect _.Items
            match items |> List.tryFind (validItem >> not) with
            | Some item -> Error(InvalidProjectItem item)
            | None ->
                match duplicateBy (fun (item: ProjectItem) -> item.ItemId) items with
                | Some item -> Error(DuplicateProjectItemId item.ItemId)
                | None ->
                    let addressable = items |> List.choose (fun (item: ProjectItem) -> contentId item.Content |> Option.map (fun id -> id, item))
                    match duplicateBy fst addressable with
                    | Some(id, _) -> Error(DuplicateProjectContent id)
                    | None -> Ok { Revision = revision; PageCount = pages.Length; NodeCount = items.Length; Items = List.sortBy itemKey items }

    let resolveMembership (expectedRepository: RepositoryCoordinates) (targetContentId: LiveId) (snapshot: ProjectSnapshot) =
        if not (validRepo expectedRepository) then Error InvalidExpectedRepository
        elif obj.ReferenceEquals(snapshot, null) then Error InvalidTargetContentIdentity
        else
            match snapshot.Items |> List.tryFind (fun item -> contentId item.Content = Some targetContentId) with
            | None -> Ok MissingMembership
            | Some item when item.Archived -> Ok(ArchivedMembership item)
            | Some ({ Content = RepositoryIssue(repo, _, _) | PullRequest(repo, _, _) } as item) when repo <> expectedRepository -> Ok(ExternalRepositoryMembership item)
            | Some ({ Content = DraftIssue _ } as item) -> Ok(DraftMembership item)
            | Some ({ Content = RedactedContent _ } as item) -> Ok(RedactedMembership item)
            | Some ({ Content = UnknownContent _ } as item) -> Ok(UnknownMembership item)
            | Some item -> Ok(ActiveMembership item)

    let readStatus (projectId: LiveId) (itemId: LiveId) (observation: StatusObservation) =
        if obj.ReferenceEquals(observation, null) then Error(StatusObservationRefused InvalidCompletenessEvidence) else
        match observation with
        | StatusIncomplete(reason, cursor) -> Error(StatusObservationRefused(ObservationIncomplete(reason, cursor)))
        | StatusUnsupported reason -> Error(StatusObservationRefused(ObservationUnsupported reason))
        | StatusUnauthorized reason -> Error(StatusObservationRefused(ObservationUnauthorized reason))
        | StatusUnreadable reason -> Error(StatusObservationUnreadable reason)
        | StatusIndeterminate reason -> Error(StatusObservationRefused(ObservationIndeterminate reason))
        | StatusComplete(revision, _, _) when not (validText revision) -> Error(StatusObservationRefused MissingObservationRevision)
        | StatusComplete(_, evidence, fields) when obj.ReferenceEquals(evidence, null) || obj.ReferenceEquals(fields, null) || evidence.PageCount <= 0 || evidence.NodeCount < 0 || not evidence.TerminalPage || evidence.NodeCount <> fields.Length -> Error InvalidStatusCompletenessEvidence
        | StatusComplete(_, _, []) -> Error StatusFieldMissing
        | StatusComplete(_, _, _ :: _ :: _) -> Error StatusFieldDuplicated
        | StatusComplete(revision, _, [ field ]) ->
            if obj.ReferenceEquals(field, null) || field.ProjectId <> projectId || field.ItemId <> itemId || SemanticName.value field.FieldName <> "Status" || obj.ReferenceEquals(field.Options, null) || List.isEmpty field.Options then Error InvalidStatusField else
            match duplicateBy (fun option -> option.Id) field.Options with
            | Some option -> Error(DuplicateStatusOptionId option.Id)
            | None ->
                match duplicateBy (fun option -> option.Name) field.Options with
                | Some option -> Error(DuplicateStatusOptionName option.Name)
                | None ->
                    match field.SelectedOptionId with
                    | Some selected when field.Options |> List.exists (fun option -> option.Id = selected) |> not -> Error(UnknownSelectedStatusOption selected)
                    | _ -> Ok { Revision = revision; Nature = ProjectionOnly; ProjectId = field.ProjectId; ItemId = field.ItemId; FieldId = field.FieldId; FieldName = field.FieldName; Options = field.Options |> List.sortBy (fun option -> LiveId.value option.Id); SelectedOptionId = field.SelectedOptionId }

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private hash values = values |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private membershipIntentText = function EnsureMember(project, content) -> [ "ensure-member"; LiveId.value project; LiveId.value content ] | EnsureNotMember(item, content) -> [ "ensure-not-member"; LiveId.value item; LiveId.value content ]
    let private membershipIdentity revision causation repository intent = hash ([ revision; causation; repository.Owner; repository.Name ] @ membershipIntentText intent)

    let planMembership expectedRevision causationIdentity (repository: RepositoryCoordinates) (intent: MembershipIntent) (snapshot: ProjectSnapshot) =
        if not (validText expectedRevision) then Error InvalidMembershipExpectedRevision
        elif not (validText causationIdentity) then Error InvalidMembershipCausationIdentity
        elif not (validRepo repository) || obj.ReferenceEquals(snapshot, null) || obj.ReferenceEquals(intent, null) then Error InvalidMembershipIntent
        elif expectedRevision <> snapshot.Revision then Error(MembershipStaleExpectedRevision snapshot.Revision)
        else
            let target = match intent with EnsureMember(_, id) | EnsureNotMember(_, id) -> id
            let key = membershipIdentity snapshot.Revision causationIdentity repository intent
            match resolveMembership repository target snapshot with
            | Error _ -> Error InvalidMembershipIntent
            | Ok(ActiveMembership item) ->
                match intent with
                | EnsureMember(projectId, _) when item.ProjectId = projectId -> Ok(MembershipNoOp { ObservedRevision = snapshot.Revision; IdempotencyIdentity = key; Intent = intent })
                | EnsureMember _ -> Error InvalidMembershipIntent
                | EnsureNotMember(itemId, content) when item.ItemId = itemId -> Ok(MembershipPlanned { Before = snapshot; Repository = repository; CausationIdentity = causationIdentity; IdempotencyIdentity = key; Operation = RemoveMembershipOperation(item.ProjectId, itemId, content) })
                | _ -> Error InvalidMembershipIntent
            | Ok MissingMembership ->
                match intent with
                | EnsureMember(projectId, content) -> Ok(MembershipPlanned { Before = snapshot; Repository = repository; CausationIdentity = causationIdentity; IdempotencyIdentity = key; Operation = AddMembershipOperation(projectId, content) })
                | EnsureNotMember _ -> Ok(MembershipNoOp { ObservedRevision = snapshot.Revision; IdempotencyIdentity = key; Intent = intent })
            | Ok other -> Error(MembershipMutationIneligible other)

    let checkMembershipPreState (plan: MembershipPlan) observation =
        match readProject observation with
        | Error failure -> Error(MembershipPreStateReadRefused failure)
        | Ok observed when observed.Revision <> plan.Before.Revision -> Error(MembershipReReadRequired(plan.Before.Revision, observed.Revision))
        | Ok observed when observed <> plan.Before -> Error ConcurrentMembershipChange
        | Ok observed -> Ok observed

    let verifyMembershipPostState expectedResultRevision (resultingItem: ProjectItem option) (plan: MembershipPlan) observation =
        if not (validText expectedResultRevision) then Error InvalidMembershipResultRevision
        elif expectedResultRevision = plan.Before.Revision then Error(MembershipResultRevisionDidNotAdvance expectedResultRevision)
        else match readProject observation with
             | Error failure -> Error(MembershipPostStateReadRefused failure)
             | Ok observed when observed.Revision <> expectedResultRevision -> Error(MembershipResultRevisionMismatch(expectedResultRevision, observed.Revision))
             | Ok observed ->
                 let expectedItems =
                     match plan.Operation, resultingItem with
                     | AddMembershipOperation(projectId, content), Some item when item.ProjectId = projectId && contentId item.Content = Some content && not item.Archived -> Some(item :: plan.Before.Items |> List.sortBy itemKey)
                     | RemoveMembershipOperation(_, itemId, _), None -> Some(plan.Before.Items |> List.filter (fun item -> item.ItemId <> itemId) |> List.sortBy itemKey)
                     | _ -> None
                 match expectedItems with
                 | None -> Error InvalidResultingProjectItem
                 | Some expected when observed.Items = expected -> Ok observed
                 | Some _ -> Error MembershipPostStateMismatch

    let private statusIntentText = function SetStatus id -> [ "set-status"; LiveId.value id ] | ClearStatus -> [ "clear-status" ]
    let private statusIdentity revision causation (snapshot: StatusSnapshot) intent = hash ([ revision; causation; LiveId.value snapshot.ProjectId; LiveId.value snapshot.ItemId; LiveId.value snapshot.FieldId ] @ statusIntentText intent)
    let planStatus expectedRevision causationIdentity (intent: StatusIntent) (snapshot: StatusSnapshot) =
        if not (validText expectedRevision) then Error InvalidStatusExpectedRevision
        elif not (validText causationIdentity) then Error InvalidStatusCausationIdentity
        elif obj.ReferenceEquals(snapshot, null) || obj.ReferenceEquals(intent, null) then Error InvalidStatusIntent
        elif expectedRevision <> snapshot.Revision then Error(StatusStaleExpectedRevision snapshot.Revision)
        else
            let key = statusIdentity snapshot.Revision causationIdentity snapshot intent
            match intent with
            | SetStatus option when snapshot.Options |> List.exists (fun current -> current.Id = option) |> not -> Error(RequestedStatusOptionMissing option)
            | SetStatus option when snapshot.SelectedOptionId = Some option -> Ok(StatusNoOp { ObservedRevision = snapshot.Revision; IdempotencyIdentity = key; Intent = intent })
            | ClearStatus when snapshot.SelectedOptionId.IsNone -> Ok(StatusNoOp { ObservedRevision = snapshot.Revision; IdempotencyIdentity = key; Intent = intent })
            | SetStatus option -> Ok(StatusPlanned { Before = snapshot; CausationIdentity = causationIdentity; IdempotencyIdentity = key; Operation = SetStatusOperation option })
            | ClearStatus -> Ok(StatusPlanned { Before = snapshot; CausationIdentity = causationIdentity; IdempotencyIdentity = key; Operation = ClearStatusOperation })

    let checkStatusPreState (plan: StatusPlan) observation =
        match readStatus plan.Before.ProjectId plan.Before.ItemId observation with
        | Error failure -> Error(StatusPreStateReadRefused failure)
        | Ok observed when observed.Revision <> plan.Before.Revision -> Error(StatusReReadRequired(plan.Before.Revision, observed.Revision))
        | Ok observed when observed <> plan.Before -> Error ConcurrentStatusChange
        | Ok observed -> Ok observed

    let verifyStatusPostState expectedResultRevision (plan: StatusPlan) observation =
        if not (validText expectedResultRevision) then Error InvalidStatusResultRevision
        elif expectedResultRevision = plan.Before.Revision then Error(StatusResultRevisionDidNotAdvance expectedResultRevision)
        else match readStatus plan.Before.ProjectId plan.Before.ItemId observation with
             | Error failure -> Error(StatusPostStateReadRefused failure)
             | Ok observed when observed.Revision <> expectedResultRevision -> Error(StatusResultRevisionMismatch(expectedResultRevision, observed.Revision))
             | Ok observed ->
                 let expectedSelected = match plan.Operation with SetStatusOperation option -> Some option | ClearStatusOperation -> None
                 let expected = { plan.Before with Revision = expectedResultRevision; SelectedOptionId = expectedSelected }
                 if observed = expected then Ok observed else Error StatusPostStateMismatch
