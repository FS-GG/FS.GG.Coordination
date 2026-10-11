namespace FS.GG.Execution.Contracts
open System
[<RequireQualifiedAccess>]
module Budget =
    let describe (budget: ExecutionBudget): BudgetDescription =
        { Domain = budget.Domain; StartedAt = budget.StartedAt; WorkExpiresAt = budget.WorkExpiresAt; TotalExpiresAt = budget.TotalExpiresAt }
    let create (start: ClockStamp) (total: TimeSpan) (reserve: TimeSpan): Result<ExecutionBudget,BudgetFailure> =
        if start.Domain.Id = Guid.Empty || start.Domain.Frequency <= 0L || start.Ticks < 0L || total <= TimeSpan.Zero || reserve < TimeSpan.Zero || reserve >= total then Error InvalidBudget
        else
            // BigInteger prevents intermediate multiplication wrap; floor cannot extend expiry.
            let clockTicks (duration: TimeSpan) = bigint duration.Ticks * bigint start.Domain.Frequency / bigint TimeSpan.TicksPerSecond
            let final = bigint start.Ticks + clockTicks total
            let work = bigint start.Ticks + clockTicks (total - reserve)
            if final > bigint Int64.MaxValue || final <= bigint start.Ticks || work <= bigint start.Ticks then Error InvalidBudget
            else Ok { Domain = start.Domain; StartedAt = start.Ticks; WorkExpiresAt = int64 work; TotalExpiresAt = int64 final }
    let remaining (now: ClockStamp) (budget: ExecutionBudget) =
        if now.Domain <> budget.Domain then Error ClockDomainMismatch
        elif now.Ticks < budget.StartedAt then Error ClockMovedBackwards
        else
            let duration expiry =
                let ticks = bigint (max 0L (expiry - now.Ticks)) * bigint TimeSpan.TicksPerSecond / bigint budget.Domain.Frequency
                TimeSpan.FromTicks(int64 (min ticks (bigint Int64.MaxValue)))
            Ok { WorkRemaining = duration budget.WorkExpiresAt; TotalRemaining = duration budget.TotalExpiresAt
                 WorkExpired = now.Ticks >= budget.WorkExpiresAt; TotalExpired = now.Ticks >= budget.TotalExpiresAt }
    let clip now phase maximum budget =
        if maximum < TimeSpan.Zero then Error InvalidBudget
        else
            remaining now budget |> Result.bind (fun view ->
                let available = match phase with Work -> view.WorkRemaining | Cleanup -> view.TotalRemaining
                if available <= TimeSpan.Zero then Error BudgetExpired else Ok(min available maximum))
