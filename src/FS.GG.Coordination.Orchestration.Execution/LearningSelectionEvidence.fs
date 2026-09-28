namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Threading
open System.Threading.Tasks

type LearningExecutableIdentity =
    {
        Path: string
        Version: string option
        Sha256: string option
    }

[<RequireQualifiedAccess>]
type LearningCapabilityStatus =
    | Supported
    | Unsupported of code: string
    | Unknown of code: string

type LearningSelectionQuery =
    {
        Provider: ProviderIdentity
        Executable: LearningExecutableIdentity
        Requested: RequestedSelection
        MaximumAge: TimeSpan
    }

type LearningSelectionEvidence =
    {
        Schema: string
        Provider: ProviderIdentity
        Executable: LearningExecutableIdentity
        Requested: RequestedSelection
        Status: LearningCapabilityStatus
        Provenance: string
        ObservedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
    }

type LearningNativeSelection =
    {
        Provider: string option
        Model: string option
        Effort: string option
        Backend: string option
    }

[<RequireQualifiedAccess>]
type LearningSelectionDisposition =
    | Matched
    | Deviation of codes: string list
    | ObservationUnknown of codes: string list

type LearningSelectionObservation =
    {
        Requested: RequestedSelection
        Resolved: ResolvedSelection
        Native: LearningNativeSelection
        Disposition: LearningSelectionDisposition
    }

type ILearningExecutionProvider =
    abstract member ObserveLearningSelection: RequestedSelection * CancellationToken -> Task<LearningSelectionEvidence>

    abstract member LaunchLearning: LaunchIntent * CancellationToken -> Task<LaunchResult>

[<RequireQualifiedAccess>]
module LearningSelectionEvidence =
    let schema = "fsgg.orchestration.learning-selection-evidence/1"

    let private bounded value =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 512

    let private lowercaseSha256 (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

    let private utc (value: DateTimeOffset) = value.Offset = TimeSpan.Zero

    let authorize now (query: LearningSelectionQuery) (evidence: LearningSelectionEvidence) =
        if evidence.Schema <> schema then
            Error "learning-selection-evidence-schema-refused"
        elif evidence.Provider <> query.Provider then
            Error "learning-selection-provider-identity-changed"
        elif evidence.Executable <> query.Executable then
            Error "learning-selection-executable-identity-changed"
        elif evidence.Requested <> query.Requested then
            Error "learning-selection-request-changed"
        elif query.MaximumAge <= TimeSpan.Zero then
            Error "learning-selection-freshness-invalid"
        elif
            not (bounded evidence.Provider.Provider)
            || not (bounded evidence.Provider.AdapterVersion)
            || not (bounded evidence.Executable.Path)
            || not (bounded evidence.Provenance)
        then
            Error "learning-selection-evidence-invalid"
        elif evidence.Executable.Version |> Option.exists (fun value -> not (bounded value)) then
            Error "learning-selection-version-invalid"
        elif evidence.Executable.Sha256 |> Option.exists (lowercaseSha256 >> not) then
            Error "learning-selection-executable-digest-invalid"
        elif
            not (utc now && utc evidence.ObservedAt && utc evidence.ExpiresAt)
            || evidence.ObservedAt > now
            || evidence.ExpiresAt <= evidence.ObservedAt
            || now >= evidence.ExpiresAt
            || now - evidence.ObservedAt > query.MaximumAge
        then
            Error "learning-selection-evidence-stale"
        else
            match evidence.Status with
            | LearningCapabilityStatus.Supported when
                evidence.Executable.Version.IsNone || evidence.Executable.Sha256.IsNone
                ->
                Error "learning-selection-executable-identity-incomplete"
            | LearningCapabilityStatus.Supported -> Ok()
            | LearningCapabilityStatus.Unsupported code when bounded code ->
                Error("learning-selection-unsupported:" + code)
            | LearningCapabilityStatus.Unknown code when bounded code -> Error("learning-selection-unknown:" + code)
            | _ -> Error "learning-selection-capability-code-invalid"

[<RequireQualifiedAccess>]
module LearningSelectionObservation =
    let private missing requested observed code =
        match requested, observed with
        | Some _, None -> Some code
        | _ -> None

    let private mismatch requested observed code =
        match requested, observed with
        | Some expected, Some actual when expected <> actual -> Some code
        | _ -> None

    let create (requested: RequestedSelection) (resolved: ResolvedSelection) (native: LearningNativeSelection) =
        let unknown =
            [
                missing requested.Model native.Model "native-model-unobserved"
                missing requested.Effort native.Effort "native-effort-unobserved"
            ]
            |> List.choose id

        let deviations =
            [
                mismatch requested.Model native.Model "native-model-mismatch"
                mismatch requested.Effort native.Effort "native-effort-mismatch"
            ]
            |> List.choose id

        let disposition =
            if not deviations.IsEmpty then
                LearningSelectionDisposition.Deviation deviations
            elif not unknown.IsEmpty then
                LearningSelectionDisposition.ObservationUnknown unknown
            else
                LearningSelectionDisposition.Matched

        {
            Requested = requested
            Resolved = resolved
            Native = native
            Disposition = disposition
        }
