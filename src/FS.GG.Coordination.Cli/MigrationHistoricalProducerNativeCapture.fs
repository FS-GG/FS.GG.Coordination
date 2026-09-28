namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Text.RegularExpressions
open System.Threading.Tasks

type MigrationHistoricalProducerNativeResponse =
    {
        StatusCode: int
        FinalUri: string
        Headers: Map<string, string>
        PayloadBytes: byte array
    }

type IMigrationHistoricalProducerNativeTransport =
    abstract StartFreshPass: passOrdinal: int -> Result<string, string>

    abstract Get:
        passIdentity: string * requestedUri: string -> Result<MigrationHistoricalProducerNativeResponse, string>

type HttpMigrationHistoricalProducerNativeTransport(client: HttpClient, token: string) =
    interface IMigrationHistoricalProducerNativeTransport with
        member _.StartFreshPass passOrdinal =
            if passOrdinal <> 1 && passOrdinal <> 2 then
                Error "pass-ordinal"
            else
                Ok($"pass-{passOrdinal}-{Guid.NewGuid():N}")

        member _.Get(passIdentity, requestedUri) =
            try
                if String.IsNullOrWhiteSpace passIdentity || String.IsNullOrWhiteSpace token then
                    Error "credential-or-pass-unavailable"
                else
                    use request = new HttpRequestMessage(HttpMethod.Get, requestedUri)
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                    request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
                    request.Headers.Add("X-GitHub-Api-Version", "2022-11-28")
                    request.Headers.Add("User-Agent", "fsgg-gs2-09-7-historical-producer-capture")
                    request.Headers.CacheControl <- CacheControlHeaderValue(NoCache = true, NoStore = true)
                    use response = client.Send(request)
                    let bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()

                    let headers =
                        seq {
                            yield!
                                response.Headers
                                |> Seq.map (fun item -> item.Key.ToLowerInvariant(), String.concat "," item.Value)

                            yield!
                                response.Content.Headers
                                |> Seq.map (fun item -> item.Key.ToLowerInvariant(), String.concat "," item.Value)
                        }
                        |> Map.ofSeq

                    Ok
                        {
                            StatusCode = int response.StatusCode
                            FinalUri = response.RequestMessage.RequestUri.AbsoluteUri
                            Headers = headers
                            PayloadBytes = bytes
                        }
            with
            | :? HttpRequestException
            | :? TaskCanceledException
            | :? InvalidOperationException as error -> Error(error.GetType().Name)

type MigrationHistoricalProducerNativePass =
    {
        PassIdentity: string
        Pages: MigrationHistoricalProducerNativePage list
    }

type MigrationHistoricalProducerNativeCapture =
    {
        FirstPass: MigrationHistoricalProducerNativePass
        SecondPass: MigrationHistoricalProducerNativePass
        Plan: MigrationHistoricalProducerRosterPlan
    }

