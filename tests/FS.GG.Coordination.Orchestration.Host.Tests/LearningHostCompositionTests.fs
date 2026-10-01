module FS.GG.Coordination.Orchestration.Host.Tests.LearningHostCompositionTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.Observer

type private CountingObserverStore() =
    let mutable reads = 0

    member _.Reads = reads

    interface IObserverJournalStore with
        member _.AppendObserver(_, _) = Task.FromResult(ObserverInvalidAppend "read-only")

        member _.RecoverObserver(_, _) =
            reads <- reads + 1
            Task.FromResult(Error [ ObserverStoreUnavailable "missing-owner-record" ])

let private selected =
    {
        ObserverId = "observer-learning-owner"
        MaximumEvidenceAge = TimeSpan.FromMinutes 5.
        InstalledHostConfigPath = "/owned/telemetry-host.json"
        InstalledOwnerUid = 32768u
        InstalledExecutableOwnerUid = 32768u
        MaximumCapabilityAge = TimeSpan.FromMinutes 5.
    }

[<Fact>]
let ``disabled production composition performs no owner read`` () =
    let store = CountingObserverStore()

    let composition =
        MainHostComposition.learningOperational TimeProvider.System None store

    Assert.True(composition.IsNone)
    Assert.Equal(0, store.Reads)

[<Fact>]
let ``enabled composition retains unavailable producer gates without manufacturing readiness`` () =
    task {
        let store = CountingObserverStore()

        let composition =
            MainHostComposition.learningOperational TimeProvider.System (Some selected) store
            |> Option.defaultWith (fun () -> failwith "composition missing")

        Assert.Equal(
            [
                "learning-installed-origin-receipt-producer-unavailable"
                "learning-native-work-item-window-route-binding-unavailable"
            ],
            composition.UnavailableProducerFacts
        )

        let! result =
            composition.Readiness.ReadLearningOperationalReadiness(
                { WindowId = "window-1"; OriginalItemId = "original-1" },
                CancellationToken.None
            )

        Assert.Equal(Error "learning-operational-readiness-authority-unavailable", result)
        Assert.Equal(1, store.Reads)
    }
