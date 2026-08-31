#load "../src/FS.GG.Coordination.GitHub/Transport.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubTransportQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let fixturePath = Path.Combine(root, "tests/fixtures/github-transport/contract.json")
if not (File.Exists fixturePath) then fail "GTQ-FIXTURE-MISSING" fixturePath

let fixture = JsonDocument.Parse(File.ReadAllBytes fixturePath)
let json = fixture.RootElement
let exactNames = json.EnumerateObject() |> Seq.map _.Name |> Seq.toList
if exactNames <> [ "controls"; "graph"; "redaction"; "rest"; "schema" ] then fail "GTQ-FIXTURE-SHAPE" (String.concat "," exactNames)
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-transport-fixture/1" then fail "GTQ-FIXTURE-SCHEMA" fixturePath

let fixtureControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let required = GitHubTransportQualification.requiredControls |> List.map GitHubTransportQualification.controlId
if fixtureControls <> required then fail "GTQ-FIXTURE-INVENTORY" (String.concat "," fixtureControls)

// Generated controls derive their inventory and representative values from the committed fixture.
let generatedResults () =
    let outcome red green control = { Control = control; MutationRed = red; BaselineGreen = green }
    let rest = json.GetProperty("rest")
    let first = Uri(rest.GetProperty("first").GetString())
    let second = Uri(rest.GetProperty("second").GetString())
    let complete: RestPage<string> list =
        [ { Uri = first; Items = [ "one" ]; Next = Some second }
          { Uri = second; Items = [ "two" ]; Next = None } ]
    let request replay =
        Rest { Method = Get; Uri = first; Headers = Map.empty; Body = None; ApiVersion = ApiVersion.required; Idempotency = replay }
    let budget remaining =
        { Limit = Some 100; Remaining = Some remaining; ResetAt = Some(DateTimeOffset.MaxValue); Cost = Some 1 }
    let response = Response { StatusCode = 502; Headers = Map.empty; Body = ""; ETag = None; RateBudget = budget 10 }
    let graph document =
        GraphQL { Uri = Uri("https://api.github.test/graphql"); Document = document; Variables = Map.empty; Headers = Map.empty; ApiVersion = ApiVersion.required; Idempotency = ReplaySafe }
    let allowList = json.GetProperty("redaction").GetProperty("allowList").EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
    let secret = json.GetProperty("redaction").GetProperty("secret").GetString()
    let clean: CapturedFixture = { Request = [ { Path = "authorization"; Value = secret; Classification = Secret } ]; Response = [] }
    let leak: CapturedFixture = { Request = [ { Path = "authorization"; Value = secret; Classification = Public } ]; Response = [] }
    let now = DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero)
    [ outcome (Transport.collectRest first [ List.head complete ] = Error MissingPage) (Transport.collectRest first complete = Ok [ "one"; "two" ]) Truncation
      outcome (Transport.decideRetry 2 1 (request NeverReplay) response = Stop ReplayForbidden) (match Transport.decideRetry 2 1 (request ReplaySafe) response with RetryAfter _ -> true | _ -> false) UnsafeReplay
      outcome (Transport.evaluateRevision (IfMatch "expected") (RevisionValue "old") = RevisionStale "old") (Transport.evaluateRevision (IfMatch "expected") (RevisionValue "expected") = RevisionAccepted) StaleRevision
      outcome (Transport.schedule now 1 (budget 0) = Refused RateExhausted) (Transport.schedule now 1 (budget 2) = Scheduled 1) RateExhaustion
      outcome (Transport.collectGraphQL [ { Cursor = None; Items = [ "one" ]; HasNextPage = true; EndCursor = None } ] = Error MissingContinuation) (Transport.collectGraphQL [ { Cursor = None; Items = [ "one" ]; HasNextPage = false; EndCursor = None } ] = Ok [ "one" ]) IncompletePagination
      outcome (match Transport.projectFixture allowList leak with Error(SensitiveFieldMisclassified _) -> true | _ -> false) (match Transport.projectFixture allowList clean with Ok value -> value.Contains("[REDACTED]", StringComparison.Ordinal) && not (value.Contains(secret, StringComparison.Ordinal)) | _ -> false) RedactionLeakage
      outcome (Transport.collectRest first [ List.head complete; List.head complete ] = Error AmbiguousContinuationMapping) (Transport.validateRequest (graph (json.GetProperty("graph").GetProperty("document").GetString())) = Ok ()) AmbiguousMapping ]

