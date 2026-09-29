namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

type LearningOperationalWindowRequest =
    {
        Enabled: bool
        WindowId: string
        SeedReferenceSha256: string
        Repository: string
        CalendarAdmissionBlock: string
        OriginalItemId: string
        AuthorityId: string
        AuthorityRevision: string
        AuthoritySha256: string
        OptedInAt: DateTimeOffset
        EnrollmentOpensAt: DateTimeOffset
        EnrollmentClosesAt: DateTimeOffset
    }

type LearningOperationalReadinessEvidence =
    {
        Schema: string
        WindowId: string
        Repository: string
        WorkClassId: string
        OriginalItemId: string
        AcceptedPlanSha256: string
        CanonicalWorkItemSha256: string
        CoverageRosterSha256: string
        DispatchCensusSha256: string
        NativeDeliverySha256: string
        SharedCostRosterSha256: string
        ObservedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        CompleteNativeUsage: bool
        UnassignedSharedAllocation: bool
        Provenance: string
    }

type LearningOperationalWindowBinding =
    {
        Schema: string
        BindingSha256: string
        WindowId: string
        WindowRevision: string
        PolicyRepository: string
        PolicyRevision: string
        PolicyPath: string
        PolicySha256: string
        PolicyStatus: string
        Repository: string
        WorkClassId: string
        CalendarAdmissionBlock: string
        OriginalItemId: string
        SeedReferenceSha256: string
        AssignmentInputSha256: string
        Arm: string
        AssignedAt: DateTimeOffset
        AuthorityId: string
        AuthorityRevision: string
        AuthoritySha256: string
        OptedInAt: DateTimeOffset
        EnrollmentOpensAt: DateTimeOffset
        EnrollmentClosesAt: DateTimeOffset
        EligibilityEvidenceSha256: string
        EligibilityObservedAt: DateTimeOffset
        EligibilityExpiresAt: DateTimeOffset
        AcceptedPlanSha256: string
        CanonicalWorkItemSha256: string
        CoverageRosterSha256: string
        DispatchCensusSha256: string
        NativeDeliverySha256: string
        SharedCostRosterSha256: string
        MinimumEnrollmentDays: int
        MaximumEnrollmentDays: int
        OutcomeDeadlineDays: int
        RepairObservationDays: int
        MaximumFollowupDays: int
        MinimumIndependentOriginalsPerArm: int
        FinalOnlyInference: bool
    }

type PreparedLearningOperationalWindow internal (binding: LearningOperationalWindowBinding) =
    member _.Binding = binding

type LearningOperationalWindowKey =
    {
        WindowId: string
        OriginalItemId: string
    }

type LearningOperationalReadinessSnapshot =
    {
        Request: LearningOperationalWindowRequest
        Evidence: LearningOperationalReadinessEvidence
    }

/// Owner boundary for independently retained plan, work-item, coverage, delivery and cost evidence.
/// Production admission never accepts a caller-supplied readiness record directly.
type ILearningOperationalReadinessSource =
    abstract ReadLearningOperationalReadiness:
        LearningOperationalWindowKey * CancellationToken -> Task<Result<LearningOperationalReadinessSnapshot, string>>

type ILearningOperationalWindowStore =
    abstract BindLearningOperationalWindow:
        LearningOperationalWindowBinding * CancellationToken -> Task<Result<LearningOperationalWindowBinding, string>>

    abstract ReadLearningOperationalWindow:
        windowId: string * originalItemId: string * CancellationToken ->
            Task<Result<LearningOperationalWindowBinding, string>>

