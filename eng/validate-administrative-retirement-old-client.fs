module AdministrativeRetirementOldClientFixture

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Orchestration.Host

type RefusingPublisher() =
    interface IGitCandidatePublisher with
        member _.Publish(_, _, _, _, _, _) =
            Task.FromResult(Error "fixture-publisher-must-not-run")

type FixedExecutor(outcome: TransportOutcome) =
    interface IGitHubRequestExecutor with
        member _.Send(_, _) = Task.FromResult outcome

type InterceptingExecutor
    (inner: IGitHubRequestExecutor, readyPath: string, releasePath: string, responsePath: string, maximumWait: TimeSpan)
    =
    let mutable observed: TransportOutcome option = None
    member _.Observed = observed

    interface IGitHubRequestExecutor with
        member _.Send(request, cancellationToken) =
            task {
                match request with
                | Rest value when
                    value.Method = RestMethod.Put
                    && value.Uri.AbsolutePath.EndsWith("/merge", StringComparison.Ordinal)
                    ->
                    File.WriteAllText(
                        readyPath,
                        JsonSerializer.Serialize
                            {|
                                uri = value.Uri.AbsoluteUri
                                body = value.Body
                            |}
                    )

                    let mutable remaining =
                        max 0 (int (Math.Ceiling(maximumWait.TotalMilliseconds / 50.)))

                    let mutable cancelled = false

                    while not (File.Exists releasePath) && remaining > 0 do
                        try
                            do! Task.Delay(50, cancellationToken)
                        with :? OperationCanceledException ->
                            cancelled <- true
                            remaining <- 0

                        remaining <- remaining - 1

                    if not (File.Exists releasePath) then
                        observed <- Some TimedOut

                        File.WriteAllText(
                            responsePath,
                            if cancelled then
                                "{\"kind\":\"intercept-cancelled\"}"
                            else
                                "{\"kind\":\"intercept-timeout\"}"
                        )

                        return TimedOut
                    else
                        let! result = inner.Send(request, cancellationToken)
                        observed <- Some result

                        match result with
                        | Response response ->
                            File.WriteAllText(
                                responsePath,
                                JsonSerializer.Serialize
                                    {|
                                        kind = "response"
                                        statusCode = response.StatusCode
                                        body = response.Body
                                        headers = response.Headers
                                    |}
                            )
                        | NetworkFailure -> File.WriteAllText(responsePath, "{\"kind\":\"network-failure\"}")
                        | TimedOut -> File.WriteAllText(responsePath, "{\"kind\":\"transport-timeout\"}")

                        return result
                | _ -> return! inner.Send(request, cancellationToken)
            }

let requireNativeHeadMismatch =
    function
    | Some(Response response) when
        response.StatusCode = 409
        && response.Body.Contains("head branch was modified", StringComparison.OrdinalIgnoreCase)
        ->
        ()
    | Some(Response response) ->
        failwithf "expected native 409 head mismatch; status=%d body=%s" response.StatusCode response.Body
    | Some NetworkFailure -> failwith "native head mismatch was not observed: network failure"
    | Some TimedOut -> failwith "native head mismatch was not observed: timeout or cancellation"
    | None -> failwith "native head mismatch was not observed: merge PUT was not intercepted"

let response status body =
    Response
        {
            StatusCode = status
            Headers = Map.empty
            Body = body
            ETag = None
            RateBudget =
                {
                    Limit = None
                    Remaining = None
                    ResetAt = None
                    Cost = None
                }
        }

