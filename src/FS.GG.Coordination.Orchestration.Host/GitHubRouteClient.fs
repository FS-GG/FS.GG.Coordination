namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.GitHub

type IGitHubRequestExecutor = abstract Send:GitHubRequest * CancellationToken -> Task<TransportOutcome>
type IGitCandidatePublisher = abstract Publish:string * string * string option * string * byte array * CancellationToken -> Task<Result<string,string>>

/// Executes validated GitHub requests with the Host's private delivery identity.
[<Sealed>]
type HttpGitHubRequestExecutor(client:HttpClient,token:string,maximumResponseBytes:int) =
    let requestGate=new SemaphoreSlim(1,1)
    let mutable nextAllowedAt=DateTimeOffset.MinValue
    let header (name:string) (headers:Map<string,string>) =
        headers
        |> Map.toSeq
        |> Seq.tryPick(fun (key,value)->if key.Equals(name,StringComparison.OrdinalIgnoreCase) then Some value else None)
    let delayUntilAllowed (ct:CancellationToken) = task {
        let delay=nextAllowedAt-DateTimeOffset.UtcNow
        if delay>TimeSpan.Zero then do! Task.Delay(delay,ct) }
    let updatePacing status (headers:Map<string,string>) =
        let now=DateTimeOffset.UtcNow
        let retryAfter =
            header "Retry-After" headers
            |> Option.bind(fun value->
                match Double.TryParse(value,Globalization.NumberStyles.Number,Globalization.CultureInfo.InvariantCulture) with
                | true,seconds when seconds>=0. -> Some(now.AddSeconds seconds)
                | _ -> match DateTimeOffset.TryParse value with true,time->Some time|_->None)
        let remaining=header "X-RateLimit-Remaining" headers|>Option.bind(fun value->match Int32.TryParse value with true,count->Some count|_->None)
        let resetAt=header "X-RateLimit-Reset" headers|>Option.bind(fun value->match Int64.TryParse value with true,epoch->Some(DateTimeOffset.FromUnixTimeSeconds epoch)|_->None)
        let conservative=now.AddMilliseconds 250.
        let limited =
            if status=429 || status=403 then retryAfter|>Option.orElse resetAt|>Option.defaultValue(now.AddSeconds 15.)
            elif remaining|>Option.exists(fun value->value<=1) then resetAt|>Option.defaultValue(now.AddSeconds 15.)
            elif remaining.IsNone then conservative
            else now
        nextAllowedAt<-max nextAllowedAt limited
    interface IGitHubRequestExecutor with
        member _.Send(request,ct)=task {
            try
                do! requestGate.WaitAsync ct
                try
                    do! delayUntilAllowed ct
                    match Transport.validateRequest request with
                    | Error _ -> return NetworkFailure
                    | Ok() ->
                        let methodValue,uri,headers,body =
                            match request with
                            | Rest value ->
                                (match value.Method with RestMethod.Get->HttpMethod.Get|RestMethod.Post->HttpMethod.Post|RestMethod.Put->HttpMethod.Put|RestMethod.Patch->HttpMethod.Patch|RestMethod.Delete->HttpMethod.Delete),value.Uri,value.Headers,value.Body
                            | GraphQL value -> HttpMethod.Post,value.Uri,value.Headers,Some(JsonSerializer.Serialize {|query=value.Document;variables=value.Variables|})
                        use message=new HttpRequestMessage(methodValue,uri)
                        message.Headers.Authorization<-AuthenticationHeaderValue("Bearer",token)
                        message.Headers.UserAgent.ParseAdd("fsgg-coordination-orchestration-host/1")
                        message.Headers.Add("X-GitHub-Api-Version",ApiVersion.value ApiVersion.required)
                        headers|>Map.iter(fun name value->message.Headers.TryAddWithoutValidation(name,value)|>ignore)
                        body|>Option.iter(fun value->message.Content<-new StringContent(value,Encoding.UTF8,"application/json"))
                        use! response=client.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,ct)
                        use! stream=response.Content.ReadAsStreamAsync ct
                        use output=new MemoryStream()
                        let buffer=Array.zeroCreate<byte> 8192
                        let mutable total=0
                        let mutable reading=true
                        let mutable overflow=false
                        while reading do
                            let! count=stream.ReadAsync(buffer.AsMemory(),ct)
                            if count=0 then reading<-false
                            elif total+count>maximumResponseBytes then overflow<-true;reading<-false
                            else output.Write(buffer,0,count);total<-total+count
                        if overflow then return NetworkFailure else
                        let headerMap=Seq.append (response.Headers:>seq<_>) (response.Content.Headers:>seq<_>)|>Seq.map(fun (item:KeyValuePair<string,System.Collections.Generic.IEnumerable<string>>)->item.Key,String.concat "," item.Value)|>Map.ofSeq
                        updatePacing (int response.StatusCode) headerMap
                        let tryInt name=header name headerMap|>Option.bind(fun text->match Int32.TryParse text with true,value->Some value|_->None)
                        let tryTime name=header name headerMap|>Option.bind(fun text->match Int64.TryParse text with true,value->Some(DateTimeOffset.FromUnixTimeSeconds value)|_->None)
                        return Response{StatusCode=int response.StatusCode;Headers=headerMap;Body=Encoding.UTF8.GetString(output.ToArray());ETag=response.Headers.ETag|>Option.ofObj|>Option.map string;RateBudget={Limit=tryInt "X-RateLimit-Limit";Remaining=tryInt "X-RateLimit-Remaining";ResetAt=tryTime "X-RateLimit-Reset";Cost=None}}
                finally requestGate.Release()|>ignore
            with :? OperationCanceledException -> return TimedOut | _ -> return NetworkFailure }

