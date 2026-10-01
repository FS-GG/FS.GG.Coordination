namespace FS.GG.Coordination.Orchestration.Host.Tests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Host

module private TelemetryFixture =
    let key = { WindowId = "window-1"; OriginalItemId = "LEARN-01.4" }
    let source producer record =
        {| producerId = producer; revision = "7"; recordId = record; observedAt = "2026-10-01T10:00:00Z" |}
    let roles = [ "root"; "child"; "retry"; "review"; "rescue"; "repair" ]
    let states = [ "prospective"; "assigned"; "completed"; "cancelled"; "failed"; "unfinished" ]
    let json status =
        let members =
            List.map2 (fun role state ->
                {| itemId = "item-" + role; originalItemId = "LEARN-01.4"; role = role; state = state
                   source = source "coordination-observer-learning-member/1" ("member-" + role)
                   nativeUsageSha256 = if state = "prospective" then null else String('a', 64)
                   sharedCostSha256 = if state = "prospective" then null else String('b', 64) |}) roles states
        JsonSerializer.Serialize(
            {| schema = "fsgg.learn.telemetry-readiness-census/1"; status = status
               key = {| windowId = "window-1"; originalItemId = "LEARN-01.4" |}
               source = source "protected-learning-export/2" "window-1:LEARN-01.4"
               nativeDeliveryRevision = String('c', 64); members = members
               unavailable = if status = "ready" then [||] else [| "native-pages-unavailable" |]
               workspaceSha256 = String('d', 64) |})
    let identity name =
        { ProducerId = name; Revision = "1"; RecordId = name + "-record"
          ObservedAt = DateTimeOffset.Parse "2026-10-01T09:59:00Z" }

type LearningTelemetryReadinessSourceTests() =
    [<Fact>]
    member _.``closed ready document retains all roles and states``() =
        match LearningTelemetryReadinessDocument.parse TelemetryFixture.key (TelemetryFixture.json "ready") with
        | Error reason -> failwith reason
        | Ok(source, revision, members) ->
            Assert.Equal("protected-learning-export/2", source.ProducerId)
            Assert.Equal(String('c', 64), revision)
            Assert.True(Set.ofList TelemetryFixture.roles = (members |> List.map (fun value -> value.Role) |> Set.ofList))
            Assert.True(Set.ofList TelemetryFixture.states = (members |> List.map (fun value -> value.State) |> Set.ofList))
            let prospective = members |> List.find (fun value -> value.State = "prospective")
            Assert.True(prospective.NativeUsageSha256.IsNone)
            Assert.True(prospective.SharedCostSha256.IsNone)

    [<Fact>]
    member _.``unavailable telemetry never asks installed producers to manufacture readiness``() = task {
        let mutable calls = 0
        let installed (_key, _token) = calls <- calls + 1; Task.FromResult(Ok(TelemetryFixture.identity "installed"))
        let run _ _ = Task.FromResult(Ok(TelemetryFixture.json "unavailable"))
        let source = LearningTelemetryReadinessSource(Unchecked.defaultof<_>, installed, installed, run)
        let! result = (source :> ILearningOperationalCensusSource).ReadLearningOperationalCensus(TelemetryFixture.key, CancellationToken.None)
        match result with
        | Ok _ -> failwith "unavailable evidence was accepted"
        | Error reason -> Assert.Contains("native-pages-unavailable", reason)
        Assert.Equal(0, calls)
    }

    [<Fact>]
    member _.``ready telemetry joins independent installed custody and capability``() = task {
        let installed (_key, _token) = Task.FromResult(Ok(TelemetryFixture.identity "installed-custody"))
        let capability (_key, _token) = Task.FromResult(Ok(TelemetryFixture.identity "provider-capability"))
        let run _ _ = Task.FromResult(Ok(TelemetryFixture.json "ready"))
        let source = LearningTelemetryReadinessSource(Unchecked.defaultof<_>, installed, capability, run)
        let! result = (source :> ILearningOperationalCensusSource).ReadLearningOperationalCensus(TelemetryFixture.key, CancellationToken.None)
        match result with
        | Error reason -> failwith reason
        | Ok census ->
            Assert.Equal("installed-custody", census.InstalledCustody.Value.ProducerId)
            Assert.Equal("provider-capability", census.ProviderCapability.Value.ProducerId)
            Assert.Equal(6, census.Members.Length)
    }

    [<Theory>]
    [<InlineData("window-2", "LEARN-01.4")>]
    [<InlineData("window-1", "foreign")>]
    member _.``window and original binding cannot be substituted``(windowId, originalItemId) =
        let key = { WindowId = windowId; OriginalItemId = originalItemId }
        match LearningTelemetryReadinessDocument.parse key (TelemetryFixture.json "ready") with
        | Ok _ -> failwith "substituted key was accepted"
        | Error reason -> Assert.Equal("learning-telemetry-reader-key-mismatch", reason)
