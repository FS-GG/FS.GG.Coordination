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

    let private model model effort =
        $"{{\"model\":\"{model}\",\"supportedReasoningEfforts\":[{{\"reasoningEffort\":\"{effort}\",\"description\":\"fixture\"}}]}}"

    let private result requestId data nextCursor =
        let cursor = nextCursor |> Option.map (sprintf "\"%s\"") |> Option.defaultValue "null"
        $"{{\"id\":{requestId},\"result\":{{\"data\":[{data}],\"nextCursor\":{cursor}}}}}"

    let nativeScript root scenario =
        let responses, beforeResponse =
            match scenario with
            | "supported" ->
                [ result 2 (model "different-model" "low") (Some "page-2")
                  result 3 (model "fixture-model" "medium") None ],
                ""
            | "absent-model" -> [ result 2 (model "different-model" "medium") None ], ""
            | "absent-effort" -> [ result 2 (model "fixture-model" "low") None ], ""
            | "malformed" -> [ "{bad" ], ""
            | "malformed-entry" ->
                [ "{\"id\":2,\"result\":{\"data\":[{\"model\":\"fixture-model\",\"supportedReasoningEfforts\":[{}]}],\"nextCursor\":null}}" ],
                ""
            | "oversized" -> [ String.replicate 5000 "x" ], ""
            | "duplicate-pages" ->
                [ result 2 (model "different-model" "low") (Some "again")
                  result 3 (model "different-model" "low") (Some "again") ],
                ""
            | "changed-executable" ->
                [ result 2 (model "fixture-model" "medium") None ],
                "printf '# changed\\n' >> \"$0\""
            | "slow" -> [ result 2 (model "fixture-model" "medium") None ], "sleep 3"
            | "transport" -> [], "exit 12"
            | "incomplete" ->
                [ for index in 2..9 ->
                      result index (model $"different-{index}" "low") (Some $"page-{index}") ],
                ""
            | value -> failwithf "unknown scenario %s" value

        let responseCases =
            responses
            |> List.mapi (fun index response ->
                $"  {index + 1}) printf '%%s\\n' '{response}' ;;" )
            |> String.concat "\n"

        Fixture.rawScript
            root
            "codex-native-fixture"
            $"""
if [ "$1" = --version ]; then printf '%%s\n' 'codex-cli 0.154.0'; exit 0; fi
if [ "$1" = login ]; then printf '%%s\n' 'Logged in using ChatGPT'; exit 0; fi
if [ "$1" = app-server ]; then
  IFS= read -r initialize
  printf '%%s\n' '{{"id":1,"result":{{"userAgent":"fixture","platformFamily":"unix","platformOs":"linux"}}}}'
  IFS= read -r initialized
  page=0
  while IFS= read -r request; do
    page=$((page+1))
    {beforeResponse}
    case "$page" in
{responseCases}
      *) exit 13 ;;
    esac
  done
  exit 0
fi
printf '%%s\n' started > '{root}/model-started'
final=''
while [ "$#" -gt 0 ]; do
  if [ "$1" = --output-last-message ]; then final="$2"; shift 2; else shift; fi
done
cat >/dev/null
printf '%%s\n' '{{"status":"completed","summary":"done"}}' > "$final"
printf '%%s\n' '{{"type":"thread.started","thread_id":"thread-fixture-1"}}'
printf '%%s\n' '{{"type":"turn.started"}}'
printf '%%s\n' '{{"type":"turn.completed","provider":"openai","model":"fixture-model","effort":"medium","backend":"subscription","usage":{{"input_tokens":2,"cached_input_tokens":0,"output_tokens":1}}}}'
"""

    let productionProvider root scenario =
        let workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName
        let executable = nativeScript root scenario
        let mutable options = Fixture.options executable (Path.Combine(root, "state"))

        if scenario = "slow" then
            options <- { options with StartupTimeout = TimeSpan.FromMilliseconds 150. }

        let provider =
            CodexExecution.provider options (Input(Fixture.prompt)) (CandidateInspector(true)) TimeProvider.System

        workspace, provider

type LearningProviderCapabilityTests() =
    [<Fact>]
    member _.``production factory collects every native model page before exact launch``() =
        task {
            let root = Directory.CreateTempSubdirectory("learning-native-supported-").FullName
            let workspace, provider = LearningCapabilityFixture.productionProvider root "supported"
            let learning = provider :?> ILearningExecutionProvider
            let intent = Fixture.intent workspace (TimeSpan.FromSeconds 5.)
            let! evidence = learning.ObserveLearningSelection(intent.Requested, CancellationToken.None)
            Assert.Equal(LearningCapabilityStatus.Supported, evidence.Status)
            Assert.StartsWith("codex-app-server:model/list;config-sha256=", evidence.Provenance)
            Assert.False(File.Exists(Path.Combine(root, "model-started")))

            let! launched = learning.LaunchLearning(intent, CancellationToken.None)

            let reference =
                match launched with
                | LaunchStarted value -> value.Session
                | value -> failwithf "%A" value

            let! terminal = Fixture.waitTerminal provider reference
            Assert.Equal(Succeeded, terminal.Lifecycle)
            Assert.True(File.Exists(Path.Combine(root, "model-started")))
        }

    [<Theory>]
    [<InlineData("absent-model", "learning-selection-unsupported:requested-model-unsupported")>]
    [<InlineData("absent-effort", "learning-selection-unsupported:requested-effort-unsupported")>]
    [<InlineData("malformed", "learning-selection-unknown:codex-model-list-invalid")>]
    [<InlineData("malformed-entry", "learning-selection-unknown:codex-model-list-invalid")>]
    [<InlineData("oversized", "learning-selection-unknown:codex-model-list-bytes-exceeded")>]
    [<InlineData("duplicate-pages", "learning-selection-unknown:codex-model-list-duplicate-page")>]
    [<InlineData("incomplete", "learning-selection-unknown:codex-model-list-incomplete")>]
    [<InlineData("transport", "learning-selection-unknown:codex-model-list-transport-unavailable")>]
    [<InlineData("slow", "learning-selection-unknown:codex-model-list-deadline-exceeded")>]
    [<InlineData("changed-executable", "learning-selection-unknown:codex-model-list-executable-changed")>]
    member _.``production factory refuses unproven native selection without model start``(scenario, expected) =
        task {
            let root = Directory.CreateTempSubdirectory("learning-native-refused-").FullName
            let workspace, provider = LearningCapabilityFixture.productionProvider root scenario
            let learning = provider :?> ILearningExecutionProvider

            let! launched =
                learning.LaunchLearning(Fixture.intent workspace (TimeSpan.FromSeconds 5.), CancellationToken.None)

            Assert.Equal(LaunchRefused expected, launched)
            Assert.False(File.Exists(Path.Combine(root, "model-started")))
        }

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
    member _.``changed account configuration boundary refuses at final launch consumer``() =
        task {
            let root = Directory.CreateTempSubdirectory("learning-capability-config-").FullName

            let! result =
                LearningCapabilityFixture.launch root (fun query ->
                    CodexLearningProviderCapability.supported
                        DateTimeOffset.UtcNow
                        query
                        "codex-app-server:model/list;config-sha256=ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")

            Assert.Equal(LaunchRefused "learning-selection-config-boundary-changed", result)
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
