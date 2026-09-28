module FS.GG.Coordination.Orchestration.Observer.Tests.LearningProposalTests

open System
open Xunit
open FS.GG.Coordination.Orchestration.Observer

let private sha character = String.replicate 64 character

let private source path =
    {
        Repository = "FS-GG/FS.GG.Coordination"
        Path = path
        Revision = "source-revision"
        Sha256 = sha "a"
    }

let private contextRequest =
    let plan =
        {
            PlanId = "learn-01.3-plan"
            PlanSha256 = sha "b"
            Source = source "docs/roadmaps/learn-01-context-shadow.md"
            WorkClassId = LearningContext.WorkClassId
            ContractId = LearningContext.ContractId
            ContractRevision = "contract-revision"
            AuthoritativeObligations = [ "objective" ]
            State = Reusable
        }

    let reference =
        {
            ReferenceId = "governing"
            Source = source "docs/roadmaps/learn-01-context-shadow.md"
            Class = Mandatory
            Obligations = [ "objective" ]
            EstimatedBytes = 100L
            SelectedForFocused = true
            Trust = GoverningInstruction
            ClaimsInstructionAuthority = false
            RetrievalMethod = "exact-source-reference"
            InclusionReason = "authoritative-objective"
        }

    {
        ItemId = "FS-GG/FS.GG.Coordination#learn-01.3"
        OriginalItemId = "FS-GG/FS.GG.Coordination#learn-01.3"
        Relation = Original
        Treatment =
            {
                OriginalItemId = "FS-GG/FS.GG.Coordination#learn-01.3"
                Arm = Focused
                ShadowBindingSha256 = sha "c"
            }
        InheritedTreatment = None
        Plan = Some plan
        ExpectedPlanId = plan.PlanId
        ExpectedPlanSha256 = plan.PlanSha256
        ExpectedPlanSource = plan.Source
        ExpectedContractRevision = plan.ContractRevision
        RequiredMandatoryReferenceIds = [ reference.ReferenceId ]
        RequiredAuthoritativeObligations = [ "objective" ]
        References = [ reference ]
        Capacity =
            {
                MaximumReferences = 4
                MaximumEstimatedBytes = 1_000L
                MaximumConcurrentPreviews = 2
                ActivePreviews = 0
                ReservedPreviews = 0
            }
        SyntheticShadowOnly = true
    }

let private directEvidence =
    {
        SingleRepository = true
        StableContract = true
        KnownImplementationLocation = true
        ExistingTestBoundary = true
        NoProtectedOperation = true
        NoCrossRepositoryContractChange = true
        EstimatedTouchedFiles = 2
        EvidenceReferences = [ "source-contract"; "focused-tests" ]
    }

let private request action =
    {
        ProposalId = "learn-01.3-proposal"
        ItemId = "FS-GG/FS.GG.Coordination#learn-01.3"
        OriginalItemId = "FS-GG/FS.GG.Coordination#learn-01.3"
        Action = action
        ContextRequest = None
        InvestigationQuestions = []
        Slices = []
        IntegrationContract = None
        DirectSmallRequested = false
        DirectSmallEvidence = None
        SyntheticShadowOnly = true
    }

let private proposed value =
    match LearningProposal.propose value with
    | Ok proposal -> proposal
    | Error refusal -> failwithf "unexpected refusal: %A" refusal

[<Fact>]
let ``keep reuses the valid plan and feeds the inert context compiler`` () =
    let proposal =
        { request Keep with ContextRequest = Some contextRequest }
        |> proposed

    Assert.Equal(Keep, proposal.Action)
    Assert.True(proposal.Planner.IsNone)
    Assert.True(proposal.ContextManifest.IsSome)
    Assert.Equal(LearningContext.FocusedRecipeId, proposal.ContextManifest.Value.Recipe.RecipeId)
    Assert.Equal(LearningContext.WorkerModel, proposal.Worker.Model)
    Assert.Equal(LearningContext.WorkerEffort, proposal.Worker.Effort)
    Assert.False(proposal.DirectSmallEligible)
    Assert.True(proposal.ShadowOnly)
    Assert.False(proposal.CanDispatch)
    Assert.False(proposal.CanWrite)
    Assert.False(proposal.IsAssignmentFact)

[<Fact>]
let ``create uses fixed Astra high planning unless direct-small is explicitly eligible`` () =
    let standard = request Create |> proposed
    let direct =
        { request Create with
            DirectSmallRequested = true
            DirectSmallEvidence = Some directEvidence
        }
        |> proposed

    Assert.Equal(LearningProposal.PlannerModel, standard.Planner.Value.Model)
    Assert.Equal(LearningProposal.PlannerEffort, standard.Planner.Value.Effort)
    Assert.False(standard.DirectSmallEligible)
    Assert.True(direct.DirectSmallEligible)
    Assert.True(direct.Planner.IsNone)
    Assert.Equal<string list>([ "focused-tests"; "source-contract" ], direct.DirectSmallEvidence.Value.EvidenceReferences)
    Assert.Equal(64, direct.CanonicalSha256.Length)

[<Fact>]
let ``direct-small refuses an unsupported or unbounded shortcut`` () =
    let evidence =
        { directEvidence with
            StableContract = false
            NoProtectedOperation = false
            EstimatedTouchedFiles = LearningProposal.MaximumDirectSmallFiles + 1
            EvidenceReferences = []
        }

    match
        LearningProposal.propose
            { request Create with
                DirectSmallRequested = true
                DirectSmallEvidence = Some evidence
            }
    with
    | Error(DirectSmallIneligible reasons) ->
        Assert.Contains("unstable-contract", reasons)
        Assert.Contains("protected-operation", reasons)
        Assert.Contains("touch-set-out-of-bounds", reasons)
        Assert.Contains("evidence-reference-missing", reasons)
    | other -> failwithf "unexpected result: %A" other

