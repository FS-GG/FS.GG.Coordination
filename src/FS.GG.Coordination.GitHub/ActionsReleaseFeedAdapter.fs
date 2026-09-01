namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type ArtifactSurface = ActionsRuns | Checks | MergeGroups | Releases | Attestations | Packages | Feeds | ServedDownloads
type LifecycleOutcome = Requested | Queued | InProgress | Completed | Skipped | Cancelled | Stale | Neutral | TimedOut | ActionRequired
type ArtifactState = Present | Immutable | Deleted | Tampered | Expired
type ArtifactEvidence = { Surface: ArtifactSurface; Identity: string; Repository: string; Subject: string; Attempt: int option; Lifecycle: LifecycleOutcome option; State: ArtifactState; Attributes: Map<string, string>; Digest: string option }
type ArtifactSurfaceObservation = Supported of Revision: string * Complete: bool * Pages: int * Evidence: ArtifactEvidence list | Unauthorized of reason: string | Unavailable of reason: string | Incomplete of reason: string | ExpiredSurface of reason: string | DeletedSurface of reason: string | Unreadable of reason: string | StaleSurface of expectedRevision: string * actualRevision: string
type ActionsReleaseFeedObservation = { Repository: string; RepositoryNodeId: string; CapturedRevision: string; Surfaces: Map<ArtifactSurface, ArtifactSurfaceObservation>; Fingerprint: string }
type RetrievalClass = AuthenticatedPackage | AuthenticatedFeed | AnonymousPublic
type ServedContent = { RequestUri: string; Redirects: string list; FinalUri: string; Status: int; ContentType: string; Length: int64; Retrieval: RetrievalClass; Sha256: string }
type EvidenceStage = UploadAccepted of requestId: string | DurableMetadata of identity: string * digest: string | AuthenticatedRetrieval of ServedContent | PublicServedBytes of ServedContent
type ArtifactFailure = InvalidRepository | MissingSurface of ArtifactSurface | PartialSurface of ArtifactSurface * string | DuplicateIdentity of ArtifactSurface * string | IdentityDrift of ArtifactSurface * string | InvalidLifecycle of ArtifactSurface * string | InvalidDigest of ArtifactSurface * string | InvalidAttestation of string | InvalidPackageCoordinates of string | InvalidRedirect of string | SecretMaterialForbidden | InvalidFingerprint

