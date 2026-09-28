namespace FS.GG.Coordination.Orchestration.Observer

open System
open System.IO
open System.Security.Cryptography
open System.Text

type LearningPlanningAction =
    | Create
    | Keep
    | Investigate
    | Decompose

type LearningModelProfile =
    {
        Model: string
        Effort: string
    }

type DirectSmallEvidence =
    {
        SingleRepository: bool
        StableContract: bool
        KnownImplementationLocation: bool
        ExistingTestBoundary: bool
        NoProtectedOperation: bool
        NoCrossRepositoryContractChange: bool
        EstimatedTouchedFiles: int
        EvidenceReferences: string list
    }

type LearningProposalSlice =
    {
        SliceId: string
        TouchSet: string list
        IntegrationObligations: string list
    }

type LearningProposalRequest =
    {
        ProposalId: string
        ItemId: string
        OriginalItemId: string
        Action: LearningPlanningAction
        ContextRequest: LearningContextRequest option
        InvestigationQuestions: string list
        Slices: LearningProposalSlice list
        IntegrationContract: string option
        DirectSmallRequested: bool
        DirectSmallEvidence: DirectSmallEvidence option
        SyntheticShadowOnly: bool
    }

type LearningProposal =
    {
        ProposalId: string
        ItemId: string
        OriginalItemId: string
        Action: LearningPlanningAction
        Planner: LearningModelProfile option
        Worker: LearningModelProfile
        DirectSmallEligible: bool
        DirectSmallEvidence: DirectSmallEvidence option
        ContextManifest: LearningContextManifest option
        InvestigationQuestions: string list
        Slices: LearningProposalSlice list
        IntegrationContract: string option
        CanonicalSha256: string
        ShadowOnly: bool
        CanDispatch: bool
        CanWrite: bool
        IsAssignmentFact: bool
    }

type LearningProposalRefusal =
    | InvalidLearningProposalInput of detail: string
    | InvalidKeepContext of LearningContextRefusal
    | DirectSmallIneligible of reasons: string list
    | InvestigationEvidenceMissing
    | DecompositionEvidenceMissing
    | DecompositionTouchSetOverlap of path: string

