#load "../src/FS.GG.Coordination.GitHub/IssueFields.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubIssueFieldQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let fixturePath = Path.Combine(root, "tests/fixtures/github-issue-field/contract.json")
if not (File.Exists fixturePath) then fail "GIFQ-FIXTURE-MISSING" fixturePath

let fixture = JsonDocument.Parse(File.ReadAllBytes fixturePath)
let json = fixture.RootElement
let exactNames = json.EnumerateObject() |> Seq.map _.Name |> Seq.toList
if exactNames <> [ "controls"; "generated"; "schema"; "synthetic" ] then fail "GIFQ-FIXTURE-SHAPE" (String.concat "," exactNames)
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-issue-field-fixture/1" then fail "GIFQ-FIXTURE-SCHEMA" fixturePath
if not (json.GetProperty("synthetic").GetBoolean()) then fail "GIFQ-FIXTURE-PROVENANCE" "Q3 fixture must disclose synthetic provenance"

let fixtureControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let required = GitHubIssueFieldQualification.requiredControls |> List.map GitHubIssueFieldQualification.controlId
if fixtureControls <> required then fail "GIFQ-FIXTURE-INVENTORY" (String.concat "," fixtureControls)

let name value = SemanticName.tryCreate value |> Result.defaultWith (fail "GIFQ-NAME")
let liveId value = LiveId.tryCreate value |> Result.defaultWith (fail "GIFQ-ID")
let complete revision values =
    Complete { Revision = revision; Evidence = { PageCount = 1; NodeCount = List.length values; TerminalPage = true }; Values = values }
let outcome red green control: GitHubIssueFieldControlResult = { Control = control; MutationRed = red; BaselineGreen = green }

// Generated controls derive their representative identities and values from the committed fixture.
let generatedResults () =
    let source = json.GetProperty("generated")
    let revision = source.GetProperty("revision").GetString()
    let causation = source.GetProperty("causation").GetString()
    let issue = liveId (source.GetProperty("issueId").GetString())
    let fieldId = liveId (source.GetProperty("fieldId").GetString())
    let readyName = name (source.GetProperty("option").GetString())
    let doneName = name "Done"
    let ready = SingleSelectValue readyName
    let doneValue = SingleSelectValue doneName
    let current = { IssueId = issue; FieldId = fieldId; Value = ready }
    let statusIdentity = { Kind = Field; Id = fieldId; Name = name "Status" }
    let duplicateIdentity = { statusIdentity with Id = liveId "F_fixture_duplicate" }
    let declaration = { Name = name "Status"; DataType = SingleSelect; Options = [ readyName; doneName ] }
    let live options dataType = { Id = fieldId; Name = name "Status"; DataType = dataType; Options = options }
    let readyOption = { Id = liveId "O_fixture_ready"; Name = readyName }
    let doneOption = { Id = liveId "O_fixture_done"; Name = doneName }
    let incomplete = Complete { Revision = revision; Evidence = { PageCount = 2; NodeCount = 1; TerminalPage = false }; Values = [ current ] }
    [ outcome
          (IssueFields.readCurrentValue issue fieldId incomplete = Error(SchemaObservationRefused InvalidCompletenessEvidence))
          (match IssueFields.readCurrentValue issue fieldId (complete revision [ current ]) with Ok observed -> observed.Revision = revision | _ -> false)
          Pagination
      outcome
          (IssueFields.resolveIdentity (name "Status") Field (complete revision [ statusIdentity; duplicateIdentity ]) = Error IdentityDuplicated)
          (IssueFields.resolveIdentity (name "Status") Field (complete revision [ statusIdentity ]) = Ok statusIdentity)
          DuplicateIdentity
      outcome
          (IssueFields.validateField declaration (complete revision [ live [] Text ]) = Error(FieldTypeDrift(SingleSelect, Text)))
          (IssueFields.validateField declaration (complete revision [ live [ readyOption; doneOption ] SingleSelect ]) |> Result.isOk)
          TypeDrift
      outcome
          (IssueFields.validateField declaration (complete revision [ live [ readyOption ] SingleSelect ]) = Error(MissingOption doneName))
          (IssueFields.validateField declaration (complete revision [ live [ readyOption; doneOption ] SingleSelect ]) |> Result.isOk)
          OptionDrift
      outcome
          (IssueFields.plan "newer-revision" causation (UpdateField(issue, fieldId, doneValue)) (complete revision [ FieldPresent ready ]) = Error(StaleExpectedRevision revision))
          (IssueFields.plan revision causation (UpdateField(issue, fieldId, doneValue)) (complete revision [ FieldPresent ready ]) |> Result.isOk)
          StaleRevision
      outcome
          (IssueFields.plan revision causation (ClearField(issue, fieldId)) (Incomplete("missing page", Some "cursor")) = Error(PlanObservationRefused(ObservationIncomplete("missing page", Some "cursor"))))
          (IssueFields.plan revision causation (ClearField(issue, fieldId)) (complete revision [ FieldPresent ready ]) |> Result.isOk)
          IncompleteObservation
      outcome
          (match IssueFields.plan revision causation (UpdateField(issue, fieldId, ready)) (complete revision [ FieldPresent ready ]) with Ok(NoOp _) -> true | _ -> false)
          (match IssueFields.plan revision causation (UpdateField(issue, fieldId, doneValue)) (complete revision [ FieldPresent ready ]) with Ok(Planned _) -> true | _ -> false)
          NoOpMutation ]

