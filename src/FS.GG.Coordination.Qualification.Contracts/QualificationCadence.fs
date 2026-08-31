module FS.GG.Coordination.Qualification.Contracts.QualificationCadence

open System
open System.IO
open System.Text.Json

type Outcome = Passed | ActionableDefect | InfrastructureFailure | UnattributedFailure
type Boundary = Child | Closure | Production
type RecommendationKind = Retain | Increase | Reduce | InsufficientData

type Observation =
    { Gate: string
      RunId: int64
      Attempt: int
      ObservedAt: DateTimeOffset
      DurationSeconds: int
      RunnerMinutes: decimal
      Reused: bool
      Outcome: Outcome
      Boundary: Boundary
      ClosureEquivalent: bool
      DetectionDelayHours: decimal option }

type Policy =
    { Version: string
      WindowDays: int
      FreshnessHours: int
      MinimumObservations: int
      ExpensiveRunnerMinutes: decimal
      LowYieldMaximum: decimal
      MinimumCadence: Map<string, string> }

type Recommendation =
    { Gate: string
      Kind: RecommendationKind
      ReasonCodes: string list
      ObservationCount: int
      UniqueDefectCount: int
      RunnerMinutes: decimal
      ExpectedDetectionDelayHours: decimal option
      Confidence: string
      PolicyVersion: string }

let evaluate (now: DateTimeOffset) (policy: Policy) (gate: string) (observations: Observation list) =
    if String.IsNullOrWhiteSpace gate then invalidArg (nameof gate) "gate is required"
    if policy.WindowDays < 1 || policy.FreshnessHours < 1 || policy.MinimumObservations < 1 then invalidArg (nameof policy) "cadence policy bounds must be positive"
    let cutoff = now.AddDays(-float policy.WindowDays)
    let sample = observations |> List.filter (fun value -> value.Gate = gate && value.ObservedAt >= cutoff && value.ObservedAt <= now)
    let total = sample |> List.sumBy (fun (value: Observation) -> value.RunnerMinutes)
    let defects = sample |> List.filter (fun (value: Observation) -> value.Outcome = ActionableDefect)
    let closureMiss = defects |> List.exists (fun value -> value.Boundary = Closure || value.Boundary = Production)
    let unattributed = sample |> List.exists (fun value -> value.Outcome = UnattributedFailure)
    let fresh = sample |> List.exists (fun value -> value.ObservedAt >= now.AddHours(-float policy.FreshnessHours))
    let equivalent = not sample.IsEmpty && sample |> List.forall _.ClosureEquivalent
    let minimum = policy.MinimumCadence |> Map.tryFind gate
    let average = if sample.IsEmpty then 0m else total / decimal sample.Length
    let yieldRate = if sample.IsEmpty then 0m else decimal defects.Length / decimal sample.Length
    let delays = sample |> List.choose _.DetectionDelayHours
    let expectedDelay = if delays.IsEmpty then None else Some(delays |> List.average)
    let kind, reasons, confidence =
        if sample.Length < policy.MinimumObservations || not fresh then
            InsufficientData, [ if not fresh then "telemetry-stale" else "sample-below-minimum" ], "insufficient"
        elif closureMiss then Increase, [ "closure-discovered-miss" ], "high"
        elif unattributed then Retain, [ "failure-unattributed" ], "low"
        elif minimum.IsSome then Retain, [ "minimum-cadence-protected" ], "high"
        elif yieldRate > policy.LowYieldMaximum then Increase, [ "unique-defect-yield-high" ], "high"
        elif not equivalent then Retain, [ "closure-equivalence-unproven" ], "low"
        elif average >= policy.ExpensiveRunnerMinutes && yieldRate <= policy.LowYieldMaximum then
            Reduce, [ "cost-high"; "unique-defect-yield-low"; "closure-equivalent" ], "moderate"
        else Retain, [ "current-cadence-supported" ], "moderate"
    { Gate = gate; Kind = kind; ReasonCodes = reasons; ObservationCount = sample.Length; UniqueDefectCount = defects.Length
      RunnerMinutes = total; ExpectedDetectionDelayHours = expectedDelay; Confidence = confidence; PolicyVersion = policy.Version }

let recommendationBytes value =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartObject()
    writer.WriteString("schema", "fsgg.coordination.qualification-cadence-recommendation/1")
    writer.WriteString("gate", value.Gate)
    writer.WriteString("recommendation", match value.Kind with Retain -> "retain" | Increase -> "increase" | Reduce -> "reduce" | InsufficientData -> "insufficient-data")
    writer.WriteStartArray("reasonCodes")
    for reason in value.ReasonCodes do writer.WriteStringValue reason
    writer.WriteEndArray()
    writer.WriteNumber("observationCount", value.ObservationCount)
    writer.WriteNumber("uniqueDefectCount", value.UniqueDefectCount)
    writer.WriteNumber("runnerMinutes", value.RunnerMinutes)
    match value.ExpectedDetectionDelayHours with Some delay -> writer.WriteNumber("expectedDetectionDelayHours", delay) | None -> writer.WriteNull("expectedDetectionDelayHours")
    writer.WriteString("confidence", value.Confidence)
    writer.WriteString("policyVersion", value.PolicyVersion)
    writer.WriteEndObject()
    writer.Flush()
    Array.append (stream.ToArray()) [| byte '\n' |]
