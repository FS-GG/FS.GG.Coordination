namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution

type ICodexExecutionInput =
    abstract member ReadUtf8: digest:string * CancellationToken -> Task<Result<byte array,string>>

type ICodexCandidateInspector =
    abstract member Verify: workspace:string * CandidateReference * CancellationToken -> Task<Result<unit,string>>

type CodexExecutionProviderOptions =
    { Executable: string
      ExpectedVersion: string
      StateRoot: string
      MaximumStreamBytes: int
      StartupTimeout: TimeSpan
      EnvironmentAllowList: Set<string> }

[<RequireQualifiedAccess>]
module CodexExecutionProviderOptions =
    let create executable stateRoot =
        { Executable=executable;ExpectedVersion="codex-cli 0.154.0";StateRoot=stateRoot
          MaximumStreamBytes=1024*1024;StartupTimeout=TimeSpan.FromSeconds 15.
          EnvironmentAllowList=set ["HOME";"PATH";"LANG";"LC_ALL";"TERM";"TMPDIR";"CODEX_HOME";"XDG_CONFIG_HOME";"XDG_DATA_HOME";"XDG_CACHE_HOME"] }

type CodexCommand = { FileName:string; Arguments:string list; WorkingDirectory:string }

[<RequireQualifiedAccess>]
module CodexCommand =
    let private common (options:CodexExecutionProviderOptions) (intent:LaunchIntent) schemaPath outputPath =
        [ "exec";"--json";"--color";"never";"--sandbox";"workspace-write"
          "-C";intent.Workspace;"--output-schema";schemaPath;"--output-last-message";outputPath ]
        @ (intent.Requested.Model |> Option.map(fun value -> ["--model";value]) |> Option.defaultValue [])
        @ (intent.Requested.Effort |> Option.map(fun value -> ["-c";$"model_reasoning_effort={JsonSerializer.Serialize value}"]) |> Option.defaultValue [])
    let launch options intent schemaPath outputPath =
        { FileName=options.Executable;Arguments=common options intent schemaPath outputPath @ ["-"];WorkingDirectory=intent.Workspace }
    let resume options intent schemaPath outputPath threadId =
        { FileName=options.Executable;Arguments=common options intent schemaPath outputPath @ ["resume";threadId;"-"];WorkingDirectory=intent.Workspace }

type private CompletionEnvelope =
    { InputDigest:string;CandidateId:Guid;HeadSha:string;TreeSha:string }

type private RunningProcess =
    { Intent:LaunchIntent;Reference:ProviderSessionReference;Process:Process;Directory:string
      StdoutPath:string;StderrPath:string;FinalPath:string;StartedAt:DateTimeOffset
      Completion:Task<SessionObservation>;CancelRequested:bool ref }

