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
      ProviderResourceId: string
      CustodyStoreResourceId: string
      CustodyStoreArtifactSha256: string
      CustodyStoreAclPolicySha256: string
      CustodyReaderPrincipalId: string
      CustodyWriterPrincipalId: string
      CandidatePrincipalId: string }

type ProtectedIssueCensusRead =
    { ReadOrdinal: int64
      RequestMethod: string
      RequestUri: string
      ResponseUri: string
      StatusCode: int
      ProviderResourceId: string
      ResponseHeaders: (string * string) list
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

type ProtectedIssueCensusStoredRead =
    { Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      Read: ProtectedIssueCensusRead }

type ProtectedIssueCensusStoreDescription =
    { ResourceId: string
      ArtifactSha256: string
      AclPolicySha256: string
      ReaderPrincipalId: string
      WriterPrincipalId: string
      CandidatePrincipalId: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      ImmutableObjects: bool }

type IProtectedIssueCensusStorePort =
    abstract Describe: unit -> ProtectedIssueCensusStoreDescription
    abstract ReadObject: ProtectedIssueCensusSelection * string -> ProtectedIssueCensusStoredRead option

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

    let private validHeaders (pins: ProtectedIssueCensusPins) (read: ProtectedIssueCensusRead) =
        try
            let headerName (name: string) =
                exactAtom name
                && (name |> Seq.forall (fun ch ->
                    (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')
                    || (ch >= '0' && ch <= '9') || ch = '-'))
            let headerValue (value: string) =
                not (isNull value)
                && (value |> Seq.forall (fun ch -> ch = '\t' || (ch >= ' ' && ch <= '~')))
            let headers = read.ResponseHeaders
            let names = headers |> List.map (fst >> _.ToLowerInvariant())
            let link =
                headers |> List.tryPick (fun (name, value) ->
                    if name.Equals("link", StringComparison.OrdinalIgnoreCase)
                    then Some value else None)
            read.ProviderResourceId = pins.ProviderResourceId
            && (headers |> List.forall (fun (name, value) -> headerName name && headerValue value))
            && names.Length = (names |> Set.ofList |> Set.count)
            && read.LinkHeader = link
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
        && exactAtom pins.ProviderResourceId
        && exactAtom pins.CustodyStoreResourceId
        && exactSha 64 pins.CustodyStoreArtifactSha256
        && exactSha 64 pins.CustodyStoreAclPolicySha256
        && exactAtom pins.CustodyReaderPrincipalId
        && exactAtom pins.CustodyWriterPrincipalId
        && exactAtom pins.CandidatePrincipalId
        && pins.CustodyReaderPrincipalId <> pins.CandidatePrincipalId
        && pins.CustodyWriterPrincipalId <> pins.CandidatePrincipalId

    let private validStoreDescription (pins: ProtectedIssueCensusPins)
                                      (description: ProtectedIssueCensusStoreDescription) =
        description.ResourceId = pins.CustodyStoreResourceId
        && description.ArtifactSha256 = pins.CustodyStoreArtifactSha256
        && description.AclPolicySha256 = pins.CustodyStoreAclPolicySha256
        && description.ReaderPrincipalId = pins.CustodyReaderPrincipalId
        && description.WriterPrincipalId = pins.CustodyWriterPrincipalId
        && description.CandidatePrincipalId = pins.CandidatePrincipalId
        && not description.CandidateMayRead
        && not description.CandidateMayWrite
        && description.ImmutableObjects

    let private preflightStore (pins: ProtectedIssueCensusPins)
                               (store: IProtectedIssueCensusStorePort option) =
        match store with
        | None -> Error "protected-census-store-unavailable"
        | Some protectedStore ->
            try
                if validStoreDescription pins (protectedStore.Describe()) then Ok ()
                else Error "protected-census-store-installation"
            with _ -> Error "protected-census-store-unavailable"

    let private response (read: ProtectedIssueCensusRead) =
        let headers =
            read.ResponseHeaders
            |> List.map (fun (name, value) -> name.ToLowerInvariant(), value)
            |> Map.ofList
        Response
            { StatusCode=read.StatusCode; Headers=headers; Body=read.RawBody; ETag=None
              RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }

    let private capture (read: ProtectedIssueCensusRead) =
        Rest { Method=Get; Uri=Uri read.RequestUri; Headers=Map.empty; Body=None
               ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }, response read

    let private verifyStoredReads (pins: ProtectedIssueCensusPins)
                                  (selection: ProtectedIssueCensusSelection)
                                  (store: IProtectedIssueCensusStorePort option)
                                  (reads: ProtectedIssueCensusRead list) =
        match store with
        | None -> Error "protected-census-store-unavailable"
        | Some protectedStore ->
            try
                if not (validStoreDescription pins (protectedStore.Describe())) then
                    Error "protected-census-store-installation"
                else
                    let rec verify remaining =
                        match remaining with
                        | [] -> Ok ()
                        | read :: rest ->
                            match protectedStore.ReadObject(selection, read.CustodyObjectId) with
                            | None -> Error "protected-census-store-unavailable"
                            | Some stored when stored.Selection <> selection
                                               || stored.CustodyStoreResourceId <> pins.CustodyStoreResourceId
                                               || stored.Read <> read ->
                                Error "protected-census-store-binding"
                            | Some _ -> verify rest
                    verify reads
            with _ -> Error "protected-census-store-unavailable"

    let bind (pins: ProtectedIssueCensusPins) (selection: ProtectedIssueCensusSelection)
             (port: IProtectedIssueCensusPort option) (store: IProtectedIssueCensusStorePort option)
             (options: MigrationInspectProviderOptions)
             (population: MigrationIssuePopulation) =
        if not (validPins pins) then Error "protected-census-pins"
        elif not (validSelection selection options) then Error "protected-census-selection"
        else
            let storeReady =
                match port with
                | None -> Ok ()
                | Some _ -> preflightStore pins store
            match port, storeReady with
            | None, _ -> Error "protected-census-port-unavailable"
            | Some _, Error reason -> Error reason
            | Some protectedPort, Ok () ->
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
                            let headersValid = reads |> List.forall (validHeaders pins)
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
                            elif not headersValid then Error "protected-census-header-or-provider"
                            elif not readShape || not ordered
                               || objectIds.Length <> (objectIds |> Set.ofList |> Set.count)
                               || batch.Identity.RequestUri <> expectedIdentity.AbsoluteUri
                               || batch.Identity.LinkHeader.IsSome
                               || not pagesMatch then Error "protected-census-object-or-page"
                            else
                                match verifyStoredReads pins selection store reads with
                                | Error reason -> Error reason
                                | Ok () ->
                                    let captures = reads |> List.map capture
                                    MigrationInspectProviderAdapter.bindIssues options population captures
                                    |> Result.mapError (fun reason -> $"protected-census-raw-typed:{reason}")
                                    |> Result.map (fun inspect ->
                                        let parts =
                                            [ "fsgg.gs2-09.7.protected-issue-census/v4"
                                              string selection.RunId; string selection.RunAttempt
                                              selection.RunNonce; selection.CandidateSha; selection.WorkflowSha
                                              selection.ApiOrigin; selection.Owner; selection.Repository
                                              string selection.RepositoryId; pins.ReaderResourceId
                                              pins.ReaderArtifactSha256; pins.ProviderResourceId
                                              pins.CustodyStoreResourceId
                                              pins.CustodyStoreArtifactSha256
                                              pins.CustodyStoreAclPolicySha256
                                              pins.CustodyReaderPrincipalId
                                              pins.CustodyWriterPrincipalId
                                              pins.CandidatePrincipalId
                                              string batch.SealedPageCount ]
                                            @ (reads |> List.collect (fun read ->
                                                [ string read.ReadOrdinal; read.CustodyObjectId
                                                  read.RequestMethod; read.RequestUri; read.ResponseUri
                                                  read.ProviderResourceId; sha read.RawBody
                                                  string read.ResponseHeaders.Length ]
                                                @ (read.ResponseHeaders |> List.collect (fun (name, value) ->
                                                    [ name; value ]))))
                                        { Inspect=inspect; CustodyObjectIds=objectIds
                                          CorpusSha256=parts |> List.map framed |> String.concat "" |> sha })
                with _ -> Error "protected-census-read-unavailable"