let selfTest () =
    let request =
        Rest
            {
                Method = RestMethod.Put
                Uri = Uri "https://api.github.test/repos/FS-GG/fixture/pulls/1/merge"
                Headers = Map.empty
                Body = Some "{}"
                ApiVersion = ApiVersion.required
                Idempotency = ReplaySafe
            }

    let exercise name outcome expected =
        let root =
            Path.Combine(Path.GetTempPath(), "fsgg-retirement-interceptor-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore
        let ready = Path.Combine(root, "ready.json")
        let release = Path.Combine(root, "release")
        let result = Path.Combine(root, "result.json")
        File.WriteAllText(release, "release")

        try
            let intercepted =
                InterceptingExecutor(FixedExecutor(outcome), ready, release, result, TimeSpan.FromSeconds 1.)

            let returned =
                (intercepted :> IGitHubRequestExecutor).Send(request, CancellationToken.None).GetAwaiter().GetResult()

            if returned <> outcome || not (File.Exists ready) || not (File.Exists result) then
                failwithf "interceptor did not retain %s" name

            let accepted =
                try
                    requireNativeHeadMismatch intercepted.Observed
                    true
                with _ ->
                    false

            if accepted <> expected then
                failwithf "native mismatch classification wrong for %s" name
        finally
            Directory.Delete(root, true)

    exercise "native-409" (response 409 "{\"message\":\"Head branch was modified\"}") true
    exercise "unrelated-409" (response 409 "{\"message\":\"Conflict\"}") false
    exercise "success" (response 200 "{\"merged\":true}") false
    exercise "network" NetworkFailure false
    exercise "timeout" TimedOut false

    let timeoutRoot =
        Path.Combine(Path.GetTempPath(), "fsgg-retirement-interceptor-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory timeoutRoot |> ignore

    try
        let intercepted =
            InterceptingExecutor(
                FixedExecutor(response 409 "{\"message\":\"Head branch was modified\"}"),
                Path.Combine(timeoutRoot, "ready"),
                Path.Combine(timeoutRoot, "never-release"),
                Path.Combine(timeoutRoot, "result"),
                TimeSpan.Zero
            )

        let returned =
            (intercepted :> IGitHubRequestExecutor).Send(request, CancellationToken.None).GetAwaiter().GetResult()

        if
            returned <> TimedOut
            || not (File.ReadAllText(Path.Combine(timeoutRoot, "result")).Contains("intercept-timeout"))
        then
            failwith "intercept timeout was not retained"

        let accepted =
            try
                requireNativeHeadMismatch intercepted.Observed
                true
            with _ ->
                false

        if accepted then
            failwith "intercept timeout was accepted as a native mismatch"

        let prePutAccepted =
            try
                requireNativeHeadMismatch None
                true
            with _ ->
                false

        if prePutAccepted then
            failwith "pre-PUT refusal was accepted as a native mismatch"
    finally
        Directory.Delete(timeoutRoot, true)

    let cancellationRoot =
        Path.Combine(Path.GetTempPath(), "fsgg-retirement-interceptor-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory cancellationRoot |> ignore

    try
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let intercepted =
            InterceptingExecutor(
                FixedExecutor NetworkFailure,
                Path.Combine(cancellationRoot, "ready"),
                Path.Combine(cancellationRoot, "never-release"),
                Path.Combine(cancellationRoot, "result"),
                TimeSpan.FromSeconds 1.
            )

        let returned =
            (intercepted :> IGitHubRequestExecutor).Send(request, cancellation.Token).GetAwaiter().GetResult()

        if
            returned <> TimedOut
            || not (File.ReadAllText(Path.Combine(cancellationRoot, "result")).Contains("intercept-cancelled"))
        then
            failwith "intercept cancellation was not retained"

        let accepted =
            try
                requireNativeHeadMismatch intercepted.Observed
                true
            with _ ->
                false

        if accepted then
            failwith "intercept cancellation was accepted as a native mismatch"
    finally
        Directory.Delete(cancellationRoot, true)

    printfn "ADMINISTRATIVE_RETIREMENT_OLD_CLIENT_INTERCEPTOR_SELF_TEST_OK"

let run (arguments: string array) =
    if arguments = [| "self-test" |] then
        selfTest ()
    else
        if arguments.Length <> 11 then
            failwith "expected mode repository issue pull-request node-id branch base head ready release response"

        let mode = arguments[0]
        let repository = arguments[1]
        let issue = Int32.Parse arguments[2]
        let pullRequest = Int32.Parse arguments[3]
        let pullRequestNodeId = arguments[4]
        let branchRef = arguments[5]
        let baseRef = arguments[6]
        let head = arguments[7]
        let token = Environment.GetEnvironmentVariable "GH_TOKEN"

        if String.IsNullOrWhiteSpace token then
            failwith "GH_TOKEN is required"

        use http = new HttpClient(BaseAddress = Uri "https://api.github.com/")

        let transport =
            HttpGitHubRequestExecutor(http, token, 1024 * 1024) :> IGitHubRequestExecutor

        let interceptor =
            if mode = "intercept-merge" then
                Some(
                    InterceptingExecutor(transport, arguments[8], arguments[9], arguments[10], TimeSpan.FromMinutes 10.)
                )
            else
                None

        let executor =
            interceptor
            |> Option.map (fun value -> value :> IGitHubRequestExecutor)
            |> Option.defaultValue transport

        let target =
            {
                ApiRoot = Uri "https://api.github.com/"
                Repository = repository
                IssueNumber = issue
                Principal = "retirement-native-fixture"
                BaseRef = baseRef.Replace("refs/heads/", "")
                RoutineOperation = "internal-docs"
                ClaimLease = TimeSpan.FromMinutes 5.
            }

        let client =
            GitHubRouteClient(executor, RefusingPublisher(), target, TimeProvider.System)

        if mode = "intercept-merge" then
            let result =
                client.MergeAuthorized(branchRef, head, Guid.NewGuid(), CancellationToken.None).GetAwaiter().GetResult()

            requireNativeHeadMismatch interceptor.Value.Observed

            match result with
            | Error "github-native-delivery-not-observed" ->
                printfn "ADMINISTRATIVE_RETIREMENT_OLD_CLIENT_DELAYED_NATIVE_409_HEAD_MISMATCH number=%d" pullRequest
            | value -> failwithf "native mismatch produced an unexpected old-client result: %A" value
        elif mode = "post-terminal" then
            match
                client
                    .CreatePullRequest(branchRef, head, Guid.NewGuid(), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()
            with
            | Error "github-pull-request-binding-conflict" ->
                match
                    client.Merge(branchRef, head, Guid.NewGuid(), CancellationToken.None).GetAwaiter().GetResult()
                with
                | Error "github-pull-request-not-open-at-head" ->
                    printfn
                        "ADMINISTRATIVE_RETIREMENT_OLD_CLIENT_HEAD_CONFLICT_AND_REFUSED_MERGE number=%d node=%s"
                        pullRequest
                        pullRequestNodeId
                | value -> failwithf "closed pull request merge was not refused: %A" value
            | value -> failwithf "retirement-head pull request conflict was not observed: %A" value
        else
            failwith "unknown fixture mode"

[<EntryPoint>]
let main arguments =
    run arguments
    0
