namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

type MigrationReviewDeliveryRequest =
    { Kind: string
      Method: string
      Uri: string
      Headers: Map<string, string>
      Body: string option
      Variables: Map<string, string>
      ApiVersion: string
      Idempotency: string }

type MigrationReviewDeliveryRead =
    { Request: MigrationReviewDeliveryRequest
      RequestSha256: string
      StatusCode: int
      ResponseHeaders: Map<string, string>
      RawBody: string
      RawSha256: string
      NextRequestUri: string option }

type MigrationReviewDeliveryRepository =
    { RepositoryId: int64
      NodeId: string
      FullName: string
      Read: MigrationReviewDeliveryRead }

type MigrationReviewDeliveryPullRequest =
    { Number: int
      NodeId: string
      HeadSha: string }

type MigrationReviewDeliveryRecord =
    | Review of pullRequestNumber:int * id:int64
    | InlineComment of pullRequestNumber:int * id:int64
    | CheckRun of pullRequestNumber:int * commitSha:string * id:int64 * name:string * status:string * conclusion:string option
    | CommitStatus of pullRequestNumber:int * commitSha:string * id:int64 * context:string * state:string
    | PullDelivery of pullRequestNumber:int * mergeCommit:string option
    | MergeObject of pullRequestNumber:int * mergeCommit:string
    | Tag of name:string * commitSha:string
    | Release of id:int64 * tagName:string * draft:bool

type MigrationReviewDeliveryNativeStream =
    { Kind: string
      Subject: string
      Reads: MigrationReviewDeliveryRead list
      Records: MigrationReviewDeliveryRecord list }

type MigrationReviewDeliveryNativePass =
    { Repository: MigrationReviewDeliveryRepository
      PullRequestCensus: MigrationReviewDeliveryRead list
      PullRequests: MigrationReviewDeliveryPullRequest list
      Streams: MigrationReviewDeliveryNativeStream list
      Fingerprint: string }

type MigrationReviewDeliveryNativeTwoPass =
    { First: MigrationReviewDeliveryNativePass
      Second: MigrationReviewDeliveryNativePass }

type MigrationJournalRef = { RefName: string; HeadSha: string }

type MigrationJournalDecodedRecord =
    { Schema: string
      Kind: string
      Subject: string
      OperationId: string
      Generation: int64
      MergeCommit: string option
      ProtectedRunId: int64 option
      ProtectedRunCommit: string option
      ProtectedRunConclusion: string option }

type MigrationJournalHistoryEntry =
    { RefName: string
      CommitSha: string
      ParentSha: string option
      TreeSha: string
      HeadPath: string
      EventPath: string
      Reads: MigrationReviewDeliveryRead list
      Record: MigrationJournalDecodedRecord }

type MigrationJournalNamespaceCensus =
    { Prefix: string
      Reads: MigrationReviewDeliveryRead list
      Refs: MigrationJournalRef list }

type MigrationJournalPass =
    { Repository: MigrationReviewDeliveryRepository
      Namespaces: MigrationJournalNamespaceCensus list
      Histories: MigrationJournalHistoryEntry list
      Fingerprint: string }

type MigrationJournalTwoPass = { First: MigrationJournalPass; Second: MigrationJournalPass }

type MigrationReviewDeliveryCorrespondence =
    { PullRequestNumber: int
      HeadSha: string
      MergeCommit: string option
      CheckRunIds: int64 list
      StatusIds: int64 list
      ReviewJournalRefs: string list
      DeliveryJournalRefs: string list
      TagNames: string list
      ReleaseIds: int64 list }

type MigrationReviewDeliveryCompletePass =
    { Native: MigrationReviewDeliveryNativePass
      Journals: MigrationJournalPass
      Correspondence: MigrationReviewDeliveryCorrespondence list
      Fingerprint: string }

type MigrationReviewDeliveryCompleteTwoPass =
    { First: MigrationReviewDeliveryCompletePass
      Second: MigrationReviewDeliveryCompletePass }

