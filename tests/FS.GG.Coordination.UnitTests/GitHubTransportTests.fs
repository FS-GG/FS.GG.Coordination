module FS.GG.Coordination.GitHubTransportTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private request idempotency =
    Rest { Method = Get; Uri = Uri("https://api.github.test/items"); Headers = Map.empty; Body = None; ApiVersion = ApiVersion.required; Idempotency = idempotency }

let private budget remaining =
    { Limit = Some 100; Remaining = Some remaining; ResetAt = Some DateTimeOffset.MaxValue; Cost = Some 1 }

[<Fact>]
let ``API version and replay authority fail closed`` () =
    Assert.Equal(Error "GitHub REST requests require API version 2022-11-28", ApiVersion.tryCreate "latest")
    let unavailable = Response { StatusCode = 503; Headers = Map.empty; Body = ""; ETag = None; RateBudget = budget 5 }
    Assert.Equal(Stop ReplayForbidden, Transport.decideRetry 3 1 (request NeverReplay) unavailable)
    match Transport.decideRetry 3 1 (request ReplaySafe) unavailable with
    | RetryAfter delay -> Assert.Equal(TimeSpan.FromSeconds 1.0, delay)
    | value -> failwith $"expected retry, got {value}"

[<Fact>]
let ``revision and rate state preserve unknown stale and exhausted meaning`` () =
    Assert.Equal(RevisionMissing, Transport.evaluateRevision (IfMatch "v2") RevisionAbsent)
    Assert.Equal(RevisionUnknown, Transport.evaluateRevision (IfMatch "v2") RevisionUnreadable)
    Assert.Equal(RevisionStale "v1", Transport.evaluateRevision (IfMatch "v2") (RevisionValue "v1"))
    Assert.Equal(Refused MissingRateFacts, Transport.schedule DateTimeOffset.UtcNow 1 { Limit = None; Remaining = None; ResetAt = None; Cost = None })
    Assert.Equal(Refused RateExhausted, Transport.schedule DateTimeOffset.UtcNow 1 (budget 0))
    Assert.Equal(Refused InvalidRateFacts, Transport.schedule DateTimeOffset.UtcNow -1 (budget 5))

[<Fact>]
let ``REST and GraphQL traversal require terminal completeness`` () =
    let first = Uri("https://api.github.test/items?page=1")
    let second = Uri("https://api.github.test/items?page=2")
    let pages: RestPage<int> list = [ { Uri = first; Items = [ 1 ]; Next = Some second }; { Uri = second; Items = [ 2 ]; Next = None } ]
    Assert.Equal(Ok [ 1; 2 ], Transport.collectRest first pages)
    Assert.Equal(Error MissingPage, Transport.collectRest first [ List.head pages ])
    Assert.Equal(Error RepeatedContinuation, Transport.collectRest first [ { Uri = first; Items = [ 1 ]; Next = Some first } ])
    Assert.Equal(Error AmbiguousContinuationMapping, Transport.collectRest first [ List.head pages; List.head pages ])
    Assert.Equal(Error MalformedPage, Transport.collectRest first [ { Uri = null; Items = [ 1 ]; Next = None } ])
    Assert.Equal(Error MalformedPage, Transport.collectRest first [ { Uri = first; Items = [ 1 ]; Next = Some null } ])
    Assert.Equal(Error MissingContinuation, Transport.collectGraphQL [ { Cursor = None; Items = [ 1 ]; HasNextPage = true; EndCursor = None } ])

[<Fact>]
let ``REST Link parsing preserves next relation and rejects ambiguous mapping`` () =
    let next = Uri("https://api.github.test/items?page=2")
    Assert.Equal(Ok(Some next), Transport.tryNextLink $"<{next.AbsoluteUri}>; rel=\"next\", <https://api.github.test/items?page=5>; rel=\"last\"")
    Assert.Equal(Error MalformedContinuation, Transport.tryNextLink "not-a-link")
    Assert.Equal(Error AmbiguousContinuationMapping, Transport.tryNextLink $"<{next.AbsoluteUri}>; rel=\"next\", <https://api.github.test/items?page=3>; rel=\"next\"")

[<Fact>]
let ``fixture projection is stable allow-listed and leak rejecting`` () =
    let allowed = Set.ofList [ "authorization"; "method" ]
    let fixture =
        { Request = [ { Path = "method"; Value = "GET"; Classification = Public }; { Path = "authorization"; Value = "bearer secret"; Classification = Secret } ]
          Response = [ { Path = "request-id"; Value = "unstable"; Classification = Unstable } ] }
    Assert.Equal(Ok "request.authorization=[REDACTED]\nrequest.method=GET\n", Transport.projectFixture allowed fixture)
    let leak = { fixture with Request = [ { Path = "authorization"; Value = "bearer secret"; Classification = Public } ] }
    Assert.Equal(Error(SensitiveFieldMisclassified "authorization"), Transport.projectFixture allowed leak)
    let malformed = { fixture with Request = [ { Path = null; Value = "value"; Classification = Public } ] }
    Assert.Equal(Error(InvalidFixtureField "<null>"), Transport.projectFixture allowed malformed)

[<Fact>]
let ``qualification rejects producer gaps and accepts two closed inventories`` () =
    let passing = GitHubTransportQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubTransportQualification.validate passing passing)
    let missing = List.tail passing
    match GitHubTransportQualification.validate missing passing with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GTQ-INVENTORY" && finding.ControlId = "generated")
    | Ok () -> failwith "a truncated generated inventory was accepted"
