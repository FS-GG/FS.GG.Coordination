namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open System.Runtime.InteropServices
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Runner.Protocol

type CandidateArtifact = { Manifest:ExecutorArtifactManifest; BundlePath:string }

[<RequireQualifiedAccess>]
module private SafePath =
    let child root relative =
        if String.IsNullOrWhiteSpace relative || Path.IsPathRooted relative then Error "path-refused"
        else
            let rootPath=Path.GetFullPath(root)+string Path.DirectorySeparatorChar
            let path=Path.GetFullPath(Path.Combine(root,relative))
            if not(path.StartsWith(rootPath,StringComparison.Ordinal)) then Error "path-escape-refused" else Ok path
    let noLinks root path =
        let root=Path.GetFullPath root
        let prefix=if root.EndsWith(string Path.DirectorySeparatorChar,StringComparison.Ordinal) then root else root+string Path.DirectorySeparatorChar
        let rec loop current =
            if current=root then true
            elif String.IsNullOrEmpty current||not(current.StartsWith(prefix,StringComparison.Ordinal)) then false
            elif File.Exists current then isNull(FileInfo(current).LinkTarget) && loop (Path.GetDirectoryName current)
            elif Directory.Exists current then isNull(DirectoryInfo(current).LinkTarget) && loop (Path.GetDirectoryName current)
            else loop (Path.GetDirectoryName current)
        loop (Path.GetFullPath path)

[<RequireQualifiedAccess>]
module private BoundedProcess =
    let private drain (stream:Stream) maximum = task {
        let buffer=Array.zeroCreate<byte> 4096
        use kept=new MemoryStream(min maximum 4096)
        let mutable finished=false
        let mutable overflow=false
        while not finished do
            let! count=stream.ReadAsync buffer
            if count=0 then finished<-true
            else
                let remaining=maximum-int kept.Length
                if remaining>0 then kept.Write(buffer,0,min remaining count)
                if count>remaining then overflow<-true
        return Encoding.UTF8.GetString(kept.ToArray()),overflow }
    let runWithCancellation (cancellationToken:CancellationToken) executable cwd arguments =
        let start=ProcessStartInfo(executable,WorkingDirectory=cwd,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true)
        arguments |> List.iter start.ArgumentList.Add
        start.Environment["GIT_CONFIG_NOSYSTEM"]<-"1"
        start.Environment["GIT_TERMINAL_PROMPT"]<-"0"
        start.Environment["GIT_OPTIONAL_LOCKS"]<-"0"
        use child=Process.Start start
        use cancellation=cancellationToken.Register(fun ()->try child.Kill(true) with _->())
        let output=drain child.StandardOutput.BaseStream (4*1024*1024)
        let error=drain child.StandardError.BaseStream (1024*1024)
        let exited=child.WaitForExit(30000)
        let mutable reaped=exited
        if not exited then
            try child.Kill(true) with _->()
            reaped<-child.WaitForExit(5000)
            if not reaped then try child.StandardOutput.Close();child.StandardError.Close() with _->()
        if not(Task.WaitAll([|output:>Task;error:>Task|],2000)) then
            try child.StandardOutput.Close();child.StandardError.Close() with _->()
            Error "subprocess-pipe-ambiguous"
        elif cancellationToken.IsCancellationRequested then Error "subprocess-cancelled"
        elif not reaped then Error "subprocess-kill-ambiguous"
        elif output.Result|>snd || error.Result|>snd then Error "subprocess-output-bounds-refused"
        elif not exited then Error "subprocess-timeout"
        elif child.ExitCode<>0 then Error("subprocess-refused:"+((error.Result|>fst).Trim() |> fun value -> if value.Length>256 then value[..255] else value))
        else Ok((output.Result|>fst).Trim())
    let run executable cwd arguments=runWithCancellation CancellationToken.None executable cwd arguments

[<RequireQualifiedAccess>]
module private Git =
    let run cwd (arguments:string list) =
        BoundedProcess.run "git" cwd arguments
    let runWithCancellation cancellationToken cwd (arguments:string list) =
        BoundedProcess.runWithCancellation cancellationToken "git" cwd arguments
    let objectId value =
        not(String.IsNullOrWhiteSpace value)&&(value.Length=40||value.Length=64)
        && (value |> Seq.forall(fun c->Char.IsAsciiHexDigit c && not(Char.IsUpper c)))

