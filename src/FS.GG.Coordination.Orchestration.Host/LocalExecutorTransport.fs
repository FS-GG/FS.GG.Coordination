namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Buffers.Binary
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Runner.Protocol

/// Serializes the Host protocol onto one supervised local runner child. A failed,
/// cancelled, or malformed exchange discards the child so the next reconciliation
/// starts from the runner's durable state with a clean frame boundary.
[<Sealed>]
type LocalExecutorTransport(configuration:LocalExecutorConfiguration, ?maximumAggregateBytes:int, ?shutdownTimeout:TimeSpan) =
    let maximumAggregateBytes=defaultArg maximumAggregateBytes (150*1024*1024)
    let shutdownTimeout=defaultArg shutdownTimeout (TimeSpan.FromSeconds 5.)
    let gate=new SemaphoreSlim(1,1)
    let mutable child:Process option=None
    let mutable disposed=false

    let requireAbsolute name value =
        if String.IsNullOrWhiteSpace value || not(Path.IsPathFullyQualified value) then invalidArg name "absolute path required"
    do
        for name,value in
            [ "runnerExecutable",configuration.RunnerExecutable; "repositoryRoot",configuration.RepositoryRoot
              "workspaceRoot",configuration.WorkspaceRoot; "inputRoot",configuration.InputRoot
              "stateRoot",configuration.StateRoot; "artifactRoot",configuration.ArtifactRoot
              "codexExecutable",configuration.CodexExecutable ] do requireAbsolute name value
        if String.IsNullOrWhiteSpace configuration.ExecutorBinding then invalidArg "executorBinding" "nonblank binding required"
        if maximumAggregateBytes<ExecutorWire.maximumControlBytes || maximumAggregateBytes>150*1024*1024 then invalidArg "maximumAggregateBytes" "invalid aggregate bound"

    let terminate (childProcess:Process) =
        try childProcess.StandardInput.Close() with _ -> ()
        try
            if not childProcess.HasExited && not(childProcess.WaitForExit(int shutdownTimeout.TotalMilliseconds)) then
                childProcess.Kill(true)
                childProcess.WaitForExit(int shutdownTimeout.TotalMilliseconds)|>ignore
        with _ ->
            try if not childProcess.HasExited then childProcess.Kill(true) with _ -> ()
        try childProcess.Dispose() with _ -> ()

    let discard () =
        match child with
        | Some childProcess -> child<-None;terminate childProcess
        | None -> ()

    let start () =
        if disposed then raise(ObjectDisposedException(nameof LocalExecutorTransport))
        let info=ProcessStartInfo(configuration.RunnerExecutable,UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true)
        info.WorkingDirectory<-configuration.RepositoryRoot
        for argument in
            [ "executor-stdio"; "--repository-root"; configuration.RepositoryRoot
              "--workspace-root"; configuration.WorkspaceRoot; "--input-root"; configuration.InputRoot
              "--state-root"; configuration.StateRoot; "--artifact-root"; configuration.ArtifactRoot
              "--codex-executable"; configuration.CodexExecutable; "--executor-binding"; configuration.ExecutorBinding ] do
            info.ArgumentList.Add argument
        let childProcess=Process.Start info
        if isNull childProcess then raise(InvalidOperationException "executor-child-start-refused")
        // Drain stderr so bounded protocol progress never depends on diagnostic pipe capacity.
        childProcess.ErrorDataReceived.Add(fun _ -> ())
        childProcess.BeginErrorReadLine()
        child<-Some childProcess
        childProcess

    let running () =
        match child with
        | Some childProcess when not childProcess.HasExited -> childProcess
        | Some childProcess -> terminate childProcess;child<-None;start()
        | None -> start()

    let boundedRequest (frames:byte array list) =
        not(List.isEmpty frames) && frames.Length<=64
        && frames|>List.forall(fun frame->not(isNull frame)&&frame.Length>0&&frame.Length<=2*ExecutorWire.maximumContentBytes)
        && frames|>List.sumBy(fun frame->int64 frame.Length)<=int64 maximumAggregateBytes

    let command frames =
        frames|>List.tryLast|>Option.bind(fun frame->ExecutorWire.parseCommandV2 frame|>Result.toOption)

    let workspaceBaseline frames =
        frames
        |>List.choose(fun frame->ExecutorWire.parseWorkspaceManifest frame|>Result.toOption)
        |>List.tryExactlyOne
        |>Option.map _.BaselineObjectId

    let frameCommandId (bytes:byte array) =
        try
            use document=JsonDocument.Parse(ReadOnlyMemory bytes,JsonDocumentOptions(MaxDepth=8))
            match document.RootElement.TryGetProperty "commandId" with
            | true,value -> match value.TryGetGuid() with true,id->Some id|_->None
            | _ -> None
        with :? JsonException -> None

    let terminal bytes = Result.isOk(ExecutorWire.parseResponse bytes) || Result.isOk(ExecutorWire.parseOperationOutcome bytes)

    let responseFrameBound expected candidate baseline bytes =
        match ExecutorWire.parseArtifactManifest bytes with
        | Ok manifest ->
            manifest.CandidateId=candidate
            && baseline|>Option.exists((=) manifest.BaselineObjectId)
        | Error _ -> frameCommandId bytes=Some expected

    let writeFrame (stream:Stream) (bytes:byte array) token = task {
        let header=Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteInt32BigEndian(header,bytes.Length)
        do! stream.WriteAsync(header,token)
        do! stream.WriteAsync(bytes,token) }

    let readExact (stream:Stream) (buffer:byte array) token = task {
        let mutable offset=0
        while offset<buffer.Length do
            let! count=stream.ReadAsync(buffer.AsMemory(offset),token)
            if count=0 then raise(EndOfStreamException "executor-child-output-ended")
            offset<-offset+count }

    let exchange (frames:byte array list) (token:CancellationToken) = task {
        if disposed then return Error "executor-child-disposed"
        elif not(boundedRequest frames) then return Error "executor-child-request-bounds-refused"
        else
            match command frames with
            | None -> return Error "executor-child-command-refused"
            | Some commandValue ->
                let expected=commandValue.CommandId
                let baseline=workspaceBaseline frames
                try
                    do! gate.WaitAsync token
                    let! result = task {
                        try
                            let childProcess=running()
                            for frame in frames do do! writeFrame childProcess.StandardInput.BaseStream frame token
                            do! childProcess.StandardInput.BaseStream.FlushAsync token
                            let output=ResizeArray<byte array>()
                            let mutable total=0
                            let mutable complete=false
                            while not complete do
                                let header=Array.zeroCreate<byte> 4
                                do! readExact childProcess.StandardOutput.BaseStream header token
                                let size=BinaryPrimitives.ReadInt32BigEndian header
                                if size<1 || size>2*ExecutorWire.maximumContentBytes || output.Count>=64 || total>maximumAggregateBytes-size then
                                    raise(InvalidDataException "executor-child-response-bounds-refused")
                                let bytes=Array.zeroCreate<byte> size
                                do! readExact childProcess.StandardOutput.BaseStream bytes token
                                if not(responseFrameBound expected commandValue.CandidateId baseline bytes) then
                                    raise(InvalidDataException "executor-child-stale-or-malformed-frame-refused")
                                output.Add bytes;total<-total+size
                                complete<-terminal bytes
                            return Ok{Frames=List.ofSeq output}
                        with
                        | :? OperationCanceledException -> discard();return Error "executor-child-exchange-cancelled"
                        | :? EndOfStreamException -> discard();return Error "executor-child-exited"
                        | :? InvalidDataException as error -> discard();return Error error.Message
                        | _ -> discard();return Error "executor-child-exchange-refused" }
                    gate.Release()|>ignore
                    return result
                with :? OperationCanceledException -> return Error "executor-child-exchange-cancelled" }

    member _.IsRunning = child|>Option.exists(fun value->not value.HasExited)
    interface IAuthenticatedExecutorTransport with member _.Exchange(frames,token)=exchange frames token
    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed<-true
                gate.Wait()
                try discard() finally gate.Release()|>ignore
                gate.Dispose()
