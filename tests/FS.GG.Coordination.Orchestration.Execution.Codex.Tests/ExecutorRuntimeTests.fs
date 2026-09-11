module FS.GG.Coordination.OrchestrationExecutorRuntimeTests

open System
open System.Buffers.Binary
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Runner.Client
open FS.GG.Coordination.Orchestration.Runner.Protocol
open Xunit

module private RuntimeFixture =
    let repositoryRoot =
        let rec find (directory:DirectoryInfo)=
            if File.Exists(Path.Combine(directory.FullName,"FS.GG.Coordination.sln")) then directory.FullName
            elif isNull directory.Parent then failwith "repository root not found"
            else find directory.Parent
        find(DirectoryInfo(AppContext.BaseDirectory))
    let run cwd (args:string list) =
        let start=ProcessStartInfo(args.Head,WorkingDirectory=cwd,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true)
        args.Tail|>List.iter start.ArgumentList.Add
        use child=Process.Start start
        let output=child.StandardOutput.ReadToEnd()
        let error=child.StandardError.ReadToEnd()
        child.WaitForExit()
        Assert.True(child.ExitCode=0,error)
        output.Trim()
    let git cwd args=run cwd ("git"::args)
    let sha (bytes:byte array)=SHA256.HashData bytes|>Convert.ToHexString|>_.ToLowerInvariant()
    let repo () =
        let root=Directory.CreateTempSubdirectory("executor-repo-").FullName
        git root ["init";"--initial-branch=main"]|>ignore
        git root ["config";"user.name";"Fixture"]|>ignore
        git root ["config";"user.email";"fixture@example.invalid"]|>ignore
        Directory.CreateDirectory(Path.Combine(root,"docs"))|>ignore
        File.WriteAllText(Path.Combine(root,"docs/item.md"),"base\n")
        git root ["add";"."]|>ignore;git root ["commit";"-m";"base"]|>ignore
        root,git root ["rev-parse";"HEAD"]
    let manifest baseline digest =
        {Schema=ExecutorWire.workspaceManifestSchema;Workspace="pilot";RepositoryBinding="selected-repository";BaselineObjectId=baseline;AllowedPaths=[|"docs/**"|];Validations=[|"git-diff-check"|];InputDigest=digest}
    let command manifestDigest digest baseline kind session =
        let now=DateTimeOffset.UtcNow
        let unsigned={Schema=ExecutorWire.commandSchemaV2;CommandId=Guid.NewGuid();BodySha256="";Kind=kind;WorkItemPersistenceId="routine-documentation-delivery";RouteOperationId=Guid.Parse "10000000-0000-0000-0000-000000000001";AssignmentId=Guid.Parse "10000000-0000-0000-0000-000000000001";AttemptId=Guid.Parse "20000000-0000-0000-0000-000000000002";CandidateId=Guid.Parse "30000000-0000-0000-0000-000000000003";Generation=7L;ExpectedRevision=1L;RecordedAt=now;Deadline=now.AddMinutes 5.;MaximumRuntimeSeconds=300L;MaximumAttempts=1;Workspace="pilot";WorkspaceManifestSha256=manifestDigest;RequestedModel=null;RequestedEffort=null;InputDigest=digest;ExecutorBinding="fixture-executor";ProviderSessionReference=session;ArtifactDigest=null;ContentOffset=0L;ContentLength=0}
        {unsigned with BodySha256=ExecutorWire.commandV2Digest unsigned}
    let frame (bytes:byte array) =
        let result=Array.zeroCreate<byte> (4+bytes.Length)
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0,4),bytes.Length)
        Array.Copy(bytes,0,result,4,bytes.Length)
        result
    let frames values=values|>Array.collect frame
    let readFrames (bytes:byte array)=
        let values=ResizeArray<byte array>()
        let mutable offset=0
        while offset<bytes.Length do
            let size=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset,4))
            offset<-offset+4
            values.Add bytes[offset..offset+size-1]
            offset<-offset+size
        values|>Seq.toList
    let readFrame (stream:Stream) = task {
        let header=Array.zeroCreate<byte> 4
        do! stream.ReadExactlyAsync header
        let size=BinaryPrimitives.ReadInt32BigEndian header
        let bytes=Array.zeroCreate<byte> size
        do! stream.ReadExactlyAsync bytes
        return bytes }
    let fakeCodex root =
        let path=Path.Combine(root,"codex-fixture")
        File.WriteAllText(path,"#!/bin/sh\nif [ \"$1\" = --version ]; then echo 'codex-cli 0.154.0'; exit 0; fi\nif [ \"$1\" = login ]; then echo 'Logged in using ChatGPT'; exit 0; fi\nexit 9\n")
        File.SetUnixFileMode(path,UnixFileMode.UserRead|||UnixFileMode.UserWrite|||UnixFileMode.UserExecute)
        path
    let successfulCodex root digest candidateId =
        let path=Path.Combine(root,"codex-success")
        let script=$"""#!/bin/sh
if [ "$1" = --version ]; then echo 'codex-cli 0.154.0'; exit 0; fi
if [ "$1" = login ]; then echo 'Logged in using ChatGPT'; exit 0; fi
workspace=''; final=''
while [ $# -gt 0 ]; do
  if [ "$1" = -C ]; then workspace="$2"; shift 2
  elif [ "$1" = --output-last-message ]; then final="$2"; shift 2
  else shift
  fi
done
printf 'spawn\n' >> '{root}/spawns'
printf '%%s\n' '{{"type":"thread.started","thread_id":"thread-runtime-1"}}'
cat >/dev/null
cd "$workspace"
printf 'candidate\n' > docs/item.md
git add docs/item.md
git commit -m candidate >/dev/null
head=$(git rev-parse HEAD)
tree=$(git rev-parse 'HEAD^{{tree}}')
printf '{{"inputDigest":"{digest}","candidateId":"{candidateId}","headSha":"%%s","treeSha":"%%s"}}\n' "$head" "$tree" > "$final"
printf '%%s\n' '{{"type":"turn.completed","usage":{{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1,"reasoning_output_tokens":0}}}}'
"""
        File.WriteAllText(path,script)
        File.SetUnixFileMode(path,UnixFileMode.UserRead|||UnixFileMode.UserWrite|||UnixFileMode.UserExecute)
        path
    let hangingCodex root =
        let path=Path.Combine(root,"codex-hanging")
        let script=$"""#!/bin/sh
if [ "$1" = --version ]; then echo 'codex-cli 0.154.0'; exit 0; fi
if [ "$1" = login ]; then echo 'Logged in using ChatGPT'; exit 0; fi
printf 'spawn\n' >> '{root}/spawns'
printf '%%s\n' '{{"type":"thread.started","thread_id":"thread-hanging-1"}}'
sleep 30 &
printf '%%s' $! > '{root}/child-pid'
wait
"""
        File.WriteAllText(path,script)
        File.SetUnixFileMode(path,UnixFileMode.UserRead|||UnixFileMode.UserWrite|||UnixFileMode.UserExecute)
        path
    let vanishingCodex root =
        let path=Path.Combine(root,"codex-vanishing")
        let script=$"""#!/bin/sh
if [ "$1" = --version ]; then echo 'codex-cli 0.154.0'; exit 0; fi
if [ "$1" = login ]; then
  count=0; [ -f '{root}/login-count' ] && count=$(cat '{root}/login-count')
  count=$((count + 1)); printf '%%s' "$count" > '{root}/login-count'
  echo 'Logged in using ChatGPT'; [ "$count" -ge 2 ] && rm -f '{path}'; exit 0
fi
exit 9
"""
        File.WriteAllText(path,script)
        File.SetUnixFileMode(path,UnixFileMode.UserRead|||UnixFileMode.UserWrite|||UnixFileMode.UserExecute)
        path
    let runnerExecutable () =
        match Environment.GetEnvironmentVariable "FSGG_RUNNER_EXECUTABLE" with
        | value when not(String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
        | _ ->
            let configuration = if AppContext.BaseDirectory.Contains("/Release/") then "Release" else "Debug"
            Path.Combine(repositoryRoot,"src/FS.GG.Coordination.Orchestration.Runner.Client/bin",configuration,"net10.0/linux-x64/fsgg-coord-orchestration-runner")
    let startRunner repository workspaceRoot inputRoot stateRoot artifactRoot codex =
        let start=ProcessStartInfo(runnerExecutable(),UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true)
        for argument in ["executor-stdio";"--repository-root";repository;"--workspace-root";workspaceRoot;"--input-root";inputRoot;"--state-root";stateRoot;"--artifact-root";artifactRoot;"--codex-executable";codex;"--executor-binding";"fixture-executor"] do start.ArgumentList.Add argument
        Process.Start start

type ExecutorRuntimeTests() =
    [<Fact>]
    member _.``version two command binds closed workspace manifest without changing version one``() =
        let _,baseline=RuntimeFixture.repo()
        let digest=String.replicate 64 "a"
        let manifest=RuntimeFixture.manifest baseline digest
        let encoded=ExecutorWire.encodeWorkspaceManifest manifest
        let command=RuntimeFixture.command (RuntimeFixture.sha encoded) digest baseline "launch" null
        Assert.Equal(Ok command,ExecutorWire.parseCommandV2(ExecutorWire.encodeCommandV2 command))
        Assert.Equal(ExecutorWire.commandSchema,"fsgg.orchestration.executor-command/1")
        let wrong={command with WorkspaceManifestSha256=String.replicate 64 "b"}
        Assert.Equal(Error "executor-command-digest-refused",ExecutorWire.parseCommandV2(ExecutorWire.encodeCommandV2 wrong))

    [<Fact>]
    member _.``executor extension codecs reject unknown duplicate null and invalid outcome shapes``() =
        let _,baseline=RuntimeFixture.repo()
        let digest=String.replicate 64 "a"
        let manifest=RuntimeFixture.manifest baseline digest
        let encoded=ExecutorWire.encodeWorkspaceManifest manifest|>Encoding.UTF8.GetString
        let unknown=encoded.Replace("{","{\"unknown\":true,",StringComparison.Ordinal)|>Encoding.UTF8.GetBytes
        let duplicate=encoded.Replace("{","{\"workspace\":\"other\",",StringComparison.Ordinal)|>Encoding.UTF8.GetBytes
        Assert.Equal(Error "runner-message-shape-refused",ExecutorWire.parseWorkspaceManifest unknown)
        Assert.Equal(Error "runner-message-shape-refused",ExecutorWire.parseWorkspaceManifest duplicate)
        let content={Schema=ExecutorWire.artifactContentSchema;CommandId=Guid.NewGuid();CandidateId=Guid.NewGuid();BundleSha256=digest;Offset=0L;Final=true;ContentBase64="YQ=="}
        let nullContent=ExecutorWire.encodeArtifactContent content|>Encoding.UTF8.GetString|>fun value->value.Replace("\"YQ==\"","null",StringComparison.Ordinal)|>Encoding.UTF8.GetBytes
        Assert.Equal(Error "executor-artifact-content-refused",ExecutorWire.parseArtifactContent nullContent)
        let outcome={Schema=ExecutorWire.operationOutcomeSchema;CommandId=Guid.NewGuid();BodySha256=digest;Operation="launch";Disposition="success";ProviderSessionReference=null;ObservedAt=DateTimeOffset.UtcNow;Reason=""}
        Assert.Equal(Error "executor-operation-outcome-refused",ExecutorWire.parseOperationOutcome(ExecutorWire.encodeOperationOutcome outcome))

    [<Fact>]
    member _.``real Git candidate produces reconstructable digest-bound bundle``() =
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-roots-").FullName
        let workspaceRoot=Directory.CreateDirectory(Path.Combine(roots,"workspaces")).FullName
        let artifactRoot=Directory.CreateDirectory(Path.Combine(roots,"artifacts")).FullName
        let digest=String.replicate 64 "a"
        let manifest=RuntimeFixture.manifest baseline digest
        let assignment=Guid.NewGuid()
        let attempt=Guid.NewGuid()
        let workspace=ExecutorWorkspace.materialize repository workspaceRoot assignment attempt 1L manifest|>Result.defaultWith failwith
        File.WriteAllText(Path.Combine(workspace,"docs/item.md"),"accepted\n")
        RuntimeFixture.git workspace ["add";"docs/item.md"]|>ignore
        RuntimeFixture.git workspace ["commit";"-m";"candidate"]|>ignore
        let head=RuntimeFixture.git workspace ["rev-parse";"HEAD"]
        let tree=RuntimeFixture.git workspace ["rev-parse";"HEAD^{tree}"]
        let candidate={CandidateId=Guid.NewGuid();HeadSha=head;TreeSha=tree}
        let inspector=GitCandidateInspector(workspace,manifest,artifactRoot,Guid.NewGuid(),candidate.CandidateId)
        let artifact=inspector.CreateArtifact(candidate,4096)|>Result.defaultWith failwith
        Assert.Equal(Ok artifact.Manifest,ExecutorWire.parseArtifactManifest(ExecutorWire.encodeArtifactManifest artifact.Manifest))
        use stream=File.OpenRead artifact.BundlePath
        Assert.Equal(artifact.Manifest.BundleSha256,SHA256.HashData stream|>Convert.ToHexString|>_.ToLowerInvariant())
        RuntimeFixture.git workspace ["bundle";"verify";artifact.BundlePath]|>ignore
        let secondRoot=Directory.CreateDirectory(Path.Combine(roots,"artifacts-2")).FullName
        let repeated=GitCandidateInspector(workspace,manifest,secondRoot,artifact.Manifest.CommandId,candidate.CandidateId).CreateArtifact(candidate,4096)|>Result.defaultWith failwith
        Assert.Equal(artifact.Manifest.BundleSha256,repeated.Manifest.BundleSha256)
        Assert.Equal(artifact.Manifest.ManifestSha256,repeated.Manifest.ManifestSha256)

    [<Fact>]
    member _.``committed whitespace damage and dirty or disallowed candidate fail closed``() =
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-reject-").FullName
        let workspaceRoot=Directory.CreateDirectory(Path.Combine(roots,"workspaces")).FullName
        let artifactRoot=Directory.CreateDirectory(Path.Combine(roots,"artifacts")).FullName
        let digest=String.replicate 64 "a"
        let manifest=RuntimeFixture.manifest baseline digest
        let workspace=ExecutorWorkspace.materialize repository workspaceRoot (Guid.NewGuid()) (Guid.NewGuid()) 1L manifest|>Result.defaultWith failwith
        File.WriteAllText(Path.Combine(workspace,"docs/item.md"),"bad trailing space  \n")
        RuntimeFixture.git workspace ["add";"."]|>ignore
        RuntimeFixture.git workspace ["commit";"-m";"bad"]|>ignore
        let candidate={CandidateId=Guid.NewGuid();HeadSha=RuntimeFixture.git workspace ["rev-parse";"HEAD"];TreeSha=RuntimeFixture.git workspace ["rev-parse";"HEAD^{tree}"]}
        let inspector=GitCandidateInspector(workspace,manifest,artifactRoot,Guid.NewGuid(),candidate.CandidateId):>FS.GG.Coordination.Orchestration.Execution.Codex.ICodexCandidateInspector
        let result=inspector.Verify(workspace,candidate,CancellationToken.None).Result
        Assert.True(Result.isError result)
        RuntimeFixture.git workspace ["reset";"--hard";baseline]|>ignore
        File.WriteAllText(Path.Combine(workspace,"docs/item.md"),"valid\n")
        RuntimeFixture.git workspace ["add";"docs/item.md"]|>ignore
        RuntimeFixture.git workspace ["commit";"-m";"valid"]|>ignore
        File.WriteAllText(Path.Combine(workspace,"untracked.txt"),"tamper")
        let validHead=RuntimeFixture.git workspace ["rev-parse";"HEAD"]
        let dirtyCandidate={candidate with HeadSha=validHead;TreeSha=RuntimeFixture.git workspace ["rev-parse";validHead+"^{tree}"]}
        Assert.Equal(Error "candidate-worktree-dirty",inspector.Verify(workspace,dirtyCandidate,CancellationToken.None).Result)
        File.Delete(Path.Combine(workspace,"untracked.txt"))
        RuntimeFixture.git workspace ["reset";"--hard";baseline]|>ignore
        Directory.CreateDirectory(Path.Combine(workspace,"docs2"))|>ignore
        File.WriteAllText(Path.Combine(workspace,"docs2/item.md"),"outside directory boundary\n")
        RuntimeFixture.git workspace ["add";"docs2/item.md"]|>ignore
        RuntimeFixture.git workspace ["commit";"-m";"outside touch set"]|>ignore
        let outsideHead=RuntimeFixture.git workspace ["rev-parse";"HEAD"]
        let outside={candidate with HeadSha=outsideHead;TreeSha=RuntimeFixture.git workspace ["rev-parse";outsideHead+"^{tree}"]}
        Assert.Equal(Error "candidate-touch-set-refused",inspector.Verify(workspace,outside,CancellationToken.None).Result)

    [<Fact>]
    member _.``framed executable readiness uses selected ChatGPT subscription adapter``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-runtime-").FullName
        let paths names=names|>List.map(fun name->Directory.CreateDirectory(Path.Combine(roots,name)).FullName)
        let workspaceRoot,inputRoot,stateRoot,artifactRoot=match paths ["workspaces";"inputs";"state";"artifacts"] with [a;b;c;d]->a,b,c,d|_->failwith "paths"
        let prompt=Encoding.UTF8.GetBytes "bounded input"
        let digest=RuntimeFixture.sha prompt
        let manifest=RuntimeFixture.manifest baseline digest
        let manifestBytes=ExecutorWire.encodeWorkspaceManifest manifest
        let inputManifest={Schema=ExecutorWire.inputManifestSchema;InputDigest=digest;MediaType="text/plain; charset=utf-8";SizeBytes=int64 prompt.Length;ChunkBytes=4096}
        let content={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String prompt}
        let command=RuntimeFixture.command (RuntimeFixture.sha manifestBytes) digest baseline "readiness" null
        let inputBytes=RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeInputManifest inputManifest;ExecutorWire.encodeContent content;ExecutorWire.encodeCommandV2 command|]
        use input=new MemoryStream(inputBytes)
        use output=new MemoryStream()
        let options={RepositoryRoot=repository;WorkspaceRoot=workspaceRoot;InputRoot=inputRoot;StateRoot=stateRoot;ArtifactRoot=artifactRoot;CodexExecutable=RuntimeFixture.fakeCodex roots;ExecutorBinding="fixture-executor";MaximumFrameBytes=2*ExecutorWire.maximumContentBytes}
        do! ExecutorRuntime(options,TimeProvider.System).Run(input,output,CancellationToken.None)
        let frames=RuntimeFixture.readFrames(output.ToArray())
        Assert.Single frames|>ignore
        let response=ExecutorWire.parseResponse frames.Head|>Result.defaultWith failwith
        Assert.Equal("authenticated",response.AuthenticationState)
        Assert.Equal("codex-login-status:chatgpt-subscription",response.AuthenticationProvenance) }

    [<Fact>]
    member _.``actual runner apphost keeps protocol on stdout and diagnostics separate``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-apphost-").FullName
        let mk name=Directory.CreateDirectory(Path.Combine(roots,name)).FullName
        let workspaceRoot,inputRoot,stateRoot,artifactRoot=mk "workspaces",mk "inputs",mk "state",mk "artifacts"
        let prompt=Encoding.UTF8.GetBytes "bounded input"
        let digest=RuntimeFixture.sha prompt
        let manifest=RuntimeFixture.manifest baseline digest
        let manifestBytes=ExecutorWire.encodeWorkspaceManifest manifest
        let inputManifest={Schema=ExecutorWire.inputManifestSchema;InputDigest=digest;MediaType="text/plain; charset=utf-8";SizeBytes=int64 prompt.Length;ChunkBytes=4096}
        let content={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String prompt}
        let command=RuntimeFixture.command (RuntimeFixture.sha manifestBytes) digest baseline "readiness" null
        let stdin=RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeInputManifest inputManifest;ExecutorWire.encodeContent content;ExecutorWire.encodeCommandV2 command|]
        use child=RuntimeFixture.startRunner repository workspaceRoot inputRoot stateRoot artifactRoot (RuntimeFixture.fakeCodex roots)
        let stderr=child.StandardError.ReadToEndAsync()
        do! child.StandardInput.BaseStream.WriteAsync stdin
        do! child.StandardInput.BaseStream.FlushAsync()
        let! first=RuntimeFixture.readFrame child.StandardOutput.BaseStream
        let firstResponse=ExecutorWire.parseResponse first|>Result.defaultWith failwith
        Assert.Equal("authenticated",firstResponse.AuthenticationState)
        for _ in 1..69 do
            let unsigned={command with CommandId=Guid.NewGuid();BodySha256=""}
            let next={unsigned with BodySha256=ExecutorWire.commandV2Digest unsigned}
            let framed=RuntimeFixture.frame(ExecutorWire.encodeCommandV2 next)
            do! child.StandardInput.BaseStream.WriteAsync framed
            do! child.StandardInput.BaseStream.FlushAsync()
            let! nextFrame=RuntimeFixture.readFrame child.StandardOutput.BaseStream
            let nextResponse=ExecutorWire.parseResponse nextFrame|>Result.defaultWith failwith
            Assert.Equal("authenticated",nextResponse.AuthenticationState)
        child.StandardInput.Close()
        do! child.WaitForExitAsync()
        Assert.True(child.ExitCode=0,stderr.Result)
        Assert.Equal("",stderr.Result) }

    [<Fact>]
    member _.``actual runner restart after process creation preserves ambiguity and never respawns``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-apphost-crash-").FullName
        let mk name=Directory.CreateDirectory(Path.Combine(roots,name)).FullName
        let workspaceRoot,inputRoot,stateRoot,artifactRoot=mk "workspaces",mk "inputs",mk "state",mk "artifacts"
        let prompt=Encoding.UTF8.GetBytes "bounded input"
        let digest=RuntimeFixture.sha prompt
        let manifest=RuntimeFixture.manifest baseline digest
        let manifestBytes=ExecutorWire.encodeWorkspaceManifest manifest
        let inputManifest={Schema=ExecutorWire.inputManifestSchema;InputDigest=digest;MediaType="text/plain";SizeBytes=int64 prompt.Length;ChunkBytes=4096}
        let content={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String prompt}
        let launch=RuntimeFixture.command (RuntimeFixture.sha manifestBytes) digest baseline "launch" null
        let codex=RuntimeFixture.hangingCodex roots
        use first=RuntimeFixture.startRunner repository workspaceRoot inputRoot stateRoot artifactRoot codex
        let initial=RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeInputManifest inputManifest;ExecutorWire.encodeContent content;ExecutorWire.encodeCommandV2 launch|]
        do! first.StandardInput.BaseStream.WriteAsync initial
        do! first.StandardInput.BaseStream.FlushAsync()
        let! persistedFrame=RuntimeFixture.readFrame first.StandardOutput.BaseStream
        let persisted=ExecutorWire.parseReceipt persistedFrame|>Result.defaultWith failwith
        Assert.Equal("persisted",persisted.Disposition)
        let! observedFrame=RuntimeFixture.readFrame first.StandardOutput.BaseStream
        let observed=ExecutorWire.parseReceipt observedFrame|>Result.defaultWith failwith
        Assert.True(observed.ProcessCreationObserved)
        let! runningFrame=RuntimeFixture.readFrame first.StandardOutput.BaseStream
        let running=ExecutorWire.parseResponse runningFrame|>Result.defaultWith failwith
        Assert.Equal("running",running.Lifecycle)
        first.Kill(true)
        do! first.WaitForExitAsync()
        use replacement=RuntimeFixture.startRunner repository workspaceRoot inputRoot stateRoot artifactRoot codex
        let retry=RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeCommandV2 launch|]
        do! replacement.StandardInput.BaseStream.WriteAsync retry
        replacement.StandardInput.Close()
        use replay=new MemoryStream()
        do! replacement.StandardOutput.BaseStream.CopyToAsync replay
        do! replacement.WaitForExitAsync()
        let frames=RuntimeFixture.readFrames(replay.ToArray())
        Assert.Contains(frames,fun frame->ExecutorWire.parseReceipt frame|>Result.toOption|>Option.exists(fun value->value.Disposition="duplicate"))
        Assert.Contains(frames,fun frame->ExecutorWire.parseOperationOutcome frame|>Result.toOption|>Option.exists(fun value->value.Disposition="ambiguous"))
        Assert.Equal(1,File.ReadAllLines(Path.Combine(roots,"spawns")).Length) }

    [<Fact>]
    member _.``actual runner accepts cancel while provider process is blocked``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-apphost-cancel-").FullName
        let mk name=Directory.CreateDirectory(Path.Combine(roots,name)).FullName
        let workspaceRoot,inputRoot,stateRoot,artifactRoot=mk "workspaces",mk "inputs",mk "state",mk "artifacts"
        let prompt=Encoding.UTF8.GetBytes "bounded input"
        let digest=RuntimeFixture.sha prompt
        let manifest=RuntimeFixture.manifest baseline digest
        let manifestBytes=ExecutorWire.encodeWorkspaceManifest manifest
        let inputManifest={Schema=ExecutorWire.inputManifestSchema;InputDigest=digest;MediaType="text/plain";SizeBytes=int64 prompt.Length;ChunkBytes=4096}
        let content={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String prompt}
        let launch=RuntimeFixture.command (RuntimeFixture.sha manifestBytes) digest baseline "launch" null
        use child=RuntimeFixture.startRunner repository workspaceRoot inputRoot stateRoot artifactRoot (RuntimeFixture.hangingCodex roots)
        do! child.StandardInput.BaseStream.WriteAsync(RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeInputManifest inputManifest;ExecutorWire.encodeContent content;ExecutorWire.encodeCommandV2 launch|])
        do! child.StandardInput.BaseStream.FlushAsync()
        let! persistedFrame=RuntimeFixture.readFrame child.StandardOutput.BaseStream
        Assert.Equal("persisted",(ExecutorWire.parseReceipt persistedFrame|>Result.defaultWith failwith).Disposition)
        let! observedFrame=RuntimeFixture.readFrame child.StandardOutput.BaseStream
        Assert.True((ExecutorWire.parseReceipt observedFrame|>Result.defaultWith failwith).ProcessCreationObserved)
        let! runningFrame=RuntimeFixture.readFrame child.StandardOutput.BaseStream
        let running=ExecutorWire.parseResponse runningFrame|>Result.defaultWith failwith
        let unsigned={launch with CommandId=Guid.NewGuid();BodySha256="";Kind="cancel";ProviderSessionReference=running.ProviderSessionReference}
        let cancel={unsigned with BodySha256=ExecutorWire.commandV2Digest unsigned}
        let timer=Stopwatch.StartNew()
        do! child.StandardInput.BaseStream.WriteAsync(RuntimeFixture.frame(ExecutorWire.encodeCommandV2 cancel))
        do! child.StandardInput.BaseStream.FlushAsync()
        let! cancelledFrame=RuntimeFixture.readFrame child.StandardOutput.BaseStream
        let cancelled=ExecutorWire.parseOperationOutcome cancelledFrame|>Result.defaultWith failwith
        Assert.Equal("accepted",cancelled.Disposition)
        Assert.True(timer.Elapsed<TimeSpan.FromSeconds 2.)
        child.StandardInput.Close()
        do! child.WaitForExitAsync()
        let childPid=int(File.ReadAllText(Path.Combine(roots,"child-pid")))
        do! Threading.Tasks.Task.Delay 50
        Assert.ThrowsAny<ArgumentException>(fun ()->Process.GetProcessById childPid|>ignore)|>ignore }

    [<Fact>]
    member _.``actual runner crash before process creation retains persisted launch ambiguity``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-apphost-precreate-").FullName
        let mk name=Directory.CreateDirectory(Path.Combine(roots,name)).FullName
        let workspaceRoot,inputRoot,stateRoot,artifactRoot=mk "workspaces",mk "inputs",mk "state",mk "artifacts"
        let prompt=Encoding.UTF8.GetBytes "bounded input"
        let digest=RuntimeFixture.sha prompt
        let manifest=RuntimeFixture.manifest baseline digest
        let manifestBytes=ExecutorWire.encodeWorkspaceManifest manifest
        let inputManifest={Schema=ExecutorWire.inputManifestSchema;InputDigest=digest;MediaType="text/plain";SizeBytes=int64 prompt.Length;ChunkBytes=4096}
        let content={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String prompt}
        let launch=RuntimeFixture.command (RuntimeFixture.sha manifestBytes) digest baseline "launch" null
        let codex=RuntimeFixture.vanishingCodex roots
        use first=RuntimeFixture.startRunner repository workspaceRoot inputRoot stateRoot artifactRoot codex
        do! first.StandardInput.BaseStream.WriteAsync(RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeInputManifest inputManifest;ExecutorWire.encodeContent content;ExecutorWire.encodeCommandV2 launch|])
        first.StandardInput.Close()
        use initialOutput=new MemoryStream()
        do! first.StandardOutput.BaseStream.CopyToAsync initialOutput
        do! first.WaitForExitAsync()
        let initialFrames=RuntimeFixture.readFrames(initialOutput.ToArray())
        Assert.Contains(initialFrames,fun frame->ExecutorWire.parseReceipt frame|>Result.toOption|>Option.exists(fun value->value.Disposition="persisted" && not value.ProcessCreationObserved))
        Assert.Contains(initialFrames,fun frame->ExecutorWire.parseOperationOutcome frame|>Result.toOption|>Option.exists(fun value->value.Disposition="ambiguous"))
        use replacement=RuntimeFixture.startRunner repository workspaceRoot inputRoot stateRoot artifactRoot codex
        do! replacement.StandardInput.BaseStream.WriteAsync(RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeCommandV2 launch|])
        replacement.StandardInput.Close()
        use replay=new MemoryStream()
        do! replacement.StandardOutput.BaseStream.CopyToAsync replay
        do! replacement.WaitForExitAsync()
        let frames=RuntimeFixture.readFrames(replay.ToArray())
        Assert.Contains(frames,fun frame->ExecutorWire.parseReceipt frame|>Result.toOption|>Option.exists(fun value->value.Disposition="duplicate"))
        Assert.Contains(frames,fun frame->ExecutorWire.parseOperationOutcome frame|>Result.toOption|>Option.exists(fun value->value.Disposition="ambiguous")) }

    [<Fact>]
    member _.``framed launch observes verified Git candidate and streams reconstructable bundle``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-e2e-").FullName
        let mk name=Directory.CreateDirectory(Path.Combine(roots,name)).FullName
        let workspaceRoot,inputRoot,stateRoot,artifactRoot=mk "workspaces",mk "inputs",mk "state",mk "artifacts"
        let prompt=Encoding.UTF8.GetBytes "bounded input"
        let digest=RuntimeFixture.sha prompt
        let workspaceManifest=RuntimeFixture.manifest baseline digest
        let manifestBytes=ExecutorWire.encodeWorkspaceManifest workspaceManifest
        let launch=RuntimeFixture.command (RuntimeFixture.sha manifestBytes) digest baseline "launch" null
        let executable=RuntimeFixture.successfulCodex roots digest launch.CandidateId
        let options={RepositoryRoot=repository;WorkspaceRoot=workspaceRoot;InputRoot=inputRoot;StateRoot=stateRoot;ArtifactRoot=artifactRoot;CodexExecutable=executable;ExecutorBinding="fixture-executor";MaximumFrameBytes=2*ExecutorWire.maximumContentBytes}
        let runtime=ExecutorRuntime(options,TimeProvider.System)
        let inputManifest={Schema=ExecutorWire.inputManifestSchema;InputDigest=digest;MediaType="text/plain; charset=utf-8";SizeBytes=int64 prompt.Length;ChunkBytes=4096}
        let content={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String prompt}
        use launchInput=new MemoryStream(RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeInputManifest inputManifest;ExecutorWire.encodeContent content;ExecutorWire.encodeCommandV2 launch|])
        use launchOutput=new MemoryStream()
        do! runtime.Run(launchInput,launchOutput,CancellationToken.None)
        let started=RuntimeFixture.readFrames(launchOutput.ToArray())|>List.pick(fun frame->ExecutorWire.parseResponse frame|>Result.toOption)
        Assert.Equal("running",started.Lifecycle)
        let mutable artifact:ExecutorArtifactManifest option=None
        let mutable attempts=0
        while artifact.IsNone && attempts<100 do
            attempts<-attempts+1
            let unsigned={launch with CommandId=Guid.NewGuid();BodySha256="";Kind="observe"}
            let observe={unsigned with BodySha256=ExecutorWire.commandV2Digest unsigned}
            use observeInput=new MemoryStream(RuntimeFixture.frames [|ExecutorWire.encodeCommandV2 observe|])
            use observeOutput=new MemoryStream()
            do! runtime.Run(observeInput,observeOutput,CancellationToken.None)
            artifact<-RuntimeFixture.readFrames(observeOutput.ToArray())|>List.tryPick(fun frame->ExecutorWire.parseArtifactManifest frame|>Result.toOption)
            if artifact.IsNone then do! Threading.Tasks.Task.Delay 10
        Assert.True(artifact.IsSome,"candidate artifact was not observed")
        let candidateArtifact=artifact.Value
        let unsigned={launch with CommandId=Guid.NewGuid();BodySha256="";Kind="content-read";ArtifactDigest=candidateArtifact.BundleSha256;ContentOffset=0L;ContentLength=int candidateArtifact.BundleSizeBytes}
        let read={unsigned with BodySha256=ExecutorWire.commandV2Digest unsigned}
        use readInput=new MemoryStream(RuntimeFixture.frames [|ExecutorWire.encodeCommandV2 read|])
        use readOutput=new MemoryStream()
        do! ExecutorRuntime(options,TimeProvider.System).Run(readInput,readOutput,CancellationToken.None)
        let _,bundle=RuntimeFixture.readFrames(readOutput.ToArray()).Head|>ExecutorWire.parseArtifactContent|>Result.defaultWith failwith
        Assert.Equal(candidateArtifact.BundleSha256,RuntimeFixture.sha bundle)
        let received=Path.Combine(roots,"received.bundle")
        File.WriteAllBytes(received,bundle)
        RuntimeFixture.git repository ["bundle";"verify";received]|>ignore
        use replayInput=new MemoryStream(RuntimeFixture.frames [|ExecutorWire.encodeCommandV2 read|])
        use replayOutput=new MemoryStream()
        do! ExecutorRuntime(options,TimeProvider.System).Run(replayInput,replayOutput,CancellationToken.None)
        let _,replayed=RuntimeFixture.readFrames(replayOutput.ToArray()).Head|>ExecutorWire.parseArtifactContent|>Result.defaultWith failwith
        Assert.Equal<byte>(bundle,replayed)
        File.AppendAllText(Path.Combine(artifactRoot,candidateArtifact.CandidateId.ToString("N")+".bundle"),"tamper")
        use tamperInput=new MemoryStream(RuntimeFixture.frames [|ExecutorWire.encodeCommandV2 read|])
        use tamperOutput=new MemoryStream()
        do! ExecutorRuntime(options,TimeProvider.System).Run(tamperInput,tamperOutput,CancellationToken.None)
        let refused=RuntimeFixture.readFrames(tamperOutput.ToArray()).Head|>ExecutorWire.parseOperationOutcome|>Result.defaultWith failwith
        Assert.Equal("candidate-artifact-tampered",refused.Reason) }

    [<Fact>]
    member _.``duplicate launch and executor restart reconcile without a second process``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-duplicate-").FullName
        let mk name=Directory.CreateDirectory(Path.Combine(roots,name)).FullName
        let workspaceRoot,inputRoot,stateRoot,artifactRoot=mk "workspaces",mk "inputs",mk "state",mk "artifacts"
        let prompt=Encoding.UTF8.GetBytes "bounded input"
        let digest=RuntimeFixture.sha prompt
        let manifest=RuntimeFixture.manifest baseline digest
        let manifestBytes=ExecutorWire.encodeWorkspaceManifest manifest
        let launch=RuntimeFixture.command (RuntimeFixture.sha manifestBytes) digest baseline "launch" null
        let executable=RuntimeFixture.successfulCodex roots digest launch.CandidateId
        let options={RepositoryRoot=repository;WorkspaceRoot=workspaceRoot;InputRoot=inputRoot;StateRoot=stateRoot;ArtifactRoot=artifactRoot;CodexExecutable=executable;ExecutorBinding="fixture-executor";MaximumFrameBytes=2*ExecutorWire.maximumContentBytes}
        let inputManifest={Schema=ExecutorWire.inputManifestSchema;InputDigest=digest;MediaType="text/plain; charset=utf-8";SizeBytes=int64 prompt.Length;ChunkBytes=4096}
        let content={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String prompt}
        use firstInput=new MemoryStream(RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeInputManifest inputManifest;ExecutorWire.encodeContent content;ExecutorWire.encodeCommandV2 launch;ExecutorWire.encodeCommandV2 launch|])
        use firstOutput=new MemoryStream()
        do! ExecutorRuntime(options,TimeProvider.System).Run(firstInput,firstOutput,CancellationToken.None)
        Assert.Equal(1,File.ReadAllLines(Path.Combine(roots,"spawns")).Length)
        let replacement=ExecutorRuntime(options,TimeProvider.System)
        use retryInput=new MemoryStream(RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeCommandV2 launch|])
        use retryOutput=new MemoryStream()
        do! replacement.Run(retryInput,retryOutput,CancellationToken.None)
        let outcomes=RuntimeFixture.readFrames(retryOutput.ToArray())|>List.choose(fun frame->ExecutorWire.parseOperationOutcome frame|>Result.toOption)
        Assert.Single outcomes|>ignore
        Assert.Equal("ambiguous",outcomes.Head.Disposition)
        let changedUnsigned={launch with CommandId=Guid.NewGuid();BodySha256="";Generation=8L}
        let changed={changedUnsigned with BodySha256=ExecutorWire.commandV2Digest changedUnsigned}
        use changedInput=new MemoryStream(RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeCommandV2 changed|])
        use changedOutput=new MemoryStream()
        do! ExecutorRuntime(options,TimeProvider.System).Run(changedInput,changedOutput,CancellationToken.None)
        let changedOutcome=RuntimeFixture.readFrames(changedOutput.ToArray()).Head|>ExecutorWire.parseOperationOutcome|>Result.defaultWith failwith
        Assert.Equal("executor-generation-stale-or-overlapping",changedOutcome.Reason)
        Assert.Equal(1,File.ReadAllLines(Path.Combine(roots,"spawns")).Length) }

    [<Fact>]
    member _.``cancel remains responsive while provider output is blocked and stale generation refuses``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let roots=Directory.CreateTempSubdirectory("executor-cancel-").FullName
        let mk name=Directory.CreateDirectory(Path.Combine(roots,name)).FullName
        let workspaceRoot,inputRoot,stateRoot,artifactRoot=mk "workspaces",mk "inputs",mk "state",mk "artifacts"
        let prompt=Encoding.UTF8.GetBytes "bounded input"
        let digest=RuntimeFixture.sha prompt
        let manifest=RuntimeFixture.manifest baseline digest
        let manifestBytes=ExecutorWire.encodeWorkspaceManifest manifest
        let launch=RuntimeFixture.command (RuntimeFixture.sha manifestBytes) digest baseline "launch" null
        let options={RepositoryRoot=repository;WorkspaceRoot=workspaceRoot;InputRoot=inputRoot;StateRoot=stateRoot;ArtifactRoot=artifactRoot;CodexExecutable=RuntimeFixture.hangingCodex roots;ExecutorBinding="fixture-executor";MaximumFrameBytes=2*ExecutorWire.maximumContentBytes}
        let runtime=ExecutorRuntime(options,TimeProvider.System)
        let inputManifest={Schema=ExecutorWire.inputManifestSchema;InputDigest=digest;MediaType="text/plain";SizeBytes=int64 prompt.Length;ChunkBytes=4096}
        let content={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String prompt}
        use launchInput=new MemoryStream(RuntimeFixture.frames [|manifestBytes;ExecutorWire.encodeInputManifest inputManifest;ExecutorWire.encodeContent content;ExecutorWire.encodeCommandV2 launch|])
        use launchOutput=new MemoryStream()
        do! runtime.Run(launchInput,launchOutput,CancellationToken.None)
        let started=RuntimeFixture.readFrames(launchOutput.ToArray())|>List.pick(fun frame->ExecutorWire.parseResponse frame|>Result.toOption)
        let cancelUnsigned={launch with CommandId=Guid.NewGuid();BodySha256="";Kind="cancel";ProviderSessionReference=started.ProviderSessionReference}
        let cancel={cancelUnsigned with BodySha256=ExecutorWire.commandV2Digest cancelUnsigned}
        let timer=Stopwatch.StartNew()
        use cancelInput=new MemoryStream(RuntimeFixture.frames [|ExecutorWire.encodeCommandV2 cancel|])
        use cancelOutput=new MemoryStream()
        do! runtime.Run(cancelInput,cancelOutput,CancellationToken.None)
        Assert.True(timer.Elapsed<TimeSpan.FromSeconds 2.)
        let outcome=RuntimeFixture.readFrames(cancelOutput.ToArray()).Head|>ExecutorWire.parseOperationOutcome|>Result.defaultWith failwith
        Assert.Equal("accepted",outcome.Disposition)
        let staleUnsigned={launch with CommandId=Guid.NewGuid();BodySha256="";Kind="reconcile";Generation=6L}
        let stale={staleUnsigned with BodySha256=ExecutorWire.commandV2Digest staleUnsigned}
        use staleInput=new MemoryStream(RuntimeFixture.frames [|ExecutorWire.encodeCommandV2 stale|])
        use staleOutput=new MemoryStream()
        do! runtime.Run(staleInput,staleOutput,CancellationToken.None)
        let refused=RuntimeFixture.readFrames(staleOutput.ToArray()).Head|>ExecutorWire.parseOperationOutcome|>Result.defaultWith failwith
        Assert.Equal("executor-generation-stale-or-overlapping",refused.Reason)
        let childPid=int(File.ReadAllText(Path.Combine(roots,"child-pid")))
        do! Threading.Tasks.Task.Delay 50
        Assert.ThrowsAny<ArgumentException>(fun ()->Process.GetProcessById(childPid)|>ignore)|>ignore }

    [<Fact>]
    member _.``oversized and truncated frames refuse before provider execution``() = task {
        let root=Directory.CreateTempSubdirectory("executor-frame-").FullName
        let repository,_=RuntimeFixture.repo()
        let mk name=Directory.CreateDirectory(Path.Combine(root,name)).FullName
        let options={RepositoryRoot=repository;WorkspaceRoot=mk "w";InputRoot=mk "i";StateRoot=mk "s";ArtifactRoot=mk "a";CodexExecutable="/does/not/run";ExecutorBinding="fixture-executor";MaximumFrameBytes=1024}
        let header=Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteInt32BigEndian(header,2048)
        use input=new MemoryStream(header)
        use output=new MemoryStream()
        let! error=Assert.ThrowsAsync<InvalidDataException>(fun ()->ExecutorRuntime(options,TimeProvider.System).Run(input,output,CancellationToken.None))
        Assert.Equal("executor-frame-size-refused",error.Message)
        let truncated=Array.zeroCreate<byte> 6
        BinaryPrimitives.WriteInt32BigEndian(truncated.AsSpan(0,4),4)
        use truncatedInput=new MemoryStream(truncated)
        use truncatedOutput=new MemoryStream()
        let! _=Assert.ThrowsAsync<EndOfStreamException>(fun ()->ExecutorRuntime(options,TimeProvider.System).Run(truncatedInput,truncatedOutput,CancellationToken.None))
        () }

    [<Fact>]
    member _.``symlink workspace and input escapes plus over-admitted chunks refuse``() = task {
        let repository,baseline=RuntimeFixture.repo()
        let root=Directory.CreateTempSubdirectory("executor-links-").FullName
        let workspaceRoot=Directory.CreateDirectory(Path.Combine(root,"workspaces")).FullName
        let outside=Directory.CreateDirectory(Path.Combine(root,"outside")).FullName
        let assignment=Guid.NewGuid()
        Directory.CreateSymbolicLink(Path.Combine(workspaceRoot,assignment.ToString("N")),outside)|>ignore
        let digest=String.replicate 64 "a"
        let manifest=RuntimeFixture.manifest baseline digest
        Assert.Equal(Error "workspace-parent-link-refused",ExecutorWorkspace.materialize repository workspaceRoot assignment (Guid.NewGuid()) 1L manifest)
        let inputRoot=Directory.CreateDirectory(Path.Combine(root,"inputs")).FullName
        let external=Path.Combine(root,"external.input")
        File.WriteAllText(external,"secret")
        File.CreateSymbolicLink(Path.Combine(inputRoot,digest+".input"),external)|>ignore
        let input=DigestInput(inputRoot,digest,1024L):>FS.GG.Coordination.Orchestration.Execution.Codex.ICodexExecutionInput
        Assert.Equal(Error "executor-input-bounds-refused",input.ReadUtf8(digest,CancellationToken.None).Result)
        let mk name=Directory.CreateDirectory(Path.Combine(root,name)).FullName
        let options={RepositoryRoot=repository;WorkspaceRoot=mk "bounded-work";InputRoot=mk "bounded-input";StateRoot=mk "state";ArtifactRoot=mk "artifacts";CodexExecutable="/does/not/run";ExecutorBinding="fixture-executor";MaximumFrameBytes=2*ExecutorWire.maximumContentBytes}
        let admitted={Schema=ExecutorWire.inputManifestSchema;InputDigest=RuntimeFixture.sha(Encoding.UTF8.GetBytes "abc");MediaType="text/plain";SizeBytes=3L;ChunkBytes=3}
        let oversized={Schema=ExecutorWire.contentSchema;CommandId=Guid.NewGuid();InputDigest=admitted.InputDigest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes "abcd")}
        use framed=new MemoryStream(RuntimeFixture.frames [|ExecutorWire.encodeInputManifest admitted;ExecutorWire.encodeContent oversized|])
        use output=new MemoryStream()
        let! error=Assert.ThrowsAsync<InvalidDataException>(fun ()->ExecutorRuntime(options,TimeProvider.System).Run(framed,output,CancellationToken.None))
        Assert.Equal("executor-input-chunk-bounds-refused",error.Message)
        Assert.False(File.Exists(Path.Combine(options.InputRoot,admitted.InputDigest+".partial"))) }