type DigestInput(root:string,digest:string,maximumBytes:int64) =
    let path=Path.Combine(root,digest+".input")
    interface ICodexExecutionInput with
        member _.ReadUtf8(requested,cancellationToken)=task {
            if requested<>digest || cancellationToken.IsCancellationRequested then return Error "executor-input-identity-refused"
            elif not(File.Exists path) then return Error "executor-input-missing"
            else
                let info=FileInfo path
                if not(SafePath.noLinks root path)||info.Length<1L||info.Length>maximumBytes||not(isNull info.LinkTarget) then return Error "executor-input-bounds-refused"
                else
                    use stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.SequentialScan)
                    if stream.Length<1L||stream.Length>maximumBytes then return Error "executor-input-bounds-refused"
                    else
                        let bytes=Array.zeroCreate<byte> (int stream.Length)
                        stream.ReadExactly bytes
                        if stream.ReadByte() <> -1 then return Error "executor-input-changed-during-read"
                        else
                            let observed=SHA256.HashData bytes|>Convert.ToHexString|>_.ToLowerInvariant()
                            if observed<>digest then return Error "executor-input-digest-mismatch" else return Ok bytes }

type GitCandidateInspector(workspace:string,manifest:ExecutorWorkspaceManifest,artifactRoot:string,commandId:Guid,candidateId:Guid) =
    let allowed (path:string) =
        manifest.AllowedPaths |> Array.exists(fun rule ->
            if rule.EndsWith("/**",StringComparison.Ordinal) then path.StartsWith(rule[..rule.Length-3],StringComparison.Ordinal)
            else path=rule)
    let verify cancellationToken (candidate:CandidateReference) =
        if candidate.CandidateId<>candidateId || not(Git.objectId candidate.HeadSha&&Git.objectId candidate.TreeSha) then Error "candidate-identity-refused"
        else
            let run=Git.runWithCancellation cancellationToken workspace
            run ["rev-parse";"HEAD"] |> Result.bind(fun head -> if head<>candidate.HeadSha then Error "candidate-head-mismatch" else Ok())
            |> Result.bind(fun ()->run ["rev-parse";candidate.HeadSha+"^{tree}"] |> Result.bind(fun tree->if tree<>candidate.TreeSha then Error "candidate-tree-mismatch" else Ok()))
            |> Result.bind(fun ()->run ["merge-base";"--is-ancestor";manifest.BaselineObjectId;candidate.HeadSha] |> Result.map ignore)
            |> Result.bind(fun ()->run ["diff";"--name-only";"--no-renames";manifest.BaselineObjectId;candidate.HeadSha]
                                  |> Result.bind(fun changed->
                                      let paths=changed.Split('\n',StringSplitOptions.RemoveEmptyEntries)
                                      if paths.Length=0||paths|>Array.exists(fun path->not(allowed path)) then Error "candidate-touch-set-refused" else Ok()))
            |> Result.bind(fun ()->run ["diff";"--check";manifest.BaselineObjectId;candidate.HeadSha] |> Result.map ignore)
            |> Result.bind(fun ()->run ["status";"--porcelain=v1";"--untracked-files=all"] |> Result.bind(fun status->if status="" then Ok() else Error "candidate-worktree-dirty"))
            |> Result.bind(fun ()->run ["ls-tree";"-r";"--format=%(objectmode) %(path)";candidate.HeadSha]
                                  |> Result.bind(fun listing->if listing.Split('\n')|>Array.exists(fun line->line.StartsWith("120000 ",StringComparison.Ordinal)) then Error "candidate-symlink-refused" else Ok()))
            |> Result.bind(fun ()->
                manifest.Validations |> Array.fold(fun state validationName -> state |> Result.bind(fun ()->
                    match validationName with
                    | "git-diff-check" -> Ok()
                    | "prose-citations" ->
                        run ["diff";"--quiet";manifest.BaselineObjectId;candidate.HeadSha;"--";"scripts/check-prose-citations.py"] |> Result.map ignore
                        |> Result.bind(fun ()->run ["show";candidate.HeadSha+":scripts/check-prose-citations.py"] |> Result.map ignore)
                        |> Result.bind(fun ()->
                            BoundedProcess.runWithCancellation cancellationToken "python3" workspace ["scripts/check-prose-citations.py";"--root";"."] |> Result.map ignore)
                    | _ -> Error "candidate-validation-unknown")) (Ok()))
    member _.CreateArtifact(candidate:CandidateReference,chunkBytes:int)=
        verify CancellationToken.None candidate |> Result.bind(fun ()->
            Directory.CreateDirectory artifactRoot|>ignore
            let bundlePath=Path.Combine(artifactRoot,candidate.CandidateId.ToString("N")+".bundle")
            let refName="refs/fsgg/candidates/"+candidate.CandidateId.ToString("N")
            Git.run workspace ["update-ref";refName;candidate.HeadSha] |> Result.map ignore |> Result.bind(fun ()->Git.run workspace ["bundle";"create";bundlePath;refName;"^"+manifest.BaselineObjectId] |> Result.map ignore)
            |> Result.bind(fun ()->
                let info=FileInfo bundlePath
                if info.Length<1L||info.Length>140L*1024L*1024L then Error "candidate-bundle-size-refused"
                else
                    use stream=File.OpenRead bundlePath
                    let digest=SHA256.HashData stream|>Convert.ToHexString|>_.ToLowerInvariant()
                    let unsigned={Schema=ExecutorWire.artifactManifestSchema;CommandId=commandId;CandidateId=candidate.CandidateId;BaselineObjectId=manifest.BaselineObjectId;HeadObjectId=candidate.HeadSha;TreeObjectId=candidate.TreeSha;BundleSha256=digest;BundleSizeBytes=info.Length;ManifestSha256="";ChunkBytes=chunkBytes}
                    let result={unsigned with ManifestSha256=ExecutorWire.artifactManifestDigest unsigned}
                    File.WriteAllBytes(Path.Combine(artifactRoot,candidate.CandidateId.ToString("N")+".manifest.json"),ExecutorWire.encodeArtifactManifest result)
                    Ok {Manifest=result;BundlePath=bundlePath}))
    interface ICodexCandidateInspector with
        member _.Verify(candidateWorkspace,candidate,cancellationToken)=Task.FromResult(if cancellationToken.IsCancellationRequested||candidateWorkspace<>workspace then Error "candidate-workspace-refused" else verify cancellationToken candidate)

