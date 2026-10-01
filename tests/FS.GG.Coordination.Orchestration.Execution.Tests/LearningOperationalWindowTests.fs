namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Orchestration.Execution

type LearningOperationalWindowTests() =
    let assignedAt = DateTimeOffset(2026, 9, 29, 5, 0, 0, TimeSpan.Zero)
    let digest character = String.replicate 64 character

    let request enabled =
        {
            Enabled = enabled
            WindowId = "learn-01.4-window-001"
            SeedReferenceSha256 = digest "a"
            Repository = "FS-GG/.github"
            CalendarAdmissionBlock = "2026-W40"
            OriginalItemId = "LEARN-01.4-test"
            AuthorityId = "unified-roadmap-owner"
            AuthorityRevision = "71973603810563def3af098f93f33740dae83b97"
            AuthoritySha256 = digest "b"
            OptedInAt = assignedAt.AddMinutes(-10.0)
            EnrollmentOpensAt = assignedAt.AddDays(-1.0)
            EnrollmentClosesAt = assignedAt.AddDays(27.0)
        }

    let evidence =
        {
            Schema = LearningOperationalWindow.readinessSchema
            WindowId = "learn-01.4-window-001"
            Repository = "FS-GG/.github"
            WorkClassId = LearningOperationalWindow.workClassId
            OriginalItemId = "LEARN-01.4-test"
            AcceptedPlanSha256 = digest "c"
            CanonicalWorkItemSha256 = digest "d"
            CoverageRosterSha256 = digest "e"
            DispatchCensusSha256 = digest "f"
            NativeDeliverySha256 = digest "1"
            SharedCostRosterSha256 = digest "2"
            ObservedAt = assignedAt.AddMinutes(-5.0)
            ExpiresAt = assignedAt.AddHours(1.0)
            CompleteNativeUsage = true
            UnassignedSharedAllocation = true
            Provenance = "controlled-owner-readiness"
        }

    let source producer record observedAt =
        {
            ProducerId = producer
            Revision = "revision-1"
            RecordId = record
            ObservedAt = observedAt
        }

    let authoritativeRecords () =
        let key =
            {
                WindowId = "learn-01.4-window-001"
                OriginalItemId = "LEARN-01.4-test"
            }

        let members =
            [
                "LEARN-01.4-test", "root"
                "LEARN-01.4-child", "child"
                "LEARN-01.4-retry", "retry"
                "LEARN-01.4-review", "review"
                "LEARN-01.4-rescue", "rescue"
                "LEARN-01.4-repair", "repair"
            ]

        let authority =
            {
                Key = key
                Source = source "durable-scheduler" "authority-1" (assignedAt.AddMinutes -4.)
                Enabled = true
                Repository = LearningOperationalWindow.policyRepository
                CalendarAdmissionBlock = "2026-W40"
                SeedReferenceSha256 = digest "a"
                AuthorityId = "unified-roadmap-owner"
                AuthorityRevision = "revision-1"
                OptedInAt = assignedAt.AddMinutes -10.
                EnrollmentOpensAt = assignedAt.AddDays -1.
                EnrollmentClosesAt = assignedAt.AddDays 27.
                RevokedAt = None
            }

        let cohort =
            {
                Key = key
                Source = source "durable-cohort" "cohort-1" (assignedAt.AddMinutes -3.)
                AppliedAt = assignedAt.AddMinutes -6.
                AcceptedPlanSha256 = digest "c"
                CanonicalWorkItemSha256 = digest "d"
                Members =
                    members
                    |> List.map (fun (itemId, role) ->
                        {
                            ItemId = itemId
                            OriginalItemId = key.OriginalItemId
                            Role = role
                        })
            }

        let states =
            [ "prospective"; "completed"; "failed"; "cancelled"; "unfinished"; "assigned" ]

        let census =
            {
                Key = key
                Source = source "utel-native-census" "census-1" (assignedAt.AddMinutes -2.)
                InstalledCustody = Some(source "installed-collector-owner" "custody-1" (assignedAt.AddMinutes -2.))
                ProviderCapability = Some(source "installed-provider-owner" "capability-1" (assignedAt.AddMinutes -2.))
                NativeDeliveryRevision = "capture-2"
                Members =
                    List.map2
                        (fun (index, (itemId, role)) state ->
                            let actual = state <> "prospective"

                            {
                                ItemId = itemId
                                OriginalItemId = key.OriginalItemId
                                Role = role
                                State = state
                                Source = source "utel-native-census" ("input-" + itemId) (assignedAt.AddMinutes -2.)
                                NativeUsageSha256 = if actual then Some(digest "e") else None
                                SharedCostSha256 = if actual then Some(digest "f") else None
                                Execution =
                                    if not actual then
                                        None
                                    else
                                        Some
                                            {
                                                AssignmentId = Guid.Parse($"10000000-0000-0000-0000-00000000000{index + 1}")
                                                AttemptId = Guid.Parse($"20000000-0000-0000-0000-00000000000{index + 1}")
                                                Generation = 3L
                                                Phase =
                                                    match state with
                                                    | "completed"
                                                    | "cancelled"
                                                    | "failed" -> LearningOperationalExecutionPhase.Terminal
                                                    | _ -> LearningOperationalExecutionPhase.Started
                                                FirstDispatchSha256 = Some(digest "7")
                                                Source = source "execution-journal" ($"execution-{index}") (assignedAt.AddMinutes -2.)
                                            }
                            })
                        (members |> List.indexed)
                        states
            }

        key, authority, cohort, census

    let executionBinding schema qualificationOnly operational =
        let value0 =
            {
                Schema = schema
                BindingSha256 = ""
                TreatmentAssignmentSha256 = digest "3"
                TreatmentOwnerPrincipalId = "owner"
                TreatmentWorkflowRevision = "7"
                TreatmentGeneration = 3L
                TreatmentAssignedAt = assignedAt
                TreatmentProposalSha256 = digest "4"
                TreatmentContextManifestSha256 = digest "5"
                TreatmentArm = "focused"
                SubjectBindingSha256 = digest "6"
                ItemId = "LEARN-01.4-test"
                OriginalItemId = "LEARN-01.4-test"
                Relation = "original"
                ParentItemId = None
                AssignmentId = Guid.Parse "10000000-0000-0000-0000-000000000001"
                AttemptId = Guid.Parse "20000000-0000-0000-0000-000000000002"
                Generation = 3L
                ProposalSha256 = digest "7"
                ContextManifestSha256 = digest "8"
                RenderedInputSha256 = digest "9"
                Requested =
                    {
                        Model = Some "gpt-5.6-sol"
                        Effort = Some "medium"
                    }
                Deadline = assignedAt.AddHours 1.0
                MaximumRuntimeSeconds = 3600L
                MaximumAttempts = 1
                SnapshotId = "snapshot"
                SnapshotDigest = digest "a"
                SnapshotCapturedAt = assignedAt
                ManifestId = "manifest"
                ManifestVersion = "1"
                ExperimentContractId = "learn-01-current-focused-v1"
                PolicyRepository = LearningOperationalWindow.policyRepository
                PolicyRevision = LearningOperationalWindow.policyRevision
                PolicyPath = LearningOperationalWindow.policyPath
                PolicySha256 = LearningOperationalWindow.policySha256
                PolicyStatus = LearningOperationalWindow.policyStatus
                WorkClassId = LearningOperationalWindow.workClassId
                RubricVersion = "1"
                RecipeId = "unified-roadmap-focused-manifest-v1"
                RecipeDigest = digest "b"
                Arm = "focused"
                QualificationOnly = qualificationOnly
                OperationalWindow = operational
            }

        { value0 with
            BindingSha256 = LearningExecutionBinding.digest value0
        }

    [<Fact>]
    member _.``disabled default refuses before assignment``() =
        match LearningOperationalWindow.prepare assignedAt (request false) evidence with
        | Error reason -> Assert.Equal("learning-operational-window-disabled", reason)
        | Ok _ -> failwith "disabled window unexpectedly prepared"

    [<Fact>]
    member _.``authoritative composition hashes complete durable producers and permits unborn target``() =
        let key, authority, cohort, census = authoritativeRecords ()

        let first =
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                census
            |> Result.defaultWith failwith

        let second =
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                census
            |> Result.defaultWith failwith

        Assert.Equal(first, second)
        Assert.True(first.Evidence.CompleteNativeUsage)
        Assert.True(first.Evidence.UnassignedSharedAllocation)
        Assert.Equal(assignedAt.AddMinutes 6., first.Evidence.ExpiresAt)
        Assert.Equal("durable-scheduler+durable-cohort+utel-native-census", first.Evidence.Provenance)

        Assert.True(
            LearningOperationalWindow.prepare assignedAt first.Request first.Evidence
            |> Result.isOk
        )

    [<Fact>]
    member _.``assigned target is readable for the post-bind fence but cannot create an initial window``() =
        let key, authority, cohort, census = authoritativeRecords ()

        let assigned =
            { census with
                Members =
                    census.Members
                    |> List.map (fun memberValue ->
                        if memberValue.ItemId = key.OriginalItemId then
                            { memberValue with
                                State = "assigned"
                                NativeUsageSha256 = None
                                SharedCostSha256 = None
                                Execution =
                                    Some
                                        {
                                            AssignmentId = Guid.Parse "10000000-0000-0000-0000-000000000001"
                                            AttemptId = Guid.Parse "20000000-0000-0000-0000-000000000001"
                                            Generation = 3L
                                            Phase = LearningOperationalExecutionPhase.AssignedUnlaunched
                                            FirstDispatchSha256 = None
                                            Source = source "execution-journal" "assigned-root" (assignedAt.AddMinutes -1.)
                                        }
                            }
                        else
                            memberValue)
            }

        let current =
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                assigned
            |> Result.defaultWith failwith

        Assert.False(current.Evidence.UnassignedSharedAllocation)
        Assert.Equal(
            Error "learning-operational-window-readiness-incomplete",
            LearningOperationalWindow.prepare assignedAt current.Request current.Evidence
        )

    [<Fact>]
    member _.``assigned execution phase never manufactures future accounting``() =
        let key, authority, cohort, census = authoritativeRecords ()
        let root = census.Members |> List.find (fun value -> value.Role = "root")

        let execution =
            {
                AssignmentId = Guid.Parse "10000000-0000-0000-0000-000000000001"
                AttemptId = Guid.Parse "20000000-0000-0000-0000-000000000001"
                Generation = 3L
                Phase = LearningOperationalExecutionPhase.AssignedUnlaunched
                FirstDispatchSha256 = None
                Source = source "execution-journal" "assigned-root" (assignedAt.AddMinutes -1.)
            }

        let censusWith replacement =
            { census with
                Members = census.Members |> List.map (fun value -> if value.Role = "root" then replacement else value)
            }

        let compose replacement =
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                (censusWith replacement)

        let missingPhase =
            { root with State = "assigned"; Execution = None }

        let earlyCounter =
            { root with
                State = "assigned"
                Execution = Some execution
                NativeUsageSha256 = Some(digest "8")
            }

        let startedWithoutCounters =
            { root with
                State = "assigned"
                Execution =
                    Some
                        { execution with
                            Phase = LearningOperationalExecutionPhase.Started
                            FirstDispatchSha256 = Some(digest "7")
                        }
            }

        for replacement in [ missingPhase; earlyCounter; startedWithoutCounters ] do
            Assert.Equal(Error "learning-operational-readiness-accounting-unknown", compose replacement)

    [<Fact>]
    member _.``empty or partial authoritative census never certifies coverage``() =
        let key, authority, cohort, census = authoritativeRecords ()

        let empty = { census with Members = [] }

        let partial =
            { census with
                Members = census.Members |> List.tail
            }

        Assert.Equal(
            Error "learning-operational-readiness-census-refused",
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                empty
        )

        Assert.Equal(
            Error "learning-operational-readiness-census-refused",
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                partial
        )

    [<Fact>]
    member _.``missing installed custody or provider capability stays ineligible``() =
        let key, authority, cohort, census = authoritativeRecords ()

        for incomplete in
            [
                { census with InstalledCustody = None }
                { census with
                    ProviderCapability = None
                }
            ] do
            Assert.Equal(
                Error "learning-operational-readiness-census-refused",
                LearningOperationalWindow.composeAuthoritativeReadiness
                    assignedAt
                    (TimeSpan.FromMinutes 10.)
                    key
                    authority
                    cohort
                    incomplete
            )

    [<Fact>]
    member _.``revocation and missing actual counters remain unknown``() =
        let key, authority, cohort, census = authoritativeRecords ()

        let revoked =
            { authority with
                RevokedAt = Some(assignedAt.AddMinutes -1.)
            }

        let missingActual =
            { census with
                Members =
                    census.Members
                    |> List.map (fun memberValue ->
                        if memberValue.State = "failed" then
                            { memberValue with
                                NativeUsageSha256 = None
                            }
                        else
                            memberValue)
            }

        Assert.Equal(
            Error "learning-operational-readiness-authority-refused",
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                revoked
                cohort
                census
        )

        Assert.Equal(
            Error "learning-operational-readiness-accounting-unknown",
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                missingActual
        )

        Assert.Equal(
            Error "learning-operational-readiness-order-refused",
            LearningOperationalWindow.composeAuthoritativeReadiness
                (assignedAt.AddMinutes 7.)
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                census
        )

    [<Fact>]
    member _.``fixed roster must precede observation and role joins cannot drift``() =
        let key, authority, cohort, census = authoritativeRecords ()

        let late =
            { cohort with
                AppliedAt = assignedAt.AddMinutes -1.
            }

        let drift =
            { census with
                Members =
                    census.Members
                    |> List.map (fun memberValue ->
                        if memberValue.Role = "child" then
                            { memberValue with Role = "review" }
                        else
                            memberValue)
            }

        Assert.Equal(
            Error "learning-operational-readiness-roster-order-refused",
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                late
                census
        )

        Assert.Equal(
            Error "learning-operational-readiness-census-refused",
            LearningOperationalWindow.composeAuthoritativeReadiness
                assignedAt
                (TimeSpan.FromMinutes 10.)
                key
                authority
                cohort
                drift
        )

    [<Fact>]
    member _.``authoritative source reads owners and never promotes an unavailable producer``() =
        task {
            let key, authorityRecord, cohortRecord, censusRecord = authoritativeRecords ()

            let authority =
                { new ILearningOperationalAuthoritySource with
                    member _.ReadLearningOperationalAuthority(_, _) = Task.FromResult(Ok authorityRecord)
                }

            let cohort =
                { new ILearningOperationalCohortSource with
                    member _.ReadLearningOperationalCohort(_, _) = Task.FromResult(Ok cohortRecord)
                }

            let census =
                { new ILearningOperationalCensusSource with
                    member _.ReadLearningOperationalCensus(_, _) = Task.FromResult(Ok censusRecord)
                }

            let clock =
                { new TimeProvider() with
                    override _.GetUtcNow() = assignedAt
                }

            let source =
                AuthoritativeLearningOperationalReadinessSource(
                    clock,
                    TimeSpan.FromMinutes 10.,
                    authority,
                    cohort,
                    census
                )
                :> ILearningOperationalReadinessSource

            let! composed =
                source.ReadLearningOperationalReadiness(key, Threading.CancellationToken.None)

            Assert.True(composed |> Result.isOk)

            let missingCohort =
                { new ILearningOperationalCohortSource with
                    member _.ReadLearningOperationalCohort(_, _) =
                        Task.FromResult(Error "owner-unavailable")
                }

            let unavailable =
                AuthoritativeLearningOperationalReadinessSource(
                    clock,
                    TimeSpan.FromMinutes 10.,
                    authority,
                    missingCohort,
                    census
                )
                :> ILearningOperationalReadinessSource

            let! refused =
                unavailable.ReadLearningOperationalReadiness(key, Threading.CancellationToken.None)

            Assert.Equal(Error "learning-operational-readiness-cohort-unavailable", refused)
        }

    [<Fact>]
    member _.``frozen assignment is derived before treatment and binds fixed contract``() =
        let prepared =
            LearningOperationalWindow.prepare assignedAt (request true) evidence
            |> Result.defaultWith failwith

        let binding = prepared.Binding

        Assert.Equal("focused", binding.Arm)
        Assert.Equal("59e8549d0d975f0281ff6e381c05ee40d8332f3e9ba4559270d5c83ecf85a9d8", binding.AssignmentInputSha256)
        Assert.Equal(28, binding.MinimumEnrollmentDays)
        Assert.Equal(84, binding.MaximumEnrollmentDays)
        Assert.Equal(14, binding.OutcomeDeadlineDays)
        Assert.Equal(30, binding.RepairObservationDays)
        Assert.Equal(60, binding.MaximumFollowupDays)
        Assert.Equal(6908, binding.MinimumIndependentOriginalsPerArm)
        Assert.True(binding.FinalOnlyInference)
        Assert.Equal(LearningOperationalWindow.policySha256, binding.PolicySha256)
        Assert.Equal(Ok binding, LearningOperationalWindow.validate binding)

    [<Fact>]
    member _.``incomplete native coverage and unallocated shared cost refuse before draw``() =
        let missingUsage =
            { evidence with
                CompleteNativeUsage = false
            }

        let allocated =
            { evidence with
                UnassignedSharedAllocation = false
            }

        Assert.Equal(
            Error "learning-operational-window-readiness-incomplete",
            LearningOperationalWindow.prepare assignedAt (request true) missingUsage
        )

        Assert.Equal(
            Error "learning-operational-window-readiness-incomplete",
            LearningOperationalWindow.prepare assignedAt (request true) allocated
        )

    [<Fact>]
    member _.``caller arm mutation and seed drift cannot redraw``() =
        let binding =
            (LearningOperationalWindow.prepare assignedAt (request true) evidence
             |> Result.defaultWith failwith)
                .Binding

        let changedArm0 =
            { binding with
                Arm = "current"
                BindingSha256 = ""
            }

        let changedArm =
            { changedArm0 with
                BindingSha256 = LearningOperationalWindow.digest changedArm0
            }

        let changedSeed0 =
            { binding with
                SeedReferenceSha256 = digest "9"
                BindingSha256 = ""
            }

        let changedSeed =
            { changedSeed0 with
                BindingSha256 = LearningOperationalWindow.digest changedSeed0
            }

        Assert.Equal(Error "learning-operational-window-arm-refused", LearningOperationalWindow.validate changedArm)
        Assert.Equal(Error "learning-operational-window-arm-refused", LearningOperationalWindow.validate changedSeed)

    [<Fact>]
    member _.``expired eligibility and shortened enrollment window refuse``() =
        let expired = { evidence with ExpiresAt = assignedAt }

        let shortRequest =
            { request true with
                EnrollmentClosesAt = assignedAt.AddDays(20.0)
            }

        Assert.Equal(
            Error "learning-operational-window-order-refused",
            LearningOperationalWindow.prepare assignedAt (request true) expired
        )

        Assert.Equal(
            Error "learning-operational-window-fixed-contract-refused",
            LearningOperationalWindow.prepare assignedAt shortRequest evidence
        )

    [<Fact>]
    member _.``legacy binding bytes remain version one and cannot be relabeled``() =
        let legacy = executionBinding LearningExecutionBinding.schema true None
        let bytes = LearningExecutionBinding.canonicalBytes legacy
        let fields = (Text.Encoding.UTF8.GetString bytes).Split('\n')

        Assert.Equal(42, fields.Length)
        Assert.Equal(Ok legacy, LearningExecutionBinding.decode bytes)

        let relabeled0 =
            { legacy with
                QualificationOnly = false
                BindingSha256 = ""
            }

        let relabeled =
            { relabeled0 with
                BindingSha256 = LearningExecutionBinding.digest relabeled0
            }

        Assert.Equal(Error "learning-execution-binding-enrollment-refused", LearningExecutionBinding.validate relabeled)

    [<Fact>]
    member _.``operational binding is versioned and round trips the frozen window``() =
        let window =
            (LearningOperationalWindow.prepare assignedAt (request true) evidence
             |> Result.defaultWith failwith)
                .Binding

        let binding =
            executionBinding LearningExecutionBinding.operationalSchema false (Some window)

        let bytes = LearningExecutionBinding.canonicalBytes binding
        let fields = (Text.Encoding.UTF8.GetString bytes).Split('\n')

        Assert.Equal(43, fields.Length)
        Assert.Equal(Ok binding, LearningExecutionBinding.decode bytes)

        let changedWindow0 =
            { window with
                Arm = "current"
                BindingSha256 = ""
            }

        let changedWindow =
            { changedWindow0 with
                BindingSha256 = LearningOperationalWindow.digest changedWindow0
            }

        let changedBinding0 =
            { binding with
                OperationalWindow = Some changedWindow
                BindingSha256 = ""
            }

        let changedBinding =
            { changedBinding0 with
                BindingSha256 = LearningExecutionBinding.digest changedBinding0
            }

        Assert.Equal(Error "learning-operational-window-arm-refused", LearningExecutionBinding.validate changedBinding)
