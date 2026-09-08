namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type RuntimeBuildInputs =
    { OutputType: string; IsPackable: bool; PublishProfile: string option; RuntimeIdentifier: string option
      SelfContained: bool; Listening: bool; DeploymentConfigured: bool; ProductionAuthority: bool
      EvaluatedProjects: string list }
type RuntimeClauseDisposition = { Clause: string; Disposition: string; Evidence: string }
type RuntimeRecoveryExercise =
    { ExerciseId: string; Failure: string; ExpectedSubjects: string list; RecoveredSubjects: string list
      ExpectedPages: int list; RecoveredPages: int list; AuthorityBefore: string; AuthorityAfter: string
      RecoveryPath: string; ProviderConfirmed: bool; Settlement: string
      DiagnosticInput: string option; DiagnosticOutput: string option }
type RuntimeAcceptedChild = { UnitId: string; ReceiptDigest: string }
type RuntimeOperationsFacts =
    { Unit: string; PrerequisiteReceiptSha256: string; RoadmapRevision: string; RoadmapSha256: string
      CandidateHead: string; BuildInputs: RuntimeBuildInputs; Clauses: RuntimeClauseDisposition list
      Exercises: RuntimeRecoveryExercise list; AcceptedChildren: RuntimeAcceptedChild list; ModelIdentity: string
      ProductionV2: bool; InstalledAuditExecution: bool; PollingReduced: bool; Gs208Claimed: bool }
type RuntimeOperationsReport =
    { SchemaVersion: int; Unit: string; PrerequisiteReceiptSha256: string; RoadmapRevision: string
      RoadmapSha256: string; CandidateHead: string; RuntimeDisposition: string; AuditAuthority: string
      BuildInputs: RuntimeBuildInputs; Clauses: RuntimeClauseDisposition list; Exercises: RuntimeRecoveryExercise list
      AcceptedChildren: RuntimeAcceptedChild list; ModelIdentity: string; Limits: string list
      ProductionV2: bool; InstalledAuditExecution: bool; PollingReduced: bool; Gs208Claimed: bool; Seal: string }
[<RequireQualifiedAccess>]
type RuntimeOperationsFinding =
    | MissingField of string | ChangedPrerequisite | ChangedRoadmap | SubstitutedHead | HostActivated of string
    | ClauseInventoryChanged | UnsupportedDisposition of string | MissingRecovery of string | OmittedSubject of string
    | OmittedPage of string | StaleReplayAuthority of string | InventedSettlement of string | SecretLeak of string
    | ChildReceiptChanged of string | ModelIdentityChanged | UnsupportedClaim of string | AlteredSeal
    | ReplayConflict | InvalidSerialization of string
type RuntimeOperationsControlResult = { ControlId: string; ControlPassed: bool; BaselineGreen: bool; Evidence: string }

