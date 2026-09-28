namespace FS.GG.Coordination.Orchestration.Observer

open System
open System.IO
open System.Security.Cryptography
open System.Text

type LearningContextArm =
    | Current
    | Focused

type LearningContextRelation =
    | Original
    | Descendant of parentItemId: string
    | Retry of priorItemId: string

type LearningContextReferenceClass =
    | Mandatory
    | Optional

type LearningContextTrust =
    | GoverningInstruction
    | AuthoritativePlan
    | UntrustedData

type LearningContextSource =
    {
        Repository: string
        Path: string
        Revision: string
        Sha256: string
    }

type LearningContextReference =
    {
        ReferenceId: string
        Source: LearningContextSource
        Class: LearningContextReferenceClass
        Obligations: string list
        EstimatedBytes: int64
        SelectedForFocused: bool
        Trust: LearningContextTrust
        ClaimsInstructionAuthority: bool
        RetrievalMethod: string
        InclusionReason: string
    }

type ReusablePlanState =
    | Reusable
    | Stale
    | Incompatible

type ReusableLearningPlan =
    {
        PlanId: string
        PlanSha256: string
        Source: LearningContextSource
        WorkClassId: string
        ContractId: string
        ContractRevision: string
        AuthoritativeObligations: string list
        State: ReusablePlanState
    }

type SyntheticTreatment =
    {
        OriginalItemId: string
        Arm: LearningContextArm
        ShadowBindingSha256: string
    }

type LearningContextCapacity =
    {
        MaximumReferences: int
        MaximumEstimatedBytes: int64
        MaximumConcurrentPreviews: int
        ActivePreviews: int
        ReservedPreviews: int
    }

type LearningContextRequest =
    {
        ItemId: string
        OriginalItemId: string
        Relation: LearningContextRelation
        Treatment: SyntheticTreatment
        InheritedTreatment: SyntheticTreatment option
        Plan: ReusableLearningPlan option
        ExpectedPlanId: string
        ExpectedPlanSha256: string
        ExpectedPlanSource: LearningContextSource
        ExpectedContractRevision: string
        RequiredMandatoryReferenceIds: string list
        References: LearningContextReference list
        Capacity: LearningContextCapacity
        SyntheticShadowOnly: bool
    }

type LearningContextRecipe =
    {
        RecipeId: string
        ManifestVersion: string
        Model: string
        Effort: string
    }

type LearningContextManifest =
    {
        ItemId: string
        OriginalItemId: string
        Relation: LearningContextRelation
        Arm: LearningContextArm
        TreatmentBindingSha256: string
        PlanId: string
        PlanSha256: string
        PlanSource: LearningContextSource
        ContractRevision: string
        Recipe: LearningContextRecipe
        MandatoryReferences: LearningContextReference list
        OptionalReferences: LearningContextReference list
        OmittedOptionalReferences: LearningContextReference list
        AuthoritativeObligations: string list
        EstimatedBytes: int64
        Bounds: LearningContextCapacity
        CanonicalSha256: string
        ShadowOnly: bool
        CanDispatch: bool
        CanWrite: bool
        IsAssignmentFact: bool
    }

type LearningContextRefusal =
    | MissingValidPlan
    | StaleValidPlan
    | IncompatibleValidPlan
    | StalePlanSource
    | MissingAuthoritativeObligation of obligation: string
    | MissingMandatoryReference of referenceId: string
    | UntrustedInstructionInjection of referenceId: string
    | OversizedContext of estimatedBytes: int64 * maximumBytes: int64
    | ContextCapacityConflict
    | TreatmentInheritanceConflict
    | InvalidLearningContextInput of detail: string

