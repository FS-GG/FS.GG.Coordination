namespace FS.GG.Execution.Contracts

[<RequireQualifiedAccess>]
module Validation =
    val limits: ContractLimits
    val identity: OperationIdentity -> ValidationError list
    val diagnostic: Diagnostic -> ValidationError list
    val sanitizeDiagnostic: CommandSpec -> Diagnostic -> Diagnostic
    val observation: Observation<'T> -> ValidationError list
    val generation: ProcessGeneration -> ValidationError list
    val capture: CapturePolicy -> ValidationError list
    val command: CommandSpec -> ValidationError list
    val cleanup: CleanupResult -> ValidationError list
    val output: CapturePolicy -> OutputSnapshot -> ValidationError list
    val capability: CapabilityObservation -> ValidationError list
    val obtainedLimit: ResourceLimits -> ObtainedLimit -> ValidationError list
