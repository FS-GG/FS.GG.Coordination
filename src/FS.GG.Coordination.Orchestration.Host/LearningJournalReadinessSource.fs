namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Observer

[<RequireQualifiedAccess>]
module private LearningJournalReadinessMapping =
    let role =
        function
        | LearningOperationalOwnerRole.Root -> "root"
        | LearningOperationalOwnerRole.Child -> "child"
        | LearningOperationalOwnerRole.Retry -> "retry"
        | LearningOperationalOwnerRole.Review -> "review"
        | LearningOperationalOwnerRole.Rescue -> "rescue"
        | LearningOperationalOwnerRole.Repair -> "repair"

    let identity (eventValue: ObserverStoredEvent) =
        {
            ProducerId = "observer-journal"
            Revision = $"{eventValue.ObserverId}:{eventValue.Sequence}"
            RecordId = eventValue.EventId.ToString("N")
            ObservedAt = eventValue.RecordedAt
        }

    let keyMatches (key: LearningOperationalWindowKey) (window: DurableLearningOperationalOwnerWindow) =
        window.WindowId = key.WindowId && window.OriginalItemId = key.OriginalItemId

    let recover
        (store: IObserverJournalStore)
        observerId
        (key: LearningOperationalWindowKey)
        (token: CancellationToken)
        =
        task {
            let! result = store.RecoverObserver(observerId, token)

            match result with
            | Error _ -> return Error "learning-journal-recovery-unavailable"
            | Ok recovery ->
                let admissions =
                    recovery.Events
                    |> List.choose (fun eventValue ->
                        match eventValue.Event with
                        | LearningOperationalOwnerWindowAdmitted window when keyMatches key window ->
                            Some(eventValue, window)
                        | _ -> None)

                let revocations =
                    recovery.Events
                    |> List.choose (fun eventValue ->
                        match eventValue.Event with
                        | LearningOperationalOwnerWindowRevoked revocation when
                            revocation.WindowId = key.WindowId
                            && revocation.OriginalItemId = key.OriginalItemId
                            ->
                            Some(eventValue, revocation)
                        | _ -> None)

                match admissions, revocations with
                | [ admission ], [] -> return Ok(admission, None)
                | [ admission ], [ revocation ] when snd revocation |> _.RevokedAt >= (snd admission).AdmittedAt ->
                    return Ok(admission, Some revocation)
                | [], _ -> return Error "learning-journal-window-missing"
                | _ -> return Error "learning-journal-window-conflict"
        }

/// Read-only adapter over the existing validated Observer journal. It creates no
/// authority and never consults the post-readiness operational-window table.
type LearningJournalReadinessSource(store: IObserverJournalStore, observerId: string) =
    do
        if String.IsNullOrWhiteSpace observerId then
            invalidArg (nameof observerId) "observer journal identity is required"

    interface ILearningOperationalAuthoritySource with
        member _.ReadLearningOperationalAuthority(key, token) =
            task {
                let! recovered = LearningJournalReadinessMapping.recover store observerId key token

                return
                    recovered
                    |> Result.map (fun ((admissionEvent, window), revocation) ->
                        let sourceEvent = revocation |> Option.map fst |> Option.defaultValue admissionEvent

                        {
                            Key = key
                            Source = LearningJournalReadinessMapping.identity sourceEvent
                            Enabled = true
                            Repository = window.Repository
                            CalendarAdmissionBlock = window.CalendarAdmissionBlock
                            SeedReferenceSha256 = window.SeedReferenceSha256
                            AuthorityId = window.AuthorityId
                            AuthorityRevision = window.AuthorityRevision
                            OptedInAt = window.OptedInAt
                            EnrollmentOpensAt = window.EnrollmentOpensAt
                            EnrollmentClosesAt = window.EnrollmentClosesAt
                            RevokedAt = revocation |> Option.map (snd >> _.RevokedAt)
                        })
            }

    interface ILearningOperationalCohortSource with
        member _.ReadLearningOperationalCohort(key, token) =
            task {
                let! recovered = LearningJournalReadinessMapping.recover store observerId key token

                return
                    recovered
                    |> Result.map (fun ((eventValue, window), _revocation) ->
                        {
                            Key = key
                            Source = LearningJournalReadinessMapping.identity eventValue
                            AppliedAt = window.AdmittedAt
                            AcceptedPlanSha256 = window.AcceptedPlanSha256
                            CanonicalWorkItemSha256 = window.CanonicalWorkItemSha256
                            Members =
                                window.Members
                                |> List.map (fun memberValue ->
                                    {
                                        ItemId = memberValue.ItemId
                                        OriginalItemId = memberValue.OriginalItemId
                                        Role = LearningJournalReadinessMapping.role memberValue.Role
                                    })
                        })
            }

    interface ILearningOperationalRosterIdentitySource with
        member _.ReadLearningOperationalRosterIdentity(key, token) =
            task {
                let! recovered = LearningJournalReadinessMapping.recover store observerId key token

                return
                    recovered
                    |> Result.map (fun ((eventValue, window), _revocation) ->
                        {
                            Key = key
                            Source = LearningJournalReadinessMapping.identity eventValue
                            AdmittedAt = window.AdmittedAt
                            AcceptedPlanSha256 = window.AcceptedPlanSha256
                            CanonicalWorkItemSha256 = window.CanonicalWorkItemSha256
                            Members =
                                window.Members
                                |> List.map (fun memberValue ->
                                    {
                                        ItemId = memberValue.ItemId
                                        OriginalItemId = memberValue.OriginalItemId
                                        Role = LearningJournalReadinessMapping.role memberValue.Role
                                    })
                        })
            }
