module FS.GG.Coordination.Orchestration.Host.Tests.HostTests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.Pilot

module private Fixture =
    let now = DateTimeOffset.Parse "2026-09-10T15:00:00Z"
    let permitId = Guid.Parse "62000000-0000-0000-0000-000000000001"
    let permit =
        { SchemaVersion = 1; PermitId = permitId
          SubjectId = WorkItemIdentity.create "R_host" 8L "I_host" 21L
          JobClass = "routine-documentation-delivery"; StableOwnerId = "stable-route"; PilotOwnerId = "pilot-route"
          Generation = Id.generation 3L; AttemptLimit = 2L; TokenLimit = 100L; RuntimeSecondsLimit = 120L
          CostMicrosLimit = 1000L; ExpiresAt = now.AddHours 1.; Capacity = 2; RecoveryCapacity = 1
          StartupPolicy = "manual"; AutoResume = false }
    let readback purpose =
        { SubjectId = permit.SubjectId; Generation = permit.Generation; Purpose = purpose; OperationId = None
          Provider = "github-readback-fixture"; SourceRevision = "revision-1"; EvidenceSha256 = String.replicate 64 "a"
          ObservedAt = now }
        |> fun input ->
            let capability =
                { new IPilotReadbackCapability with member _.ReadCurrent(_, _) = Task.FromResult(Ok input) }
            PilotReadback.read
                ({ new TimeProvider() with override _.GetUtcNow() = now }) capability
                { Permit = permit; Purpose = purpose; OperationId = None; NotBefore = now.AddSeconds -1. }
                CancellationToken.None
            |> _.GetAwaiter().GetResult() |> Result.defaultWith failwith
    let pilotOwned =
        [ PermitIssued permit
          TransferIntentPersisted
              { SubjectId = permit.SubjectId; Generation = permit.Generation; EvidenceSha256 = String.replicate 64 "b"
                Quiesced = true; Excluded = true; ObservedAt = now }
          TransferAcknowledged(readback TransferAcknowledgement) ]
        |> Pilot.replay

    type FixedClock() = inherit TimeProvider() override _.GetUtcNow() = now

    let unusedWorkItems =
        { new IJournalStore with
            member _.CheckReadiness _ = Task.FromResult(Ok())
            member _.Recover(_, _) = Task.FromResult(Error [ StoreUnavailable "unused" ])
            member _.Append(_, _) = Task.FromResult(InvalidAppend "unused")
            member _.SaveSnapshot(_, _) = Task.FromResult(Ok())
            member _.SaveProjectionCheckpoint(_, _) = Task.FromResult(Ok()) }

    let durableStore state =
        let accepted = Dictionary<Guid, string * int64>()
        let appended = ResizeArray<PilotAppendRequest>()
        let store =
            { CheckReadiness = fun _ -> Task.FromResult(Ok())
              WorkItems = unusedWorkItems
              Recover = fun _ _ -> Task.FromResult(Ok { Events = []; State = state })
              Append = fun request _ ->
                  let digest = PilotCodec.commandSha256 request.Command
                  match accepted.TryGetValue request.Command.CommandId with
                  | true, (priorDigest, sequence) when priorDigest = digest -> Task.FromResult(PilotDuplicate sequence)
                  | true, _ -> Task.FromResult PilotConflict
                  | false, _ when request.Events.IsEmpty -> Task.FromResult(PilotInvalidAppend "new-command-requires-events")
                  | false, _ ->
                      let sequence = request.Command.ExpectedSequence + int64 request.Events.Length
                      accepted.Add(request.Command.CommandId, (digest, sequence))
                      appended.Add request
                      Task.FromResult(PilotAppended sequence) }
        store, appended

[<Fact>]
let ``pilot permit admits only the hosted writer job class`` () =
    Assert.True(Pilot.validatePermit Fixture.permit)
    Assert.False(Pilot.validatePermit { Fixture.permit with JobClass = "routine-implementation" })

