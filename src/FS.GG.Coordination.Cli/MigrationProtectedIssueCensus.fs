namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type ProtectedIssueCensusSelection =
    { RunId: int64
      RunAttempt: int
      RunNonce: string
      CandidateSha: string
      WorkflowSha: string
      ApiOrigin: string
      Owner: string
      Repository: string
      RepositoryId: int64 }

type ProtectedIssueCensusPins =
    { ReaderResourceId: string
      ReaderArtifactSha256: string
      CustodyStoreResourceId: string }

type ProtectedIssueCensusRead =
    { ReadOrdinal: int64
      RequestMethod: string
      RequestUri: string
      ResponseUri: string
      StatusCode: int
      LinkHeader: string option
      RawBody: string
      RawBodyBytesBase64: string
      CustodyObjectId: string }

type ProtectedIssueCensusBatch =
    { Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      Complete: bool
      SealedPageCount: int
      Identity: ProtectedIssueCensusRead
      Pages: ProtectedIssueCensusRead list }

type IProtectedIssueCensusPort =
    abstract Describe: unit -> ProtectedIssueCensusPins
    abstract Read: ProtectedIssueCensusSelection -> ProtectedIssueCensusBatch option

type ProtectedIssueCensusProof =
    { Inspect: GitHubMigrationInspectAuthority
      CustodyObjectIds: string list
      CorpusSha256: string }

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensus =
    let private exactAtom (value: string) = not (String.IsNullOrWhiteSpace value)

    let private exactSha length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun ch ->
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private framed (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private validCapture (read: ProtectedIssueCensusRead) =
        try
            let bytes = Convert.FromBase64String read.RawBodyBytesBase64
            let text = UTF8Encoding(false, true).GetString bytes
            read.RequestMethod = "GET"
            && read.ResponseUri = read.RequestUri
            && Convert.ToBase64String bytes = read.RawBodyBytesBase64
            && text = read.RawBody
        with _ -> false

    let private validSelection (selection: ProtectedIssueCensusSelection)
                               (options: MigrationInspectProviderOptions) =
        selection.RunId > 0L && selection.RunAttempt > 0
        && exactAtom selection.RunNonce
        && exactSha 40 selection.CandidateSha && exactSha 40 selection.WorkflowSha
        && selection.RepositoryId > 0L
        && selection.RepositoryId = options.Repository.ExpectedRepositoryId
        && selection.Owner = options.Repository.Owner
        && selection.Repository = options.Repository.Repository
        && not (isNull options.Repository.ApiBase)
        && options.Repository.ApiBase.IsAbsoluteUri
        && selection.ApiOrigin = options.Repository.ApiBase.GetLeftPart(UriPartial.Authority)

    let private validPins (pins: ProtectedIssueCensusPins) =
        exactAtom pins.ReaderResourceId
        && exactSha 64 pins.ReaderArtifactSha256
        && exactAtom pins.CustodyStoreResourceId

    let private response (read: ProtectedIssueCensusRead) =
        let headers = read.LinkHeader |> Option.map (fun value -> Map.ofList [ "link", value ])
                      |> Option.defaultValue Map.empty
        Response
            { StatusCode=read.StatusCode; Headers=headers; Body=read.RawBody; ETag=None
              RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }

    let private capture (read: ProtectedIssueCensusRead) =
        Rest { Method=Get; Uri=Uri read.RequestUri; Headers=Map.empty; Body=None
               ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }, response read

    let bind (pins: ProtectedIssueCensusPins) (selection: ProtectedIssueCensusSelection)
             (port: IProtectedIssueCensusPort option) (options: MigrationInspectProviderOptions)
             (population: MigrationIssuePopulation) =
        if not (validPins pins) then Error "protected-census-pins"
        elif not (validSelection selection options) then Error "protected-census-selection"
        else
            match port with
            | None -> Error "protected-census-port-unavailable"
            | Some protectedPort ->
                try
                    if protectedPort.Describe() <> pins then Error "protected-census-installation"
                    else
                        match protectedPort.Read selection with
                        | None -> Error "protected-census-read-unavailable"
                        | Some batch when batch.Selection <> selection
                                          || batch.CustodyStoreResourceId <> pins.CustodyStoreResourceId ->
                            Error "protected-census-binding"
                        | Some batch when not batch.Complete
                                          || batch.SealedPageCount < 1
                                          || batch.SealedPageCount <> population.PageCount
                                          || batch.Pages.Length <> batch.SealedPageCount
                                          || population.Pages.Length <> population.PageCount ->
                            Error "protected-census-incomplete"
                        | Some batch ->
                            let expectedIdentity =
                                Uri(options.Repository.ApiBase,
                                    $"repos/{Uri.EscapeDataString selection.Owner}/{Uri.EscapeDataString selection.Repository}")
                            let reads = batch.Identity :: batch.Pages
                            let objectIds = reads |> List.map _.CustodyObjectId
                            let ordinals = reads |> List.map _.ReadOrdinal
                            let readShape =
                                reads |> List.forall (fun read ->
                                    read.ReadOrdinal > 0L
                                    && exactAtom read.CustodyObjectId
                                    && exactAtom read.RawBody
                                    && read.StatusCode = 200)
                            let capturesValid = reads |> List.forall validCapture
                            let ordered =
                                ordinals |> List.mapi (fun index ordinal -> ordinal = int64 (index + 1))
                                         |> List.forall id
                            let pagesMatch =
                                List.zip batch.Pages population.Pages
                                |> List.forall (fun (read, page) ->
                                    let next =
                                        Transport.tryNextLink (defaultArg read.LinkHeader "")
                                        |> Result.map (Option.map _.AbsoluteUri)
                                    read.RequestUri = page.RequestedUri
                                    && sha read.RawBody = page.PayloadSha256
                                    && next = Ok page.NextUri)
                            if not capturesValid then Error "protected-census-capture-shape"
                            elif not readShape || not ordered
                               || objectIds.Length <> (objectIds |> Set.ofList |> Set.count)
                               || batch.Identity.RequestUri <> expectedIdentity.AbsoluteUri
                               || batch.Identity.LinkHeader.IsSome
                               || not pagesMatch then Error "protected-census-object-or-page"
                            else
                                let captures = reads |> List.map capture
                                MigrationInspectProviderAdapter.bindIssues options population captures
                                |> Result.mapError (fun reason -> $"protected-census-raw-typed:{reason}")
                                |> Result.map (fun inspect ->
                                    let parts =
                                        [ "fsgg.gs2-09.7.protected-issue-census/v1"
                                          string selection.RunId; string selection.RunAttempt
                                          selection.RunNonce; selection.CandidateSha; selection.WorkflowSha
                                          selection.ApiOrigin; selection.Owner; selection.Repository
                                          string selection.RepositoryId; pins.ReaderResourceId
                                          pins.ReaderArtifactSha256; pins.CustodyStoreResourceId
                                          string batch.SealedPageCount ]
                                        @ (reads |> List.collect (fun read ->
                                            [ string read.ReadOrdinal; read.CustodyObjectId
                                              read.RequestMethod; read.RequestUri; read.ResponseUri
                                              sha read.RawBody
                                              defaultArg read.LinkHeader "" ]))
                                    { Inspect=inspect; CustodyObjectIds=objectIds
                                      CorpusSha256=parts |> List.map framed |> String.concat "" |> sha })
                with _ -> Error "protected-census-read-unavailable"