[<RequireQualifiedAccess>]
module LearningOperationalWindow =
    [<Literal>]
    let schema = "fsgg.orchestration.learning-operational-window/1"

    [<Literal>]
    let readinessSchema = "fsgg.orchestration.learning-operational-readiness/1"

    [<Literal>]
    let windowRevision = "1"

    [<Literal>]
    let policyRepository = "FS-GG/.github"

    [<Literal>]
    let policyRevision = "2e553e41e58ee2f5e27aedcffc7403ce50e7cdd4"

    [<Literal>]
    let policyPath = "policy/learn-01-current-focused-v1.json"

    [<Literal>]
    let policySha256 =
        "91713679fd486459188f2144e75cc69b77720c7841b6e75cd5d4d35620ed4179"

    [<Literal>]
    let policyStatus = "source-contract-not-enrolled"

    [<Literal>]
    let workClassId = "github-routine-source-with-valid-plan-v1"

    [<Literal>]
    let minimumEnrollmentDays = 28

    [<Literal>]
    let maximumEnrollmentDays = 84

    [<Literal>]
    let outcomeDeadlineDays = 14

    [<Literal>]
    let repairObservationDays = 30

    [<Literal>]
    let maximumFollowupDays = 60

    [<Literal>]
    let minimumIndependentOriginalsPerArm = 6908

    let private shaBytes (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private shaText (value: string) =
        Encoding.UTF8.GetBytes value |> shaBytes

    let private sha (value: string) =
        not (isNull value)
        && value.Length = 64
        && value
           |> Seq.forall (fun character -> character >= '0' && character <= '9' || character >= 'a' && character <= 'f')

    let private text maximum (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && value.Length <= maximum

    let private utc (value: DateTimeOffset) = value.Offset = TimeSpan.Zero

    let private line (value: string) =
        Convert.ToBase64String(Encoding.UTF8.GetBytes value)

    let canonicalBytes (value: LearningOperationalWindowBinding) =
        [
            value.Schema
            value.WindowId
            value.WindowRevision
            value.PolicyRepository
            value.PolicyRevision
            value.PolicyPath
            value.PolicySha256
            value.PolicyStatus
            value.Repository
            value.WorkClassId
            value.CalendarAdmissionBlock
            value.OriginalItemId
            value.SeedReferenceSha256
            value.AssignmentInputSha256
            value.Arm
            value.AssignedAt.ToString("O")
            value.AuthorityId
            value.AuthorityRevision
            value.AuthoritySha256
            value.OptedInAt.ToString("O")
            value.EnrollmentOpensAt.ToString("O")
            value.EnrollmentClosesAt.ToString("O")
            value.EligibilityEvidenceSha256
            value.EligibilityObservedAt.ToString("O")
            value.EligibilityExpiresAt.ToString("O")
            value.AcceptedPlanSha256
            value.CanonicalWorkItemSha256
            value.CoverageRosterSha256
            value.DispatchCensusSha256
            value.NativeDeliverySha256
            value.SharedCostRosterSha256
            string value.MinimumEnrollmentDays
            string value.MaximumEnrollmentDays
            string value.OutcomeDeadlineDays
            string value.RepairObservationDays
            string value.MaximumFollowupDays
            string value.MinimumIndependentOriginalsPerArm
            string value.FinalOnlyInference
        ]
        |> List.map line
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let digest (value: LearningOperationalWindowBinding) =
        canonicalBytes { value with BindingSha256 = "" } |> shaBytes

    let readinessBytes (value: LearningOperationalReadinessEvidence) =
        [
            value.Schema
            value.WindowId
            value.Repository
            value.WorkClassId
            value.OriginalItemId
            value.AcceptedPlanSha256
            value.CanonicalWorkItemSha256
            value.CoverageRosterSha256
            value.DispatchCensusSha256
            value.NativeDeliverySha256
            value.SharedCostRosterSha256
            value.ObservedAt.ToString("O")
            value.ExpiresAt.ToString("O")
            string value.CompleteNativeUsage
            string value.UnassignedSharedAllocation
            value.Provenance
        ]
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let readinessDigest (value: LearningOperationalReadinessEvidence) = readinessBytes value |> shaBytes

    let assignmentInput (request: LearningOperationalWindowRequest) =
        String.concat
            "\u0000"
            [
                request.SeedReferenceSha256
                request.Repository
                workClassId
                request.CalendarAdmissionBlock
                request.OriginalItemId
            ]

    let deriveArm (request: LearningOperationalWindowRequest) =
        let bytes = assignmentInput request |> Encoding.UTF8.GetBytes |> SHA256.HashData

        if BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(0, 8)) < (1UL <<< 63) then
            "focused"
        else
            "current"

    let validate (value: LearningOperationalWindowBinding) =
        let enrollmentDays = (value.EnrollmentClosesAt - value.EnrollmentOpensAt).TotalDays

        let expectedInput =
            String.concat
                "\u0000"
                [
                    value.SeedReferenceSha256
                    value.Repository
                    value.WorkClassId
                    value.CalendarAdmissionBlock
                    value.OriginalItemId
                ]

        let expectedBytes = expectedInput |> Encoding.UTF8.GetBytes |> SHA256.HashData

        let expectedArm =
            if BinaryPrimitives.ReadUInt64BigEndian(expectedBytes.AsSpan(0, 8)) < (1UL <<< 63) then
                "focused"
            else
                "current"

        if value.Schema <> schema || value.BindingSha256 <> digest value then
            Error "learning-operational-window-digest-refused"
        elif value.WindowRevision <> windowRevision then
            Error "learning-operational-window-revision-refused"
        elif
            value.PolicyRepository <> policyRepository
            || value.PolicyRevision <> policyRevision
            || value.PolicyPath <> policyPath
            || value.PolicySha256 <> policySha256
            || value.PolicyStatus <> policyStatus
            || value.Repository <> policyRepository
            || value.WorkClassId <> workClassId
        then
            Error "learning-operational-window-policy-refused"
        elif
            [
                value.SeedReferenceSha256
                value.AssignmentInputSha256
                value.AuthoritySha256
                value.EligibilityEvidenceSha256
                value.AcceptedPlanSha256
                value.CanonicalWorkItemSha256
                value.CoverageRosterSha256
                value.DispatchCensusSha256
                value.NativeDeliverySha256
                value.SharedCostRosterSha256
            ]
            |> List.exists (sha >> not)
        then
            Error "learning-operational-window-sha-refused"
        elif
            [
                value.WindowId
                value.CalendarAdmissionBlock
                value.OriginalItemId
                value.AuthorityId
                value.AuthorityRevision
            ]
            |> List.exists (text 512 >> not)
        then
            Error "learning-operational-window-text-refused"
        elif value.AssignmentInputSha256 <> shaText expectedInput || value.Arm <> expectedArm then
            Error "learning-operational-window-arm-refused"
        elif
            [
                value.AssignedAt
                value.OptedInAt
                value.EnrollmentOpensAt
                value.EnrollmentClosesAt
                value.EligibilityObservedAt
                value.EligibilityExpiresAt
            ]
            |> List.exists (utc >> not)
        then
            Error "learning-operational-window-time-refused"
        elif
            value.MinimumEnrollmentDays <> minimumEnrollmentDays
            || value.MaximumEnrollmentDays <> maximumEnrollmentDays
            || value.OutcomeDeadlineDays <> outcomeDeadlineDays
            || value.RepairObservationDays <> repairObservationDays
            || value.MaximumFollowupDays <> maximumFollowupDays
            || value.MinimumIndependentOriginalsPerArm <> minimumIndependentOriginalsPerArm
            || not value.FinalOnlyInference
            || enrollmentDays < float minimumEnrollmentDays
            || enrollmentDays > float maximumEnrollmentDays
        then
            Error "learning-operational-window-fixed-contract-refused"
        elif
            value.OptedInAt > value.AssignedAt
            || value.EnrollmentOpensAt > value.AssignedAt
            || value.EnrollmentClosesAt <= value.AssignedAt
            || value.EligibilityObservedAt > value.AssignedAt
            || value.EligibilityExpiresAt <= value.AssignedAt
        then
            Error "learning-operational-window-order-refused"
        else
            Ok value

    let validateCurrent (now: DateTimeOffset) (value: LearningOperationalWindowBinding) =
        validate value
        |> Result.bind (fun valid ->
            if not (utc now) then
                Error "learning-operational-window-current-time-refused"
            elif now < valid.EnrollmentOpensAt || now >= valid.EnrollmentClosesAt then
                Error "learning-operational-window-not-open"
            elif now < valid.EligibilityObservedAt || now >= valid.EligibilityExpiresAt then
                Error "learning-operational-window-readiness-stale"
            else
                Ok valid)

    let decode (bytes: byte array) =
        try
            let values =
                (Encoding.UTF8.GetString bytes).Split('\n', StringSplitOptions.None)
                |> Array.map (fun value -> Convert.FromBase64String value |> Encoding.UTF8.GetString)

            if values.Length <> 38 then
                Error "learning-operational-window-shape-refused"
            else
                {
                    Schema = values[0]
                    BindingSha256 = ""
                    WindowId = values[1]
                    WindowRevision = values[2]
                    PolicyRepository = values[3]
                    PolicyRevision = values[4]
                    PolicyPath = values[5]
                    PolicySha256 = values[6]
                    PolicyStatus = values[7]
                    Repository = values[8]
                    WorkClassId = values[9]
                    CalendarAdmissionBlock = values[10]
                    OriginalItemId = values[11]
                    SeedReferenceSha256 = values[12]
                    AssignmentInputSha256 = values[13]
                    Arm = values[14]
                    AssignedAt = DateTimeOffset.Parse values[15]
                    AuthorityId = values[16]
                    AuthorityRevision = values[17]
                    AuthoritySha256 = values[18]
                    OptedInAt = DateTimeOffset.Parse values[19]
                    EnrollmentOpensAt = DateTimeOffset.Parse values[20]
                    EnrollmentClosesAt = DateTimeOffset.Parse values[21]
                    EligibilityEvidenceSha256 = values[22]
                    EligibilityObservedAt = DateTimeOffset.Parse values[23]
                    EligibilityExpiresAt = DateTimeOffset.Parse values[24]
                    AcceptedPlanSha256 = values[25]
                    CanonicalWorkItemSha256 = values[26]
                    CoverageRosterSha256 = values[27]
                    DispatchCensusSha256 = values[28]
                    NativeDeliverySha256 = values[29]
                    SharedCostRosterSha256 = values[30]
                    MinimumEnrollmentDays = Int32.Parse values[31]
                    MaximumEnrollmentDays = Int32.Parse values[32]
                    OutcomeDeadlineDays = Int32.Parse values[33]
                    RepairObservationDays = Int32.Parse values[34]
                    MaximumFollowupDays = Int32.Parse values[35]
                    MinimumIndependentOriginalsPerArm = Int32.Parse values[36]
                    FinalOnlyInference = Boolean.Parse values[37]
                }
                |> fun value ->
                    { value with
                        BindingSha256 = digest value
                    }
                |> validate
        with
        | :? FormatException
        | :? ArgumentException -> Error "learning-operational-window-format-refused"

    let prepare
        (assignedAt: DateTimeOffset)
        (request: LearningOperationalWindowRequest)
        (evidence: LearningOperationalReadinessEvidence)
        =
        if not request.Enabled then
            Error "learning-operational-window-disabled"
        elif
            evidence.Schema <> readinessSchema
            || evidence.WindowId <> request.WindowId
            || evidence.Repository <> request.Repository
            || evidence.WorkClassId <> workClassId
            || evidence.OriginalItemId <> request.OriginalItemId
        then
            Error "learning-operational-window-readiness-identity-refused"
        elif not evidence.CompleteNativeUsage || not evidence.UnassignedSharedAllocation then
            Error "learning-operational-window-readiness-incomplete"
        elif
            [
                evidence.AcceptedPlanSha256
                evidence.CanonicalWorkItemSha256
                evidence.CoverageRosterSha256
                evidence.DispatchCensusSha256
                evidence.NativeDeliverySha256
                evidence.SharedCostRosterSha256
            ]
            |> List.exists (sha >> not)
        then
            Error "learning-operational-window-readiness-sha-refused"
        elif not (text 512 evidence.Provenance) then
            Error "learning-operational-window-readiness-provenance-refused"
        else
            let value0 =
                {
                    Schema = schema
                    BindingSha256 = ""
                    WindowId = request.WindowId
                    WindowRevision = windowRevision
                    PolicyRepository = policyRepository
                    PolicyRevision = policyRevision
                    PolicyPath = policyPath
                    PolicySha256 = policySha256
                    PolicyStatus = policyStatus
                    Repository = request.Repository
                    WorkClassId = workClassId
                    CalendarAdmissionBlock = request.CalendarAdmissionBlock
                    OriginalItemId = request.OriginalItemId
                    SeedReferenceSha256 = request.SeedReferenceSha256
                    AssignmentInputSha256 = assignmentInput request |> shaText
                    Arm = deriveArm request
                    AssignedAt = assignedAt
                    AuthorityId = request.AuthorityId
                    AuthorityRevision = request.AuthorityRevision
                    AuthoritySha256 = request.AuthoritySha256
                    OptedInAt = request.OptedInAt
                    EnrollmentOpensAt = request.EnrollmentOpensAt
                    EnrollmentClosesAt = request.EnrollmentClosesAt
                    EligibilityEvidenceSha256 = readinessDigest evidence
                    EligibilityObservedAt = evidence.ObservedAt
                    EligibilityExpiresAt = evidence.ExpiresAt
                    AcceptedPlanSha256 = evidence.AcceptedPlanSha256
                    CanonicalWorkItemSha256 = evidence.CanonicalWorkItemSha256
                    CoverageRosterSha256 = evidence.CoverageRosterSha256
                    DispatchCensusSha256 = evidence.DispatchCensusSha256
                    NativeDeliverySha256 = evidence.NativeDeliverySha256
                    SharedCostRosterSha256 = evidence.SharedCostRosterSha256
                    MinimumEnrollmentDays = minimumEnrollmentDays
                    MaximumEnrollmentDays = maximumEnrollmentDays
                    OutcomeDeadlineDays = outcomeDeadlineDays
                    RepairObservationDays = repairObservationDays
                    MaximumFollowupDays = maximumFollowupDays
                    MinimumIndependentOriginalsPerArm = minimumIndependentOriginalsPerArm
                    FinalOnlyInference = true
                }

            let value =
                { value0 with
                    BindingSha256 = digest value0
                }

            validate value |> Result.map PreparedLearningOperationalWindow
