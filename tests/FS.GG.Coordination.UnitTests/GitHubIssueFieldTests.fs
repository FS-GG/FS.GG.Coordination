module FS.GG.Coordination.GitHubIssueFieldTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private name value = SemanticName.tryCreate value |> Result.defaultWith failwith
let private liveId value = LiveId.tryCreate value |> Result.defaultWith failwith

let private complete revision values =
    Complete
        { Revision = revision
          Evidence = { PageCount = 1; NodeCount = List.length values; TerminalPage = true }
          Values = values }

let private identity kind id value = { Kind = kind; Id = liveId id; Name = name value }
let private option id value = { Id = liveId id; Name = name value }

[<Fact>]
let ``semantic identity resolves every supported kind only from a unique complete observation`` () =
    let identities =
        [ identity Repository "R_1" "FS-GG/Coordination"
          identity Issue "I_1" "Issue 128"
          identity IssueType "IT_1" "Task"
          identity Field "F_1" "Status"
          identity Option "O_1" "Ready" ]

    for expected in identities do
        Assert.Equal(Ok expected, IssueFields.resolveIdentity expected.Name expected.Kind (complete "rev-1" identities))

    Assert.Equal(Error IdentityMissing, IssueFields.resolveIdentity (name "Missing") Field (complete "rev-1" identities))
    Assert.Equal(Error(ObservationRefused(ObservationUnauthorized "denied")), IssueFields.resolveIdentity (name "Status") Field (Unauthorized "denied"))

[<Fact>]
let ``identity ambiguity and duplicate live ids fail closed`` () =
    let duplicateName = [ identity Field "F_1" "Status"; identity Field "F_2" "Status" ]
    Assert.Equal(Error IdentityDuplicated, IssueFields.resolveIdentity (name "Status") Field (complete "rev-1" duplicateName))

    let repeatedId = [ identity Field "F_1" "Status"; identity Option "F_1" "Ready" ]
    match IssueFields.resolveIdentity (name "Status") Field (complete "rev-1" repeatedId) with
    | Error(DuplicateLiveId id) -> Assert.Equal("F_1", LiveId.value id)
    | value -> failwith $"expected duplicate live id, got {value}"

[<Fact>]
let ``field validation enforces type and an exact closed option set`` () =
    let declaration = { Name = name "Status"; DataType = SingleSelect; Options = [ name "Ready"; name "Done" ] }
    let field options dataType = { Id = liveId "F_1"; Name = name "Status"; DataType = dataType; Options = options }
    let ready = option "O_1" "Ready"
    let doneValue = option "O_2" "Done"
    Assert.Equal(Ok(field [ ready; doneValue ] SingleSelect), IssueFields.validateField declaration (complete "rev-1" [ field [ ready; doneValue ] SingleSelect ]))
    Assert.Equal(Error(FieldTypeDrift(SingleSelect, Text)), IssueFields.validateField declaration (complete "rev-1" [ field [] Text ]))
    Assert.Equal(Error(MissingOption(name "Done")), IssueFields.validateField declaration (complete "rev-1" [ field [ ready ] SingleSelect ]))
    Assert.Equal(Error(UnexpectedOption(name "Parked")), IssueFields.validateField declaration (complete "rev-1" [ field [ ready; doneValue; option "O_3" "Parked" ] SingleSelect ]))
    Assert.Equal(Error(DuplicateOptionName(name "Ready")), IssueFields.validateField declaration (complete "rev-1" [ field [ ready; option "O_3" "Ready" ] SingleSelect ]))
    match IssueFields.validateField declaration (complete "rev-1" [ field [ ready; option "O_1" "Done" ] SingleSelect ]) with
    | Error(DuplicateOptionId id) -> Assert.Equal("O_1", LiveId.value id)
    | value -> failwith $"expected duplicate option id, got {value}"
    let duplicateFieldId =
        [ field [ ready; doneValue ] SingleSelect
          { Id = liveId "F_1"; Name = name "Other"; DataType = Text; Options = [] } ]
    match IssueFields.validateField declaration (complete "rev-1" duplicateFieldId) with
    | Error(DuplicateFieldId id) -> Assert.Equal("F_1", LiveId.value id)
    | value -> failwith $"expected duplicate field id, got {value}"
    Assert.Equal(Error InvalidFieldDeclaration, IssueFields.validateField { declaration with DataType = Text } (complete "rev-1" []))

