module FS.GG.Coordination.Orchestration.Observer.Tests.LearningContextTests

open System
open Xunit
open FS.GG.Coordination.Orchestration.Observer

let private sha character = String.replicate 64 character

let private source path revision character =
    {
        Repository = "FS-GG/.github"
        Path = path
        Revision = revision
        Sha256 = sha character
    }

let private planSource = source "docs/roadmaps/learn-01.md" "plan-revision" "a"

let private reference id classValue obligations bytes selected trust =
    {
        ReferenceId = id
        Source = source ($"references/{id}.md") "source-revision" (string (id.Length % 10))
        Class = classValue
        Obligations = obligations
        EstimatedBytes = bytes
        SelectedForFocused = selected
        Trust = trust
        ClaimsInstructionAuthority = false
        RetrievalMethod = "exact-source-reference"
        InclusionReason = "applicable-to-valid-plan"
    }

let private plan =
    {
        PlanId = "learn-01.3-plan"
        PlanSha256 = sha "b"
        Source = planSource
        WorkClassId = LearningContext.WorkClassId
        ContractId = LearningContext.ContractId
        ContractRevision = "contract-revision"
        AuthoritativeObligations = [ "objective"; "authority"; "acceptance" ]
        State = Reusable
    }

let private treatment arm =
    {
        OriginalItemId = "FS-GG/FS.GG.Coordination#learn-01.3"
        Arm = arm
        ShadowBindingSha256 = sha "c"
    }

let private references =
    [
        reference "governing" Mandatory [ "objective"; "authority" ] 100L true GoverningInstruction
        reference "acceptance" Mandatory [ "acceptance" ] 80L true AuthoritativePlan
        reference "focused-source" Optional [] 40L true UntrustedData
        reference "history" Optional [] 60L false UntrustedData
    ]

let private request arm =
    {
        ItemId = "FS-GG/FS.GG.Coordination#learn-01.3"
        OriginalItemId = "FS-GG/FS.GG.Coordination#learn-01.3"
        Relation = Original
        Treatment = treatment arm
        InheritedTreatment = None
        Plan = Some plan
        ExpectedPlanId = plan.PlanId
        ExpectedPlanSha256 = plan.PlanSha256
        ExpectedPlanSource = plan.Source
        ExpectedContractRevision = plan.ContractRevision
        RequiredMandatoryReferenceIds = [ "governing"; "acceptance" ]
        RequiredAuthoritativeObligations = [ "objective"; "authority"; "acceptance" ]
        References = references
        Capacity =
            {
                MaximumReferences = 8
                MaximumEstimatedBytes = 1_000L
                MaximumConcurrentPreviews = 2
                ActivePreviews = 0
                ReservedPreviews = 0
            }
        SyntheticShadowOnly = true
    }

let private compiled requestValue =
    match LearningContext.compile requestValue with
    | Ok value -> value
    | Error error -> failwithf "unexpected refusal: %A" error

[<Fact>]
let ``current and focused compile fixed Sol medium inert shadow recipes`` () =
    let current = request Current |> compiled
    let focused = request Focused |> compiled

    Assert.Equal(LearningContext.CurrentRecipeId, current.Recipe.RecipeId)
    Assert.Equal(LearningContext.FocusedRecipeId, focused.Recipe.RecipeId)
    Assert.Equal(LearningContext.WorkerModel, current.Recipe.Model)
    Assert.Equal(LearningContext.WorkerEffort, current.Recipe.Effort)
    Assert.Equal(2, current.OptionalReferences.Length)
    Assert.Single(focused.OptionalReferences) |> ignore
    Assert.Single(focused.OmittedOptionalReferences) |> ignore
    Assert.Equal(280L, current.EstimatedBytes)
    Assert.Equal(220L, focused.EstimatedBytes)

    for preview in [ current; focused ] do
        Assert.True(preview.ShadowOnly)
        Assert.False(preview.CanDispatch)
        Assert.False(preview.CanWrite)
        Assert.False(preview.IsAssignmentFact)
        Assert.Equal(1_000L, preview.Bounds.MaximumEstimatedBytes)
        Assert.Equal(64, preview.CanonicalSha256.Length)

[<Fact>]
let ``manifest digest is canonical across caller reference and obligation ordering`` () =
    let baseline = request Focused |> compiled

    let reordered =
        { request Focused with
            References =
                references
                |> List.rev
                |> List.map (fun value ->
                    { value with
                        Source =
                            { value.Source with
                                Sha256 = value.Source.Sha256.ToUpperInvariant()
                            }
                        Obligations = List.rev value.Obligations
                    })
            Plan =
                Some
                    { plan with
                        AuthoritativeObligations = List.rev plan.AuthoritativeObligations
                    }
            RequiredAuthoritativeObligations = (request Focused).RequiredAuthoritativeObligations |> List.rev
        }
        |> compiled

    Assert.Equal(baseline.CanonicalSha256, reordered.CanonicalSha256)
    Assert.Equal<LearningContextReference list>(baseline.MandatoryReferences, reordered.MandatoryReferences)

    let changedBounds =
        { request Focused with
            Capacity =
                { (request Focused).Capacity with
                    MaximumEstimatedBytes = 999L
                }
        }
        |> compiled

    Assert.NotEqual<string>(baseline.CanonicalSha256, changedBounds.CanonicalSha256)

    let changedAuthority =
        { request Focused with
            RequiredAuthoritativeObligations = "maintenance" :: (request Focused).RequiredAuthoritativeObligations
            Plan =
                Some
                    { plan with
                        AuthoritativeObligations = "maintenance" :: plan.AuthoritativeObligations
                    }
            References =
                references
                |> List.map (fun value ->
                    if value.ReferenceId = "governing" then
                        { value with
                            Obligations = "maintenance" :: value.Obligations
                        }
                    else
                        value)
        }
        |> compiled

    Assert.NotEqual<string>(baseline.CanonicalSha256, changedAuthority.CanonicalSha256)

