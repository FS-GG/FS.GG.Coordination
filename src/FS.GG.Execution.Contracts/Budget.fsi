namespace FS.GG.Execution.Contracts
open System
[<RequireQualifiedAccess>]
module Budget =
    val describe: ExecutionBudget -> BudgetDescription
    val create: ClockStamp -> TimeSpan -> TimeSpan -> Result<ExecutionBudget,BudgetFailure>
    val remaining: ClockStamp -> ExecutionBudget -> Result<BudgetView,BudgetFailure>
    val clip: ClockStamp -> BudgetPhase -> TimeSpan -> ExecutionBudget -> Result<TimeSpan,BudgetFailure>
