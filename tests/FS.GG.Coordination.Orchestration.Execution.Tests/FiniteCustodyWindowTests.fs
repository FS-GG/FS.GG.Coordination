namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open Xunit
open FS.GG.Execution.Contracts
open FS.GG.Coordination.Orchestration.Execution

module FiniteCustodyWindowTests =
    let private domain = { Id = Guid.Parse "7c287080-68e9-4846-b369-cb46f37ec374"; Frequency = 1000L }
    let private at ticks = { Domain = domain; Ticks = ticks }
    let private accepted = function Ok value -> value | Error reason -> failwithf "%A" reason
    let private budget (total: float) (reserve: float) =
        Budget.create (at 1000L) (TimeSpan.FromSeconds total) (TimeSpan.FromSeconds reserve) |> accepted

    [<Fact>]
    let ``preparation spends the original clock and retains both reserves`` () =
        let original = budget 300. 15.
        let window = FiniteCustodyWindow.prepare (at 21000L) original |> accepted
        let description = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
        Assert.Equal(1000L, description.StartedAt)
        Assert.Equal(286000L, description.WorkExpiresAt)
        Assert.Equal(301000L, description.TotalExpiresAt)
        Assert.Equal(271000L, FiniteCustodyWindow.normalWorkExpiresAt window)
        Assert.False(FiniteCustodyWindow.normalWorkAllowed (at 271000L) window)

    [<Fact>]
    let ``shorter original grant clips normal work child work and cleanup`` () =
        let window = FiniteCustodyWindow.prepare (at 1000L) (budget 100. 15.) |> accepted
        let description = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
        Assert.Equal(71000L, FiniteCustodyWindow.normalWorkExpiresAt window)
        Assert.Equal(86000L, description.WorkExpiresAt)
        Assert.Equal(101000L, description.TotalExpiresAt)

    [<Fact>]
    let ``larger caller budget never widens finite profile`` () =
        let window = FiniteCustodyWindow.prepare (at 1000L) (budget 600. 15.) |> accepted
        let description = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
        Assert.Equal(301000L, description.TotalExpiresAt)
        Assert.Equal(286000L, description.WorkExpiresAt)
        Assert.Equal(271000L, FiniteCustodyWindow.normalWorkExpiresAt window)

    [<Fact>]
    let ``late preparation cannot renew normal admission`` () =
        Assert.Equal(Error BudgetExpired, FiniteCustodyWindow.prepare (at 271000L) (budget 300. 15.))

    [<Fact>]
    let ``insufficient original reserves refuse before launch`` () =
        Assert.Equal(Error InvalidBudget, FiniteCustodyWindow.prepare (at 1000L) (budget 30. 15.))

    [<Fact>]
    let ``foreign clock domain cannot restore a live grant`` () =
        let foreign = { Domain = { domain with Id = Guid.NewGuid() }; Ticks = 1000L }
        Assert.Equal(Error ClockDomainMismatch, FiniteCustodyWindow.prepare foreign (budget 300. 15.))

    [<Fact>]
    let ``source profile remains unavailable for production`` () =
        Assert.False FiniteCustodyWindow.productionAvailable
