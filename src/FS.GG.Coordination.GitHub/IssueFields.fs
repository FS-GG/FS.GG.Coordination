namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text

type SemanticName = private SemanticName of string

[<RequireQualifiedAccess>]
module SemanticName =
    let tryCreate (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            Error "semantic name must be non-empty and already trimmed"
        else
            Ok(SemanticName value)

    let value (SemanticName value) = value

type LiveId = private LiveId of string

[<RequireQualifiedAccess>]
module LiveId =
    let tryCreate (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            Error "live id must be non-empty and already trimmed"
        else
            Ok(LiveId value)

    let value (LiveId value) = value

type IdentityKind = Repository | Issue | IssueType | Field | Option

type LiveIdentity =
    { Kind: IdentityKind
      Id: LiveId
      Name: SemanticName }

type PageEvidence =
    { PageCount: int
      NodeCount: int
      TerminalPage: bool }

type CompleteObservation<'value> =
    { Revision: string
      Evidence: PageEvidence
      Values: 'value list }

type Observation<'value> =
    | Complete of CompleteObservation<'value>
    | Incomplete of reason: string * cursor: string option
    | Unsupported of reason: string
    | Unauthorized of reason: string
    | Indeterminate of reason: string

type ObservationRefusal =
    | ObservationIncomplete of reason: string * cursor: string option
    | ObservationUnsupported of reason: string
    | ObservationUnauthorized of reason: string
    | ObservationIndeterminate of reason: string
    | InvalidCompletenessEvidence
    | MissingObservationRevision

type ResolutionFailure =
    | ObservationRefused of ObservationRefusal
    | IdentityMissing
    | IdentityDuplicated
    | DuplicateLiveId of LiveId

type FieldDataType = Text | Number | Date | SingleSelect

type FieldDeclaration =
    { Name: SemanticName
      DataType: FieldDataType
      Options: SemanticName list }

type LiveOption = { Id: LiveId; Name: SemanticName }

type LiveField =
    { Id: LiveId
      Name: SemanticName
      DataType: FieldDataType
      Options: LiveOption list }

type SchemaFailure =
    | SchemaObservationRefused of ObservationRefusal
    | InvalidFieldDeclaration
    | InvalidLiveField
    | FieldMissing
    | FieldDuplicated
    | DuplicateFieldId of LiveId
    | FieldTypeDrift of expected: FieldDataType * observed: FieldDataType
    | DuplicateOptionName of SemanticName
    | DuplicateOptionId of LiveId
    | MissingOption of SemanticName
    | UnexpectedOption of SemanticName
    | CurrentValueMissing
    | CurrentValueDuplicated

type FieldValue =
    | TextValue of string
    | NumberValue of decimal
    | DateValue of DateOnly
    | SingleSelectValue of SemanticName

type CurrentFieldValue =
    { IssueId: LiveId
      FieldId: LiveId
      Value: FieldValue }

type ObservedFieldValue =
    { Revision: string
      Evidence: PageEvidence
      Value: CurrentFieldValue }

type CurrentMutationState =
    | IssueAbsent
    | IssuePresent
    | FieldAbsent
    | FieldPresent of FieldValue

type MutationIntent =
    | CreateIssue of repositoryId: LiveId * title: string
    | UpdateField of issueId: LiveId * fieldId: LiveId * value: FieldValue
    | ClearField of issueId: LiveId * fieldId: LiveId

type MutationOperation =
    | CreateIssueOperation of repositoryId: LiveId * title: string
    | UpdateFieldOperation of issueId: LiveId * fieldId: LiveId * value: FieldValue
    | ClearFieldOperation of issueId: LiveId * fieldId: LiveId

type MutationPlan =
    { ExpectedRevision: string
      IdempotencyIdentity: string
      Operation: MutationOperation }

type NoOpReceipt =
    { ObservedRevision: string
      IdempotencyIdentity: string }

type PlanDecision = Planned of MutationPlan | NoOp of NoOpReceipt

type PlanRefusal =
    | PlanObservationRefused of ObservationRefusal
    | InvalidExpectedRevision
    | StaleExpectedRevision of observed: string
    | InvalidCausationIdentity
    | InvalidMutationIntent
    | AmbiguousCurrentState
    | IncompatibleCurrentState

[<RequireQualifiedAccess>]
module IssueFields =
    let complete (observation: Observation<'value>) =
        if obj.ReferenceEquals(observation, null) then
            Error InvalidCompletenessEvidence
        else
            match observation with
            | Incomplete(reason, cursor) -> Error(ObservationIncomplete(reason, cursor))
            | Unsupported reason -> Error(ObservationUnsupported reason)
            | Unauthorized reason -> Error(ObservationUnauthorized reason)
            | Indeterminate reason -> Error(ObservationIndeterminate reason)
            | Complete value when obj.ReferenceEquals(value, null) -> Error InvalidCompletenessEvidence
            | Complete value when String.IsNullOrWhiteSpace value.Revision -> Error MissingObservationRevision
            | Complete value when
                value.Revision <> value.Revision.Trim()
                || obj.ReferenceEquals(value.Values, null)
                || obj.ReferenceEquals(value.Evidence, null)
                || List.exists (fun item -> obj.ReferenceEquals(item, null)) value.Values
                || value.Evidence.PageCount <= 0
                || value.Evidence.NodeCount < 0
                || value.Evidence.NodeCount <> value.Values.Length
                || not value.Evidence.TerminalPage
                -> Error InvalidCompletenessEvidence
            | Complete value -> Ok value

    let duplicateBy projection values =
        values
        |> List.groupBy projection
        |> List.tryPick (fun (key, matches) -> if matches.Length > 1 then Some key else None)

    let resolveIdentity (expected: SemanticName) (kind: IdentityKind) (observation: Observation<LiveIdentity>) =
        match complete observation with
        | Error refusal -> Error(ObservationRefused refusal)
        | Ok snapshot ->
            match duplicateBy (fun (identity: LiveIdentity) -> identity.Id) snapshot.Values with
            | Some id -> Error(DuplicateLiveId id)
            | None ->
                match snapshot.Values |> List.filter (fun identity -> identity.Kind = kind && identity.Name = expected) with
                | [] -> Error IdentityMissing
                | [ identity ] -> Ok identity
                | _ -> Error IdentityDuplicated

    let validateField (declaration: FieldDeclaration) (observation: Observation<LiveField>) =
        if obj.ReferenceEquals(declaration, null)
           || obj.ReferenceEquals(declaration.Options, null)
           || List.exists (fun optionName -> obj.ReferenceEquals(optionName, null)) declaration.Options
           || (declaration.DataType <> SingleSelect && not (List.isEmpty declaration.Options)) then
            Error InvalidFieldDeclaration
        else
            match complete observation with
            | Error refusal -> Error(SchemaObservationRefused refusal)
            | Ok snapshot ->
                match duplicateBy (fun (field: LiveField) -> field.Id) snapshot.Values with
                | Some id -> Error(DuplicateFieldId id)
                | None ->
                    match snapshot.Values |> List.filter (fun field -> field.Name = declaration.Name) with
                    | [] -> Error FieldMissing
                    | _ :: _ :: _ -> Error FieldDuplicated
                    | [ field ] when field.DataType <> declaration.DataType -> Error(FieldTypeDrift(declaration.DataType, field.DataType))
                    | [ field ] when obj.ReferenceEquals(field.Options, null) || List.exists (fun optionValue -> obj.ReferenceEquals(optionValue, null)) field.Options -> Error InvalidLiveField
                    | [ field ] ->
                        match duplicateBy (fun (option: LiveOption) -> option.Name) field.Options with
                        | Some name -> Error(DuplicateOptionName name)
                        | None ->
                            match duplicateBy (fun (option: LiveOption) -> option.Id) field.Options with
                            | Some id -> Error(DuplicateOptionId id)
                            | None ->
                                match duplicateBy id declaration.Options with
                                | Some name -> Error(DuplicateOptionName name)
                                | None ->
                                    let expected = declaration.Options |> Set.ofList
                                    let observed = field.Options |> List.map (fun option -> option.Name) |> Set.ofList
                                    match Set.difference expected observed |> Set.toList, Set.difference observed expected |> Set.toList with
                                    | missing :: _, _ -> Error(MissingOption missing)
                                    | [], extra :: _ -> Error(UnexpectedOption extra)
                                    | [], [] -> Ok field

    let readCurrentValue (issueId: LiveId) (fieldId: LiveId) (observation: Observation<CurrentFieldValue>) =
        match complete observation with
        | Error refusal -> Error(SchemaObservationRefused refusal)
        | Ok snapshot ->
            match snapshot.Values |> List.filter (fun value -> value.IssueId = issueId && value.FieldId = fieldId) with
            | [] -> Error CurrentValueMissing
            | [ value ] -> Ok { Revision = snapshot.Revision; Evidence = snapshot.Evidence; Value = value }
            | _ -> Error CurrentValueDuplicated

    let fieldValueText value =
        match value with
        | TextValue text -> $"text:{text}"
        | NumberValue number -> $"number:{number.ToString(CultureInfo.InvariantCulture)}"
        | DateValue date ->
            let rendered = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            $"date:{rendered}"
        | SingleSelectValue name -> $"option:{SemanticName.value name}"

    let canonicalPart (value: string) =
        $"{Encoding.UTF8.GetByteCount value}:{value}"

    let intentText intent =
        match intent with
        | CreateIssue(repositoryId, title) ->
            String.concat "" [ canonicalPart "create"; canonicalPart (LiveId.value repositoryId); canonicalPart title ]
        | UpdateField(issueId, fieldId, value) ->
            String.concat "" [ canonicalPart "update"; canonicalPart (LiveId.value issueId); canonicalPart (LiveId.value fieldId); canonicalPart (fieldValueText value) ]
        | ClearField(issueId, fieldId) ->
            String.concat "" [ canonicalPart "clear"; canonicalPart (LiveId.value issueId); canonicalPart (LiveId.value fieldId) ]

    let idempotency revision causation intent =
        String.concat "" [ canonicalPart revision; canonicalPart causation; canonicalPart (intentText intent) ]
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let validIntent intent =
        match intent with
        | CreateIssue(_, title) -> not (String.IsNullOrWhiteSpace title) && title = title.Trim()
        | UpdateField(_, _, TextValue text) -> not (isNull text)
        | _ -> true

    let plan expectedRevision causationIdentity intent observation =
        if String.IsNullOrWhiteSpace expectedRevision || expectedRevision <> expectedRevision.Trim() then
            Error InvalidExpectedRevision
        elif String.IsNullOrWhiteSpace causationIdentity || causationIdentity <> causationIdentity.Trim() then
            Error InvalidCausationIdentity
        elif obj.ReferenceEquals(intent, null) then
            Error InvalidMutationIntent
        elif not (validIntent intent) then
            Error InvalidMutationIntent
        else
            match complete observation with
            | Error refusal -> Error(PlanObservationRefused refusal)
            | Ok snapshot when snapshot.Revision <> expectedRevision -> Error(StaleExpectedRevision snapshot.Revision)
            | Ok snapshot ->
                match snapshot.Values with
                | [ current ] ->
                    let identity = idempotency snapshot.Revision causationIdentity intent
                    let noOp = NoOp { ObservedRevision = snapshot.Revision; IdempotencyIdentity = identity }
                    let planned operation = Planned { ExpectedRevision = snapshot.Revision; IdempotencyIdentity = identity; Operation = operation }
                    match intent, current with
                    | CreateIssue _, IssuePresent -> Ok noOp
                    | CreateIssue(repositoryId, title), IssueAbsent -> Ok(planned (CreateIssueOperation(repositoryId, title)))
                    | UpdateField(_, _, desired), FieldPresent currentValue when currentValue = desired -> Ok noOp
                    | UpdateField(issueId, fieldId, desired), FieldPresent _
                    | UpdateField(issueId, fieldId, desired), FieldAbsent -> Ok(planned (UpdateFieldOperation(issueId, fieldId, desired)))
                    | ClearField _, FieldAbsent -> Ok noOp
                    | ClearField(issueId, fieldId), FieldPresent _ -> Ok(planned (ClearFieldOperation(issueId, fieldId)))
                    | _ -> Error IncompatibleCurrentState
                | _ -> Error AmbiguousCurrentState