[<Fact>]
let ``investigate requires bounded questions and retains fixed planner profile`` () =
    match LearningProposal.propose (request Investigate) with
    | Error InvestigationEvidenceMissing -> ()
    | other -> failwithf "unexpected result: %A" other

    let proposal =
        { request Investigate with
            InvestigationQuestions = [ "Can the failure be reproduced from the accepted entry point?" ]
        }
        |> proposed

    Assert.Equal(Investigate, proposal.Action)
    Assert.Equal(LearningProposal.PlannerModel, proposal.Planner.Value.Model)
    Assert.Single(proposal.InvestigationQuestions) |> ignore
    Assert.True(proposal.ContextManifest.IsNone)

[<Fact>]
let ``decompose accepts disjoint independently deliverable slices and canonicalizes order`` () =
    let first =
        {
            SliceId = "producer"
            TouchSet = [ "src/producer.fs" ]
            IntegrationObligations = [ "contract" ]
        }

    let second =
        {
            SliceId = "consumer"
            TouchSet = [ "src/consumer.fs" ]
            IntegrationObligations = [ "contract" ]
        }

    let compile slices =
        { request Decompose with
            Slices = slices
            IntegrationContract = Some "producer-before-consumer-v1"
        }
        |> proposed

    let normal = compile [ first; second ]
    let reordered = compile [ second; first ]

    Assert.Equal(normal.CanonicalSha256, reordered.CanonicalSha256)
    Assert.Equal<string list>([ "consumer"; "producer" ], normal.Slices |> List.map _.SliceId)
    Assert.Equal(LearningProposal.PlannerModel, normal.Planner.Value.Model)
    Assert.True(normal.ContextManifest.IsNone)

[<Fact>]
let ``decompose refuses overlapping touch sets`` () =
    let slice id =
        {
            SliceId = id
            TouchSet = [ "src/shared.fs" ]
            IntegrationObligations = [ "contract" ]
        }

    match
        LearningProposal.propose
            { request Decompose with
                Slices = [ slice "producer"; slice "consumer" ]
                IntegrationContract = Some "producer-before-consumer-v1"
            }
    with
    | Error(DecompositionTouchSetOverlap "src/shared.fs") -> ()
    | other -> failwithf "unexpected result: %A" other

[<Fact>]
let ``decompose refuses nested file scopes across slices`` () =
    let slice id path =
        {
            SliceId = id
            TouchSet = [ path ]
            IntegrationObligations = [ "contract" ]
        }

    match
        LearningProposal.propose
            { request Decompose with
                Slices = [ slice "producer" "src"; slice "consumer" "src/file.fs" ]
                IntegrationContract = Some "producer-before-consumer-v1"
            }
    with
    | Error(DecompositionTouchSetOverlap "src") -> ()
    | other -> failwithf "unexpected result: %A" other

[<Theory>]
[<InlineData("src/")>]
[<InlineData("src/**")>]
[<InlineData("src/../shared.fs")>]
[<InlineData("./src/file.fs")>]
[<InlineData("src\\file.fs")>]
let ``decompose refuses noncanonical aliases directories and patterns`` path =
    let slice id selectedPath =
        {
            SliceId = id
            TouchSet = [ selectedPath ]
            IntegrationObligations = [ "contract" ]
        }

    match
        LearningProposal.propose
            { request Decompose with
                Slices = [ slice "producer" path; slice "consumer" "tests/file.fs" ]
                IntegrationContract = Some "producer-before-consumer-v1"
            }
    with
    | Error(InvalidLearningProposalInput "invalid-decomposition-slice") -> ()
    | other -> failwithf "unexpected result: %A" other

[<Fact>]
let ``actions cannot borrow evidence or authority from another proposal route`` () =
    let invalidKeep =
        { request Keep with
            ContextRequest = Some contextRequest
            InvestigationQuestions = [ "borrowed" ]
        }

    let invalidCreate =
        { request Create with ContextRequest = Some contextRequest }

    let invalidShadow =
        { request Create with SyntheticShadowOnly = false }

    for candidate in [ invalidKeep; invalidCreate; invalidShadow ] do
        match LearningProposal.propose candidate with
        | Error _ -> ()
        | Ok value -> failwithf "invalid route produced a proposal: %A" value

[<Fact>]
let ``keep propagates compiler refusal instead of creating a replacement plan`` () =
    let staleContext =
        { contextRequest with
            Plan = contextRequest.Plan |> Option.map (fun plan -> { plan with State = Stale })
        }

    match LearningProposal.propose { request Keep with ContextRequest = Some staleContext } with
    | Error(InvalidKeepContext StaleValidPlan) -> ()
    | other -> failwithf "unexpected result: %A" other

[<Fact>]
let ``keep binds the compiled manifest to the proposal identity`` () =
    match
        LearningProposal.propose
            { request Keep with
                ItemId = "FS-GG/FS.GG.Coordination#foreign"
                ContextRequest = Some contextRequest
            }
    with
    | Error(InvalidLearningProposalInput "keep-context-identity-mismatch") -> ()
    | other -> failwithf "unexpected result: %A" other