[<RequireQualifiedAccess>]
module ExecutorWorkspace =
    let materialize repositoryRoot workspaceRoot (assignmentId:Guid) (attemptId:Guid) generation (manifest:ExecutorWorkspaceManifest) =
        if not(Directory.Exists repositoryRoot)||not(Directory.Exists workspaceRoot)
           ||not(SafePath.noLinks (Path.GetPathRoot repositoryRoot) repositoryRoot)||not(SafePath.noLinks (Path.GetPathRoot workspaceRoot) workspaceRoot) then Error "executor-root-refused"
        elif manifest.RepositoryBinding<>"selected-repository" then Error "repository-binding-refused"
        else
            let relative=Path.Combine(assignmentId.ToString("N"),attemptId.ToString("N"),generation.ToString())
            SafePath.child workspaceRoot relative |> Result.bind(fun target->
                let bindingPath=target+".binding"
                let bindingDigest=ExecutorWire.encodeWorkspaceManifest manifest|>RunnerWire.sha256
                if File.Exists target then Error "workspace-conflict-refused"
                elif Directory.Exists target then
                    let binding=FileInfo bindingPath
                    if not(SafePath.noLinks workspaceRoot target)||not binding.Exists||not(isNull binding.LinkTarget)||binding.Length<>64L||File.ReadAllText(bindingPath)<>bindingDigest then Error "workspace-binding-conflict"
                    else Ok target
                else
                    let parent=Path.GetDirectoryName target
                    if not(SafePath.noLinks workspaceRoot parent) then Error "workspace-parent-link-refused"
                    else
                        Directory.CreateDirectory(parent)|>ignore
                        Git.run workspaceRoot ["clone";"--no-hardlinks";"--no-checkout";"--";repositoryRoot;target] |> Result.map ignore
                        |> Result.bind(fun ()->Git.run target ["cat-file";"-e";manifest.BaselineObjectId+"^{commit}"] |> Result.map ignore)
                        |> Result.bind(fun ()->Git.run target ["checkout";"--detach";manifest.BaselineObjectId] |> Result.map ignore)
                        |> Result.bind(fun ()->
                            if not(SafePath.noLinks workspaceRoot target) then Error "workspace-link-refused"
                            else File.WriteAllText(bindingPath,bindingDigest);Ok target))