[<RequireQualifiedAccess>]
module ActionsReleaseFeedAdapter =
    let surfaces = [ ActionsRuns; Checks; MergeGroups; Releases; Attestations; Packages; Feeds; ServedDownloads ]
    let surfaceId = function ActionsRuns -> "actions-runs" | Checks -> "checks" | MergeGroups -> "merge-groups" | Releases -> "releases" | Attestations -> "attestations" | Packages -> "packages" | Feeds -> "feeds" | ServedDownloads -> "served-downloads"
    let lifecycleId = function Requested -> "requested" | Queued -> "queued" | InProgress -> "in-progress" | Completed -> "completed" | Skipped -> "skipped" | Cancelled -> "cancelled" | Stale -> "stale" | Neutral -> "neutral" | TimedOut -> "timed-out" | ActionRequired -> "action-required"
    let sha256 (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let private textHash (value: string) = value |> Encoding.UTF8.GetBytes |> sha256
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private enc (value: string) = Convert.ToBase64String(Encoding.UTF8.GetBytes value)
    let private stateId = function Present -> "present" | Immutable -> "immutable" | Deleted -> "deleted" | Tampered -> "tampered" | Expired -> "expired"
    let private digestValid (value: string) = value.Length = 64 && value |> Seq.forall Uri.IsHexDigit
    let private forbiddenKey (key: string) =
        [ "authorization"; "credential"; "password"; "secret"; "token" ]
        |> List.exists (fun word -> key.Contains(word, StringComparison.OrdinalIgnoreCase))
    let private evidenceText evidence =
        let attrs = evidence.Attributes |> Map.toList |> List.sortBy fst |> List.map (fun (key, value) -> $"{enc key}={enc value}") |> String.concat ","
        String.concat "|" [ surfaceId evidence.Surface; enc evidence.Identity; enc evidence.Repository; enc evidence.Subject; evidence.Attempt |> Option.map string |> Option.defaultValue "-"; evidence.Lifecycle |> Option.map lifecycleId |> Option.defaultValue "-"; stateId evidence.State; attrs; evidence.Digest |> Option.defaultValue "-" ]
    let private observationText = function
        | Supported(revision, complete, pages, evidence) -> $"supported|{enc revision}|{complete}|{pages}|" + (evidence |> List.sortBy (fun value -> value.Identity, value.Attempt) |> List.map evidenceText |> String.concat ";")
        | Unauthorized reason -> $"unauthorized|{enc reason}"
        | Unavailable reason -> $"unavailable|{enc reason}"
        | Incomplete reason -> $"incomplete|{enc reason}"
        | ExpiredSurface reason -> $"expired|{enc reason}"
        | DeletedSurface reason -> $"deleted|{enc reason}"
        | Unreadable reason -> $"unreadable|{enc reason}"
        | StaleSurface(expected, actual) -> $"stale|{enc expected}|{enc actual}"
    let fingerprint repository repositoryNodeId capturedRevision surfaceMap =
        let body =
            surfaces
            |> List.map (fun surface -> $"{surfaceId surface}=" + (surfaceMap |> Map.tryFind surface |> Option.map observationText |> Option.defaultValue "missing"))
            |> String.concat "\n"
        textHash $"{enc repository}|{enc repositoryNodeId}|{enc capturedRevision}\n{body}\n"
    let private validateEvidence repository surface evidence =
        if evidence.Surface <> surface || evidence.Repository <> repository then Error(IdentityDrift(surface, evidence.Identity))
        elif [ evidence.Identity; evidence.Repository; evidence.Subject ] |> List.exists (validText >> not) then Error(IdentityDrift(surface, evidence.Identity))
        elif evidence.Attributes |> Map.exists (fun key _ -> forbiddenKey key) then Error SecretMaterialForbidden
        elif evidence.Digest |> Option.exists (digestValid >> not) then Error(InvalidDigest(surface, evidence.Identity))
        elif (surface = ActionsRuns || surface = Checks) && evidence.Lifecycle.IsNone then Error(InvalidLifecycle(surface, evidence.Identity))
        elif surface = ActionsRuns && (evidence.Attempt |> Option.defaultValue 0) < 1 then Error(InvalidLifecycle(surface, evidence.Identity))
        elif surface = Attestations && not (evidence.Attributes.ContainsKey "predicate" && evidence.Attributes.ContainsKey "subject-digest" && evidence.Digest.IsSome) then Error(InvalidAttestation evidence.Identity)
        elif (surface = Packages || surface = Feeds) && not ([ "owner"; "name"; "version"; "feed" ] |> List.forall evidence.Attributes.ContainsKey) then Error(InvalidPackageCoordinates evidence.Identity)
        else Ok ()
    let validate observation =
        if [ observation.Repository; observation.RepositoryNodeId; observation.CapturedRevision ] |> List.exists (validText >> not) then Error InvalidRepository
        else
            let folder state surface =
                state |> Result.bind (fun () ->
                    match observation.Surfaces |> Map.tryFind surface with
                    | None -> Error(MissingSurface surface)
                    | Some(Supported(_, false, _, _)) -> Error(PartialSurface(surface, "pagination incomplete"))
                    | Some(Supported(revision, true, pages, _)) when revision <> observation.CapturedRevision || pages < 1 -> Error(PartialSurface(surface, "revision or page identity drift"))
                    | Some(Supported(_, true, _, evidence)) ->
                        match evidence |> List.groupBy (fun value -> value.Identity, value.Attempt) |> List.tryFind (fun (_, values) -> values.Length > 1) with
                        | Some((identity, _), _) -> Error(DuplicateIdentity(surface, identity))
                        | None -> evidence |> List.fold (fun result value -> result |> Result.bind (fun () -> validateEvidence observation.Repository surface value)) (Ok ())
                    | Some(Unauthorized reason) -> Error(PartialSurface(surface, reason))
                    | Some(Unavailable reason) -> Error(PartialSurface(surface, reason))
                    | Some(Incomplete reason) -> Error(PartialSurface(surface, reason))
                    | Some(ExpiredSurface reason) -> Error(PartialSurface(surface, reason))
                    | Some(DeletedSurface reason) -> Error(PartialSurface(surface, reason))
                    | Some(Unreadable reason) -> Error(PartialSurface(surface, reason))
                    | Some(StaleSurface(expected, actual)) -> Error(PartialSurface(surface, $"stale {expected} {actual}")))
            match surfaces |> List.fold folder (Ok ()) with
            | Error failure -> Error failure
            | Ok () when observation.Fingerprint <> fingerprint observation.Repository observation.RepositoryNodeId observation.CapturedRevision observation.Surfaces -> Error InvalidFingerprint
            | Ok () -> Ok observation
    let observeServedContent requestUri redirects finalUri status contentType retrieval (bytes: byte array) =
        let absolute value =
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttps -> true
            | _ -> false
        if not (absolute requestUri) || not (absolute finalUri) || redirects |> List.exists (absolute >> not) then Error(InvalidRedirect finalUri)
        elif status < 200 || status > 299 || not (validText contentType) then Error(InvalidRedirect finalUri)
        else Ok { RequestUri = requestUri; Redirects = redirects; FinalUri = finalUri; Status = status; ContentType = contentType; Length = int64 bytes.LongLength; Retrieval = retrieval; Sha256 = sha256 bytes }
    let validateStages stages =
        let rank = function UploadAccepted _ -> 0 | DurableMetadata _ -> 1 | AuthenticatedRetrieval _ -> 2 | PublicServedBytes _ -> 3
        let ranks = stages |> List.map rank
        if ranks <> List.sort ranks || (ranks |> List.distinct).Length <> ranks.Length then Error(InvalidRedirect "evidence stage order")
        elif stages |> List.exists (function DurableMetadata(_, digest) -> not (digestValid digest) | AuthenticatedRetrieval content | PublicServedBytes content -> not (digestValid content.Sha256) | _ -> false) then Error(InvalidDigest(ServedDownloads, "evidence-stage"))
        elif stages |> List.exists (function PublicServedBytes content -> content.Retrieval <> AnonymousPublic | AuthenticatedRetrieval content -> content.Retrieval = AnonymousPublic | _ -> false) then Error(InvalidRedirect "retrieval class")
        else Ok stages