// Independent controls are separately authored, use different values, and do not consume
// the fixture-driven scenario constructor above.
let independentResults () =
    let outcome red green control = { Control = control; MutationRed = red; BaselineGreen = green }
    let a = Uri("http://127.0.0.1:47101/page/a")
    let b = Uri("http://127.0.0.1:47101/page/b")
    let request replay = Rest { Method = Patch; Uri = a; Headers = Map.empty; Body = Some "{}"; ApiVersion = ApiVersion.required; Idempotency = replay }
    let response = Response { StatusCode = 429; Headers = Map.empty; Body = ""; ETag = None; RateBudget = { Limit = Some 10; Remaining = Some 0; ResetAt = Some DateTimeOffset.MaxValue; Cost = Some 1 } }
    let full: RestPage<int> list = [ { Uri = a; Items = [ 3 ]; Next = Some b }; { Uri = b; Items = [ 5 ]; Next = None } ]
    let rate remaining = { Limit = Some 10; Remaining = Some remaining; ResetAt = Some DateTimeOffset.MaxValue; Cost = Some 2 }
    let classified classification = { Request = [ { Path = "token"; Value = "github_pat_fixture"; Classification = classification } ]; Response = [] }
    let graph body = GraphQL { Uri = Uri("http://127.0.0.1:47101/graphql"); Document = body; Variables = Map.empty; Headers = Map.empty; ApiVersion = ApiVersion.required; Idempotency = ReplaySafe }
    let now = DateTimeOffset(2026, 8, 31, 1, 0, 0, TimeSpan.Zero)
    [ outcome (Transport.collectRest a [ List.head full ] = Error MissingPage) (Transport.collectRest a full = Ok [ 3; 5 ]) Truncation
      outcome (Transport.decideRetry 4 1 (request NeverReplay) response = Stop ReplayForbidden) (match Transport.decideRetry 4 1 (request (ReplayWithKey "k")) response with RetryAfter _ -> true | _ -> false) UnsafeReplay
      outcome (Transport.evaluateRevision (IfMatch "new") RevisionUnreadable = RevisionUnknown) (Transport.evaluateRevision Unconditional RevisionUnreadable = RevisionAccepted) StaleRevision
      outcome (Transport.schedule now 2 (rate 0) = Refused RateExhausted) (Transport.schedule now 2 (rate 4) = Scheduled 2) RateExhaustion
      outcome (Transport.collectGraphQL [ { Cursor = None; Items = [ 8 ]; HasNextPage = false; EndCursor = Some "impossible" } ] = Error UnexpectedContinuation) (Transport.collectGraphQL [ { Cursor = None; Items = [ 8 ]; HasNextPage = false; EndCursor = None } ] = Ok [ 8 ]) IncompletePagination
      outcome (match Transport.projectFixture (Set.singleton "token") (classified Public) with Error(SensitiveFieldMisclassified _) -> true | _ -> false) (match Transport.projectFixture (Set.singleton "token") (classified Secret) with Ok value -> value = "request.token=[REDACTED]\n" | _ -> false) RedactionLeakage
      outcome (Transport.collectGraphQL [ { Cursor = None; Items = [ 1 ]; HasNextPage = false; EndCursor = None }; { Cursor = None; Items = [ 2 ]; HasNextPage = false; EndCursor = None } ] = Error AmbiguousContinuationMapping) (Transport.validateRequest (graph "query { rateLimit { remaining } }") = Ok ()) AmbiguousMapping ]

let generated = generatedResults ()
let independent = independentResults ()
match GitHubTransportQualification.validate generated independent with
| Ok () -> printfn "github-transport-contract OK controls=%d q=Q3 network=offline" generated.Length
| Error findings ->
    findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message)
    fail "GTQ-FAILED" $"{findings.Length} finding(s)"
fixture.Dispose()