[<RequireQualifiedAccess>]
module LearningContext =
    [<Literal>]
    let WorkClassId = "github-routine-source-with-valid-plan-v1"

    [<Literal>]
    let ContractId = "learn-01-current-focused-v1"

    [<Literal>]
    let CurrentRecipeId = "unified-roadmap-bounded-handoff-v1"

    [<Literal>]
    let FocusedRecipeId = "unified-roadmap-focused-manifest-v1"

    [<Literal>]
    let ManifestVersion = "learn-01-context-shadow/1"

    [<Literal>]
    let WorkerModel = "gpt-5.6-sol"

    [<Literal>]
    let WorkerEffort = "medium"

    let private validText (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private validSha (value: string) =
        validText value && value.Length = 64 && value |> Seq.forall Uri.IsHexDigit

    let private sourceFields (source: LearningContextSource) =
        [
            source.Repository
            source.Path
            source.Revision
            source.Sha256.ToLowerInvariant()
        ]

    let private validSource (source: LearningContextSource) =
        validText source.Repository
        && validText source.Path
        && validText source.Revision
        && validSha source.Sha256

    let private sameSource (left: LearningContextSource) (right: LearningContextSource) =
        left.Repository = right.Repository
        && left.Path = right.Path
        && left.Revision = right.Revision
        && String.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase)

    let private normalizeSource (source: LearningContextSource) =
        { source with
            Sha256 = source.Sha256.ToLowerInvariant()
        }

    let private normalizeReference (reference: LearningContextReference) =
        { reference with
            Source = normalizeSource reference.Source
            Obligations = reference.Obligations |> List.sort
        }

    let private canonicalBytes (fields: string list) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)

        fields
        |> List.iter (fun (field: string) ->
            let bytes: byte array = Encoding.UTF8.GetBytes field
            writer.Write bytes.Length
            writer.Write bytes)

        writer.Flush()
        stream.ToArray()

    let private sha256 fields =
        canonicalBytes fields
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private relationFields =
        function
        | Original -> [ "original" ]
        | Descendant parent -> [ "descendant"; parent ]
        | Retry prior -> [ "retry"; prior ]

    let private armText =
        function
        | Current -> "current"
        | Focused -> "focused"

    let private classText =
        function
        | Mandatory -> "mandatory"
        | Optional -> "optional"

    let private trustText =
        function
        | GoverningInstruction -> "governing-instruction"
        | AuthoritativePlan -> "authoritative-plan"
        | UntrustedData -> "untrusted-data"

    let private referenceFields (reference: LearningContextReference) =
        [
            reference.ReferenceId
            yield! sourceFields reference.Source
            classText reference.Class
            string reference.EstimatedBytes
            string reference.SelectedForFocused
            trustText reference.Trust
            string reference.ClaimsInstructionAuthority
            reference.RetrievalMethod
            reference.InclusionReason
            string reference.Obligations.Length
            yield! reference.Obligations |> List.sort
        ]

    let private recipe arm =
        {
            RecipeId =
                match arm with
                | Current -> CurrentRecipeId
                | Focused -> FocusedRecipeId
            ManifestVersion = ManifestVersion
            Model = WorkerModel
            Effort = WorkerEffort
        }

    let private validTreatment (treatment: SyntheticTreatment) =
        validText treatment.OriginalItemId && validSha treatment.ShadowBindingSha256

    let private validRelation (request: LearningContextRequest) =
        match request.Relation with
        | Original -> request.ItemId = request.OriginalItemId && request.InheritedTreatment.IsNone
        | Descendant parent
        | Retry parent ->
            validText parent
            && parent <> request.ItemId
            && request.InheritedTreatment = Some request.Treatment

    let private validReference (reference: LearningContextReference) =
        validText reference.ReferenceId
        && validSource reference.Source
        && reference.EstimatedBytes >= 0L
        && validText reference.RetrievalMethod
        && validText reference.InclusionReason
        && (reference.Obligations |> List.forall validText)

    let private checkedSum (values: int64 list) : int64 option =
        values
        |> List.fold
            (fun total value ->
                total
                |> Option.bind (fun prior ->
                    if value > Int64.MaxValue - prior then
                        None
                    else
                        Some(prior + value)))
            (Some 0L)

    let compile (request: LearningContextRequest) : Result<LearningContextManifest, LearningContextRefusal> =
        let duplicateReferences =
            request.References
            |> List.countBy _.ReferenceId
            |> List.exists (fun (_, count) -> count <> 1)

        let invalidObligations (obligations: string list) =
            obligations |> List.exists (validText >> not)
            || (obligations |> List.distinct |> List.length) <> obligations.Length

        if
            not request.SyntheticShadowOnly
            || not (validText request.ItemId)
            || not (validText request.OriginalItemId)
            || not (validText request.ExpectedPlanId)
            || not (validSha request.ExpectedPlanSha256)
            || not (validSource request.ExpectedPlanSource)
            || not (validText request.ExpectedContractRevision)
            || not (validTreatment request.Treatment)
            || request.Treatment.OriginalItemId <> request.OriginalItemId
            || request.Capacity.MaximumReferences < 1
            || request.Capacity.MaximumEstimatedBytes < 1L
            || request.Capacity.MaximumConcurrentPreviews < 1
            || request.Capacity.ActivePreviews < 0
            || request.Capacity.ReservedPreviews < 0
            || duplicateReferences
            || request.References |> List.exists (validReference >> not)
            || invalidObligations request.RequiredMandatoryReferenceIds
        then
            Error(InvalidLearningContextInput "invalid-or-non-synthetic-shadow-request")
        elif not (validRelation request) then
            Error TreatmentInheritanceConflict
        elif
            request.Capacity.ActivePreviews > request.Capacity.MaximumConcurrentPreviews
            || request.Capacity.ReservedPreviews >
                request.Capacity.MaximumConcurrentPreviews - request.Capacity.ActivePreviews
            || request.Capacity.ActivePreviews + request.Capacity.ReservedPreviews
               >= request.Capacity.MaximumConcurrentPreviews
        then
            Error ContextCapacityConflict
        else
            match request.Plan with
            | None -> Error MissingValidPlan
            | Some plan when plan.State = Stale -> Error StaleValidPlan
            | Some plan when plan.State = Incompatible -> Error IncompatibleValidPlan
            | Some plan when
                not (validText plan.PlanId)
                || not (validSha plan.PlanSha256)
                || not (validSource plan.Source)
                || invalidObligations plan.AuthoritativeObligations
                || plan.PlanId <> request.ExpectedPlanId
                || not (String.Equals(plan.PlanSha256, request.ExpectedPlanSha256, StringComparison.OrdinalIgnoreCase))
                || plan.WorkClassId <> WorkClassId
                || plan.ContractId <> ContractId
                || plan.ContractRevision <> request.ExpectedContractRevision
                ->
                Error IncompatibleValidPlan
            | Some plan when not (sameSource plan.Source request.ExpectedPlanSource) -> Error StalePlanSource
            | Some plan ->
                let references = request.References |> List.map normalizeReference

                let mandatory =
                    references |> List.filter (fun reference -> reference.Class = Mandatory)

                let optional =
                    references |> List.filter (fun reference -> reference.Class = Optional)

                match
                    request.RequiredMandatoryReferenceIds
                    |> List.tryFind (fun required ->
                        mandatory
                        |> List.exists (fun reference -> reference.ReferenceId = required)
                        |> not)
                with
                | Some missing -> Error(MissingMandatoryReference missing)
                | None ->
                    let coveredObligations = mandatory |> List.collect _.Obligations |> Set.ofList

                    match
                        plan.AuthoritativeObligations
                        |> List.tryFind (fun obligation -> not (Set.contains obligation coveredObligations))
                    with
                    | Some missing -> Error(MissingAuthoritativeObligation missing)
                    | None ->
                        match
                            references
                            |> List.tryFind (fun reference ->
                                reference.Trust = UntrustedData && reference.ClaimsInstructionAuthority)
                        with
                        | Some injected -> Error(UntrustedInstructionInjection injected.ReferenceId)
                        | None ->
                            let selectedOptional, omittedOptional =
                                match request.Treatment.Arm with
                                | Current -> optional, []
                                | Focused ->
                                    optional |> List.filter _.SelectedForFocused,
                                    optional |> List.filter (_.SelectedForFocused >> not)

                            let included = mandatory @ selectedOptional

                            if included.Length > request.Capacity.MaximumReferences then
                                Error ContextCapacityConflict
                            else
                                match included |> List.map _.EstimatedBytes |> checkedSum with
                                | None ->
                                    Error(OversizedContext(Int64.MaxValue, request.Capacity.MaximumEstimatedBytes))
                                | Some estimatedBytes when estimatedBytes > request.Capacity.MaximumEstimatedBytes ->
                                    Error(OversizedContext(estimatedBytes, request.Capacity.MaximumEstimatedBytes))
                                | Some estimatedBytes ->
                                    let selectedRecipe = recipe request.Treatment.Arm
                                    let orderedMandatory = mandatory |> List.sortBy _.ReferenceId
                                    let orderedOptional = selectedOptional |> List.sortBy _.ReferenceId
                                    let orderedOmitted = omittedOptional |> List.sortBy _.ReferenceId
                                    let obligations = plan.AuthoritativeObligations |> List.sort

                                    let fields =
                                        [
                                            ManifestVersion
                                            request.ItemId
                                            request.OriginalItemId
                                            yield! relationFields request.Relation
                                            armText request.Treatment.Arm
                                            request.Treatment.ShadowBindingSha256.ToLowerInvariant()
                                            plan.PlanId
                                            plan.PlanSha256.ToLowerInvariant()
                                            yield! sourceFields plan.Source
                                            plan.ContractRevision
                                            selectedRecipe.RecipeId
                                            selectedRecipe.Model
                                            selectedRecipe.Effort
                                            string estimatedBytes
                                            string request.Capacity.MaximumReferences
                                            string request.Capacity.MaximumEstimatedBytes
                                            string request.Capacity.MaximumConcurrentPreviews
                                            string request.Capacity.ActivePreviews
                                            string request.Capacity.ReservedPreviews
                                            string obligations.Length
                                            yield! obligations
                                            string orderedMandatory.Length
                                            for reference in orderedMandatory do
                                                yield! referenceFields reference
                                            string orderedOptional.Length
                                            for reference in orderedOptional do
                                                yield! referenceFields reference
                                            string orderedOmitted.Length
                                            for reference in orderedOmitted do
                                                yield! referenceFields reference
                                        ]

                                    Ok
                                        {
                                            ItemId = request.ItemId
                                            OriginalItemId = request.OriginalItemId
                                            Relation = request.Relation
                                            Arm = request.Treatment.Arm
                                            TreatmentBindingSha256 =
                                                request.Treatment.ShadowBindingSha256.ToLowerInvariant()
                                            PlanId = plan.PlanId
                                            PlanSha256 = plan.PlanSha256.ToLowerInvariant()
                                            PlanSource = normalizeSource plan.Source
                                            ContractRevision = plan.ContractRevision
                                            Recipe = selectedRecipe
                                            MandatoryReferences = orderedMandatory
                                            OptionalReferences = orderedOptional
                                            OmittedOptionalReferences = orderedOmitted
                                            AuthoritativeObligations = obligations
                                            EstimatedBytes = estimatedBytes
                                            Bounds = request.Capacity
                                            CanonicalSha256 = sha256 fields
                                            ShadowOnly = true
                                            CanDispatch = false
                                            CanWrite = false
                                            IsAssignmentFact = false
                                        }