[<RequireQualifiedAccess>]
module MigrationHistoricalProducerNativeCapture =
    let private subjects =
        [
            "FS-GG/FS.GG.Coordination", "issue", 274
            "FS-GG/Net", "issue", 71
            "FS-GG/.github", "pull-request", 2214
            "FS-GG/.github", "pull-request", 2879
            "FS-GG/.github", "pull-request", 3231
        ]

    let private refuse reason =
        Error $"historical-producer-native-capture-refused:{reason}"

    let private uri repository number page =
        let suffix = if page = 1 then "" else $"&page={page}"
        $"https://api.github.com/repos/{repository}/issues/{number}/comments?per_page=100{suffix}"

    let private parseLinks repository number currentPage (headers: Map<string, string>) =
        match Map.tryFind "link" headers with
        | None -> Ok None
        | Some value when String.IsNullOrWhiteSpace value -> refuse "malformed-link"
        | Some value ->
            let pieces = value.Split(',', StringSplitOptions.RemoveEmptyEntries)

            let parsed =
                pieces
                |> Array.map (fun piece ->
                    let matched =
                        Regex.Match(piece, "^\\s*<([^>]+)>;\\s*rel=\"(first|prev|next|last)\"\\s*$")

                    if matched.Success then
                        Ok(matched.Groups[2].Value, matched.Groups[1].Value)
                    else
                        refuse "malformed-link")

            match
                parsed
                |> Array.tryPick (function
                    | Error error -> Some error
                    | _ -> None)
            with
            | Some error -> Error error
            | None ->
                let links =
                    parsed
                    |> Array.choose (function
                        | Ok item -> Some item
                        | _ -> None)

                if links.Length <> (links |> Array.map fst |> Set.ofArray |> Set.count) then
                    refuse "duplicate-link-relation"
                elif
                    links
                    |> Array.exists (fun (relation, value) ->
                        let expectedPage =
                            match relation with
                            | "next" -> currentPage + 1
                            | "prev" -> max 1 (currentPage - 1)
                            | "first" -> 1
                            | "last" ->
                                let marker = "&page="
                                let index = value.LastIndexOf(marker, StringComparison.Ordinal)

                                if index < 0 then
                                    0
                                else
                                    match Int32.TryParse(value.Substring(index + marker.Length)) with
                                    | true, parsed -> parsed
                                    | _ -> 0
                            | _ -> 0

                        expectedPage <= 0 || value <> uri repository number expectedPage)
                then
                    refuse "escaped-or-nonsequential-link"
                else
                    Ok(links |> Array.tryFind (fst >> (=) "next") |> Option.map snd)

    let private readSubject
        passIdentity
        repository
        kind
        number
        (transport: IMigrationHistoricalProducerNativeTransport)
        =
        let rec loop pageNumber visited accumulated =
            if pageNumber > 100 then
                refuse "page-limit"
            else
                let requested = uri repository number pageNumber

                if Set.contains requested visited then
                    refuse "duplicate-page"
                else
                    match transport.Get(passIdentity, requested) with
                    | Error reason -> refuse $"transport:{reason}"
                    | Ok response when response.StatusCode <> 200 -> refuse $"http-status-{response.StatusCode}"
                    | Ok response when response.FinalUri <> requested -> refuse "redirect-or-request-drift"
                    | Ok response when isNull response.PayloadBytes || response.PayloadBytes.Length > 16 * 1024 * 1024 ->
                        refuse "payload-size"
                    | Ok response ->
                        parseLinks repository number pageNumber response.Headers
                        |> Result.bind (fun next ->
                            let current =
                                {
                                    Repository = repository
                                    SubjectKind = kind
                                    SubjectNumber = number
                                    RequestedUri = requested
                                    NextUri = next
                                    PayloadBytes = Array.copy response.PayloadBytes
                                }

                            match next with
                            | None -> Ok(accumulated @ [ current ])
                            | Some _ -> loop (pageNumber + 1) (Set.add requested visited) (accumulated @ [ current ]))

        loop 1 Set.empty []

    let private readPass ordinal (transport: IMigrationHistoricalProducerNativeTransport) =
        match transport.StartFreshPass ordinal with
        | Error reason -> refuse $"fresh-pass:{reason}"
        | Ok identity when String.IsNullOrWhiteSpace identity -> refuse "fresh-pass-identity"
        | Ok identity ->
            subjects
            |> List.fold
                (fun state (repository, kind, number) ->
                    state
                    |> Result.bind (fun pages ->
                        readSubject identity repository kind number transport
                        |> Result.map (fun captured -> pages @ captured)))
                (Ok [])
            |> Result.map (fun pages ->
                {
                    PassIdentity = identity
                    Pages = pages
                })

    let capture proposal sourceReads (transport: IMigrationHistoricalProducerNativeTransport) =
        readPass 1 transport
        |> Result.bind (fun first ->
            readPass 2 transport
            |> Result.bind (fun second ->
                if first.PassIdentity = second.PassIdentity then
                    refuse "fresh-pass-identity-reused"
                else
                    MigrationHistoricalProducerRosterEvidence.prepare proposal sourceReads first.Pages second.Pages
                    |> Result.mapError (fun reason -> $"historical-producer-native-capture-refused:{reason}")
                    |> Result.map (fun plan ->
                        {
                            FirstPass = first
                            SecondPass = second
                            Plan = plan
                        })))
