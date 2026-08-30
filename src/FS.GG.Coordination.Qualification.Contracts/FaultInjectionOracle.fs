module FS.GG.Coordination.Qualification.Contracts.FaultInjectionOracle

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

let private acceptedSteps root =
    let path = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/settings-plans.json")
    try
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let fields = document.RootElement.GetProperty("content").GetProperty("specification").GetProperty("value").GetProperty("fields")
        let phase =
            fields.EnumerateArray()
            |> Seq.find (fun field -> field.GetProperty("name").GetString() = "phaseContract")
            |> _.GetProperty("value").GetProperty("value").GetString()
        Regex.Matches(phase, "DSPH-[A-Za-z]+")
        |> Seq.cast<Match> |> Seq.map _.Value |> Seq.distinct |> Seq.toList |> Ok
    with error -> Error $"FIO-SOURCE: %s{error.Message}"

let private has kind (execution: FaultInjection.Execution) = execution.Trace |> List.exists (fun event -> event.Kind = kind)
let private traceSteps kind (execution: FaultInjection.Execution) = execution.Trace |> List.filter (fun event -> event.Kind = kind) |> List.map _.Step |> Set.ofList

let validate (root: string) (executions: FaultInjection.Execution list) =
    match acceptedSteps root with
    | Error error -> Error error
    | Ok steps ->
        let byId = executions |> List.map (fun item -> item.Id, item) |> Map.ofList
        let expectedIds =
            [ for step in steps do yield $"before/%s{step}"; yield $"after/%s{step}"
              yield! [ "lost-response"; "duplicate-event"; "reordered-events"; "partial-page"
                       "rate-budget-exhausted"; "permission-revoked"; "concurrent-revision" ] ]
        let observedIds = executions |> List.map _.Id
        if steps <> [ "DSPH-Inspect"; "DSPH-Plan"; "DSPH-Apply"; "DSPH-Verify" ] then Error "FIO-STEP-AUTHORITY: accepted phase inventory differs"
        elif observedIds <> expectedIds then Error "FIO-COVERAGE: execution inventory differs"
        else
            let findings = ResizeArray<string>()
            let require condition code = if not condition then findings.Add code
            for step in steps do
                let before = byId[$"before/%s{step}"]
                require (before.Outcome="converged" && has "fault-before" before && has "retry" before) $"FIO-BEFORE:%s{step}"
                require (traceSteps "applied" before = Set.ofList steps) $"FIO-BEFORE-COMPLETE:%s{step}"
                let after = byId[$"after/%s{step}"]
                require (after.Outcome="converged" && has "response-lost" after && has "retry" after && has "idempotent" after) $"FIO-AFTER:%s{step}"
                require (traceSteps "applied" after = Set.ofList steps) $"FIO-AFTER-COMPLETE:%s{step}"
            let lost = byId["lost-response"]
            require (lost.Outcome="converged" && has "response-lost" lost && has "idempotent" lost) "FIO-LOST-RESPONSE"
            let duplicate = byId["duplicate-event"]
            require (duplicate.Outcome="converged" && has "duplicate-delivered" duplicate && has "duplicate-discarded" duplicate) "FIO-DUPLICATE"
            let reordered = byId["reordered-events"]
            require (reordered.Outcome="converged" && has "events-reversed" reordered && has "events-reduced-by-ordinal" reordered) "FIO-REORDER"
            for id,code in
                [ "partial-page","OBS-Incomplete"; "rate-budget-exhausted","MOUT-RateLimited"
                  "permission-revoked","OBS-Unauthorized"; "concurrent-revision","MOUT-RevisionConflict" ] do
                let execution=byId[id]
                require (execution.Outcome="refused" && execution.RefusalCode=Some code && has "refused" execution) $"FIO-REFUSAL:%s{id}"
            if findings.Count=0 then Ok() else Error(String.concat ";" findings)
