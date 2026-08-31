namespace FS.GG.Coordination.GitHub

open System

type ApiVersion = private ApiVersion of string

[<RequireQualifiedAccess>]
module ApiVersion =
    let required = ApiVersion "2022-11-28"
    let value (ApiVersion value) = value
    let tryCreate value =
        if value = "2022-11-28" then Ok required
        else Error "GitHub REST requests require API version 2022-11-28"

type RestMethod = Get | Post | Put | Patch | Delete
type IdempotencyClass = ReplaySafe | ReplayWithKey of string | NeverReplay

type RestRequest =
    { Method: RestMethod
      Uri: Uri
      Headers: Map<string, string>
      Body: string option
      ApiVersion: ApiVersion
      Idempotency: IdempotencyClass }

type GraphQLRequest =
    { Uri: Uri
      Document: string
      Variables: Map<string, string>
      Headers: Map<string, string>
      ApiVersion: ApiVersion
      Idempotency: IdempotencyClass }

type GitHubRequest = Rest of RestRequest | GraphQL of GraphQLRequest

type RateBudget =
    { Limit: int option
      Remaining: int option
      ResetAt: DateTimeOffset option
      Cost: int option }

type ResponseEnvelope =
    { StatusCode: int
      Headers: Map<string, string>
      Body: string
      ETag: string option
      RateBudget: RateBudget }

type RequestFailure = MissingApiVersion | InvalidUri | MissingGraphQLDocument | MissingIdempotencyKey
type TransportOutcome = Response of ResponseEnvelope | NetworkFailure | TimedOut
type RetryStop = NotTransient | ReplayForbidden | AttemptsExhausted
type RetryDecision = RetryAfter of TimeSpan | Stop of RetryStop
type ExpectedRevision = Unconditional | IfMatch of string
type ObservedRevision = RevisionAbsent | RevisionValue of string | RevisionUnreadable
type RevisionDecision = RevisionAccepted | RevisionMissing | RevisionStale of observed: string | RevisionUnknown
type RateRefusal = MissingRateFacts | InvalidRateFacts | RateExhausted | CostExceedsRemaining
type RateDecision = Scheduled of remainingAfter: int | Refused of RateRefusal
type RestPage<'item> = { Uri: Uri; Items: 'item list; Next: Uri option }
type GraphQLPage<'item> = { Cursor: string option; Items: 'item list; HasNextPage: bool; EndCursor: string option }
type PaginationFailure = MissingPage | RepeatedContinuation | MissingContinuation | UnexpectedContinuation | MalformedContinuation | MalformedPage | AmbiguousContinuationMapping
type FieldClassification = Public | Secret | Private | Unstable | Unclassified
type FixtureField = { Path: string; Value: string; Classification: FieldClassification }
type CapturedFixture = { Request: FixtureField list; Response: FixtureField list }
type FixtureFailure = InvalidFixtureField of string | UnclassifiedField of string | SensitiveFieldMisclassified of string