/// Imports the runner-produced Git bundle, then publishes with Git's native
/// force-with-lease CAS. Credential material is inherited only by this child.
[<Sealed>]
type GitBundlePublisher(remoteUri:Uri,token:string,maximumOutputBytes:int) =
    let run (cwd:string) (arguments:string list) (cancellationToken:CancellationToken) = task {
        let start=ProcessStartInfo("git",WorkingDirectory=cwd,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true)
        arguments|>List.iter start.ArgumentList.Add
        start.Environment["GIT_TERMINAL_PROMPT"]<-"0"; start.Environment["GIT_CONFIG_NOSYSTEM"]<-"1"
        start.Environment["GIT_CONFIG_COUNT"]<-"1"; start.Environment["GIT_CONFIG_KEY_0"]<-"http.extraHeader"
        start.Environment["GIT_CONFIG_VALUE_0"]<-"Authorization: Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:"+token))
        use child=Process.Start start
        use registration=cancellationToken.Register(fun()->try child.Kill(true) with _->())
        let readBounded (reader:StreamReader)=task {
            let buffer=Array.zeroCreate<char> 4096
            let kept=StringBuilder(min maximumOutputBytes 4096)
            let mutable doneReading=false
            let mutable overflow=false
            while not doneReading do
                let! count=reader.ReadAsync(buffer.AsMemory(),cancellationToken)
                if count=0 then doneReading<-true
                else
                    let remaining=maximumOutputBytes-kept.Length
                    if remaining>0 then kept.Append(buffer,0,min count remaining)|>ignore
                    if count>remaining then overflow<-true
            return kept.ToString(),overflow }
        let output=readBounded child.StandardOutput
        let error=readBounded child.StandardError
        do! child.WaitForExitAsync cancellationToken
        let! stdout,stdoutOverflow=output
        let! stderr,stderrOverflow=error
        if stdoutOverflow||stderrOverflow then return Error "git-output-bounds-refused"
        elif child.ExitCode=0 then return Ok stdout
        else return Error("git-publish-refused:"+(if stderr.Length>256 then stderr[..255] else stderr).Trim()) }
    interface IGitCandidatePublisher with
        member _.Publish(_,branchRef,expectedHead,headSha,bundle,cancellationToken)=task {
            let root=Path.Combine(Path.GetTempPath(),"fsgg-candidate-publish-"+Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory root|>ignore
            try
                let bundlePath=Path.Combine(root,"candidate.bundle")
                do! File.WriteAllBytesAsync(bundlePath,bundle,cancellationToken)
                let! initialized=run root ["init";"--bare";"repository.git"] cancellationToken
                match initialized with
                | Error reason -> return Error reason
                | Ok _ ->
                    let repository=Path.Combine(root,"repository.git")
                    // Candidate bundles intentionally contain only the selected
                    // baseline..head closure. Seed immutable prerequisites from
                    // the bound remote before importing; never accept a summary
                    // archive as a substitute for Git object identity.
                    let! prerequisites=run repository ["fetch";"--no-tags";remoteUri.AbsoluteUri;"+refs/heads/*:refs/remotes/origin/*"] cancellationToken
                    let! imported =
                        match prerequisites with
                        | Error reason -> Task.FromResult(Error reason)
                        | Ok _ -> run repository ["fetch";"--no-tags";bundlePath;headSha] cancellationToken
                    match imported with
                    | Error reason -> return Error reason
                    | Ok _ ->
                        let expected=defaultArg expectedHead ""
                        let! pushed=run repository ["push";"--porcelain";$"--force-with-lease={branchRef}:{expected}";remoteUri.AbsoluteUri;$"{headSha}:{branchRef}"] cancellationToken
                        match pushed with
                        | Error reason -> return Error reason
                        | Ok _ ->
                            let! observed=run repository ["ls-remote";"--refs";remoteUri.AbsoluteUri;branchRef] cancellationToken
                            return observed|>Result.bind(fun value->let fields=value.Trim().Split('\t') in if fields.Length=2&&fields[0]=headSha then Ok headSha else Error "git-publish-readback-refused")
            finally try Directory.Delete(root,true) with _->() }

type GitHubRouteTarget =
    { ApiRoot:Uri; Repository:string; IssueNumber:int; Principal:string; BaseRef:string
      RoutineOperation:string; ClaimLease:TimeSpan }

type private PullRequestFacts =
    { Number:int; NodeId:string; State:string; Merged:bool; HeadSha:string; HeadRef:string
      BaseRef:string; BaseSha:string; BaseRepository:string; Body:string
      MergeCommitSha:string option; Revision:string }

[<Sealed>]
type GitHubRouteClient(executor:IGitHubRequestExecutor,publisher:IGitCandidatePublisher,target:GitHubRouteTarget,clock:TimeProvider) =
    let parts=target.Repository.Split('/',StringSplitOptions.RemoveEmptyEntries)
    let owner,repo=if parts.Length=2 then parts[0],parts[1] else "",""
    let uri (path:string)=Uri(target.ApiRoot,$"repos/{owner}/{repo}/{path}")
    let request (methodValue:RestMethod) (path:string) (body:string option) (key:string option) = Rest{Method=methodValue;Uri=uri path;Headers=Map.empty;Body=body;ApiVersion=ApiVersion.required;Idempotency=(match key with Some value->ReplayWithKey value|None->ReplaySafe)}
    let send (methodValue:RestMethod) (path:string) (body:string option) (key:string option) (ct:CancellationToken) : Task<Result<ResponseEnvelope,string>>=task {
        if String.IsNullOrWhiteSpace owner || String.IsNullOrWhiteSpace repo then return Error "github-repository-binding-refused" else
        let! outcome=executor.Send(request methodValue path body key,ct)
        return match outcome with Response response when response.StatusCode>=200&&response.StatusCode<300->Ok response|Response response->Error($"github-status-{response.StatusCode}")|NetworkFailure->Error "github-network-unknown"|TimedOut->Error "github-timeout-unknown" }
    let parse (body:string) = try Ok(JsonDocument.Parse body) with :? JsonException->Error "github-json-refused"
    let property (name:string) (value:JsonElement)=match value.TryGetProperty name with true,item when item.ValueKind=JsonValueKind.String->Option.ofObj(item.GetString())|_->None
    let gitId (value:string)=not(String.IsNullOrWhiteSpace value)&&(value.Length=40||value.Length=64)&&(value|>Seq.forall(fun c->Char.IsAsciiHexDigit c && not(Char.IsUpper c)))
    // Matches the canonical claim marker emitted by FS.GG.Coord.GitHub.
    // Metadata is ordered and optional; unknown or reordered fields refuse.
    let marker=Regex("^<!-- fsgg:claim worker=(?<worker>[^ ]+) lease=(?<lease>[0-9]+) renewed=(?<renewed>[0-9]+)(?: session=(?<session>[a-f0-9]{32}))?(?: prev=(?<prev>[^ ]+))?(?: pathRepo=(?<pathRepo>[^ ]+))?(?: agentContract=(?<agentContract>[^ ]+))? -->$",RegexOptions.CultureInvariant)
    let routineMarker=Regex("<!-- fsgg:routine-development/v1 head=(?<head>[a-f0-9]{40}) operation=(?<operation>[a-z-]+) -->",RegexOptions.CultureInvariant)
    let paginated (headers:Map<string,string>)=headers|>Map.exists(fun key content->key.Equals("Link",StringComparison.OrdinalIgnoreCase)&&content.Contains("rel=\"next\"",StringComparison.Ordinal))
    let readComments (ct:CancellationToken) : Task<Result<(int64*string*string) list,string>>=task {
        let! response=send RestMethod.Get $"issues/{target.IssueNumber}/comments?per_page=100" None None ct
        match response with
        | Error reason->return Error reason
        | Ok value when paginated value.Headers->return Error "github-claim-census-incomplete"
        | Ok value->
            match parse value.Body with
            | Error reason->return Error reason
            | Ok document->
                use document=document
                if document.RootElement.ValueKind<>JsonValueKind.Array then return Error "github-claim-census-refused" else
                let now=clock.GetUtcNow()
                let mutable malformed=false
                let observed=document.RootElement.EnumerateArray()|>Seq.choose(fun (item:JsonElement)->
                    match item.TryGetProperty("id"),property "body" item,property "updated_at" item with
                    | (true,id),Some body,Some updated when body.IndexOf("<!-- fsgg:claim",StringComparison.Ordinal)>=0->
                        let found=marker.Match body
                        match DateTimeOffset.TryParse updated with
                        | true,time when found.Success->
                            let lease=Int32.Parse(found.Groups["lease"].Value)
                            let session=if found.Groups["session"].Success then found.Groups["session"].Value else ""
                            if time.AddMinutes(float lease)>now then Some(id.GetInt64(),found.Groups["worker"].Value,session) else None
                        | _->malformed<-true;None
                    | _->None)|>Seq.sortBy(fun(id,_,_)->id)|>Seq.toList
                return if malformed then Error "github-claim-marker-unparseable" else Ok observed }
    let readPull (branchRef:string) (ct:CancellationToken) : Task<Result<PullRequestFacts option,string>>=task {
        let branch=branchRef.Replace("refs/heads/","")
        let query=Uri.EscapeDataString(owner+":"+branch)
        let! response=send RestMethod.Get $"pulls?state=all&head={query}&base={Uri.EscapeDataString target.BaseRef}&per_page=100" None None ct
        match response with
        | Error reason->return Error reason
        | Ok value when paginated value.Headers->return Error "github-pull-request-census-incomplete"
        | Ok value->
            match parse value.Body with
            | Error reason->return Error reason
            | Ok document->
                use document=document
                if document.RootElement.ValueKind<>JsonValueKind.Array then return Error "github-pull-request-census-refused" else
                let values=document.RootElement.EnumerateArray()|>Seq.toList
                if values.Length>1 then return Error "github-pull-request-identity-ambiguous" else
                return values|>List.tryHead|>Option.map(fun (item:JsonElement)->
                    let baseValue=item.GetProperty("base")
                    {Number=item.GetProperty("number").GetInt32();NodeId=item.GetProperty("node_id").GetString();State=item.GetProperty("state").GetString();Merged=(match item.TryGetProperty "merged_at" with true,v->v.ValueKind=JsonValueKind.String|_->false);HeadSha=item.GetProperty("head").GetProperty("sha").GetString();HeadRef=item.GetProperty("head").GetProperty("ref").GetString();BaseRef=baseValue.GetProperty("ref").GetString();BaseSha=baseValue.GetProperty("sha").GetString();BaseRepository=baseValue.GetProperty("repo").GetProperty("full_name").GetString();Body=defaultArg(property "body" item) "";MergeCommitSha=property "merge_commit_sha" item;Revision=defaultArg value.ETag "github-pr-observed"})|>Ok }
    member _.AcquireClaim(claimId:string,operationId:Guid,ct:CancellationToken)=task {
        let session=operationId.ToString("N")
        let! before=readComments ct
        match before with
        | Error reason->return Error reason
        | Ok ((_,worker,observed)::_) when worker=target.Principal&&observed=session->return Ok(claimId,observed)
        | Ok ((_,_,_)::_)->return Error "github-claim-held-by-competitor"
        | Ok []->
            let lease=max 1 (int target.ClaimLease.TotalMinutes)
            let body=$"<!-- fsgg:claim worker={target.Principal} lease={lease} renewed={clock.GetUtcNow().Ticks} session={session} -->"
            let! posted=send RestMethod.Post $"issues/{target.IssueNumber}/comments" (Some(JsonSerializer.Serialize {|body=body|})) (Some session) ct
            match posted with
            | Error _ -> let! reconciled=readComments ct in return match reconciled with Ok((id,worker,found)::_) when worker=target.Principal&&found=session->Ok(claimId,string id)|Ok _->Error "github-claim-outcome-unknown"|Error reason->Error reason
            | Ok _ -> let! observed=readComments ct in return match observed with Ok((id,worker,found)::_) when worker=target.Principal&&found=session->Ok(claimId,string id)|Ok _->Error "github-claim-lost"|Error reason->Error reason }
    member _.ReadClaim(claimId:string,operationId:Guid,ct:CancellationToken)=task {
        let session=operationId.ToString("N")
        let! observed=readComments ct
        return match observed with Ok((id,worker,found)::_) when worker=target.Principal&&found=session->Ok(claimId,string id)|Ok ((_,_,_)::_)->Error "github-claim-held-by-competitor"|Ok []->Error "github-claim-not-observed"|Error reason->Error reason }
    member _.PublishBranch(branchRef:string,expectedHead:string option,headSha:string,bundle:byte array,operationId:Guid,ct:CancellationToken)=task {
        if not(gitId headSha)||bundle.Length=0 then return Error "github-candidate-publication-binding-refused" else
        let! published=publisher.Publish(target.Repository,branchRef,expectedHead,headSha,bundle,ct)
        match published with
        | Error reason->return Error reason
        | Ok observed when observed<>headSha->return Error "github-branch-publication-readback-refused"
        | Ok _->
            let name=branchRef.Replace("refs/heads/","")
            let! response=send RestMethod.Get $"git/ref/heads/{Uri.EscapeDataString name}" None None ct
            match response with
            | Error reason->return Error reason
            | Ok value->match parse value.Body with Error reason->return Error reason|Ok document->use document=document in let observed=document.RootElement.GetProperty("object").GetProperty("sha").GetString() in return if observed=headSha then Ok(branchRef,defaultArg value.ETag observed) else Error "github-branch-publication-readback-refused" }
    member _.CreatePullRequest(branchRef:string,headSha:string,operationId:Guid,ct:CancellationToken)=task {
        if target.RoutineOperation<>"internal-docs" then return Error "github-routine-profile-unsupported" else
        let! existing=readPull branchRef ct
        match existing with
        | Error reason->return Error reason
        | Ok(Some value) when value.HeadSha=headSha&&value.BaseRef=target.BaseRef&&value.BaseRepository=target.Repository->return Ok(value.NodeId,value.HeadSha)
        | Ok(Some _)->return Error "github-pull-request-binding-conflict"
        | Ok None->
            let head=branchRef.Replace("refs/heads/","")
            let marker=$"<!-- fsgg:routine-development/v1 head={headSha} operation={target.RoutineOperation} -->"
            let fields=Dictionary<string,obj>()
            fields["title"]<-"Standalone telemetry Main pilot"
            fields["head"]<-head
            fields["base"]<-target.BaseRef
            fields["body"]<-"Bounded routine documentation delivery pilot.\n\n"+marker
            let body=JsonSerializer.Serialize fields
            let! created=send RestMethod.Post "pulls" (Some body) (Some(operationId.ToString "N")) ct
            let! observed=readPull branchRef ct
            return match created,observed with _,Ok(Some value) when value.HeadSha=headSha&&value.BaseRef=target.BaseRef&&value.BaseRepository=target.Repository->Ok(value.NodeId,value.HeadSha)|Error _,Ok _->Error "github-pull-request-outcome-unknown"|_,Ok _->Error "github-pull-request-not-observed"|_,Error reason->Error reason }
    member _.ReadBranch(branchRef:string,headSha:string,ct:CancellationToken)=task {
        let name=branchRef.Replace("refs/heads/","")
        let! response=send RestMethod.Get $"git/ref/heads/{Uri.EscapeDataString name}" None None ct
        match response with
        | Error reason->return Error reason
        | Ok value->match parse value.Body with Error reason->return Error reason|Ok document->use document=document in let observed=document.RootElement.GetProperty("object").GetProperty("sha").GetString() in return if observed=headSha then Ok(branchRef,defaultArg value.ETag observed) else Error "github-branch-head-not-observed" }
    member _.ReadPullRequest(branchRef:string,headSha:string,ct:CancellationToken)=task {
        let! found=readPull branchRef ct
        return match found with Ok(Some value) when value.HeadSha=headSha&&value.BaseRef=target.BaseRef&&value.BaseRepository=target.Repository->Ok(value.NodeId,value.HeadSha,value.Revision)|Ok _->Error "github-pull-request-not-observed"|Error reason->Error reason }
    member _.CheckProtectedHead(branchRef:string,headSha:string,ct:CancellationToken)=task {
        if target.RoutineOperation<>"internal-docs" then return Error "github-routine-profile-unsupported" else
        let! found=readPull branchRef ct
        match found with
        | Ok(Some value) when value.State="open"&&value.HeadSha=headSha&&value.BaseRef=target.BaseRef&&value.BaseRepository=target.Repository->
            let markers=routineMarker.Matches value.Body
            if markers.Count<>1 || markers[0].Groups["head"].Value<>headSha || markers[0].Groups["operation"].Value<>target.RoutineOperation then return Error "github-routine-marker-refused" else
            let! detailResponse=send RestMethod.Get $"pulls/{value.Number}" None None ct
            let nativeEligible=
                match detailResponse with
                | Error reason->Error reason
                | Ok response->
                    try
                        use document=JsonDocument.Parse response.Body
                        let root=document.RootElement
                        let boolean (name:string) (expected:JsonValueKind)=match root.TryGetProperty name with true,item when item.ValueKind=expected->true|_->false
                        let mergeState=property "mergeable_state" root
                        let head=property "sha" (root.GetProperty("head"))
                        let baseRef=property "ref" (root.GetProperty("base"))
                        if boolean "draft" JsonValueKind.False && boolean "mergeable" JsonValueKind.True
                           && (mergeState=Some "clean"||mergeState=Some "unstable"||mergeState=Some "has_hooks")
                           && head=Some headSha && baseRef=Some target.BaseRef then Ok()
                        else Error "github-native-mergeability-refused"
                    with :? JsonException->Error "github-native-mergeability-refused"
            let! policyResponse=send RestMethod.Get $"contents/.fsgg/routine-development.json?ref={value.BaseSha}" None None ct
            let policy=
                match policyResponse with
                | Error reason->Error reason
                | Ok response->
                    try
                        use document=JsonDocument.Parse response.Body
                        let root=document.RootElement
                        let encoded=property "content" root|>Option.map(fun text->text.Replace("\n",""))
                        match encoded with
                        | None->Error "github-routine-policy-refused"
                        | Some content->
                            use policy=JsonDocument.Parse(Convert.FromBase64String content)
                            let value=policy.RootElement
                            match property "schema" value,value.TryGetProperty "allowedOperations" with
                            | Some "fsgg.routine-development-policy/v1",(true,items) when items.ValueKind=JsonValueKind.Array->
                                let allowed=items.EnumerateArray()|>Seq.choose(fun item->if item.ValueKind=JsonValueKind.String then Option.ofObj(item.GetString()) else None)|>Set.ofSeq
                                if allowed.Contains target.RoutineOperation then Ok() else Error "github-routine-operation-refused"
                            | _->Error "github-routine-policy-refused"
                    with
                    | :? JsonException
                    | :? FormatException -> Error "github-routine-policy-refused"
            let! protectionOutcome=executor.Send(request RestMethod.Get $"branches/{Uri.EscapeDataString target.BaseRef}/protection/required_status_checks" None None,ct)
            let required=
                match protectionOutcome with
                | Response response when response.StatusCode=404->Ok(Set.singleton "routine-eligibility")
                | Response response when response.StatusCode>=200&&response.StatusCode<300->
                    match parse response.Body with
                    | Ok document->
                        use document=document
                        match document.RootElement.TryGetProperty "checks" with
                        | true,items when items.ValueKind=JsonValueKind.Array->items.EnumerateArray()|>Seq.choose(property "context")|>Set.ofSeq|>Set.add "routine-eligibility"|>Ok
                        | _->Error "github-protected-check-policy-refused"
                    | Error reason->Error reason
                | Response response->Error($"github-status-{response.StatusCode}")
                | NetworkFailure->Error "github-network-unknown"
                | TimedOut->Error "github-timeout-unknown"
            let! checks=send RestMethod.Get $"commits/{headSha}/check-runs?per_page=100" None None ct
            match nativeEligible,policy,required,checks with
            | Error reason,_,_,_->return Error reason
            | _,Error reason,_,_->return Error reason
            | _,_,Error reason,_->return Error reason
            | _,_,_,Error reason->return Error reason
            | _,_,Ok _,Ok response when paginated response.Headers->return Error "github-check-census-incomplete"
            | Ok(),Ok(),Ok expected,Ok response->
                match parse response.Body with
                | Error reason->return Error reason
                | Ok document->
                    use document=document
                    let latest=
                        match document.RootElement.TryGetProperty "check_runs" with
                        | true,items when items.ValueKind=JsonValueKind.Array->
                            items.EnumerateArray()
                            |>Seq.choose(fun (item:JsonElement)->
                                match property "name" item,item.TryGetProperty "id",property "head_sha" item with
                                | Some name,(true,id),Some observedHead when id.ValueKind=JsonValueKind.Number&&observedHead=headSha->Some(name,id.GetInt64(),property "status" item,property "conclusion" item)
                                | _->None)
                            |>Seq.groupBy(fun(name,_,_,_)->name)
                            |>Seq.map(fun(name,values)->name,values|>Seq.maxBy(fun(_,id,_,_)->id))
                            |>Map.ofSeq
                        | _->Map.empty
                    let checksGreen=expected|>Set.forall(fun name->match latest|>Map.tryFind name with Some(_,_,Some "completed",Some "success")->true|_->false)
                    if not checksGreen then return Error "github-required-checks-not-green" else
                    // This production profile is deliberately bounded to the
                    // selected routine-documentation pilot. Base-loaded policy,
                    // its exact-head routine-eligibility result, native required
                    // contexts and GitHub's merge boundary remain authoritative.
                    // Source-change/coherent/reuse profiles are unsupported here
                    // rather than partially reimplemented.
                    return Ok(value.Number,value.NodeId,value.Revision)
        | Ok _->return Error "github-pull-request-not-open-at-head"
        | Error reason->return Error reason }
    member this.Merge(branchRef:string,headSha:string,operationId:Guid,ct:CancellationToken)=task {
        let! ready=this.CheckProtectedHead(branchRef,headSha,ct)
        match ready with
        | Error reason->return Error reason
        | Ok _->return! this.MergeAuthorized(branchRef,headSha,operationId,ct) }
    member this.MergeAuthorized(branchRef:string,headSha:string,operationId:Guid,ct:CancellationToken)=task {
        let! found=readPull branchRef ct
        match found with
        | Ok(Some value) when value.State="open"&&value.HeadSha=headSha&&value.BaseRef=target.BaseRef&&value.BaseRepository=target.Repository->
            let body=JsonSerializer.Serialize {|sha=headSha;merge_method="squash"|}
            let! _=send RestMethod.Put $"pulls/{value.Number}/merge" (Some body) (Some(operationId.ToString "N")) ct
            return! this.ReadDelivery(branchRef,headSha,ct)
        | Ok _->return Error "github-pull-request-not-open-at-head"
        | Error reason->return Error reason }
    member _.ReadDelivery(branchRef:string,expectedHead:string,ct:CancellationToken)=task {
        let! found=readPull branchRef ct
        match found with
        | Ok(Some value) when value.State="closed"&&value.Merged&&value.HeadSha=expectedHead&&value.BaseRef=target.BaseRef&&value.BaseRepository=target.Repository&&(value.MergeCommitSha|>Option.exists gitId)->return Ok(value.NodeId,value.MergeCommitSha.Value,value.Revision)
        | Ok _->return Error "github-native-delivery-not-observed"
        | Error reason->return Error reason }