[<RequireQualifiedAccess>]
module LearningProposal =
    [<Literal>]
    let PlannerModel = "gpt-6-astra"

    [<Literal>]
    let PlannerEffort = "high"

    [<Literal>]
    let MaximumDirectSmallFiles = 3

    [<Literal>]
    let MaximumInvestigationQuestions = 8

    [<Literal>]
    let MaximumDecompositionSlices = 8

    [<Literal>]
    let MaximumSlicePaths = 16

    let private planner = { Model = PlannerModel; Effort = PlannerEffort }

    let private worker =
        {
            Model = LearningContext.WorkerModel
            Effort = LearningContext.WorkerEffort
        }

    let private validText (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim() && value.Length <= 512

    let private uniqueValid values =
        values |> List.forall validText && (values |> List.distinct |> List.length) = values.Length

    let private canonicalRepositoryFile (value: string) =
        validText value
        && value.Length <= 512
        && not (value.StartsWith('/') || value.EndsWith('/'))
        && not (value.Contains("//") || value.Contains('\\'))
        && value
           |> Seq.forall (fun character ->
               Char.IsAsciiLetterOrDigit character || "._-/@+".Contains character)
        && value.Split('/')
           |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> "..")

    let private overlappingRepositoryFiles (left: string) (right: string) =
        left = right
        || left.StartsWith(right + "/", StringComparison.Ordinal)
        || right.StartsWith(left + "/", StringComparison.Ordinal)

    let private actionText =
        function
        | Create -> "create"
        | Keep -> "keep"
        | Investigate -> "investigate"
        | Decompose -> "decompose"

    let private canonicalBytes (fields: string list) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)

        fields
        |> List.iter (fun field ->
            let bytes = Encoding.UTF8.GetBytes field
            writer.Write bytes.Length
            writer.Write bytes)

        writer.Flush()
        stream.ToArray()

    let private sha256 fields =
        canonicalBytes fields
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private directSmallReasons evidence =
        [
            if not evidence.SingleRepository then
                "multiple-repositories"
            if not evidence.StableContract then
                "unstable-contract"
            if not evidence.KnownImplementationLocation then
                "implementation-location-unknown"
            if not evidence.ExistingTestBoundary then
                "test-boundary-missing"
            if not evidence.NoProtectedOperation then
                "protected-operation"
            if not evidence.NoCrossRepositoryContractChange then
                "cross-repository-contract-change"
            if evidence.EstimatedTouchedFiles < 1 || evidence.EstimatedTouchedFiles > MaximumDirectSmallFiles then
                "touch-set-out-of-bounds"
            if
                not (uniqueValid evidence.EvidenceReferences)
                || List.isEmpty evidence.EvidenceReferences
                || evidence.EvidenceReferences.Length > 16
            then
                "evidence-reference-missing"
        ]

    let private validateSlices slices =
        if List.length slices < 2 || List.length slices > MaximumDecompositionSlices then
            Error DecompositionEvidenceMissing
        elif
            slices
            |> List.exists (fun slice ->
                not (validText slice.SliceId)
                || not (uniqueValid slice.TouchSet)
                || List.isEmpty slice.TouchSet
                || slice.TouchSet.Length > MaximumSlicePaths
                || slice.TouchSet |> List.exists (canonicalRepositoryFile >> not)
                || not (uniqueValid slice.IntegrationObligations)
                || List.isEmpty slice.IntegrationObligations)
        then
            Error(InvalidLearningProposalInput "invalid-decomposition-slice")
        elif (slices |> List.map _.SliceId |> List.distinct |> List.length) <> slices.Length then
            Error(InvalidLearningProposalInput "duplicate-decomposition-slice")
        else
            let declared =
                slices
                |> List.collect (fun slice -> slice.TouchSet |> List.map (fun path -> slice.SliceId, path))

            declared
            |> List.tryPick (fun (leftSlice, leftPath) ->
                declared
                |> List.tryPick (fun (rightSlice, rightPath) ->
                    if
                        leftSlice < rightSlice
                        && overlappingRepositoryFiles leftPath rightPath
                    then
                        Some(if leftPath.Length <= rightPath.Length then leftPath else rightPath)
                    else
                        None))
            |> function
                | Some path -> Error(DecompositionTouchSetOverlap path)
                | None -> Ok()

    let propose (request: LearningProposalRequest) : Result<LearningProposal, LearningProposalRefusal> =
        if
            not request.SyntheticShadowOnly
            || not (validText request.ProposalId)
            || not (validText request.ItemId)
            || not (validText request.OriginalItemId)
            || not (uniqueValid request.InvestigationQuestions)
            || request.InvestigationQuestions.Length > MaximumInvestigationQuestions
            || (request.IntegrationContract |> Option.exists (validText >> not))
        then
            Error(InvalidLearningProposalInput "invalid-or-non-synthetic-shadow-request")
        else
            let directResult =
                if request.DirectSmallRequested then
                    match request.DirectSmallEvidence with
                    | None -> Error(DirectSmallIneligible [ "evidence-missing" ])
                    | Some evidence ->
                        match directSmallReasons evidence with
                        | [] -> Ok true
                        | reasons -> Error(DirectSmallIneligible reasons)
                elif request.DirectSmallEvidence.IsSome then
                    Error(InvalidLearningProposalInput "unrequested-direct-small-evidence")
                else
                    Ok false

            directResult
            |> Result.bind (fun directSmallEligible ->
                let manifestResult, plannerResult =
                    match request.Action with
                    | Keep when
                        request.DirectSmallRequested
                        || not (List.isEmpty request.InvestigationQuestions)
                        || not (List.isEmpty request.Slices)
                        || request.IntegrationContract.IsSome
                        ->
                        Error(InvalidLearningProposalInput "keep-has-planning-evidence"), None
                    | Keep ->
                        match request.ContextRequest with
                        | None -> Error(InvalidLearningProposalInput "keep-context-missing"), None
                        | Some context ->
                            match LearningContext.compile context with
                            | Ok manifest when
                                manifest.ItemId <> request.ItemId
                                || manifest.OriginalItemId <> request.OriginalItemId
                                ->
                                Error(InvalidLearningProposalInput "keep-context-identity-mismatch"), None
                            | Ok manifest -> Ok(Some manifest), None
                            | Error refusal -> Error(InvalidKeepContext refusal), None
                    | Create when request.ContextRequest.IsSome ->
                        Error(InvalidLearningProposalInput "create-cannot-reuse-context"), None
                    | Create when
                        not (List.isEmpty request.InvestigationQuestions)
                        || not (List.isEmpty request.Slices)
                        || request.IntegrationContract.IsSome
                        ->
                        Error(InvalidLearningProposalInput "create-has-foreign-planning-evidence"), None
                    | Create -> Ok None, if directSmallEligible then None else Some planner
                    | Investigate when
                        request.ContextRequest.IsSome
                        || request.DirectSmallRequested
                        || List.isEmpty request.InvestigationQuestions
                        || not (List.isEmpty request.Slices)
                        || request.IntegrationContract.IsSome
                        ->
                        Error InvestigationEvidenceMissing, None
                    | Investigate -> Ok None, Some planner
                    | Decompose when
                        request.ContextRequest.IsSome
                        || request.DirectSmallRequested
                        || not (List.isEmpty request.InvestigationQuestions)
                        || request.IntegrationContract.IsNone
                        ->
                        Error DecompositionEvidenceMissing, None
                    | Decompose ->
                        match validateSlices request.Slices with
                        | Error refusal -> Error refusal, None
                        | Ok() -> Ok None, Some planner

                manifestResult
                |> Result.map (fun manifest ->
                    let orderedQuestions = request.InvestigationQuestions |> List.sort
                    let orderedSlices =
                        request.Slices
                        |> List.map (fun slice ->
                            { slice with
                                TouchSet = slice.TouchSet |> List.sort
                                IntegrationObligations = slice.IntegrationObligations |> List.sort
                            })
                        |> List.sortBy _.SliceId
                    let orderedDirectEvidence =
                        request.DirectSmallEvidence
                        |> Option.map (fun evidence ->
                            { evidence with EvidenceReferences = evidence.EvidenceReferences |> List.sort })
                    let fields =
                        [
                            yield "learn-01-proposal-shadow/1"
                            yield request.ProposalId
                            yield request.ItemId
                            yield request.OriginalItemId
                            yield actionText request.Action
                            yield string directSmallEligible
                            yield defaultArg (plannerResult |> Option.map _.Model) ""
                            yield defaultArg (plannerResult |> Option.map _.Effort) ""
                            yield worker.Model
                            yield worker.Effort
                            match orderedDirectEvidence with
                            | None -> yield "no-direct-small-evidence"
                            | Some evidence ->
                                yield string evidence.SingleRepository
                                yield string evidence.StableContract
                                yield string evidence.KnownImplementationLocation
                                yield string evidence.ExistingTestBoundary
                                yield string evidence.NoProtectedOperation
                                yield string evidence.NoCrossRepositoryContractChange
                                yield string evidence.EstimatedTouchedFiles
                                yield string evidence.EvidenceReferences.Length
                                yield! evidence.EvidenceReferences
                            yield defaultArg (manifest |> Option.map _.CanonicalSha256) ""
                            yield string orderedQuestions.Length
                            yield! orderedQuestions
                            yield string orderedSlices.Length
                            for slice in orderedSlices do
                                yield slice.SliceId
                                yield string slice.TouchSet.Length
                                yield! slice.TouchSet |> List.sort
                                yield string slice.IntegrationObligations.Length
                                yield! slice.IntegrationObligations |> List.sort
                            yield defaultArg request.IntegrationContract ""
                        ]

                    {
                        ProposalId = request.ProposalId
                        ItemId = request.ItemId
                        OriginalItemId = request.OriginalItemId
                        Action = request.Action
                        Planner = plannerResult
                        Worker = worker
                        DirectSmallEligible = directSmallEligible
                        DirectSmallEvidence = orderedDirectEvidence
                        ContextManifest = manifest
                        InvestigationQuestions = orderedQuestions
                        Slices = orderedSlices
                        IntegrationContract = request.IntegrationContract
                        CanonicalSha256 = sha256 fields
                        ShadowOnly = true
                        CanDispatch = false
                        CanWrite = false
                        IsAssignmentFact = false
                    }))
