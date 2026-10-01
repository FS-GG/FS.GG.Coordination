module FS.GG.Coordination.Orchestration.Host.Tests.LearningJournalReadinessSourceTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.Observer

let private sha c = String.replicate 64 c
let private key = { WindowId = "window-1"; OriginalItemId = "original" }
let private at = DateTimeOffset.Parse("2026-10-01T10:00:00Z")
let private window =
    { WindowId = key.WindowId; OriginalItemId = key.OriginalItemId; Repository = "FS-GG/FS.GG.Coordination"
      WorkClassId = "learn-01-current-focused"; CalendarAdmissionBlock = "block-a"; SeedReferenceSha256 = sha "1"
      SharedAllocationRosterReference = "allocation-1"; SharedAllocationRosterSha256 = sha "2"
      AcceptedPlanSha256 = sha "3"; CanonicalWorkItemSha256 = sha "4"; CoverageRosterSha256 = sha "5"
      SourceObservationSha256 = sha "6"; WorkflowRevision = Id.revision 7L; Generation = Id.generation 3L
      AuthorityId = "scheduler-owner"; AuthorityRevision = "7"; AuthoritySha256 = sha "7"
      Members =
        [ LearningOperationalOwnerRole.Root; LearningOperationalOwnerRole.Child; LearningOperationalOwnerRole.Retry
          LearningOperationalOwnerRole.Review; LearningOperationalOwnerRole.Rescue; LearningOperationalOwnerRole.Repair ]
        |> List.mapi (fun index role ->
            { ItemId = (if index = 0 then "original" else $"member-{index}")
              OriginalItemId = "original"
              CanonicalWorkItemId = $"canonical-{index}"; Role = role })
      OptedInAt = at.AddHours -2.; EnrollmentOpensAt = at.AddHours -1.; EnrollmentClosesAt = at.AddHours 1.
      AdmittedAt = at.AddMinutes -5.; RecordSha256 = sha "8" }

let private stored sequence eventValue recorded =
    let eventId = if sequence = 1L then Guid.Parse("10000000-0000-0000-0000-000000000001") else Guid.Parse("20000000-0000-0000-0000-000000000001")
    { ObserverId = "observer-session-v1-owner"; Sequence = sequence; EventId = eventId
      SchemaVersion = ObserverEventCodec.schemaVersion; SerializerVersion = ObserverEventCodec.serializerVersion
      Event = eventValue; RecordedAt = recorded }

type private Store(events: ObserverStoredEvent list) =
    let mutable reads = 0
    member _.Reads = reads
    interface IObserverJournalStore with
        member _.AppendObserver(_, _) = Task.FromResult(ObserverInvalidAppend "read-only-fixture")
        member _.RecoverObserver(observerId, _) =
            reads <- reads + 1
            if observerId <> "observer-session-v1-owner" then Task.FromResult(Error [ ObserverStoreUnavailable "wrong-stream" ])
            else Task.FromResult(Ok { Events = events; State = events |> List.map _.Event |> Observer.replay })

[<Fact>]
let ``reader maps recovered owner event with exact source revision and all roles`` () = task {
    let store = Store [ stored 1L (LearningOperationalOwnerWindowAdmitted window) at ]
    let source = LearningJournalReadinessSource(store, "observer-session-v1-owner")
    let! authority = (source :> ILearningOperationalAuthoritySource).ReadLearningOperationalAuthority(key, CancellationToken.None)
    let! cohort = (source :> ILearningOperationalCohortSource).ReadLearningOperationalCohort(key, CancellationToken.None)
    let authority = authority |> Result.defaultWith failwith
    let cohort = cohort |> Result.defaultWith failwith
    Assert.True(authority.Enabled); Assert.True(Option.isNone authority.RevokedAt)
    Assert.Equal("observer-session-v1-owner:1", authority.Source.Revision)
    Assert.True([ "child"; "repair"; "rescue"; "retry"; "review"; "root" ] = (cohort.Members |> List.map _.Role |> List.sort))
    Assert.Equal(window.AcceptedPlanSha256, cohort.AcceptedPlanSha256)
    Assert.Equal(window.CanonicalWorkItemSha256, cohort.CanonicalWorkItemSha256)
    Assert.Equal(2, store.Reads)
}

[<Fact>]
let ``revocation advances authority source but does not rewrite cohort`` () = task {
    let revocation =
        { WindowId = key.WindowId; OriginalItemId = key.OriginalItemId; AuthorityId = window.AuthorityId
          Reason = "operator-stop"; RevokedAt = at; RevocationSha256 = sha "9" }
    let events = [ stored 1L (LearningOperationalOwnerWindowAdmitted window) (at.AddMinutes -1.); stored 2L (LearningOperationalOwnerWindowRevoked revocation) at ]
    let source = LearningJournalReadinessSource(Store events, "observer-session-v1-owner")
    let! authority = (source :> ILearningOperationalAuthoritySource).ReadLearningOperationalAuthority(key, CancellationToken.None)
    let! cohort = (source :> ILearningOperationalCohortSource).ReadLearningOperationalCohort(key, CancellationToken.None)
    Assert.Equal(Some at, (authority |> Result.defaultWith failwith).RevokedAt)
    Assert.Equal("observer-session-v1-owner:2", (authority |> Result.defaultWith failwith).Source.Revision)
    Assert.Equal("observer-session-v1-owner:1", (cohort |> Result.defaultWith failwith).Source.Revision)
}

[<Fact>]
let ``missing duplicate and corrupt recovery stay unavailable`` () = task {
    let cases = [ []; [ stored 1L (LearningOperationalOwnerWindowAdmitted window) at; stored 2L (LearningOperationalOwnerWindowAdmitted window) at ] ]
    for events in cases do
        let source = LearningJournalReadinessSource(Store events, "observer-session-v1-owner")
        let! result = (source :> ILearningOperationalCohortSource).ReadLearningOperationalCohort(key, CancellationToken.None)
        Assert.True(Result.isError result)
}