[<Fact>]
let ``status is ready but remains default paused with dispatch disabled`` () = task {
    let store =
        { CheckReadiness = fun _ -> Task.FromResult(Ok())
          WorkItems = Fixture.unusedWorkItems
          Recover = fun _ _ -> Task.FromResult(Ok { Events = []; State = { Fixture.pilotOwned with ReadbackCurrent = false } })
          Append = fun _ _ -> Task.FromResult(PilotInvalidAppend "unused") }
    let! status = HostRuntime.status store Fixture.permitId CancellationToken.None
    Assert.True(status.Ready)
    Assert.False(status.DispatchEnabled)
    Assert.Equal("paused", status.Mode)
    Assert.Equal(Some false, status.ReadbackCurrent)
    Assert.Contains("fresh-reconnect-readback-required", status.Findings) }

[<Fact>]
let ``storage readiness failure prevents pilot recovery`` () = task {
    let mutable recovered = false
    let store =
        { CheckReadiness = fun _ -> Task.FromResult(Error [ ReadOnlyStore ])
          WorkItems = Fixture.unusedWorkItems
          Recover = fun _ _ -> recovered <- true; Task.FromResult(Ok { Events = []; State = Pilot.initial })
          Append = fun _ _ -> Task.FromResult(PilotInvalidAppend "unused") }
    let! status = HostRuntime.status store Fixture.permitId CancellationToken.None
    Assert.False(status.Ready)
    Assert.False(recovered)
    Assert.Contains("ReadOnlyStore", status.Findings) }

[<Fact>]
let ``operator authentication is exact bearer token comparison`` () =
    let token = String.replicate 32 "x"
    Assert.True(HostRuntime.authorize token (Some("Bearer " + token)))
    Assert.False(HostRuntime.authorize token (Some("Bearer " + token + "x")))
    Assert.False(HostRuntime.authorize token None)

[<Fact>]
let ``pause control binds permit principal sequence and stable command identity`` () = task {
    let store, appended = Fixture.durableStore Fixture.pilotOwned
    let commandId = Guid.Parse "62000000-0000-0000-0000-000000000002"
    let control =
        { Schema = "fsgg.orchestration.host-control/1"; PermitId = Fixture.permitId; CommandId = commandId
          ExpectedSequence = Fixture.pilotOwned.Sequence; ExpectedGeneration = Id.generationValue Fixture.permit.Generation
          PrincipalId = Fixture.permit.PilotOwnerId; IssuedAt = Fixture.now.AddSeconds -1.
          ExpiresAt = Fixture.now.AddMinutes 1.; Reason = "operator-pause" }
    let! result = HostRuntime.applyControl (Fixture.FixedClock()) store Fixture.permitId "pilot-route" control false CancellationToken.None
    Assert.Equal(Ok(Fixture.pilotOwned.Sequence + 1L), result)
    let request = Assert.Single appended
    Assert.Equal(Fixture.permitId, request.PermitId)
    Assert.Equal(commandId, request.Command.CommandId)
    Assert.Equal(Fixture.pilotOwned.Sequence, request.Command.ExpectedSequence)
    Assert.True(request.Command.Command |> function Pause "operator-pause" -> true | _ -> false)
    Assert.Equal("pilot-route", request.Command.PrincipalId) }

[<Fact>]
let ``stale generation and sequence controls refuse without durable mutation`` () = task {
    let store, appended = Fixture.durableStore Fixture.pilotOwned
    let template =
        { Schema = "fsgg.orchestration.host-control/1"; PermitId = Fixture.permitId; CommandId = Guid.NewGuid()
          ExpectedSequence = Fixture.pilotOwned.Sequence; ExpectedGeneration = Id.generationValue Fixture.permit.Generation
          PrincipalId = "pilot-route"; IssuedAt = Fixture.now.AddSeconds -1.; ExpiresAt = Fixture.now.AddMinutes 1.
          Reason = "pause" }
    let! generation = HostRuntime.applyControl (Fixture.FixedClock()) store Fixture.permitId "pilot-route" { template with ExpectedGeneration = 99L } false CancellationToken.None
    let! sequence = HostRuntime.applyControl (Fixture.FixedClock()) store Fixture.permitId "pilot-route" { template with ExpectedSequence = 0L } false CancellationToken.None
    Assert.Equal(Error "control-authority-mismatch", generation)
    Assert.Equal(Error "wrong-expected-sequence", sequence)
    Assert.Empty(appended) }