[<Fact>]
let ``current value read preserves revision and terminal page evidence`` () =
    let issue = liveId "I_1"
    let field = liveId "F_1"
    let current = { IssueId = issue; FieldId = field; Value = SingleSelectValue(name "Ready") }
    match IssueFields.readCurrentValue issue field (complete "rev-7" [ current ]) with
    | Ok observed ->
        Assert.Equal("rev-7", observed.Revision)
        Assert.True(observed.Evidence.TerminalPage)
        Assert.Equal(current, observed.Value)
    | Error failure -> failwith $"expected current value, got {failure}"

    let broken = Complete { Revision = "rev-7"; Evidence = { PageCount = 2; NodeCount = 1; TerminalPage = false }; Values = [ current ] }
    Assert.Equal(Error(SchemaObservationRefused InvalidCompletenessEvidence), IssueFields.readCurrentValue issue field broken)
    Assert.Equal(Error(SchemaObservationRefused(ObservationIncomplete("page missing", Some "cursor"))), IssueFields.readCurrentValue issue field (Incomplete("page missing", Some "cursor")))

[<Fact>]
let ``guarded plans are deterministic and distinguish create update clear and no-op`` () =
    let repository = liveId "R_1"
    let issue = liveId "I_1"
    let field = liveId "F_1"
    let ready = SingleSelectValue(name "Ready")
    let doneValue = SingleSelectValue(name "Done")

    let create = IssueFields.plan "rev-9" "cause-1" (CreateIssue(repository, "New issue")) (complete "rev-9" [ IssueAbsent ])
    match create with
    | Ok(Planned plan) -> Assert.Equal(CreateIssueOperation(repository, "New issue"), plan.Operation)
    | value -> failwith $"expected create plan, got {value}"

    let update = IssueFields.plan "rev-9" "cause-1" (UpdateField(issue, field, doneValue)) (complete "rev-9" [ FieldPresent ready ])
    Assert.Equal(update, IssueFields.plan "rev-9" "cause-1" (UpdateField(issue, field, doneValue)) (complete "rev-9" [ FieldPresent ready ]))
    match update with
    | Ok(Planned plan) -> Assert.Equal(UpdateFieldOperation(issue, field, doneValue), plan.Operation)
    | value -> failwith $"expected update plan, got {value}"

    match IssueFields.plan "rev-9" "cause-1" (ClearField(issue, field)) (complete "rev-9" [ FieldPresent ready ]) with
    | Ok(Planned plan) -> Assert.Equal(ClearFieldOperation(issue, field), plan.Operation)
    | value -> failwith $"expected clear plan, got {value}"

    match IssueFields.plan "rev-9" "cause-1" (UpdateField(issue, field, ready)) (complete "rev-9" [ FieldPresent ready ]) with
    | Ok(NoOp receipt) -> Assert.Equal("rev-9", receipt.ObservedRevision)
    | value -> failwith $"expected no-op, got {value}"

[<Fact>]
let ``planning refuses stale incomplete and ambiguous authority`` () =
    let issue = liveId "I_1"
    let field = liveId "F_1"
    let intent = ClearField(issue, field)
    Assert.Equal(Error(StaleExpectedRevision "rev-old"), IssueFields.plan "rev-new" "cause" intent (complete "rev-old" [ FieldAbsent ]))
    Assert.Equal(Error(PlanObservationRefused(ObservationIncomplete("truncated", None))), IssueFields.plan "rev-new" "cause" intent (Incomplete("truncated", None)))
    Assert.Equal(Error AmbiguousCurrentState, IssueFields.plan "rev-new" "cause" intent (complete "rev-new" [ FieldAbsent; FieldAbsent ]))
    Assert.Equal(Error InvalidExpectedRevision, IssueFields.plan " rev-new" "cause" intent (complete "rev-new" [ FieldAbsent ]))
    Assert.Equal(Error(PlanObservationRefused InvalidCompletenessEvidence), IssueFields.plan "rev-new" "cause" intent Unchecked.defaultof<_>)
    Assert.Equal(Error InvalidMutationIntent, IssueFields.plan "rev-new" "cause" Unchecked.defaultof<_> (complete "rev-new" [ FieldAbsent ]))

[<Fact>]
let ``idempotency identities use unambiguous length-prefixed opaque values`` () =
    let desired = TextValue "value"
    let first = IssueFields.plan "rev" "cause" (UpdateField(liveId "I|F", liveId "G", desired)) (complete "rev" [ FieldAbsent ])
    let second = IssueFields.plan "rev" "cause" (UpdateField(liveId "I", liveId "F|G", desired)) (complete "rev" [ FieldAbsent ])
    match first, second with
    | Ok(Planned left), Ok(Planned right) -> Assert.NotEqual(left.IdempotencyIdentity, right.IdempotencyIdentity)
    | values -> failwith $"expected two update plans, got {values}"

[<Fact>]
let ``qualification requires two complete independently reported control inventories`` () =
    let passing: GitHubIssueFieldControlResult list =
        GitHubIssueFieldQualification.requiredControls
        |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubIssueFieldQualification.validate passing passing)
    match GitHubIssueFieldQualification.validate (List.tail passing) passing with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GIFQ-INVENTORY" && finding.ControlId = "generated")
    | Ok () -> failwith "a truncated generated inventory was accepted"