[<RequireQualifiedAccess>]
module MigrationReviewDeliveryCaptureContract =
    let journalRefPrefixes =
        [ "refs/heads/fsgg/v2/journal/review/"
          "refs/heads/fsgg/v2/journal/operation/" ]

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private oid (value: string) =
        not (isNull value) && value.Length = 40
        && value |> Seq.forall Uri.IsHexDigit

    let checkRunsPath repositoryPath commitSha =
        if String.IsNullOrWhiteSpace repositoryPath || not (oid commitSha) then invalidArg "commitSha" "invalid capture identity"
        $"{repositoryPath}/commits/{commitSha}/check-runs?filter=all&per_page=100"

    let statusesPath repositoryPath commitSha =
        if String.IsNullOrWhiteSpace repositoryPath || not (oid commitSha) then invalidArg "commitSha" "invalid capture identity"
        $"{repositoryPath}/commits/{commitSha}/statuses?per_page=100"

    let private safeHeaders (headers: Map<string, string>) =
        headers
        |> Map.filter (fun key _ ->
            not (String.Equals(key, "authorization", StringComparison.OrdinalIgnoreCase)))

    let captureRequest request =
        match request with
        | Rest value ->
            { Kind="rest"; Method=string value.Method; Uri=value.Uri.AbsoluteUri
              Headers=safeHeaders value.Headers; Body=value.Body; Variables=Map.empty
              ApiVersion=string value.ApiVersion; Idempotency=string value.Idempotency }
        | GraphQL value ->
            { Kind="graphql"; Method="POST"; Uri=value.Uri.AbsoluteUri
              Headers=safeHeaders value.Headers; Body=Some value.Document; Variables=value.Variables
              ApiVersion=string value.ApiVersion; Idempotency=string value.Idempotency }

    let requestSha256 (request: MigrationReviewDeliveryRequest) =
        [ request.Kind; request.Method; request.Uri; defaultArg request.Body ""
          request.ApiVersion; request.Idempotency
          request.Headers |> Map.toList |> List.map (fun (key, item) -> key + ":" + item) |> String.concat "\n"
          request.Variables |> Map.toList |> List.map (fun (key, item) -> key + ":" + item) |> String.concat "\n" ]
        |> String.concat "\n" |> sha

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private readParts (read: MigrationReviewDeliveryRead) =
        [ requestSha256 read.Request; read.RequestSha256; string read.StatusCode; read.RawSha256
          defaultArg read.NextRequestUri ""
          read.ResponseHeaders |> Map.toList |> List.map (fun (key, item) -> key + ":" + item) |> String.concat "\n" ]

    let private recordText = function
        | Review (pr, id) -> $"review|{pr}|{id}"
        | InlineComment (pr, id) -> $"inline|{pr}|{id}"
        | CheckRun (pr, commit, id, name, status, conclusion) ->
            let conclusionText = defaultArg conclusion ""
            $"check|{pr}|{commit}|{id}|{name}|{status}|{conclusionText}"
        | CommitStatus (pr, commit, id, context, state) -> $"status|{pr}|{commit}|{id}|{context}|{state}"
        | PullDelivery (pr, commit) ->
            let commitText = defaultArg commit ""
            $"pull|{pr}|{commitText}"
        | MergeObject (pr, commit) -> $"merge|{pr}|{commit}"
        | Tag (name, commit) -> $"tag|{name}|{commit}"
        | Release (id, tag, draft) -> $"release|{id}|{tag}|{draft}"

    let nativeFingerprint (pass: MigrationReviewDeliveryNativePass) =
        [ string pass.Repository.RepositoryId; pass.Repository.NodeId; pass.Repository.FullName ]
        @ readParts pass.Repository.Read
        @ (pass.PullRequestCensus |> List.collect readParts)
        @ (pass.PullRequests |> List.collect (fun item -> [ string item.Number; item.NodeId; item.HeadSha ]))
        @ (pass.Streams |> List.collect (fun (stream: MigrationReviewDeliveryNativeStream) ->
            [ stream.Kind; stream.Subject ]
            @ (stream.Reads |> List.collect readParts)
            @ (stream.Records |> List.map recordText)))
        |> List.map frame |> String.concat "" |> sha

    let journalFingerprint (pass: MigrationJournalPass) =
        [ string pass.Repository.RepositoryId; pass.Repository.NodeId; pass.Repository.FullName ]
        @ readParts pass.Repository.Read
        @ (pass.Namespaces |> List.collect (fun (census: MigrationJournalNamespaceCensus) ->
            [ census.Prefix ] @ (census.Reads |> List.collect readParts)
            @ (census.Refs |> List.collect (fun item -> [ item.RefName; item.HeadSha ]))))
        @ (pass.Histories |> List.collect (fun (item: MigrationJournalHistoryEntry) ->
            [ item.RefName; item.CommitSha; defaultArg item.ParentSha ""; item.TreeSha
              item.HeadPath; item.EventPath; item.Record.Schema; item.Record.Kind; item.Record.Subject
              item.Record.OperationId; string item.Record.Generation; defaultArg item.Record.MergeCommit ""
              item.Record.ProtectedRunId |> Option.map string |> Option.defaultValue ""
              defaultArg item.Record.ProtectedRunCommit ""; defaultArg item.Record.ProtectedRunConclusion "" ]
            @ (item.Reads |> List.collect readParts)))
        |> List.map frame |> String.concat "" |> sha

    let private validTerminal (reads: MigrationReviewDeliveryRead list) =
        not (List.isEmpty reads)
        && reads |> List.take (reads.Length - 1) |> List.forall (_.NextRequestUri >> Option.isSome)
        && reads |> List.last |> _.NextRequestUri |> Option.isNone
        && reads |> List.forall (fun read ->
            read.RequestSha256 = requestSha256 read.Request
            && read.RawSha256 = sha read.RawBody
            && read.StatusCode = 200)

    let validateNativeTwoPass (value: MigrationReviewDeliveryNativeTwoPass) =
        let valid (pass: MigrationReviewDeliveryNativePass) =
            pass.Fingerprint = nativeFingerprint pass
            && pass.Repository.Read.RequestSha256 = requestSha256 pass.Repository.Read.Request
            && pass.Repository.Read.RawSha256 = sha pass.Repository.Read.RawBody
            && validTerminal pass.PullRequestCensus
            && pass.Streams |> List.forall (_.Reads >> validTerminal)
        if not (valid value.First && valid value.Second) then Error "review-delivery-native-invalid"
        elif value.First <> value.Second then Error "review-delivery-native-pass-drift"
        else Ok value

    let validateJournalTwoPass (value: MigrationJournalTwoPass) =
        let prefixes = value.First.Namespaces |> List.map _.Prefix
        let valid (pass: MigrationJournalPass) =
            pass.Fingerprint = journalFingerprint pass
            && pass.Repository.Read.RequestSha256 = requestSha256 pass.Repository.Read.Request
            && pass.Repository.Read.RawSha256 = sha pass.Repository.Read.RawBody
            && (pass.Namespaces |> List.map _.Prefix) = journalRefPrefixes
            && pass.Namespaces |> List.forall (_.Reads >> validTerminal)
            && pass.Histories |> List.forall (fun item -> not (List.isEmpty item.Reads))
        if prefixes <> journalRefPrefixes || not (valid value.First && valid value.Second) then
            Error "review-delivery-journal-invalid"
        elif value.First <> value.Second then Error "review-delivery-journal-pass-drift"
        else Ok value
