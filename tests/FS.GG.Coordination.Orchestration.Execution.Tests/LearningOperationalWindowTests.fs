namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
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