[<RequireQualifiedAccess>]
module Transport =
    let private requestFacts = function
        | Rest request -> request.Uri, request.ApiVersion, request.Idempotency, None
        | GraphQL request -> request.Uri, request.ApiVersion, request.Idempotency, Some request.Document

    let validateRequest request =
        let uri, version, idempotency, document = requestFacts request
        [ if ApiVersion.value version <> ApiVersion.value ApiVersion.required then MissingApiVersion
          if isNull uri || not uri.IsAbsoluteUri then InvalidUri
          match document with
          | Some value when String.IsNullOrWhiteSpace value -> MissingGraphQLDocument
          | _ -> ()
          match idempotency with
          | ReplayWithKey value when String.IsNullOrWhiteSpace value -> MissingIdempotencyKey
          | _ -> () ]
        |> function [] -> Ok () | failures -> Error failures

    let private replayPermitted = function
        | ReplaySafe -> true
        | ReplayWithKey value -> not (String.IsNullOrWhiteSpace value)
        | NeverReplay -> false

    let private transient = function
        | NetworkFailure | TimedOut -> true
        | Response response -> response.StatusCode = 408 || response.StatusCode = 429 || response.StatusCode >= 500

    let decideRetry maxAttempts attempt request outcome =
        let _, _, idempotency, _ = requestFacts request
        if not (transient outcome) then Stop NotTransient
        elif not (replayPermitted idempotency) then Stop ReplayForbidden
        elif maxAttempts <= 0 || attempt >= maxAttempts then Stop AttemptsExhausted
        else
            let exponent = max 0 (min 10 (attempt - 1))
            RetryAfter(TimeSpan.FromSeconds(float (pown 2 exponent)))

    let evaluateRevision expected observed =
        match expected, observed with
        | Unconditional, _ -> RevisionAccepted
        | IfMatch _, RevisionAbsent -> RevisionMissing
        | IfMatch _, RevisionUnreadable -> RevisionUnknown
        | IfMatch expectedValue, RevisionValue observedValue when expectedValue = observedValue -> RevisionAccepted
        | IfMatch _, RevisionValue observedValue -> RevisionStale observedValue

    let schedule now cost budget =
        if cost < 0 then Refused InvalidRateFacts
        else
            match budget.Limit, budget.Remaining, budget.ResetAt, budget.Cost with
            | Some limit, Some remaining, Some resetAt, Some observedCost when limit >= 0 && remaining >= 0 && observedCost >= 0 && resetAt >= now ->
                let required = max cost observedCost
                if remaining = 0 then Refused RateExhausted
                elif required > remaining then Refused CostExceedsRemaining
                else Scheduled(remaining - required)
            | Some _, Some _, Some _, Some _ -> Refused InvalidRateFacts
            | _ -> Refused MissingRateFacts

    let tryNextLink (linkHeader: string) =
        if String.IsNullOrWhiteSpace linkHeader then Ok None
        else
            let parse (segment: string) =
                let value = segment.Trim()
                let close = value.IndexOf('>')
                if not (value.StartsWith("<", StringComparison.Ordinal)) || close <= 1 then Error MalformedContinuation
                else
                    let rawUri = value.Substring(1, close - 1)
                    let mutable uri = Unchecked.defaultof<Uri>
                    if not (Uri.TryCreate(rawUri, UriKind.Absolute, &uri)) then Error MalformedContinuation
                    else
                        let relations =
                            value.Substring(close + 1).Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                            |> Array.choose (fun parameter ->
                                let parts = parameter.Split('=', 2, StringSplitOptions.TrimEntries)
                                if parts.Length = 2 && parts.[0].Equals("rel", StringComparison.OrdinalIgnoreCase) then
                                    Some(parts.[1].Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray)
                                else None)
                        Ok(uri, relations |> Array.exists (Set.contains "next"))
            let parsed = linkHeader.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.map parse |> Array.toList
            match parsed |> List.tryPick (function Error error -> Some error | _ -> None) with
            | Some error -> Error error
            | None ->
                let next = parsed |> List.choose (function Ok(uri, true) -> Some uri | _ -> None)
                match next with
                | [] -> Ok None
                | [ uri ] -> Ok(Some uri)
                | _ -> Error AmbiguousContinuationMapping

    let collectRest (start: Uri) (pages: RestPage<'item> list) =
        let malformed =
            pages
            |> List.exists (fun page ->
                isNull page.Uri
                || not page.Uri.IsAbsoluteUri
                || (page.Next |> Option.exists (fun next -> isNull next || not next.IsAbsoluteUri)))
        let entries =
            if malformed then []
            else pages |> List.map (fun page -> page.Uri.AbsoluteUri, page)
        let ambiguous = entries |> List.countBy fst |> List.exists (fun (_, count) -> count <> 1)
        let indexed = entries |> Map.ofList
        let rec loop seen current collected =
            if Set.contains current seen then Error RepeatedContinuation
            else
                match Map.tryFind current indexed with
                | None -> Error MissingPage
                | Some page ->
                    let values = List.append collected page.Items
                    match page.Next with
                    | None -> Ok values
                    | Some next -> loop (Set.add current seen) next.AbsoluteUri values
        if malformed then Error MalformedPage
        elif ambiguous then Error AmbiguousContinuationMapping
        elif isNull start || not start.IsAbsoluteUri then Error MissingPage
        else loop Set.empty start.AbsoluteUri []

    let collectGraphQL (pages: GraphQLPage<'item> list) =
        let entries = pages |> List.map (fun page -> page.Cursor, page)
        let ambiguous = entries |> List.countBy fst |> List.exists (fun (_, count) -> count <> 1)
        let indexed = entries |> Map.ofList
        let rec loop seen cursor collected =
            if Set.contains cursor seen then Error RepeatedContinuation
            else
                match Map.tryFind cursor indexed with
                | None -> Error MissingPage
                | Some page ->
                    let values = List.append collected page.Items
                    match page.HasNextPage, page.EndCursor with
                    | false, None -> Ok values
                    | false, Some _ -> Error UnexpectedContinuation
                    | true, None -> Error MissingContinuation
                    | true, Some next -> loop (Set.add cursor seen) (Some next) values
        if ambiguous then Error AmbiguousContinuationMapping else loop Set.empty None []

    let private looksSensitive (path: string) (value: string) =
        let lowerPath = path.ToLowerInvariant()
        let lowerValue = value.ToLowerInvariant()
        [ "authorization"; "cookie"; "token"; "secret"; "password" ]
        |> List.exists lowerPath.Contains
        || lowerValue.StartsWith("bearer ", StringComparison.Ordinal)
        || lowerValue.StartsWith("ghp_", StringComparison.Ordinal)
        || lowerValue.StartsWith("github_pat_", StringComparison.Ordinal)

    let projectFixture (allowList: Set<string>) (fixture: CapturedFixture) =
        let project (prefix: string) (fields: FixtureField list) =
            fields
            |> List.sortBy _.Path
            |> List.fold (fun state field ->
                match state with
                | Error error -> Error error
                | Ok values ->
                    if String.IsNullOrWhiteSpace field.Path || isNull field.Value then Error(InvalidFixtureField(if isNull field.Path then "<null>" else field.Path))
                    else
                        match field.Classification with
                        | Unclassified -> Error(UnclassifiedField field.Path)
                        | Public when looksSensitive field.Path field.Value -> Error(SensitiveFieldMisclassified field.Path)
                        | Public when allowList.Contains field.Path -> Ok($"{prefix}.{field.Path}={field.Value}" :: values)
                        | Secret when allowList.Contains field.Path -> Ok($"{prefix}.{field.Path}=[REDACTED]" :: values)
                        | Public | Secret | Private | Unstable -> Ok values) (Ok [])
        match project "request" fixture.Request, project "response" fixture.Response with
        | Ok request, Ok response -> List.append request response |> List.sort |> String.concat "\n" |> fun value -> Ok(value + "\n")
        | Error error, _ | _, Error error -> Error error