// Independent controls are separately authored with different identities, revisions, and drift forms.
let independentResults () =
    let revision = "independent-rev-23"
    let issue = liveId "I_independent"
    let fieldId = liveId "F_independent"
    let activeName = name "Active"
    let closedName = name "Closed"
    let active = SingleSelectValue activeName
    let closedValue = SingleSelectValue closedName
    let value = { IssueId = issue; FieldId = fieldId; Value = active }
    let declaration = { Name = name "State"; DataType = SingleSelect; Options = [ activeName; closedName ] }
    let activeOption = { Id = liveId "O_active"; Name = activeName }
    let closedOption = { Id = liveId "O_closed"; Name = closedName }
    let field options dataType = { Id = fieldId; Name = name "State"; DataType = dataType; Options = options }
    let one = { Kind = Option; Id = liveId "O_same"; Name = activeName }
    let two = { Kind = Field; Id = liveId "O_same"; Name = name "State" }
    let malformed = Complete { Revision = revision; Evidence = { PageCount = 1; NodeCount = 0; TerminalPage = true }; Values = [ value ] }
    [ outcome
          (IssueFields.readCurrentValue issue fieldId malformed = Error(SchemaObservationRefused InvalidCompletenessEvidence))
          (IssueFields.readCurrentValue issue fieldId (complete revision [ value ]) |> Result.isOk)
          Pagination
      outcome
          (match IssueFields.resolveIdentity activeName Option (complete revision [ one; two ]) with Error(DuplicateLiveId _) -> true | _ -> false)
          (IssueFields.resolveIdentity activeName Option (complete revision [ one ]) = Ok one)
          DuplicateIdentity
      outcome
          (IssueFields.validateField declaration (complete revision [ field [] Number ]) = Error(FieldTypeDrift(SingleSelect, Number)))
          (IssueFields.validateField declaration (complete revision [ field [ activeOption; closedOption ] SingleSelect ]) |> Result.isOk)
          TypeDrift
      outcome
          (IssueFields.validateField declaration (complete revision [ field [ activeOption; closedOption; { Id = liveId "O_paused"; Name = name "Paused" } ] SingleSelect ]) = Error(UnexpectedOption(name "Paused")))
          (IssueFields.validateField declaration (complete revision [ field [ activeOption; closedOption ] SingleSelect ]) |> Result.isOk)
          OptionDrift
      outcome
          (IssueFields.plan "independent-rev-24" "independent-cause" (ClearField(issue, fieldId)) (complete revision [ FieldPresent active ]) = Error(StaleExpectedRevision revision))
          (IssueFields.plan revision "independent-cause" (ClearField(issue, fieldId)) (complete revision [ FieldPresent active ]) |> Result.isOk)
          StaleRevision
      outcome
          (IssueFields.plan revision "independent-cause" (UpdateField(issue, fieldId, closedValue)) (Unauthorized "fixture denial") = Error(PlanObservationRefused(ObservationUnauthorized "fixture denial")))
          (IssueFields.plan revision "independent-cause" (UpdateField(issue, fieldId, closedValue)) (complete revision [ FieldPresent active ]) |> Result.isOk)
          IncompleteObservation
      outcome
          (match IssueFields.plan revision "independent-cause" (ClearField(issue, fieldId)) (complete revision [ FieldAbsent ]) with Ok(NoOp _) -> true | _ -> false)
          (match IssueFields.plan revision "independent-cause" (ClearField(issue, fieldId)) (complete revision [ FieldPresent active ]) with Ok(Planned _) -> true | _ -> false)
          NoOpMutation ]

let generated = generatedResults ()
let independent = independentResults ()
match GitHubIssueFieldQualification.validate generated independent with
| Ok () -> printfn "github-issue-field-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings ->
    findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message)
    fail "GIFQ-FAILED" $"{findings.Length} finding(s)"
fixture.Dispose()
