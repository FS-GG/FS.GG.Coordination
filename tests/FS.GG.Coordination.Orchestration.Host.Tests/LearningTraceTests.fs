module FS.GG.Coordination.Orchestration.Host.Tests.LearningTraceTests

open Xunit

[<Fact>]
let ``canonical learning traces retain bounded treatment and launch semantics`` () =
    let traces = LearningTrace.loadAll ()
    Assert.Equal(11, traces.Length)

    let trace id =
        traces |> List.find (fun trace -> trace.Id = id)

    let keep = trace "testKeepLostResponseRestartUnknown"
    Assert.Equal(18, keep.States.Length)
    Assert.All(keep.States, fun state -> Assert.Empty(LearningTrace.safety state))
    let terminal = keep.States |> List.last
    Assert.Equal(1, terminal.Disposition)
    Assert.Equal(1, terminal.PreparationVersion)
    Assert.False(terminal.PlannerPresent)
    Assert.True(terminal.ContextPresent)
    Assert.True(terminal.ReplayAccepted)
    Assert.True(terminal.Restarted)
    Assert.True(terminal.DuplicateRejected)
    Assert.True(terminal.StaleRejected)
    Assert.True(terminal.CapabilityRejected)
    Assert.True(terminal.ExecutionReplayAccepted)
    Assert.True(terminal.CapacityRejected)
    Assert.True(terminal.BudgetRejected)
    Assert.True(terminal.ShadowAttempted)
    Assert.Equal(0, terminal.ShadowEffectCount)
    Assert.True(terminal.OutcomeUnknown)
    Assert.Equal(1, terminal.LaunchCount)
    Assert.Equal(0, terminal.BudgetRemaining)
    Assert.Equal(None, LearningTrace.firstDivergence (trace "testPlannedDisposition"))
    Assert.Equal(None, LearningTrace.firstDivergence (trace "testDirectSmallDisposition"))

[<Fact>]
let ``independent oracle catches production guard mutations at first divergence`` () =
    let traces = LearningTrace.loadAll ()

    let trace id =
        traces |> List.find (fun trace -> trace.Id = id)

    Assert.Equal(Some 1, LearningTrace.firstDivergence (trace "testLaunchWithoutTreatmentMutationFails"))
    Assert.Equal(Some 4, LearningTrace.firstDivergence (trace "testExecutionTreatmentMutationFails"))
    Assert.Equal(Some 1, LearningTrace.firstDivergence (trace "testRawReuseValidPlanMutationFails"))
    Assert.Equal(Some 3, LearningTrace.firstDivergence (trace "testChangedExecutionDuplicateMutationFails"))
    Assert.Equal(Some 2, LearningTrace.firstDivergence (trace "testExecutionSelfBindingMutationFails"))

    let rootExecution = trace "testRootExecutionIsValid"
    Assert.Equal(None, LearningTrace.firstDivergence rootExecution)
    Assert.False((rootExecution.States |> List.last).ChildBound)

    let identicalContextChild = trace "testIdenticalContextChildIsValid"
    Assert.Equal(None, LearningTrace.firstDivergence identicalContextChild)
    let identicalTerminal = identicalContextChild.States |> List.last
    Assert.Equal(identicalTerminal.RootManifest, identicalTerminal.ChildManifest)
    Assert.NotEqual(identicalTerminal.RootBindingIdentity, identicalTerminal.ChildBindingIdentity)
