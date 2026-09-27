namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

type MigrationClaimJournalNamespace =
    | ClaimJournalNamespace
    | OperationJournalNamespace

type MigrationClaimJournalSchemaFamily =
    | ClaimSchemaFamily
    | AdmissionSchemaFamily
    | OrdinarySchemaFamily
    | ReviewSchemaFamily

type MigrationClaimNativeSubjectKind =
    | NativeIssue
    | NativePullRequest

type MigrationClaimNativeStreamKind =
    | NativeIssueComments
    | NativeIssueEvents
    | NativeIssueTimeline

type MigrationClaimJournalRef =
    { ClaimRefName: string
      ClaimHeadSha: string }

type MigrationClaimJournalDecodedRecord =
    { Namespace: MigrationClaimJournalNamespace
      Schema: string
      Family: MigrationClaimJournalSchemaFamily
      CanonicalId: string
      OperationId: string option
      Generation: int64 }

type MigrationClaimJournalHistoryEntry =
    { ClaimCommitSha: string
      ClaimParentSha: string option
      ClaimTreeSha: string
      ClaimReads: MigrationReviewDeliveryRead list
      ClaimRecord: MigrationClaimJournalDecodedRecord }

type MigrationClaimJournalHistory =
    { ClaimHistoryRefName: string
      ClaimHistoryHeadSha: string
      ClaimEntries: MigrationClaimJournalHistoryEntry list }

type MigrationClaimJournalNamespaceCensus =
    { ClaimNamespace: MigrationClaimJournalNamespace
      ClaimPrefix: string
      ClaimNamespaceReads: MigrationReviewDeliveryRead list
      ClaimRefs: MigrationClaimJournalRef list }

type MigrationClaimJournalPass =
    { ClaimRepository: MigrationReviewDeliveryRepository
      ClaimNamespaces: MigrationClaimJournalNamespaceCensus list
      ClaimHistories: MigrationClaimJournalHistory list
      ClaimFingerprint: string }

type MigrationClaimJournalTwoPass =
    { ClaimFirst: MigrationClaimJournalPass
      ClaimSecond: MigrationClaimJournalPass }

type MigrationLegacyReceiptSource =
    { ProducerId: string
      ProducerRevision: string
      SourceIdentity: string
      SchemaFamily: string }

type MigrationLegacyReceiptInventory =
    { ProducerReads: MigrationReviewDeliveryRead list
      Sources: MigrationLegacyReceiptSource list
      Fingerprint: string }