[<Fact>]
let ``exact retry returns durable receipt and changed payload conflicts`` () = task {
    let store, appended = Fixture.durableStore Fixture.pilotOwned
    let request =
        { Schema = "fsgg.orchestration.host-control/1"; PermitId = Fixture.permitId
          CommandId = Guid.Parse "62000000-0000-0000-0000-000000000004"
          ExpectedSequence = Fixture.pilotOwned.Sequence; ExpectedGeneration = Id.generationValue Fixture.permit.Generation
          PrincipalId = "pilot-route"; IssuedAt = Fixture.now.AddSeconds -1.; ExpiresAt = Fixture.now.AddMinutes 1.
          Reason = "stable-retry" }
    let! accepted = HostRuntime.applyControl (Fixture.FixedClock()) store Fixture.permitId "pilot-route" request false CancellationToken.None
    let! replayed = HostRuntime.applyControl (Fixture.FixedClock()) store Fixture.permitId "pilot-route" request false CancellationToken.None
    let! conflict = HostRuntime.applyControl (Fixture.FixedClock()) store Fixture.permitId "pilot-route" { request with Reason = "changed" } false CancellationToken.None
    Assert.Equal(Ok(Fixture.pilotOwned.Sequence + 1L), accepted)
    Assert.Equal(accepted, replayed)
    Assert.Equal(Error "command-identity-conflict", conflict)
    Assert.Single(appended) |> ignore }

