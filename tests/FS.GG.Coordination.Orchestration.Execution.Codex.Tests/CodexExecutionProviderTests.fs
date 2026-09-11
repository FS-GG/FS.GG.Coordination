namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type Input(bytes:byte array) =
    interface ICodexExecutionInput with
        member _.ReadUtf8(_,_) = Task.FromResult(Ok bytes)

type CandidateInspector(accepted:bool) =
    interface ICodexCandidateInspector with
        member _.Verify(_,_,_) = Task.FromResult(if accepted then Ok() else Error "candidate-refused")

type Behavior = { Version:string;Login:string;LoginExit:int;Stderr:string;Body:string }

module Fixture =
    let prompt=Encoding.UTF8.GetBytes("literal $(touch never) ; ' quoted\nsecond line")
    let digest=Convert.ToHexString(SHA256.HashData prompt).ToLowerInvariant()
    let intent workspace runtime =
        { Schema=ExecutionProtocol.launchSchema
          Key={AssignmentId=Guid.NewGuid();AttemptId=Guid.NewGuid();Generation=7L}
          InputDigest=digest;Workspace=workspace;Requested={Model=Some "fixture-model";Effort=Some "medium"}
          Limits={Deadline=DateTimeOffset.UtcNow.Add runtime;MaximumRuntime=runtime;MaximumAttempts=1};RecordedAt=DateTimeOffset.UtcNow }
    let intentFor workspace runtime inputDigest = { intent workspace runtime with InputDigest=inputDigest }
    let rawScript root name text =
        let path=Path.Combine(root,name)
        File.WriteAllText(path,"#!/bin/sh\nset -eu\n"+text+"\n")
        File.SetUnixFileMode(path,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        path
    let script root behavior =
        let path=Path.Combine(root,"codex-fixture")
        let text=$"""#!/bin/sh
set -eu
if [ "$1" = --version ]; then printf '%%s\n' '{behavior.Version}'; exit 0; fi
if [ "$1" = login ]; then printf '%%s\n' '{behavior.Login}'; exit {behavior.LoginExit}; fi
printf '%%s\n' "$PWD" > '{root}/cwd'
printf '%%s\n' "$@" > '{root}/argv'
printenv GH_TOKEN > '{root}/secret' 2>/dev/null || true
final=''
while [ "$#" -gt 0 ]; do
  if [ "$1" = --output-last-message ]; then final="$2"; shift 2; else shift; fi
done
cat > '{root}/stdin'
printf '%%s\n' '{{"type":"thread.started","thread_id":"thread-fixture-1"}}'
printf '%%s\n' '{{"type":"turn.started"}}'
printf '%%s\n' '{behavior.Stderr}' >&2
{behavior.Body}
"""
        File.WriteAllText(path,text)
        File.SetUnixFileMode(path,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        path
    let options executable stateRoot =
        { CodexExecutionProviderOptions.create executable stateRoot with StartupTimeout=TimeSpan.FromSeconds 2.;MaximumStreamBytes=4096 }
    let waitTerminal (provider:IExecutionProvider) reference = task {
        let mutable result=None
        while result.IsNone do
            let! observed=provider.Observe(reference,CancellationToken.None)
            match observed with
            | Ok value when value.Lifecycle<>Running && value.Lifecycle<>Cancelling -> result<-Some value
            | _ -> do! Task.Delay 10
        return result.Value }

type CodexExecutionProviderTests() =
    let successful root =
        { Version="codex-cli 0.154.0";Login="Logged in using ChatGPT";LoginExit=0;Stderr="diagnostic"
          Body=$"head -c 100000 /dev/zero | tr '\\000' x; printf '\\n'\ni=0; while [ $i -lt 500 ]; do printf 'diagnostic-stdout-padding-%%04d\\n' $i; printf 'diagnostic-stderr-padding-%%04d\\n' $i >&2; i=$((i+1)); done\nprintf '%%s\\n' '{{\"inputDigest\":\"{Fixture.digest}\",\"candidateId\":\"30000000-0000-0000-0000-000000000003\",\"headSha\":\"abc\",\"treeSha\":\"def\"}}' > \"$final\"\nprintf '%%s\\n' '{{\"type\":\"turn.completed\",\"usage\":{{\"input_tokens\":11,\"cached_input_tokens\":2,\"output_tokens\":3,\"reasoning_output_tokens\":4}}}}'" }

    [<Fact>]
    member _.``actual subprocess uses argument list stdin cwd bounded streams and validates candidate``() = task {
        let root=Directory.CreateTempSubdirectory("codex-adapter-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let executable=Fixture.script root (successful root)
        let provider=CodexExecutionProvider(Fixture.options executable (Path.Combine(root,"state")),Input(Fixture.prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! readiness=provider.ObserveReadiness CancellationToken.None
        Assert.Equal(Authenticated "codex-login-status:chatgpt-subscription",readiness.Authentication)
        let intent=Fixture.intent workspace (TimeSpan.FromSeconds 5.)
        let! launched=provider.Launch(intent,CancellationToken.None)
        let reference=match launched with LaunchStarted value -> value.Session | value -> failwithf "%A" value
        let! terminal=Fixture.waitTerminal provider reference
        Assert.Equal(Succeeded,terminal.Lifecycle)
        Assert.Equal(Some "abc",terminal.Candidate |> Option.map _.HeadSha)
        Assert.Equal(UsageKnown(11L,"tokens","codex-exec-jsonl:turn.completed"),terminal.Usage.Values["input_tokens"])
        Assert.Equal(CostNotApplicable "codex-chatgpt-subscription-no-per-invocation-price",terminal.Usage.Cost)
        Assert.True(Fixture.prompt = File.ReadAllBytes(Path.Combine(root,"stdin")))
        Assert.Equal(workspace,File.ReadAllText(Path.Combine(root,"cwd")).Trim())
        let argv=File.ReadAllLines(Path.Combine(root,"argv"))
        Assert.Contains("--model",argv);Assert.Contains("fixture-model",argv);Assert.Contains("-",argv)
        Assert.False(File.Exists(Path.Combine(workspace,"never")))
        Assert.Equal("",File.ReadAllText(Path.Combine(root,"secret")))
        Assert.True(File.ReadAllText(terminal.Output |> List.find(fun value -> value.Kind="codex-stderr") |> _.Reference).Length<=4096)
        Assert.True(File.ReadAllText(terminal.Output |> List.find(fun value -> value.Kind="codex-jsonl") |> _.Reference).Length<=4096) }

    [<Fact>]
    member _.``login refusal prevents spawn and unknown usage is never zero``() = task {
        let root=Directory.CreateTempSubdirectory("codex-login-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let behavior={Version="codex-cli 0.154.0";Login="Not logged in";LoginExit=1;Stderr="";Body="exit 9"}
        let provider=CodexExecutionProvider(Fixture.options (Fixture.script root behavior) (Path.Combine(root,"state")),Input(Fixture.prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! readiness=provider.ObserveReadiness CancellationToken.None
        Assert.Equal(NotAuthenticated "codex-login-status-refused",readiness.Authentication)
        let! launched=provider.Launch(Fixture.intent workspace (TimeSpan.FromSeconds 2.),CancellationToken.None)
        Assert.Equal(LaunchRefused "codex-login-status-refused",launched)
        Assert.False(File.Exists(Path.Combine(root,"stdin"))) }

    [<Fact>]
    member _.``digest and candidate mismatch fail closed``() = task {
        let root=Directory.CreateTempSubdirectory("codex-digest-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let executable=Fixture.script root (successful root)
        let wrong=Input(Encoding.UTF8.GetBytes "wrong")
        let provider=CodexExecutionProvider(Fixture.options executable (Path.Combine(root,"state")),wrong,CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! launched=provider.Launch(Fixture.intent workspace (TimeSpan.FromSeconds 2.),CancellationToken.None)
        Assert.Equal(LaunchRefused "codex-input-digest-mismatch",launched) }

    [<Fact>]
    member _.``timeout and cancel terminate process descendants without claiming request is termination``() = task {
        let root=Directory.CreateTempSubdirectory("codex-cancel-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let behavior={Version="codex-cli 0.154.0";Login="Logged in using ChatGPT";LoginExit=0;Stderr="";Body=$"sleep 30 &\nprintf '%%s' $! > '{root}/child-pid'\nwait"}
        let provider=CodexExecutionProvider(Fixture.options (Fixture.script root behavior) (Path.Combine(root,"state")),Input(Fixture.prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! launched=provider.Launch(Fixture.intent workspace (TimeSpan.FromMilliseconds 400.),CancellationToken.None)
        let reference=match launched with LaunchStarted value -> value.Session | value -> failwithf "%A" value
        let! accepted=provider.Cancel(reference,CancellationToken.None)
        Assert.Equal(CancelAccepted,accepted)
        let! terminal=Fixture.waitTerminal provider reference
        Assert.Equal(OutcomeUnknown,terminal.Lifecycle)
        let child=int(File.ReadAllText(Path.Combine(root,"child-pid")))
        do! Task.Delay 50
        Assert.ThrowsAny<ArgumentException>(fun () -> Diagnostics.Process.GetProcessById child |> ignore) |> ignore }

    [<Fact>]
    member _.``fatal quota event fails even on zero exit and missing usage stays unknown``() = task {
        let root=Directory.CreateTempSubdirectory("codex-quota-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let behavior={Version="codex-cli 0.154.0";Login="Logged in using ChatGPT";LoginExit=0;Stderr="";Body="printf '%s\\n' '{\"type\":\"error\",\"message\":\"usage limit reached\"}'"}
        let provider=CodexExecutionProvider(Fixture.options (Fixture.script root behavior) (Path.Combine(root,"state")),Input(Fixture.prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! launched=provider.Launch(Fixture.intent workspace (TimeSpan.FromSeconds 2.),CancellationToken.None)
        let reference=match launched with LaunchStarted value -> value.Session | value -> failwithf "%A" value
        let! terminal=Fixture.waitTerminal provider reference
        Assert.Equal(Failed,terminal.Lifecycle)
        Assert.Equal("codex-quota-error",terminal.LifecycleReferences.Head.Kind)
        Assert.Equal(UsageUnknown "codex-exec-jsonl:usage-absent-or-malformed",terminal.Usage.Values["provider-usage"]) }

    [<Fact>]
    member _.``finite deadline kills a hung process tree``() = task {
        let root=Directory.CreateTempSubdirectory("codex-timeout-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let behavior={Version="codex-cli 0.154.0";Login="Logged in using ChatGPT";LoginExit=0;Stderr="";Body="sleep 30"}
        let provider=CodexExecutionProvider(Fixture.options (Fixture.script root behavior) (Path.Combine(root,"state")),Input(Fixture.prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! launched=provider.Launch(Fixture.intent workspace (TimeSpan.FromMilliseconds 150.),CancellationToken.None)
        let reference=match launched with LaunchStarted value -> value.Session | value -> failwithf "%A" value
        let! terminal=Fixture.waitTerminal provider reference
        Assert.Equal(DeadlineExceeded,terminal.Lifecycle) }

    [<Fact>]
    member _.``stdin backpressure is inside runtime deadline``() = task {
        let root=Directory.CreateTempSubdirectory("codex-stdin-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let executable=Fixture.rawScript root "codex-no-stdin" "if [ \"$1\" = --version ]; then echo 'codex-cli 0.154.0'; exit 0; fi\nif [ \"$1\" = login ]; then echo 'Logged in using ChatGPT'; exit 0; fi\necho '{\"type\":\"thread.started\",\"thread_id\":\"thread-backpressure\"}'\nsleep 30"
        let prompt=Array.create (4*1024*1024) 97uy
        let digest=Convert.ToHexString(SHA256.HashData prompt).ToLowerInvariant()
        let provider=CodexExecutionProvider(Fixture.options executable (Path.Combine(root,"state")),Input(prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let started=Diagnostics.Stopwatch.StartNew()
        let! launched=provider.Launch(Fixture.intentFor workspace (TimeSpan.FromMilliseconds 200.) digest,CancellationToken.None)
        let reference=match launched with LaunchStarted value -> value.Session | value -> failwithf "%A" value
        let! terminal=Fixture.waitTerminal provider reference
        Assert.Equal(DeadlineExceeded,terminal.Lifecycle)
        Assert.True(started.Elapsed<TimeSpan.FromSeconds 2.) }

    [<Fact>]
    member _.``probe timeout kills and reaps the process``() = task {
        let root=Directory.CreateTempSubdirectory("codex-probe-").FullName
        let executable=Fixture.rawScript root "codex-hung-probe" $"echo $$ > '{root}/probe-pid'\nsleep 30"
        let options={Fixture.options executable (Path.Combine(root,"state")) with StartupTimeout=TimeSpan.FromMilliseconds 150.}
        let provider=CodexExecutionProvider(options,Input(Fixture.prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! readiness=provider.ObserveReadiness CancellationToken.None
        Assert.Equal(AuthenticationUnknown "codex-login-status-unavailable",readiness.Authentication)
        let pid=int(File.ReadAllText(Path.Combine(root,"probe-pid")))
        Assert.ThrowsAny<ArgumentException>(fun () -> Diagnostics.Process.GetProcessById pid |> ignore) |> ignore }

    [<Fact>]
    member _.``zero exit and final candidate require terminal turn event``() = task {
        let root=Directory.CreateTempSubdirectory("codex-terminal-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let body=$"printf '%%s\\n' '{{\"inputDigest\":\"{Fixture.digest}\",\"candidateId\":\"30000000-0000-0000-0000-000000000003\",\"headSha\":\"abc\",\"treeSha\":\"def\"}}' > \"$final\""
        let behavior={Version="codex-cli 0.154.0";Login="Logged in using ChatGPT";LoginExit=0;Stderr="";Body=body}
        let provider=CodexExecutionProvider(Fixture.options (Fixture.script root behavior) (Path.Combine(root,"state")),Input(Fixture.prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! launched=provider.Launch(Fixture.intent workspace (TimeSpan.FromSeconds 2.),CancellationToken.None)
        let reference=match launched with LaunchStarted value -> value.Session | value -> failwithf "%A" value
        let! terminal=Fixture.waitTerminal provider reference
        Assert.Equal(OutcomeUnknown,terminal.Lifecycle)
        Assert.True(terminal.Candidate.IsNone) }

    [<Fact>]
    member _.``unaccepted candidate is ambiguous rather than success``() = task {
        let root=Directory.CreateTempSubdirectory("codex-candidate-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let provider=CodexExecutionProvider(Fixture.options (Fixture.script root (successful root)) (Path.Combine(root,"state")),Input(Fixture.prompt),CandidateInspector(false),TimeProvider.System) :> IExecutionProvider
        let! launched=provider.Launch(Fixture.intent workspace (TimeSpan.FromSeconds 2.),CancellationToken.None)
        let reference=match launched with LaunchStarted value -> value.Session | value -> failwithf "%A" value
        let! terminal=Fixture.waitTerminal provider reference
        Assert.Equal(OutcomeUnknown,terminal.Lifecycle)
        Assert.True(terminal.Candidate.IsNone) }

    [<Fact>]
    member _.``resume command retains original authority and opaque provider id is not runner id``() =
        let root=Directory.CreateTempSubdirectory("codex-resume-").FullName
        let intent=Fixture.intent root (TimeSpan.FromSeconds 5.)
        let command=CodexCommand.resume (Fixture.options "/bin/codex" root) intent "/state/schema.json" "/state/final.json" "thread-opaque"
        Assert.Equal("exec",command.Arguments[0])
        Assert.Contains("resume",command.Arguments)
        Assert.Contains("thread-opaque",command.Arguments)
        Assert.Contains("-",command.Arguments)
        Assert.DoesNotContain(intent.Key.AssignmentId.ToString(),command.Arguments)

    [<Fact>]
    member _.``persisted spawn without supervisor is ambiguous and never relaunched``() = task {
        let root=Directory.CreateTempSubdirectory("codex-reconcile-").FullName
        let workspace=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
        let intent=Fixture.intent workspace (TimeSpan.FromSeconds 5.)
        let directory=Path.Combine(root,"state",intent.Key.AssignmentId.ToString("N"),intent.Key.AttemptId.ToString("N"),"7")
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(Path.Combine(directory,"spawn-intent.json"),"{}")
        let provider=CodexExecutionProvider(Fixture.options "/does/not/run" (Path.Combine(root,"state")),Input(Fixture.prompt),CandidateInspector(true),TimeProvider.System) :> IExecutionProvider
        let! result=provider.Reconcile(intent,CancellationToken.None)
        Assert.Equal(ReconcileUnknown "codex-spawn-receipt-without-local-supervisor",result) }
