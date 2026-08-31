namespace FS.GG.Coordination.GitHub

open System

type ApiVersion = private ApiVersion of string

[<RequireQualifiedAccess>]
module ApiVersion =
    val required: ApiVersion
    val value: ApiVersion -> string
    val tryCreate: string -> Result<ApiVersion, string>

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

type RequestFailure =
    | MissingApiVersion
    | InvalidUri
    | MissingGraphQLDocument
    | MissingIdempotencyKey

type TransportOutcome = Response of ResponseEnvelope | NetworkFailure | TimedOut
type RetryStop = NotTransient | ReplayForbidden | AttemptsExhausted
type RetryDecision = RetryAfter of TimeSpan | Stop of RetryStop

type ExpectedRevision = Unconditional | IfMatch of string
type ObservedRevision = RevisionAbsent | RevisionValue of string | RevisionUnreadable
type RevisionDecision = RevisionAccepted | RevisionMissing | RevisionStale of observed: string | RevisionUnknown

type RateRefusal = MissingRateFacts | InvalidRateFacts | RateExhausted | CostExceedsRemaining
type RateDecision = Scheduled of remainingAfter: int | Refused of RateRefusal

type RestPage<'item> =
    { Uri: Uri
      Items: 'item list
      Next: Uri option }

type GraphQLPage<'item> =
    { Cursor: string option
      Items: 'item list
      HasNextPage: bool
      EndCursor: string option }

type PaginationFailure = MissingPage | RepeatedContinuation | MissingContinuation | UnexpectedContinuation | MalformedContinuation | AmbiguousContinuationMapping

type FieldClassification = Public | Secret | Private | Unstable | Unclassified
type FixtureField = { Path: string; Value: string; Classification: FieldClassification }
type CapturedFixture = { Request: FixtureField list; Response: FixtureField list }
type FixtureFailure = UnclassifiedField of string | SensitiveFieldMisclassified of string

[<RequireQualifiedAccess>]
module Transport =
    val validateRequest: GitHubRequest -> Result<unit, RequestFailure list>
    val decideRetry: maxAttempts: int -> attempt: int -> GitHubRequest -> TransportOutcome -> RetryDecision
    val evaluateRevision: ExpectedRevision -> ObservedRevision -> RevisionDecision
    val schedule: now: DateTimeOffset -> cost: int -> RateBudget -> RateDecision
    val tryNextLink: linkHeader: string -> Result<Uri option, PaginationFailure>
    val collectRest: start: Uri -> pages: RestPage<'item> list -> Result<'item list, PaginationFailure>
    val collectGraphQL: pages: GraphQLPage<'item> list -> Result<'item list, PaginationFailure>
    val projectFixture: allowList: Set<string> -> CapturedFixture -> Result<string, FixtureFailure>