module GitHubRuntimeOperationsQualification =
    let prerequisiteReceiptSha256 = "2cd764adfab89480a1272c329a2ed06101766ee92a6be98f76498c5380440e87"
    let roadmapRevision = "6d3c8283042184557d4f0db07fcc353571494bb5"
    let roadmapSha256 = "04bad334e0a48ed119bcd0df2b40a6db5333c06b1475c9ce52d078513ce8311c"
    let modelIdentity = "486e1a956d53f9809f183d336bb97824785b4937f9627e34c558a8c0ef548bc2"
    let requiredClauses =
        [ { Clause = "deployment"; Disposition = "inapplicable-no-host"; Evidence = "GS2-00.9" }
          { Clause = "rollback"; Disposition = "inapplicable-no-host"; Evidence = "GS2-00.9" }
          { Clause = "secret-rotation"; Disposition = "inapplicable-no-host"; Evidence = "GS2-00.9" }
          { Clause = "outage"; Disposition = "exercised"; Evidence = "provider-unavailability" }
          { Clause = "replay-backlog-recovery"; Disposition = "exercised"; Evidence = "backlog-replay" }
          { Clause = "regional-provider-failure"; Disposition = "exercised-provider-boundary"; Evidence = "provider-unavailability" }
          { Clause = "log-redaction"; Disposition = "exercised-diagnostic-boundary"; Evidence = "interruption" }
          { Clause = "alert-routing"; Disposition = "inapplicable-no-host"; Evidence = "GS2-00.9" }
          { Clause = "emergency-disable"; Disposition = "inapplicable-no-host"; Evidence = "GS2-00.9" } ]
    let requiredFailures = [ "event-absence"; "provider-unavailability"; "incomplete-audit"; "backlog-replay"; "interruption" ]
    let acceptedChildren =
        [ { UnitId = "GS2-07.1"; ReceiptDigest = "825781cedeebbd56aad3a3d41499d6f9bbc647da372f8a91df7c7e2a5ed336e1" }
          { UnitId = "GS2-07.2"; ReceiptDigest = "6ae56a7c9dce52f3ac25e39145b275ed5e8127a1020ee8c65a392b976661c298" }
          { UnitId = "GS2-07.3"; ReceiptDigest = "4c6a18a3c8cca8ebd59ce040f63f0192c07f9468ad155e7941f676b0b611719c" }
          { UnitId = "GS2-07.4"; ReceiptDigest = "d2cf3b943fc153047652d73de77bfdcb35fe6a087f414a494eec35542edd2a50" }
          { UnitId = "GS2-07.5"; ReceiptDigest = "dd321136fe28e135ba5ee29a3b81a2041b81c8eb29126762cf893bb98ece34d8" }
          { UnitId = "GS2-07.6"; ReceiptDigest = "eaf032038cc3ed1fb3f1a21db81a32f7af7969f84a0d9b77cd1d7eea68346bc6" }
          { UnitId = "GS2-07.7"; ReceiptDigest = prerequisiteReceiptSha256 } ]
    let requiredControls =
        [ "prerequisite"; "roadmap"; "command-identity"; "evaluated-build-inputs"; "host-activation"
          "runtime-disposition"; "clause-inventory"; "event-absence"; "provider-unavailability"
          "incomplete-audit"; "backlog-replay"; "interruption"; "subject-completeness"; "page-completeness"
          "replay-authority"; "unsettled-outcome"; "diagnostic-redaction"; "synthetic-secret"
          "child-receipts"; "comprehensive-command-set"; "cold-execution"; "model-identity"
          "no-production-v2"; "no-installed-audit"; "retained-polling"; "no-gs2-08"
          "no-host-writer"; "tamper"; "replay" ]

    let private sha40 = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
    let private sha64 = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let private hashText (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private reportBytes (report: RuntimeOperationsReport) = JsonSerializer.Serialize(report, jsonOptions)
    let private sealOf report = reportBytes { report with Seal = "" } |> hashText
    let private sortedDistinct (values: 'a list) = values <> [] && values = (values |> List.distinct |> List.sort)
    let redactDiagnostic (syntheticSecret: string) (diagnostic: string) =
        if String.IsNullOrEmpty syntheticSecret then diagnostic else diagnostic.Replace(syntheticSecret, "<redacted>", StringComparison.Ordinal)

    let compile (facts: RuntimeOperationsFacts) =
        let errors = ResizeArray<RuntimeOperationsFinding>()
        if facts.Unit <> "GS2-07.8" then errors.Add(RuntimeOperationsFinding.MissingField "unit")
        if facts.PrerequisiteReceiptSha256 <> prerequisiteReceiptSha256 then errors.Add RuntimeOperationsFinding.ChangedPrerequisite
        if facts.RoadmapRevision <> roadmapRevision || facts.RoadmapSha256 <> roadmapSha256 then errors.Add RuntimeOperationsFinding.ChangedRoadmap
        if not(sha40.IsMatch facts.CandidateHead) then errors.Add RuntimeOperationsFinding.SubstitutedHead
        let build = facts.BuildInputs
        if build.OutputType <> "Library" then errors.Add(RuntimeOperationsFinding.HostActivated "OutputType")
        if build.IsPackable then errors.Add(RuntimeOperationsFinding.HostActivated "IsPackable")
        if build.PublishProfile.IsSome then errors.Add(RuntimeOperationsFinding.HostActivated "PublishProfile")
        if build.RuntimeIdentifier.IsSome then errors.Add(RuntimeOperationsFinding.HostActivated "RuntimeIdentifier")
        if build.SelfContained then errors.Add(RuntimeOperationsFinding.HostActivated "SelfContained")
        if build.Listening then errors.Add(RuntimeOperationsFinding.HostActivated "Listening")
        if build.DeploymentConfigured then errors.Add(RuntimeOperationsFinding.HostActivated "DeploymentConfigured")
        if build.ProductionAuthority then errors.Add(RuntimeOperationsFinding.HostActivated "ProductionAuthority")
        if build.EvaluatedProjects <> [ "src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj" ] then
            errors.Add(RuntimeOperationsFinding.HostActivated "evaluated-projects")
        if facts.Clauses <> requiredClauses then errors.Add RuntimeOperationsFinding.ClauseInventoryChanged
        for clause in facts.Clauses do
            if String.IsNullOrWhiteSpace clause.Evidence then errors.Add(RuntimeOperationsFinding.UnsupportedDisposition clause.Clause)
        if facts.Exercises |> List.map _.Failure <> requiredFailures then
            for failure in requiredFailures do
                if facts.Exercises |> List.exists (fun value -> value.Failure = failure) |> not then
                    errors.Add(RuntimeOperationsFinding.MissingRecovery failure)
        for exercise in facts.Exercises do
            if String.IsNullOrWhiteSpace exercise.ExerciseId || String.IsNullOrWhiteSpace exercise.RecoveryPath then
                errors.Add(RuntimeOperationsFinding.MissingRecovery exercise.Failure)
            if not(sortedDistinct exercise.ExpectedSubjects) || exercise.RecoveredSubjects <> exercise.ExpectedSubjects then
                errors.Add(RuntimeOperationsFinding.OmittedSubject exercise.Failure)
            if not(sortedDistinct exercise.ExpectedPages) || exercise.RecoveredPages <> exercise.ExpectedPages then
                errors.Add(RuntimeOperationsFinding.OmittedPage exercise.Failure)
            if not(sha40.IsMatch exercise.AuthorityBefore) || exercise.AuthorityAfter <> exercise.AuthorityBefore then
                errors.Add(RuntimeOperationsFinding.StaleReplayAuthority exercise.Failure)
            if exercise.Failure = "interruption" then
                if exercise.ProviderConfirmed || exercise.Settlement <> "unsettled" || exercise.RecoveryPath <> "reobserve-before-resume" then
                    errors.Add(RuntimeOperationsFinding.InventedSettlement exercise.Failure)
            elif not exercise.ProviderConfirmed || exercise.Settlement <> "converged" then
                errors.Add(RuntimeOperationsFinding.InventedSettlement exercise.Failure)
            match exercise.DiagnosticInput, exercise.DiagnosticOutput with
            | Some input, Some output when input.Contains("synthetic-secret=", StringComparison.Ordinal) ->
                let secret = input.Substring(input.IndexOf("synthetic-secret=", StringComparison.Ordinal) + "synthetic-secret=".Length)
                if output.Contains(secret, StringComparison.Ordinal) || redactDiagnostic secret input <> output then
                    errors.Add(RuntimeOperationsFinding.SecretLeak exercise.Failure)
            | None, None -> ()
            | _ -> errors.Add(RuntimeOperationsFinding.SecretLeak exercise.Failure)
        if facts.AcceptedChildren <> acceptedChildren then
            let actual = facts.AcceptedChildren |> List.map _.UnitId |> Set.ofList
            for child in acceptedChildren do
                if not(actual.Contains child.UnitId) || facts.AcceptedChildren |> List.exists ((=) child) |> not then
                    errors.Add(RuntimeOperationsFinding.ChildReceiptChanged child.UnitId)
        if not(sha64.IsMatch facts.ModelIdentity) || facts.ModelIdentity <> modelIdentity then errors.Add RuntimeOperationsFinding.ModelIdentityChanged
        for name, asserted in
            [ "production-v2", facts.ProductionV2; "installed-audit-execution", facts.InstalledAuditExecution
              "polling-reduced", facts.PollingReduced; "GS2-08", facts.Gs208Claimed ] do
            if asserted then errors.Add(RuntimeOperationsFinding.UnsupportedClaim name)
        if errors.Count > 0 then Error(List.ofSeq errors)
        else
            let report =
                { SchemaVersion = 1; Unit = facts.Unit; PrerequisiteReceiptSha256 = facts.PrerequisiteReceiptSha256
                  RoadmapRevision = facts.RoadmapRevision; RoadmapSha256 = facts.RoadmapSha256; CandidateHead = facts.CandidateHead
                  RuntimeDisposition = "no-host-scheduled-audit-authoritative"; AuditAuthority = "scheduled-complete-audit"
                  BuildInputs = facts.BuildInputs; Clauses = facts.Clauses; Exercises = facts.Exercises
                  AcceptedChildren = facts.AcceptedChildren; ModelIdentity = facts.ModelIdentity
                  Limits = [ "no hosted log or alert pipeline exists"; "redaction is qualified only at the recovery diagnostic boundary"
                             "installed audit execution is not claimed"; "production v2 and GS2-08 are not claimed"
                             "polling remains enabled"; "section-7.4 attribution remains incomplete" ]
                  ProductionV2 = false; InstalledAuditExecution = false; PollingReduced = false; Gs208Claimed = false; Seal = "" }
            Ok { report with Seal = sealOf report }

    let serialize report = reportBytes report
    let verify expectedSeal report =
        let errors = ResizeArray<RuntimeOperationsFinding>()
        if report.SchemaVersion <> 1 || report.Unit <> "GS2-07.8" then errors.Add(RuntimeOperationsFinding.InvalidSerialization "identity")
        if report.PrerequisiteReceiptSha256 <> prerequisiteReceiptSha256 then errors.Add RuntimeOperationsFinding.ChangedPrerequisite
        if report.RoadmapRevision <> roadmapRevision || report.RoadmapSha256 <> roadmapSha256 then errors.Add RuntimeOperationsFinding.ChangedRoadmap
        if report.RuntimeDisposition <> "no-host-scheduled-audit-authoritative" || report.AuditAuthority <> "scheduled-complete-audit" then
            errors.Add(RuntimeOperationsFinding.HostActivated "runtime-disposition")
        if report.Clauses <> requiredClauses then errors.Add RuntimeOperationsFinding.ClauseInventoryChanged
        if report.AcceptedChildren <> acceptedChildren then errors.Add(RuntimeOperationsFinding.ChildReceiptChanged "inventory")
        if report.ModelIdentity <> modelIdentity then errors.Add RuntimeOperationsFinding.ModelIdentityChanged
        for name, asserted in
            [ "production-v2", report.ProductionV2; "installed-audit-execution", report.InstalledAuditExecution
              "polling-reduced", report.PollingReduced; "GS2-08", report.Gs208Claimed ] do
            if asserted then errors.Add(RuntimeOperationsFinding.UnsupportedClaim name)
        let actual = sealOf report
        if report.Seal <> actual || report.Seal <> expectedSeal then errors.Add RuntimeOperationsFinding.AlteredSeal
        if errors.Count = 0 then Ok report else Error(List.ofSeq errors)

    let parse (value: string) =
        try
            let report = JsonSerializer.Deserialize<RuntimeOperationsReport>(value, jsonOptions)
            if isNull(box report) then Error [ RuntimeOperationsFinding.InvalidSerialization "null" ]
            else
                match verify report.Seal report with
                | Ok parsed when serialize parsed = value.TrimEnd('\r', '\n') -> Ok parsed
                | Ok _ -> Error [ RuntimeOperationsFinding.InvalidSerialization "non-canonical bytes" ]
                | Error errors -> Error errors
        with error -> Error [ RuntimeOperationsFinding.InvalidSerialization error.Message ]

    let replay prior (facts: RuntimeOperationsFacts) =
        match compile facts with
        | Ok next when next = prior -> Ok prior
        | Ok _ -> Error [ RuntimeOperationsFinding.ReplayConflict ]
        | Error errors -> Error errors

    let validateControls generated independent =
        let validate label rows =
            [ if rows |> List.map _.ControlId <> requiredControls then yield $"{label} control inventory differs"
              if rows |> List.exists (fun row -> not row.ControlPassed || not row.BaselineGreen) then yield $"{label} control failed"
              if rows |> List.exists (fun row -> String.IsNullOrWhiteSpace row.Evidence || not(row.Evidence.Contains(row.ControlId, StringComparison.Ordinal))) then
                  yield $"{label} evidence is unbound" ]
        let errors = validate "generated" generated @ validate "independent" independent
        if (generated |> List.map _.ControlId) <> (independent |> List.map _.ControlId) then Error [ "control identities differ" ]
        elif List.zip generated independent |> List.exists (fun (left, right) -> left.Evidence = right.Evidence) then Error [ "controls are not independently identified" ]
        elif errors.IsEmpty then Ok () else Error errors