[<Fact>]
let ``serve configuration requires private files loopback and explicit identities`` () =
    let root = Path.Combine(Path.GetTempPath(), "fsgg-host-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let connection, token = Path.Combine(root, "connection"), Path.Combine(root, "token")
        File.WriteAllText(connection, "Host=127.0.0.1;Database=fixture")
        File.WriteAllText(token, String.replicate 32 "t")
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(connection, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            File.SetUnixFileMode(token, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        let arguments =
            [| "--connection-file"; connection; "--token-file"; token; "--prefix"; "http://127.0.0.1:5109/"
               "--store-id"; "main-pilot"; "--backup-identity"; Guid.NewGuid().ToString()
               "--minimum-generation-fence"; "3"; "--permit-id"; Fixture.permitId.ToString()
               "--pilot-principal"; "pilot-route"
               "--repository-node-id"; "R_host"; "--repository-database-id"; "8"
               "--issue-node-id"; "I_host"; "--issue-database-id"; "21" |]
        Assert.True(HostConfiguration.parseServe arguments |> Result.isOk)
        Assert.Equal(Error "duplicate-option", HostConfiguration.parseServe (Array.append arguments [| "--permit-id"; Fixture.permitId.ToString() |]))
        let publicArguments = arguments |> Array.copy
        publicArguments[5] <- "http://0.0.0.0:5109/"
        Assert.Equal(Error "prefix-must-be-loopback-http-root", HostConfiguration.parseServe publicArguments)
        let queryArguments = arguments |> Array.copy
        queryArguments[5] <- "http://127.0.0.1:5109/?token=forbidden"
        Assert.Equal(Error "prefix-must-be-loopback-http-root", HostConfiguration.parseServe queryArguments)
        let tokenLink = Path.Combine(root, "token-link")
        File.CreateSymbolicLink(tokenLink, token) |> ignore
        let linkArguments = arguments |> Array.copy
        linkArguments[3] <- tokenLink
        Assert.Equal(Error "secret-file-must-be-regular", HostConfiguration.parseServe linkArguments)
    finally Directory.Delete(root, true)

let private freePrefix () =
    use socket = new TcpListener(IPAddress.Loopback, 0)
    socket.Start()
    let port = (socket.LocalEndpoint :?> IPEndPoint).Port
    socket.Stop()
    $"http://127.0.0.1:{port}/"

[<Fact>]
let ``http host bounds malformed and slow control requests without stopping status`` () = task {
    let prefix, token = freePrefix(), String.replicate 32 "z"
    let configuration =
        { ConnectionString = "unused"; Token = token; Prefix = prefix; StoreId = "fixture"
          BackupIdentity = Guid.NewGuid().ToString(); MinimumGenerationFence = 0L; PermitId = Fixture.permitId
          PilotPrincipalId = "pilot-route"; WorkItemId = Fixture.permit.SubjectId
          RequestTimeout = TimeSpan.FromMilliseconds 150.; MaximumConcurrentRequests = 2 }
    let store, _ = Fixture.durableStore { Fixture.pilotOwned with ReadbackCurrent = false }
    use shutdown = new CancellationTokenSource()
    let server = HostRuntime.serve (Fixture.FixedClock()) configuration store shutdown.Token
    do! Task.Delay 50
    use client = new HttpClient()
    let! denied = client.GetAsync(prefix + "v1/status")
    Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode)
    client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
    let issuedAt = Fixture.now.AddSeconds(-1.).ToString("O")
    let expiresAt = Fixture.now.AddMinutes(1.).ToString("O")
    let validJson =
        $"{{\"schema\":\"fsgg.orchestration.host-control/1\",\"permitId\":\"{Fixture.permitId}\",\"commandId\":\"62000000-0000-0000-0000-000000000003\",\"expectedSequence\":{Fixture.pilotOwned.Sequence},\"expectedGeneration\":3,\"principalId\":\"pilot-route\",\"issuedAt\":\"{issuedAt}\",\"expiresAt\":\"{expiresAt}\",\"reason\":\"http-pause\"}}"
    use valid = new StringContent(validJson, Encoding.UTF8, "application/json")
    let! accepted = client.PostAsync(prefix + "v1/pause", valid)
    Assert.Equal(HttpStatusCode.OK, accepted.StatusCode)
    let! acceptedBody = accepted.Content.ReadAsStringAsync()
    use replay = new StringContent(validJson, Encoding.UTF8, "application/json")
    let! replayed = client.PostAsync(prefix + "v1/pause", replay)
    Assert.Equal(HttpStatusCode.OK, replayed.StatusCode)
    let! replayedBody = replayed.Content.ReadAsStringAsync()
    Assert.Equal(acceptedBody, replayedBody)
    use changed = new StringContent(validJson.Replace("http-pause", "changed-payload"), Encoding.UTF8, "application/json")
    let! conflicted = client.PostAsync(prefix + "v1/pause", changed)
    Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode)
    use malformed = new StringContent(validJson.TrimEnd('}') + ",\"unknown\":true}", Encoding.UTF8, "application/json")
    let! malformedResponse = client.PostAsync(prefix + "v1/pause", malformed)
    Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode)
    use missing = new StringContent(validJson.Replace(",\"expectedGeneration\":3", ""), Encoding.UTF8, "application/json")
    let! missingResponse = client.PostAsync(prefix + "v1/pause", missing)
    Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode)
    use duplicate = new StringContent(validJson.Replace("\"reason\":", "\"reason\":\"duplicate\",\"reason\":"), Encoding.UTF8, "application/json")
    let! duplicateResponse = client.PostAsync(prefix + "v1/pause", duplicate)
    Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode)
    use wrongMedia = new StringContent(validJson, Encoding.UTF8, "application/jsonevil")
    let! wrongMediaResponse = client.PostAsync(prefix + "v1/pause", wrongMedia)
    Assert.Equal(HttpStatusCode.BadRequest, wrongMediaResponse.StatusCode)

    use slow = new TcpClient()
    do! slow.ConnectAsync(IPAddress.Loopback, Uri(prefix).Port)
    let bytes = Encoding.ASCII.GetBytes($"POST /v1/pause HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{{")
    do! slow.GetStream().WriteAsync bytes
    do! Task.Delay 250
    let! statusResponse = client.GetAsync(prefix + "v1/status")
    Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode)
    shutdown.Cancel()
    do! server.WaitAsync(TimeSpan.FromSeconds 2.) }