[<Fact>]
let ``missing stale incompatible or source-moved plans refuse`` () =
    Assert.Equal(Error MissingValidPlan, LearningContext.compile { request Current with Plan = None })

    Assert.Equal(
        Error StaleValidPlan,
        LearningContext.compile
            { request Current with
                Plan = Some { plan with State = Stale }
            }
    )

    Assert.Equal(
        Error IncompatibleValidPlan,
        LearningContext.compile
            { request Current with
                Plan = Some { plan with State = Incompatible }
            }
    )

    Assert.Equal(
        Error StalePlanSource,
        LearningContext.compile
            { request Current with
                ExpectedPlanSource = { plan.Source with Revision = "moved" }
            }
    )

[<Fact>]
let ``mandatory reference and authoritative obligation coverage cannot be reduced`` () =
    Assert.Equal(
        Error AuthoritativeObligationSetMismatch,
        LearningContext.compile
            { request Focused with
                Plan =
                    Some
                        { plan with
                            AuthoritativeObligations = [ "objective"; "authority" ]
                        }
            }
    )

    Assert.Equal(
        Error AuthoritativeObligationSetMismatch,
        LearningContext.compile
            { request Focused with
                Plan =
                    Some
                        { plan with
                            AuthoritativeObligations = "extra" :: plan.AuthoritativeObligations
                        }
            }
    )

    Assert.Equal(
        Error(InvalidLearningContextInput "invalid-or-non-synthetic-shadow-request"),
        LearningContext.compile
            { request Focused with
                RequiredAuthoritativeObligations = []
            }
    )

    Assert.Equal(
        Error(InvalidLearningContextInput "invalid-or-non-synthetic-shadow-request"),
        LearningContext.compile
            { request Focused with
                RequiredAuthoritativeObligations = [ "objective"; "objective" ]
            }
    )

    Assert.Equal(
        Error(MissingMandatoryReference "acceptance"),
        LearningContext.compile
            { request Focused with
                References = references |> List.filter (fun value -> value.ReferenceId <> "acceptance")
            }
    )

    let uncovered =
        references
        |> List.map (fun value ->
            if value.ReferenceId = "acceptance" then
                { value with Obligations = [] }
            else
                value)

    Assert.Equal(
        Error(MissingAuthoritativeObligation "acceptance"),
        LearningContext.compile
            { request Focused with
                References = uncovered
            }
    )

[<Fact>]
let ``untrusted reference cannot inject instruction authority`` () =
    let injected =
        references
        |> List.map (fun value ->
            if value.ReferenceId = "focused-source" then
                { value with
                    ClaimsInstructionAuthority = true
                }
            else
                value)

    Assert.Equal(
        Error(UntrustedInstructionInjection "focused-source"),
        LearningContext.compile
            { request Focused with
                References = injected
            }
    )

[<Fact>]
let ``size reference and concurrent capacity limits refuse before preview`` () =
    Assert.Equal(
        Error(OversizedContext(280L, 200L)),
        LearningContext.compile
            { request Current with
                Capacity =
                    { (request Current).Capacity with
                        MaximumEstimatedBytes = 200L
                    }
            }
    )

    Assert.Equal(
        Error ContextCapacityConflict,
        LearningContext.compile
            { request Current with
                Capacity =
                    { (request Current).Capacity with
                        MaximumReferences = 3
                    }
            }
    )

    Assert.Equal(
        Error ContextCapacityConflict,
        LearningContext.compile
            { request Current with
                Capacity =
                    { (request Current).Capacity with
                        ActivePreviews = 1
                        ReservedPreviews = 1
                    }
            }
    )

[<Fact>]
let ``descendants and retries must inherit the exact original treatment`` () =
    let inherited = treatment Focused

    let descendant =
        { request Focused with
            ItemId = "FS-GG/FS.GG.Coordination#child"
            Relation = Descendant "FS-GG/FS.GG.Coordination#learn-01.3"
            InheritedTreatment = Some inherited
        }
        |> compiled

    let retry =
        { request Focused with
            ItemId = "FS-GG/FS.GG.Coordination#retry-2"
            Relation = Retry "FS-GG/FS.GG.Coordination#learn-01.3"
            InheritedTreatment = Some inherited
        }
        |> compiled

    Assert.Equal(Focused, descendant.Arm)
    Assert.Equal(inherited.ShadowBindingSha256, retry.TreatmentBindingSha256)

    let redrawn = { inherited with Arm = Current }

    Assert.Equal(
        Error TreatmentInheritanceConflict,
        LearningContext.compile
            { request Focused with
                ItemId = "FS-GG/FS.GG.Coordination#child"
                Relation = Descendant "FS-GG/FS.GG.Coordination#learn-01.3"
                InheritedTreatment = Some redrawn
            }
    )

[<Fact>]
let ``non synthetic input cannot cross the source shadow boundary`` () =
    Assert.Equal(
        Error(InvalidLearningContextInput "invalid-or-non-synthetic-shadow-request"),
        LearningContext.compile
            { request Current with
                SyntheticShadowOnly = false
            }
    )
