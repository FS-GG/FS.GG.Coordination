namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.Runner.Client

/// Publishes only a settled Core native-delivery readback corroborated by a fresh
/// GitHub PR read. The publisher's immutable outbox survives a Host restart.
type TelemetryOutcomeBridge
    (repository: string, github: GitHubRouteClient, publisher: TelemetryCliPublisher) =
    let hash (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let batch (route: HostedRoutePlan) (readback: NativeDeliveryReadback) (facts: NativeDeliveryFacts) =
        let itemId = WorkItemIdentity.persistenceId route.WorkItemId
        let source = "orchestration-delivery:" + string (Id.operationValue route.ReadbackOperationId)
        let identity = "native-item-outcome-" + hash (itemId + "\u001f" + source)
        let event = JsonObject()
        event["kind"] <- "native-item-outcome"
        event["identity"] <- identity
        event["itemId"] <- itemId
        event["revision"] <- readback.ObservedAt.UtcTicks
        event["repository"] <- repository
        event["prNumber"] <- facts.Number
        event["baseRef"] <- facts.BaseRef
        event["baseSha"] <- facts.BaseSha
        event["head"] <- facts.HeadSha
        event["outcome"] <- "delivered"
        event["codeDelivery"] <- "delivered"
        event["mergeCommit"] <- facts.MergeCommitSha
        event["occurredAt"] <- facts.MergedAt.ToString("O")
        event["observedAt"] <- readback.ObservedAt.ToString("O")
        event["sourceKind"] <- "orchestration-delivery"
        event["sourceRef"] <- source
        let population = JsonObject()
        population["kind"] <- "budget-population"
        population["identity"] <- "budget-population-" + hash (itemId + "\u001f" + source)
        population["itemId"] <- itemId
        population["revision"] <- readback.ObservedAt.UtcTicks
        population["originalItemId"] <- itemId
        population["state"] <- "completed"
        population["sourceKind"] <- "native-item"
        population["sourceRef"] <- source
        let digest = hash (identity + "\u001f" + population["identity"].GetValue<string>())
        let root = JsonObject()
        root["schema"] <- "fsgg.telemetry.ingest/1"
        root["ingestId"] <- "batch-" + digest
        root["sourceIdentity"] <- "coordination"
        root["generation"] <- "host-outcome-" + string route.RouteId
        root["cursor"] <- digest
        root["eventCount"] <- 2
        let events = JsonArray()
        events.Add event
        events.Add population
        root["events"] <- events
        "batch-" + digest, Encoding.UTF8.GetBytes(root.ToJsonString(JsonSerializerOptions(WriteIndented = false)))

    member _.CreateBatch(route: HostedRoutePlan, readback: NativeDeliveryReadback, facts: NativeDeliveryFacts) =
        batch route readback facts

    member this.Publish(route: HostedRoutePlan, readback: NativeDeliveryReadback, attemptCompleted: bool, token: CancellationToken) =
        task {
            if
                not attemptCompleted
                || readback.OperationId <> route.ReadbackOperationId
                || readback.RouteId <> route.RouteId
                || readback.AttemptId <> route.AttemptId
                || readback.CandidateId <> route.CandidateId
                || not readback.Merged
                || readback.CandidateHeadSha <> readback.ObservedPullRequestHeadSha
            then
                return PublicationUnknown "native-delivery-readback-mismatch"
            else
                let! observed = github.ReadDeliveryFacts(route.BranchRef, readback.CandidateHeadSha, token)

                match observed with
                | Error reason -> return PublicationUnknown reason
                | Ok facts when
                    facts.NodeId = readback.PullRequestNodeId
                    && facts.MergeCommitSha = readback.MergeCommitSha
                    && facts.MergedAt <= readback.ObservedAt
                    ->
                    let name, payload = this.CreateBatch(route, readback, facts)
                    return! publisher.Publish(name, payload, token)
                | Ok _ -> return PublicationUnknown "native-delivery-fresh-readback-mismatch"
        }

    member _.Flush(token: CancellationToken) = publisher.Flush token

    /// Starts at Host process startup, independent of route admission. A queued
    /// batch therefore drains after restart without replaying a claim or runner.
    member this.DrainUntilCancelled(token: CancellationToken) =
        task {
            try
                while not token.IsCancellationRequested do
                    try
                        let! _ = this.Flush token
                        ()
                    with
                    | :? OperationCanceledException when token.IsCancellationRequested -> ()
                    | _ -> ()

                    do! Task.Delay(TimeSpan.FromSeconds 5., token)
            with :? OperationCanceledException when token.IsCancellationRequested ->
                ()
        }
