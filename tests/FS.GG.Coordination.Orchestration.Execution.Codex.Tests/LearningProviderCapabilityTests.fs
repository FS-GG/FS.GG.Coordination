namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type private CapabilityDiscovery(factory: LearningSelectionQuery -> LearningSelectionEvidence) =
    interface ICodexLearningCapabilityDiscovery with
        member _.Discover(query, _) = Task.FromResult(factory query)

module private LearningCapabilityFixture =
    let behavior =
        {
            Version = "codex-cli 0.154.0"
            Login = "Logged in using ChatGPT"
            LoginExit = 0
            Stderr = ""
            Body =
                "printf '%s\\n' '{\"status\":\"completed\",\"summary\":\"done\"}' > \"$final\"\nprintf '%s\\n' '{\"type\":\"turn.completed\",\"provider\":\"openai\",\"model\":\"fixture-model\",\"effort\":\"medium\",\"backend\":\"subscription\",\"usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1}}'"
        }

    let provider root discovery =
        let workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName
        let executable = Fixture.script root behavior

        let provider =
            CodexExecutionProvider(
                Fixture.options executable (Path.Combine(root, "state")),
                Input(Fixture.prompt),
                CandidateInspector(true),
                TimeProvider.System,
                learningCapabilityDiscovery = discovery
            )

        workspace, executable, provider

    let launch root factory =
        task {
            let discovery = CapabilityDiscovery(factory) :> ICodexLearningCapabilityDiscovery
            let workspace, _, provider = provider root discovery

            let! result =
                (provider :> ILearningExecutionProvider)
                    .LaunchLearning(Fixture.intent workspace (TimeSpan.FromSeconds 5.), CancellationToken.None)

            return result
        }

    let modelList fixture query =
        let bytes =
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "learning-selection", fixture))

        CodexLearningProviderCapability.fromModelList DateTimeOffset.UtcNow query bytes

type LearningProviderCapabilityTests() =
    [<Fact>]
    member _.``pinned CLI without native capability discovery remains unknown and does not launch``() =
        task {
            let root = Directory.CreateTempSubdirectory("learning-capability-unknown-").FullName
            let workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName
            let executable = Fixture.script root LearningCapabilityFixture.behavior

            let provider =
                CodexExecutionProvider(
                    Fixture.options executable (Path.Combine(root, "state")),
                    Input(Fixture.prompt),
                    CandidateInspector(true),
                    TimeProvider.System
                )

            let! observed =
                (provider :> ILearningExecutionProvider)
                    .ObserveLearningSelection(
                        (Fixture.intent workspace (TimeSpan.FromSeconds 5.)).Requested,
                        CancellationToken.None
                    )

            Assert.Equal(
                LearningCapabilityStatus.Unknown "codex-native-selection-capability-unavailable",
                observed.Status
            )

            let! result =
                (provider :> ILearningExecutionProvider)
                    .LaunchLearning(Fixture.intent workspace (TimeSpan.FromSeconds 5.), CancellationToken.None)

            Assert.Equal(
                LaunchRefused "learning-selection-unknown:codex-native-selection-capability-unavailable",
                result
            )

            Assert.False(File.Exists(Path.Combine(root, "argv")))
        }

    [<Fact>]
    member _.``unsupported exact selection refuses before launch``() =
        task {
            let root =
                Directory.CreateTempSubdirectory("learning-capability-unsupported-").FullName

            let! result =
                LearningCapabilityFixture.launch root (fun query ->
                    LearningCapabilityFixture.modelList "unsupported-effort.json" query)

            Assert.Equal(LaunchRefused "learning-selection-unsupported:requested-effort-unsupported", result)
            Assert.False(File.Exists(Path.Combine(root, "argv")))
        }

    [<Fact>]
    member _.``unsupported model transport record refuses before launch``() =
        task {
            let root = Directory.CreateTempSubdirectory("learning-capability-model-").FullName

            let! result =
                LearningCapabilityFixture.launch root (fun query ->
                    LearningCapabilityFixture.modelList "unsupported-model.json" query)

            Assert.Equal(LaunchRefused "learning-selection-unsupported:requested-model-unsupported", result)
            Assert.False(File.Exists(Path.Combine(root, "argv")))
        }

    [<Fact>]
    member _.``stale capability refuses before launch``() =
        task {
            let root = Directory.CreateTempSubdirectory("learning-capability-stale-").FullName

            let! result =
                LearningCapabilityFixture.launch root (fun query ->
                    let observedAt = DateTimeOffset.UtcNow.AddMinutes(-10.)

                    { CodexLearningProviderCapability.supported observedAt query "controlled-native-fixture" with
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 1.
                    })

            Assert.Equal(LaunchRefused "learning-selection-evidence-stale", result)
            Assert.False(File.Exists(Path.Combine(root, "argv")))
        }

    [<Fact>]
    member _.``changed executable identity refuses before launch``() =
        task {
            let root =
                Directory.CreateTempSubdirectory("learning-capability-executable-").FullName

            let! result =
                LearningCapabilityFixture.launch root (fun query ->
                    let evidence =
                        CodexLearningProviderCapability.supported
                            DateTimeOffset.UtcNow
                            query
                            "controlled-native-fixture"

                    { evidence with
                        Executable =
                            { evidence.Executable with
                                Path = evidence.Executable.Path + ".replaced"
                            }
                    })

            Assert.Equal(LaunchRefused "learning-selection-executable-identity-changed", result)
            Assert.False(File.Exists(Path.Combine(root, "argv")))
        }

    [<Fact>]
    member _.``trusted exact capability uses existing provider command and observer path``() =
        task {
            let root =
                Directory.CreateTempSubdirectory("learning-capability-supported-").FullName

            let discovery =
                CapabilityDiscovery(fun query -> LearningCapabilityFixture.modelList "supported.json" query)
                :> ICodexLearningCapabilityDiscovery

            let workspace, _, provider = LearningCapabilityFixture.provider root discovery
            let intent = Fixture.intent workspace (TimeSpan.FromSeconds 5.)

            let! launched =
                (provider :> ILearningExecutionProvider).LaunchLearning(intent, CancellationToken.None)

            let reference =
                match launched with
                | LaunchStarted value -> value.Session
                | value -> failwithf "%A" value

            let! terminal = Fixture.waitTerminal (provider :> IExecutionProvider) reference
            Assert.Equal(Succeeded, terminal.Lifecycle)
            let arguments = File.ReadAllLines(Path.Combine(root, "argv"))
            Assert.Contains("--model", arguments)
            Assert.Contains("fixture-model", arguments)
            Assert.Contains("model_reasoning_effort=\"medium\"", arguments)
        }
