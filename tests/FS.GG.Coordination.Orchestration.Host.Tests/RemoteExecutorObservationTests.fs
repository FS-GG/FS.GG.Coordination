module FS.GG.Coordination.Orchestration.Host.Tests.RemoteExecutorObservationTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol

[<Fact>]
let ``an unchanged running observation can advance to a terminal candidate`` () =
    task {
        let assignment = Guid.NewGuid()
        let attempt = Guid.NewGuid()
        let candidate = Guid.NewGuid()
        let session = ProviderSessionReference.create "codex-thread:poll-test" |> Result.defaultWith failwith
        let now = DateTimeOffset.UtcNow
        let input = [| 1uy |]
        let inputDigest = RunnerWire.sha256 input

        let intent =
            {
                Schema = ExecutionProtocol.launchSchema
                Key = { AssignmentId = assignment; AttemptId = attempt; Generation = 2L }
                InputDigest = inputDigest
                Workspace = "poll-test"
                Requested = { Model = None; Effort = None }
                Limits = { Deadline = now.AddMinutes 30.; MaximumRuntime = TimeSpan.FromMinutes 30.; MaximumAttempts = 1 }
                RecordedAt = now
            }

        let workspace =
            {
                Schema = ExecutorWire.workspaceManifestSchema
                Workspace = "poll-test"
                RepositoryBinding = "FS-GG/.github"
                BaselineObjectId = String.replicate 40 "a"
                AllowedPaths = [| "docs/operations/orchestration-main-pilot.md" |]
                Validations = [| "git-diff-check" |]
                InputDigest = inputDigest
            }

        let binding =
            {
                WorkItemPersistenceId = "work-item-v1-" + String.replicate 64 "b"
                RouteOperationId = assignment
                CandidateId = candidate
                ExpectedRevision = 3L
                ExecutorBinding = "main-two-container-codex-subscription"
                WorkspaceManifest = workspace
                InputManifest =
                    {
                        Schema = ExecutorWire.inputManifestSchema
                        InputDigest = inputDigest
                        MediaType = "text/markdown; charset=utf-8"
                        SizeBytes = 1L
                        ChunkBytes = ExecutorWire.maximumContentBytes
                    }
                InputBytes = input
                ParentAttemptId = Nullable()
                ParentGeneration = Nullable()
                TelemetryRelation = null
            }

        let seen = HashSet<Guid>()
        let settled = HashSet<Guid>()
        let mutable polls = 0

        let store =
            { new IExecutorCommandStore with
                member _.BindRoute(_, _) = failwith "unused"
                member _.ReadRoute(_, _, _) = failwith "unused"
                member _.FindAttemptBySession(_, _) = failwith "unused"
                member _.StageInput(_, _, _) = Task.FromResult(Ok())
                member _.ReadInput(_, _) = failwith "unused"
                member _.StageWorkspaceManifest(bytes, _) = Task.FromResult(Ok(RunnerWire.sha256 bytes))
                member _.ReadWorkspaceManifest(_, _) = failwith "unused"
                member _.PersistCommand(bytes, _) =
                    let value = ExecutorWire.parseCommandV2 bytes |> Result.defaultWith failwith
                    Task.FromResult(if seen.Add value.CommandId then CommandPersisted 3L else CommandDuplicate 3L)
                member _.ReadPending(_, _) = failwith "unused"
                member _.SettleCommand(id, _, _) =
                    Task.FromResult(if settled.Add id then Ok() else Error "executor-command-settlement-refused")
                member _.ReserveSubscription(_, _, _, _) = failwith "unused"
                member _.SettleSubscription(_, _, _) = failwith "unused"
                member _.ReleaseSubscription(_, _, _, _) = failwith "unused"
                member _.ReadSubscription(_, _) = failwith "unused" }

        let resolver =
            { new IExecutorBindingResolver with
                member _.ResolveReadiness _ = failwith "unused"
                member _.Resolve(_, _) = failwith "unused"
                member _.ResolveSession(_, _) = Task.FromResult(Ok(intent, binding)) }

        let transport =
            { new IAuthenticatedExecutorTransport with
                member _.Exchange(frames, _) =
                    let command = frames |> List.last |> ExecutorWire.parseCommandV2 |> Result.defaultWith failwith
                    polls <- polls + 1
                    let completed = polls >= 2
                    let response =
                        {
                            Schema = ExecutorWire.responseSchema
                            CommandId = command.CommandId
                            BodySha256 = command.BodySha256
                            Kind = "session-observation"
                            Provider = "Codex"
                            AdapterVersion = "codex-subscription-exec/1"
                            AuthenticationState = "authenticated"
                            AuthenticationProvenance = "test"
                            SupportsResume = true
                            ProviderSessionReference = ProviderSessionReference.value session
                            Lifecycle = if completed then "succeeded" else "running"
                            RequestedModel = null
                            RequestedEffort = null
                            ResolvedModel = null
                            ResolvedEffort = null
                            Output = [||]
                            LifecycleReferences = [||]
                            Usage = [||]
                            InvocationCostState = "unknown"
                            InvocationCostAmount = Nullable()
                            InvocationCostCurrency = null
                            InvocationCostProvenance = "test"
                            BroaderCostState = "unknown"
                            BroaderCostAmount = Nullable()
                            BroaderCostCurrency = null
                            BroaderCostProvenance = "test"
                            CandidateId = if completed then candidate else Guid.Empty
                            CandidateHeadSha = if completed then String.replicate 40 "c" else null
                            CandidateTreeSha = if completed then String.replicate 40 "d" else null
                            ObservedAt = now.AddSeconds(float polls)
                            Detail = "test"
                        }
                    Task.FromResult(Ok { Frames = [ ExecutorWire.encodeResponse response ] }) }

        let provider = RemoteExecutorProvider(store, resolver, transport) :> IExecutionProvider
        let! running = provider.Observe(session, CancellationToken.None)
        let! terminal = provider.Observe(session, CancellationToken.None)
        Assert.Equal(Ok SessionLifecycle.Running, running |> Result.map _.Lifecycle)
        Assert.Equal(Ok SessionLifecycle.Succeeded, terminal |> Result.map _.Lifecycle)
        Assert.Equal(2, seen.Count)
        Assert.Equal(2, settled.Count)
        Assert.Equal(Some candidate, terminal |> Result.toOption |> Option.bind _.Candidate |> Option.map _.CandidateId)
    }
