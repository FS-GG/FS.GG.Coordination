module FS.GG.Coordination.Qualification.Contracts.FaultInjectionOracle

val validate:
    root: string ->
    executions: FaultInjection.Execution list ->
    Result<unit, string>