[<RequireQualifiedAccess>]
module MigrationClaimEventCaptureContract =
    let claimRefPrefix = "refs/heads/fsgg/v2/journal/claim/"
    let operationRefPrefix = "refs/heads/fsgg/v2/journal/operation/"
    let journalRefPrefixes = [ claimRefPrefix; operationRefPrefix ]

    let namespacePrefix = function
        | ClaimJournalNamespace -> claimRefPrefix
        | OperationJournalNamespace -> operationRefPrefix

    let requiredNativeStreams (_: MigrationClaimNativeSubjectKind) =
        [ NativeIssueComments; NativeIssueEvents; NativeIssueTimeline ]

    let schemaFamilyAllowed namespace' family =
        match namespace', family with
        | ClaimJournalNamespace, ClaimSchemaFamily -> true
        | OperationJournalNamespace, (AdmissionSchemaFamily | OrdinarySchemaFamily | ReviewSchemaFamily) -> true
        | _ -> false

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private oid value =
        not (String.IsNullOrWhiteSpace value) && value.Length = 40
        && value |> Seq.forall Uri.IsHexDigit

    let private readParts (read: MigrationReviewDeliveryRead) =
        [ MigrationReviewDeliveryCaptureContract.requestSha256 read.Request
          read.RequestSha256; string read.StatusCode; read.RawSha256
          defaultArg read.NextRequestUri "" ]

    let private namespaceText = function
        | ClaimJournalNamespace -> "claim"
        | OperationJournalNamespace -> "operation"

    let private familyText = function
        | ClaimSchemaFamily -> "claim"
        | AdmissionSchemaFamily -> "admission"
        | OrdinarySchemaFamily -> "ordinary"
        | ReviewSchemaFamily -> "review"

    let passFingerprint (pass: MigrationClaimJournalPass) =
        let historyParts =
            pass.ClaimHistories
            |> List.collect (fun history ->
                [ history.ClaimHistoryRefName; history.ClaimHistoryHeadSha ]
                @ (history.ClaimEntries
                   |> List.collect (fun entry ->
                       [ entry.ClaimCommitSha; defaultArg entry.ClaimParentSha ""; entry.ClaimTreeSha
                         namespaceText entry.ClaimRecord.Namespace; entry.ClaimRecord.Schema
                         familyText entry.ClaimRecord.Family; entry.ClaimRecord.CanonicalId
                         defaultArg entry.ClaimRecord.OperationId ""; string entry.ClaimRecord.Generation ]
                       @ (entry.ClaimReads |> List.collect readParts))))
        [ string pass.ClaimRepository.RepositoryId; pass.ClaimRepository.NodeId; pass.ClaimRepository.FullName ]
        @ readParts pass.ClaimRepository.Read
        @ (pass.ClaimNamespaces |> List.collect (fun census ->
            [ namespaceText census.ClaimNamespace; census.ClaimPrefix ]
            @ (census.ClaimNamespaceReads |> List.collect readParts)
            @ (census.ClaimRefs |> List.collect (fun item -> [ item.ClaimRefName; item.ClaimHeadSha ]))))
        @ historyParts
        |> List.map frame |> String.concat "" |> sha

    let private validRead (read: MigrationReviewDeliveryRead) =
        read.StatusCode = 200
        && read.RequestSha256 = MigrationReviewDeliveryCaptureContract.requestSha256 read.Request
        && read.RawSha256 = sha read.RawBody

    let private validNamespaceRead (repository: MigrationReviewDeliveryRepository) census =
        match census.ClaimNamespaceReads with
        | [ read ] ->
            let suffix =
                let segments = repository.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries)
                if segments.Length <> 2 then ""
                else
                    let relative = namespacePrefix census.ClaimNamespace |> fun value -> value.Substring("refs/".Length)
                    $"/repos/{Uri.EscapeDataString segments.[0]}/{Uri.EscapeDataString segments.[1]}/git/matching-refs/{relative}"
            match Uri.TryCreate(read.Request.Uri, UriKind.Absolute) with
            | true, uri ->
                not (String.IsNullOrEmpty suffix)
                && uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal)
                && String.IsNullOrEmpty uri.Query
                && read.Request.Kind = "rest" && read.Request.Method = "Get"
                && read.Request.Body.IsNone && read.Request.Variables.IsEmpty
                && read.NextRequestUri.IsNone && validRead read
            | _ -> false
        | _ -> false

    let private validatePass (pass: MigrationClaimJournalPass) =
        let expectedNamespaces = [ ClaimJournalNamespace; OperationJournalNamespace ]
        let actualNamespaces = pass.ClaimNamespaces |> List.map _.ClaimNamespace
        let refs = pass.ClaimNamespaces |> List.collect _.ClaimRefs
        let refNames = refs |> List.map _.ClaimRefName
        let historyNames = pass.ClaimHistories |> List.map _.ClaimHistoryRefName
        let namespacesValid =
            actualNamespaces = expectedNamespaces
            && pass.ClaimNamespaces |> List.forall (fun census ->
                census.ClaimPrefix = namespacePrefix census.ClaimNamespace
                && validNamespaceRead pass.ClaimRepository census
                && census.ClaimRefs |> List.forall (fun item ->
                    item.ClaimRefName.StartsWith(census.ClaimPrefix, StringComparison.Ordinal)
                    && item.ClaimRefName.Length > census.ClaimPrefix.Length && oid item.ClaimHeadSha))
        let uniqueRefs = refNames.Length = (refNames |> Set.ofList |> Set.count)
        let completeHistories = List.sort refNames = List.sort historyNames
        let historiesValid =
            pass.ClaimHistories |> List.forall (fun history ->
                let historyNamespace =
                    if history.ClaimHistoryRefName.StartsWith(claimRefPrefix, StringComparison.Ordinal) then Some ClaimJournalNamespace
                    elif history.ClaimHistoryRefName.StartsWith(operationRefPrefix, StringComparison.Ordinal) then Some OperationJournalNamespace
                    else None
                match history.ClaimEntries with
                | [] -> false
                | entries ->
                    history.ClaimHistoryHeadSha = entries.Head.ClaimCommitSha && oid history.ClaimHistoryHeadSha
                    && entries |> List.forall (fun entry ->
                        oid entry.ClaimCommitSha && oid entry.ClaimTreeSha
                        && entry.ClaimReads |> List.forall validRead
                        && entry.ClaimRecord.Generation >= 0L
                        && not (String.IsNullOrWhiteSpace entry.ClaimRecord.Schema)
                        && not (String.IsNullOrWhiteSpace entry.ClaimRecord.CanonicalId)
                        && Some entry.ClaimRecord.Namespace = historyNamespace
                        && schemaFamilyAllowed entry.ClaimRecord.Namespace entry.ClaimRecord.Family)
                    && entries
                       |> List.pairwise
                       |> List.forall (fun (current, next) ->
                           current.ClaimParentSha = Some next.ClaimCommitSha
                           && current.ClaimRecord.Generation > next.ClaimRecord.Generation)
                    && entries |> List.last |> _.ClaimParentSha |> Option.isNone)
        if not namespacesValid then Error "claim-journal-namespace-census"
        elif not uniqueRefs then Error "claim-journal-duplicate-ref"
        elif not completeHistories then Error "claim-journal-history-roster"
        elif not historiesValid then Error "claim-journal-history"
        elif pass.ClaimFingerprint <> passFingerprint pass then Error "claim-journal-fingerprint"
        else Ok pass

    let validateTwoPass value =
        validatePass value.ClaimFirst
        |> Result.bind (fun _ -> validatePass value.ClaimSecond)
        |> Result.bind (fun _ ->
            if value.ClaimFirst <> value.ClaimSecond then Error "claim-journal-pass-drift"
            else Ok value)