type CodexExecutionProvider(options:CodexExecutionProviderOptions,input:ICodexExecutionInput,candidateInspector:ICodexCandidateInspector,clock:TimeProvider) =
    let identity={Provider="Codex";AdapterVersion="codex-subscription-exec/1"}
    let sessions=ConcurrentDictionary<string,RunningProcess>()
    let sha256File path =
        use stream=File.OpenRead path
        SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant()
    let sessionValue reference = ProviderSessionReference.value reference
    let attemptDirectory (intent:LaunchIntent) =
        Path.Combine(options.StateRoot,intent.Key.AssignmentId.ToString("N"),intent.Key.AttemptId.ToString("N"),intent.Key.Generation.ToString(CultureInfo.InvariantCulture))
    let scrubEnvironment (start:ProcessStartInfo) =
        let retained =
            start.Environment
            |> Seq.choose(fun pair -> if options.EnvironmentAllowList.Contains pair.Key then Some(pair.Key,pair.Value) else None)
            |> Seq.toArray
        start.Environment.Clear()
        retained |> Array.iter(fun (key,value) -> start.Environment[key] <- value)
    let processStartInfo (command:CodexCommand) =
        let start=ProcessStartInfo(command.FileName,WorkingDirectory=command.WorkingDirectory,UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true)
        command.Arguments |> List.iter start.ArgumentList.Add
        scrubEnvironment start
        start
    let readBounded (stream:Stream) maximumBytes = task {
        let buffer=Array.zeroCreate<byte> 4096
        use captured=new MemoryStream(min maximumBytes 4096)
        let mutable complete=false
        while not complete do
            let! count=stream.ReadAsync(buffer.AsMemory())
            if count=0 then complete<-true
            else
                let remaining=maximumBytes-int captured.Length
                if remaining>0 then captured.Write(buffer,0,min remaining count)
        return Encoding.UTF8.GetString(captured.ToArray()) }
    let pumpBounded (stream:Stream) path maximumBytes (onLine:string->unit) = task {
        let buffer=Array.zeroCreate<byte> 4096
        let line=Array.zeroCreate<byte> maximumBytes
        let mutable lineLength=0
        let mutable lineOverflow=false
        let mutable written=0
        use output=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read)
        let emit () =
            if not lineOverflow && lineLength>0 then
                let length=if line[lineLength-1]=13uy then lineLength-1 else lineLength
                onLine(Encoding.UTF8.GetString(line,0,length))
            lineLength<-0;lineOverflow<-false
        let mutable complete=false
        while not complete do
            let! count=stream.ReadAsync(buffer.AsMemory())
            if count=0 then complete<-true
            else
                let remaining=maximumBytes-written
                if remaining>0 then
                    let countToWrite=min remaining count
                    do! output.WriteAsync(buffer.AsMemory(0,countToWrite))
                    written<-written+countToWrite
                for index in 0..count-1 do
                    if buffer[index]=10uy then emit()
                    elif lineLength<line.Length then line[lineLength]<-buffer[index];lineLength<-lineLength+1
                    else lineOverflow<-true
        emit() }
    let probe arguments (cancellationToken:CancellationToken) = task {
        let start=ProcessStartInfo(options.Executable,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true)
        arguments |> List.iter start.ArgumentList.Add
        scrubEnvironment start
        use proc=new Process(StartInfo=start)
        if not(proc.Start()) then raise(InvalidOperationException "codex-probe-start-refused")
        let output=readBounded proc.StandardOutput.BaseStream 4096
        let error=readBounded proc.StandardError.BaseStream 4096
        let exit=proc.WaitForExitAsync()
        let! winner=Task.WhenAny(exit,Task.Delay(options.StartupTimeout,cancellationToken))
        if not(obj.ReferenceEquals(winner,exit)) then
            try proc.Kill(true) with _ -> ()
            do! proc.WaitForExitAsync()
            cancellationToken.ThrowIfCancellationRequested()
            raise(TimeoutException "codex-probe-timeout")
        let! stdout=output
        let! stderr=error
        return proc.ExitCode,stdout,stderr }
    let readiness cancellationToken = task {
        try
            let! versionExit,versionOut,versionError=probe ["--version"] cancellationToken
            if versionExit<>0 || (versionOut+versionError).Trim()<>options.ExpectedVersion then
                return AuthenticationUnknown "codex-version-mismatch"
            else
                let! loginExit,stdout,stderr=probe ["login";"status"] cancellationToken
                if loginExit=0 && (stdout+stderr).Contains("Logged in using ChatGPT",StringComparison.OrdinalIgnoreCase) then
                    return Authenticated "codex-login-status:chatgpt-subscription"
                elif loginExit=0 then return AuthenticationUnknown "codex-login-status-unrecognized-success"
                else return NotAuthenticated "codex-login-status-refused"
        with :? OperationCanceledException -> return AuthenticationUnknown "codex-login-status-cancelled"
           | _ -> return AuthenticationUnknown "codex-login-status-unavailable" }
    let writeSchema path =
        File.WriteAllText(path,"""{"type":"object","additionalProperties":false,"required":["inputDigest","candidateId","headSha","treeSha"],"properties":{"inputDigest":{"type":"string"},"candidateId":{"type":"string"},"headSha":{"type":"string"},"treeSha":{"type":"string"}}}""")
    let unknownUsage provenance = {Values=Map["provider-usage",UsageUnknown provenance];Cost=CostNotApplicable "codex-chatgpt-subscription-no-per-invocation-price"}
    let parseUsage (lines:seq<string>) =
        lines |> Seq.tryPick(fun line ->
            try
                use document=JsonDocument.Parse line
                let root=document.RootElement
                if root.GetProperty("type").GetString()<>"turn.completed" || not(root.TryGetProperty("usage") |> fst) then None
                else
                    let usage=root.GetProperty "usage"
                    let value (name:string) =
                        try let item=usage.GetProperty name in if item.ValueKind=JsonValueKind.Number then Some(item.GetInt64()) else None
                        with _ -> None
                    let values=["input_tokens";"cached_input_tokens";"output_tokens";"reasoning_output_tokens"] |> List.map(fun name -> name,value name)
                    if values |> List.exists(snd >> Option.isNone) then None
                    else Some {Values=values |> List.map(fun (name,value) -> name,UsageKnown(value.Value,"tokens","codex-exec-jsonl:turn.completed")) |> Map.ofList
                               Cost=CostNotApplicable "codex-chatgpt-subscription-no-per-invocation-price"}
            with _ -> None)
        |> Option.defaultValue(unknownUsage "codex-exec-jsonl:usage-absent-or-malformed")
    let fatalClassification (lines:seq<string>) =
        lines |> Seq.tryPick(fun line ->
            try
                use document=JsonDocument.Parse line
                let root=document.RootElement
                let kind=root.GetProperty("type").GetString()
                let message =
                    if kind="error" then root.GetProperty("message").GetString()
                    elif kind="turn.failed" then root.GetProperty("error").GetProperty("message").GetString()
                    else null
                if isNull message then None
                elif message.Contains("quota",StringComparison.OrdinalIgnoreCase) || message.Contains("usage limit",StringComparison.OrdinalIgnoreCase) || message.Contains("rate limit",StringComparison.OrdinalIgnoreCase) then Some "codex-quota-error"
                else Some "codex-fatal-error"
            with _ -> None)
    let startProcess (intent:LaunchIntent) (commandBuilder:string->string->CodexCommand) (prompt:byte array) cancellationToken = task {
        let directory=attemptDirectory intent
        Directory.CreateDirectory directory |> ignore
        let schemaPath=Path.Combine(directory,"completion-schema.json")
        let finalPath=Path.Combine(directory,"final.json")
        let stdoutPath=Path.Combine(directory,"stdout.jsonl")
        let stderrPath=Path.Combine(directory,"stderr.log")
        let fatalPath=Path.Combine(directory,"fatal.receipt")
        writeSchema schemaPath
        let command=commandBuilder schemaPath finalPath
        let receiptPath=Path.Combine(directory,"spawn-intent.json")
        let receipt=$"{{\"schema\":\"fsgg.codex.spawn-intent/1\",\"assignmentId\":\"{intent.Key.AssignmentId}\",\"attemptId\":\"{intent.Key.AttemptId}\",\"generation\":{intent.Key.Generation},\"inputDigest\":\"{intent.InputDigest}\"}}"
        File.WriteAllText(receiptPath,receipt)
        let proc=new Process(StartInfo=processStartInfo command,EnableRaisingEvents=true)
        if not(proc.Start()) then raise(InvalidOperationException "codex-process-start-refused")
        let startedAt=clock.GetUtcNow()
        let runtimeDeadline=min intent.Limits.Deadline (intent.RecordedAt.Add intent.Limits.MaximumRuntime)
        let remaining=max TimeSpan.Zero (runtimeDeadline-startedAt)
        let cancelRequested=ref false
        let threadStarted=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable completedUsage:NormalizedUsage option=None
        let mutable fatalEvent:string option=None
        let mutable turnCompleted=false
        let stdoutPump=pumpBounded proc.StandardOutput.BaseStream stdoutPath options.MaximumStreamBytes (fun line ->
            let usage=parseUsage [line]
            if not(usage.Values.ContainsKey "provider-usage") then completedUsage<-Some usage
            fatalClassification [line] |> Option.iter(fun value -> fatalEvent<-Some value)
            try use document=JsonDocument.Parse line
                let eventType=document.RootElement.GetProperty("type").GetString()
                if eventType="thread.started" then threadStarted.TrySetResult(document.RootElement.GetProperty("thread_id").GetString()) |> ignore
                elif eventType="turn.completed" then turnCompleted<-true
            with _ -> ())
        let stderrPump=pumpBounded proc.StandardError.BaseStream stderrPath options.MaximumStreamBytes ignore
        let inputWrite = task {
            try
                do! proc.StandardInput.BaseStream.WriteAsync(ReadOnlyMemory prompt,cancellationToken)
                proc.StandardInput.Close()
            with _ ->
                try proc.StandardInput.Close() with _ -> () }
        let completion = task {
            let exit=proc.WaitForExitAsync()
            let timeout=Task.Delay(remaining)
            let! first=Task.WhenAny(exit,timeout)
            let mutable deadlineExceeded=false
            if obj.ReferenceEquals(first,timeout) then
                deadlineExceeded<-true
                try proc.Kill(true) with _ -> ()
                do! proc.WaitForExitAsync()
            do! inputWrite
            do! stdoutPump
            do! stderrPump
            let usage,fatal=completedUsage,fatalEvent
            let! lifecycle, candidate = task {
                if deadlineExceeded then return DeadlineExceeded,None
                elif cancelRequested.Value then return OutcomeUnknown,None
                elif fatal.IsSome then return Failed,None
                elif proc.ExitCode<>0 then return Failed,None
                elif not turnCompleted then return OutcomeUnknown,None
                elif not(File.Exists finalPath) then return OutcomeUnknown,None
                elif FileInfo(finalPath).Length>int64 options.MaximumStreamBytes then return OutcomeUnknown,None
                else
                    try
                        use stream=File.OpenRead finalPath
                        use document=JsonDocument.Parse(stream,JsonDocumentOptions(MaxDepth=8))
                        let root=document.RootElement
                        let envelope=
                            { InputDigest=root.GetProperty("inputDigest").GetString()
                              CandidateId=root.GetProperty("candidateId").GetGuid()
                              HeadSha=root.GetProperty("headSha").GetString()
                              TreeSha=root.GetProperty("treeSha").GetString() }
                        if not(String.Equals(envelope.InputDigest,intent.InputDigest,StringComparison.OrdinalIgnoreCase)) then return OutcomeUnknown,None
                        else
                            let candidate={CandidateId=envelope.CandidateId;HeadSha=envelope.HeadSha;TreeSha=envelope.TreeSha}
                            let! verified=candidateInspector.Verify(intent.Workspace,candidate,CancellationToken.None)
                            match verified with Ok () -> return Succeeded,Some candidate | Error _ -> return OutcomeUnknown,None
                    with _ -> return OutcomeUnknown,None }
            let session =
                match threadStarted.Task.IsCompletedSuccessfully with
                | true -> ProviderSessionReference.create("codex-thread:"+threadStarted.Task.Result) |> Result.defaultWith failwith
                | false -> ProviderSessionReference.create($"codex-process:{proc.Id}:{startedAt.ToUnixTimeMilliseconds()}") |> Result.defaultWith failwith
            let outputs=
                [ if File.Exists finalPath then {Kind="final-output";Reference=finalPath;Digest=Some(sha256File finalPath)}
                  {Kind="codex-jsonl";Reference=stdoutPath;Digest=Some(sha256File stdoutPath)}
                  {Kind="codex-stderr";Reference=stderrPath;Digest=Some(sha256File stderrPath)} ]
            fatal |> Option.iter(fun kind -> File.WriteAllText(fatalPath,kind+Environment.NewLine))
            let lifecycleReferences=fatal |> Option.map(fun kind -> [{Kind=kind;Reference=fatalPath;Digest=Some(sha256File fatalPath)}]) |> Option.defaultValue []
            return {Provider=identity;Session=session;Resolved={Model=intent.Requested.Model;Effort=intent.Requested.Effort};Lifecycle=lifecycle
                    Output=outputs;LifecycleReferences=lifecycleReferences;Usage=usage |> Option.defaultValue(unknownUsage "codex-exec-jsonl:usage-absent-or-malformed");Candidate=candidate;ObservedAt=clock.GetUtcNow()} }
        let! first=Task.WhenAny(threadStarted.Task,completion,Task.Delay(options.StartupTimeout,cancellationToken))
        if obj.ReferenceEquals(first,threadStarted.Task) then
            let! threadId=threadStarted.Task
            let reference=ProviderSessionReference.create("codex-thread:"+threadId) |> Result.defaultWith failwith
            let running={Intent=intent;Reference=reference;Process=proc;Directory=directory;StdoutPath=stdoutPath;StderrPath=stderrPath;FinalPath=finalPath;StartedAt=startedAt;Completion=completion;CancelRequested=cancelRequested}
            sessions[sessionValue reference]<-running
            return Ok running
        elif obj.ReferenceEquals(first,completion) then return Error "codex-completed-before-thread-started"
        else
            try proc.Kill(true) with _ -> ()
            do! completion :> Task
            return Error "codex-thread-start-timeout" }
    interface IExecutionProvider with
        member _.ObserveReadiness cancellationToken = task {
            let! authentication=readiness cancellationToken
            return {Identity=identity;Authentication=authentication;SupportsResume=true;ObservedAt=clock.GetUtcNow()} }
        member _.Launch(intent,cancellationToken) = task {
            if options.MaximumStreamBytes<1 || options.StartupTimeout<=TimeSpan.Zero || String.IsNullOrWhiteSpace options.StateRoot then
                return LaunchRefused "codex-adapter-options-invalid"
            else
                let! authentication=readiness cancellationToken
                match authentication with
                | Authenticated _ ->
                    let! bytes=input.ReadUtf8(intent.InputDigest,cancellationToken)
                    match bytes with
                    | Error reason -> return LaunchRefused reason
                    | Ok prompt when not(String.Equals(Convert.ToHexString(SHA256.HashData prompt),intent.InputDigest,StringComparison.OrdinalIgnoreCase)) -> return LaunchRefused "codex-input-digest-mismatch"
                    | Ok prompt ->
                        let! started=startProcess intent (fun schema output -> CodexCommand.launch options intent schema output) prompt cancellationToken
                        match started with
                        | Error reason -> return LaunchAmbiguous reason
                        | Ok running ->
                            return LaunchStarted {Provider=identity;Session=running.Reference;Resolved={Model=intent.Requested.Model;Effort=intent.Requested.Effort};Lifecycle=Running
                                                  Output=[];LifecycleReferences=[];Usage=unknownUsage "codex-turn-not-completed";Candidate=None;ObservedAt=clock.GetUtcNow()}
                | NotAuthenticated provenance -> return LaunchRefused provenance
                | AuthenticationUnknown provenance -> return LaunchRefused provenance }
        member _.Observe(reference,_) = task {
            match sessions.TryGetValue(sessionValue reference) with
            | false,_ -> return Error "codex-session-not-supervised"
            | true,running when running.Completion.IsCompleted -> return Ok running.Completion.Result
            | true,running ->
                return Ok {Provider=identity;Session=reference;Resolved={Model=running.Intent.Requested.Model;Effort=running.Intent.Requested.Effort}
                           Lifecycle=(if running.CancelRequested.Value then Cancelling else Running);Output=[];LifecycleReferences=[]
                           Usage=unknownUsage "codex-turn-not-completed";Candidate=None;ObservedAt=clock.GetUtcNow()} }
        member _.Reconcile(intent,_) = task {
            match sessions.Values |> Seq.tryFind(fun running -> running.Intent.Key=intent.Key) with
            | Some running when running.Completion.IsCompleted -> return Reconciled running.Completion.Result
            | Some running ->
                return Reconciled {Provider=identity;Session=running.Reference;Resolved={Model=intent.Requested.Model;Effort=intent.Requested.Effort};Lifecycle=Running
                                   Output=[];LifecycleReferences=[];Usage=unknownUsage "codex-turn-not-completed";Candidate=None;ObservedAt=clock.GetUtcNow()}
            | None when File.Exists(Path.Combine(attemptDirectory intent,"spawn-intent.json")) -> return ReconcileUnknown "codex-spawn-receipt-without-local-supervisor"
            | None -> return ConfirmedAbsent }
        member _.Cancel(reference,_) = task {
            match sessions.TryGetValue(sessionValue reference) with
            | false,_ -> return CancelUnknown "codex-session-not-supervised"
            | true,running when running.Process.HasExited -> return CancelRefused "codex-session-already-terminal"
            | true,running ->
                running.CancelRequested.Value<-true
                try running.Process.Kill(true);return CancelAccepted
                with _ -> return CancelUnknown "codex-process-tree-termination-ambiguous" }

[<RequireQualifiedAccess>]
module CodexExecution =
    let provider options input candidateInspector clock =
        CodexExecutionProvider(options,input,candidateInspector,clock) :> IExecutionProvider
    let coordinator options input candidateInspector journal clock =
        ExecutionSessionCoordinator(provider options input candidateInspector clock,journal,clock)
    let supervisedActorProps options input candidateInspector journal clock =
        ExecutionSessionActor.Props(coordinator options input candidateInspector journal clock)
