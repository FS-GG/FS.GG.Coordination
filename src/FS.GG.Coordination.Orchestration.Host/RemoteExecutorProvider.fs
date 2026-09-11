namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence

type ExecutorRouteBinding =
    { WorkItemPersistenceId:string; RouteOperationId:Guid; CandidateId:Guid; ExpectedRevision:int64
      ExecutorBinding:string; WorkspaceManifest:ExecutorWorkspaceManifest
      InputManifest:ExecutorInputManifest; InputBytes:byte array }

type ExecutorTransportReadback =
    { Frames:byte array list }

/// Authenticated loopback relay to the selected rootless runner. Implementations
/// receive no PostgreSQL credential and grant no GitHub-delivery role.
type IAuthenticatedExecutorTransport =
    abstract Exchange: frames:byte array list * CancellationToken -> Task<Result<ExecutorTransportReadback,string>>

type IExecutorBindingResolver =
    abstract ResolveReadiness: CancellationToken -> Task<Result<LaunchIntent * ExecutorRouteBinding,string>>
    abstract Resolve: LaunchIntent * CancellationToken -> Task<Result<ExecutorRouteBinding,string>>
    abstract ResolveSession: ProviderSessionReference * CancellationToken -> Task<Result<LaunchIntent * ExecutorRouteBinding,string>>

[<RequireQualifiedAccess>]
module RemoteCandidatePipeline =
    let store (candidateStore:ICandidateStore) expectedCandidate expectedBaseline retainUntil (readback:ExecutorTransportReadback) cancellationToken = task {
        let artifactManifest=readback.Frames|>List.tryFind(fun bytes->ExecutorWire.parseArtifactManifest bytes|>Result.isOk)
        let artifactChunks=readback.Frames|>List.filter(fun bytes->ExecutorWire.parseArtifactContent bytes|>Result.isOk)
        match artifactManifest with
        | None -> return Error "executor-artifact-manifest-missing"
        | Some bytes ->
            match ExecutorWire.parseArtifactManifest bytes with
            | Error reason -> return Error reason
            | Ok manifest when manifest.CandidateId<>expectedCandidate || manifest.BaselineObjectId<>expectedBaseline -> return Error "executor-artifact-identity-refused"
            | Ok manifest ->
                let decoded=artifactChunks|>List.map ExecutorWire.parseArtifactContent
                match decoded|>List.tryPick(function Error reason->Some reason|_->None) with
                | Some reason -> return Error reason
                | None ->
                    let ordered=decoded|>List.choose(function Ok(value,chunk)->Some(value,chunk)|_->None)|>List.sortBy(fun (value,_)->value.Offset)
                    let mutable offset=0L
                    let output=ResizeArray<byte>()
                    let mutable valid=true
                    for value,chunk in ordered do
                        if value.CandidateId<>manifest.CandidateId || value.BundleSha256<>manifest.BundleSha256 || value.Offset<>offset then valid<-false
                        else output.AddRange chunk;offset<-offset+int64 chunk.Length
                    let bundle=output.ToArray()
                    let finalPresent=ordered|>List.tryLast|>Option.exists(fun (value,_)->value.Final)
                    if not valid || offset<>manifest.BundleSizeBytes || RunnerWire.sha256 bundle<>manifest.BundleSha256 || not finalPresent then return Error "executor-artifact-content-refused"
                    else
                        let candidate={CandidateId=Id.candidate manifest.CandidateId;BaselineSha=manifest.BaselineObjectId;HeadSha=manifest.HeadObjectId;TreeSha=manifest.TreeObjectId;ManifestSha256=manifest.ManifestSha256;ContentSha256=manifest.BundleSha256;MediaType="application/vnd.fsgg.runner-candidate+zip";SizeBytes=manifest.BundleSizeBytes;RetainUntil=retainUntil;Location=ContentAddressedObject($"sha256/{manifest.BundleSha256}")}
                        let! put=candidateStore.Put({Candidate=candidate;Bytes=bundle},cancellationToken)
                        let receipt=match put with Ok value|Error(Existing value)->Some value|_->None
                        match receipt with
                        | None -> return Error(sprintf "candidate-storage-refused:%A" put)
                        | Some receipt ->
                            let! verified=candidateStore.Read(candidate.CandidateId,cancellationToken)
                            return match verified with Ok value when value.Candidate=candidate && value.Bytes=bundle->Ok(candidate,receipt)|_->Error "candidate-storage-readback-refused" }

[<Sealed>]
type RemoteExecutorProvider(store:IExecutorCommandStore,resolver:IExecutorBindingResolver,transport:IAuthenticatedExecutorTransport) =
    let nullableText value=if String.IsNullOrWhiteSpace value then None else Some value
    let observation (response:ExecutorResponse) =
        match ProviderSessionReference.create response.ProviderSessionReference with
        | Error reason -> Error reason
        | Ok session ->
            let lifecycle =
                match response.Lifecycle with
                | "starting" -> Some SessionLifecycle.Starting | "running" -> Some SessionLifecycle.Running | "cancelling" -> Some SessionLifecycle.Cancelling
                | "succeeded" -> Some SessionLifecycle.Succeeded | "failed" -> Some SessionLifecycle.Failed | "cancelled" -> Some SessionLifecycle.Cancelled
                | "deadline-exceeded" -> Some SessionLifecycle.DeadlineExceeded | "outcome-unknown" -> Some SessionLifecycle.OutcomeUnknown | _ -> None
            match lifecycle with
            | None -> Error "executor-lifecycle-refused"
            | Some lifecycle ->
                let usage=response.Usage|>Array.map(fun item->item.Name,(match item.State with "observed"->UsageKnown(item.Value.Value,item.UnitName,item.Provenance)|"not-applicable"->UsageNotApplicable item.Provenance|_->UsageUnknown item.Provenance))|>Map.ofArray
                let cost=match response.InvocationCostState with "known"->CostKnown(response.InvocationCostAmount.Value,response.InvocationCostCurrency,response.InvocationCostProvenance)|"not-applicable"->CostNotApplicable response.InvocationCostProvenance|_->CostUnknown response.InvocationCostProvenance
                let refs (values:ExecutorReference array) : OutputReference list =
                    values|>Array.map(fun value -> ({Kind=value.Kind;Reference=value.Reference;Digest=nullableText value.Digest}:OutputReference))|>List.ofArray
                let candidate=if response.CandidateId=Guid.Empty then None else Some{CandidateId=response.CandidateId;HeadSha=response.CandidateHeadSha;TreeSha=response.CandidateTreeSha}
                Ok {Provider={Provider=response.Provider;AdapterVersion=response.AdapterVersion};Session=session
                    Resolved={Model=nullableText response.ResolvedModel;Effort=nullableText response.ResolvedEffort};Lifecycle=lifecycle
                    Output=refs response.Output;LifecycleReferences=refs response.LifecycleReferences;Usage={Values=usage;Cost=cost}
                    Candidate=candidate;ObservedAt=response.ObservedAt}
    let command kind (intent:LaunchIntent) (binding:ExecutorRouteBinding) session =
        let identity=System.Text.Encoding.UTF8.GetBytes($"{intent.Key.AssignmentId:D}:{intent.Key.AttemptId:D}:{intent.Key.Generation}:{binding.ExpectedRevision}:{kind}:{session}")|>System.Security.Cryptography.SHA256.HashData
        let value={Schema=ExecutorWire.commandSchemaV2;CommandId=Guid(ReadOnlySpan(identity,0,16));BodySha256="";Kind=kind;WorkItemPersistenceId=binding.WorkItemPersistenceId;RouteOperationId=binding.RouteOperationId;AssignmentId=intent.Key.AssignmentId;AttemptId=intent.Key.AttemptId;CandidateId=binding.CandidateId;Generation=intent.Key.Generation;ExpectedRevision=binding.ExpectedRevision;RecordedAt=intent.RecordedAt;Deadline=intent.Limits.Deadline;MaximumRuntimeSeconds=int64 intent.Limits.MaximumRuntime.TotalSeconds;MaximumAttempts=intent.Limits.MaximumAttempts;Workspace=intent.Workspace;WorkspaceManifestSha256=(RunnerWire.serialize binding.WorkspaceManifest|>RunnerWire.sha256);RequestedModel=Option.toObj intent.Requested.Model;RequestedEffort=Option.toObj intent.Requested.Effort;InputDigest=intent.InputDigest;ExecutorBinding=binding.ExecutorBinding;ProviderSessionReference=session;ArtifactDigest=null;ContentOffset=0L;ContentLength=0}
        {value with BodySha256=ExecutorWire.commandV2Digest value}
    let invoke kind intent binding session token = task {
        let commandValue=command kind intent binding session
        let commandBytes=ExecutorWire.encodeCommandV2 commandValue
        let! inputPrepared=store.StageInput(ExecutorWire.encodeInputManifest binding.InputManifest,binding.InputBytes,token)
        let! manifestPrepared=store.StageWorkspaceManifest(ExecutorWire.encodeWorkspaceManifest binding.WorkspaceManifest,token)
        let expectedManifest=RunnerWire.serialize binding.WorkspaceManifest|>RunnerWire.sha256
        let! persisted =
            match inputPrepared,manifestPrepared with
            | Ok(),Ok digest when digest=expectedManifest -> store.PersistCommand(commandBytes,token)
            | _ -> Task.FromResult(CommandRefused "executor-bound-input-preparation-refused")
        match persisted with
        | CommandConflict | CommandRefused _ -> return Error "executor-command-persistence-refused"
        | CommandPersisted _ | CommandDuplicate _ ->
            let frames=if kind="launch" then [ExecutorWire.encodeInputManifest binding.InputManifest;ExecutorWire.encodeContent {Schema=ExecutorWire.contentSchema;CommandId=commandValue.CommandId;InputDigest=intent.InputDigest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String binding.InputBytes};ExecutorWire.encodeWorkspaceManifest binding.WorkspaceManifest;commandBytes] else [commandBytes]
            let! exchanged=transport.Exchange(frames,token)
            match exchanged with
            | Error reason -> return Error reason
            | Ok value ->
                let receipts=value.Frames|>List.choose(fun bytes->ExecutorWire.parseReceipt bytes|>Result.toOption)
                let outcome=value.Frames|>List.choose(fun bytes->ExecutorWire.parseOperationOutcome bytes|>Result.toOption)|>List.tryLast
                let response=value.Frames|>List.choose(fun bytes->ExecutorWire.parseResponse bytes|>Result.toOption)|>List.tryLast
                let bound id body=id=commandValue.CommandId && body=commandValue.BodySha256
                let valid=receipts|>List.forall(fun item->bound item.CommandId item.BodySha256) && outcome|>Option.forall(fun item->bound item.CommandId item.BodySha256) && response|>Option.forall(fun item->bound item.CommandId item.BodySha256)
                let terminal=value.Frames|>List.tryLast
                if not valid || terminal.IsNone then return Error "executor-readback-binding-refused"
                else
                    let! settled=store.SettleCommand(commandValue.CommandId,terminal.Value,token)
                    return settled |> Result.map(fun ()->outcome,response,value) }
    let readiness intent binding token = task {
        let value=command "readiness" intent binding null
        let bytes=ExecutorWire.encodeCommandV2 value
        let frames=[ExecutorWire.encodeInputManifest binding.InputManifest;ExecutorWire.encodeContent {Schema=ExecutorWire.contentSchema;CommandId=value.CommandId;InputDigest=intent.InputDigest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String binding.InputBytes};ExecutorWire.encodeWorkspaceManifest binding.WorkspaceManifest;bytes]
        let! exchanged=transport.Exchange(frames,token)
        match exchanged with
        | Error reason -> return Error reason
        | Ok readback ->
            match readback.Frames|>List.choose(fun bytes->ExecutorWire.parseResponse bytes|>Result.toOption)|>List.tryLast with
            | Some response when response.CommandId=value.CommandId && response.BodySha256=value.BodySha256 -> return Ok response
            | _ -> return Error "executor-readiness-binding-refused" }
    interface IExecutionProvider with
        member _.ObserveReadiness token=task {
            let! resolved=resolver.ResolveReadiness token
            match resolved with
            | Error reason -> return {Identity={Provider="remote-execution-provider";AdapterVersion="1"};Authentication=AuthenticationUnknown reason;SupportsResume=false;ObservedAt=DateTimeOffset.UtcNow}
            | Ok(intent,binding) ->
                let! invoked=readiness intent binding token
                match invoked with
                | Error reason -> return {Identity={Provider="remote-execution-provider";AdapterVersion="1"};Authentication=AuthenticationUnknown reason;SupportsResume=false;ObservedAt=DateTimeOffset.UtcNow}
                | Ok response ->
                    let authentication=match response.AuthenticationState with "authenticated"->Authenticated response.AuthenticationProvenance|"not-authenticated"->NotAuthenticated response.AuthenticationProvenance|_->AuthenticationUnknown response.AuthenticationProvenance
                    return {Identity={Provider=response.Provider;AdapterVersion=response.AdapterVersion};Authentication=authentication;SupportsResume=response.SupportsResume;ObservedAt=response.ObservedAt} }
        member _.Launch(intent,token)=task {
            let! resolved=resolver.Resolve(intent,token)
            match resolved with
            | Error reason -> return LaunchRefused reason
            | Ok binding ->
                let! staged=store.StageInput(ExecutorWire.encodeInputManifest binding.InputManifest,binding.InputBytes,token)
                match staged with
                | Error reason -> return LaunchRefused reason
                | Ok() ->
                    let! manifest=store.StageWorkspaceManifest(ExecutorWire.encodeWorkspaceManifest binding.WorkspaceManifest,token)
                    match manifest with
                    | Error reason -> return LaunchRefused reason
                    | Ok digest when digest<>(RunnerWire.serialize binding.WorkspaceManifest|>RunnerWire.sha256) -> return LaunchRefused "executor-workspace-manifest-digest-refused"
                    | Ok _ ->
                        let! invoked=invoke "launch" intent binding null token
                        match invoked with
                        | Error reason -> return LaunchAmbiguous reason
                        | Ok(_,Some response,_) ->
                            match observation response with Ok value->return LaunchStarted value|Error reason->return LaunchAmbiguous reason
                        | Ok(Some outcome,None,_) when outcome.Disposition="refused" -> return LaunchRefused outcome.Reason
                        | Ok(Some outcome,None,_) -> return LaunchAmbiguous outcome.Reason
                        | Ok(None,None,_) -> return LaunchAmbiguous "executor-launch-terminal-readback-missing" }
        member _.Reconcile(intent,token)=task {
            let! resolved=resolver.Resolve(intent,token)
            match resolved with
            | Error reason -> return ReconcileUnknown reason
            | Ok binding ->
                let! invoked=invoke "reconcile" intent binding null token
                match invoked with
                | Error reason -> return ReconcileUnknown reason
                | Ok(_,Some response,_) -> match observation response with Ok value->return Reconciled value|Error reason->return ReconcileUnknown reason
                | Ok(Some outcome,None,_) when outcome.Disposition="confirmed-absent" -> return ConfirmedAbsent
                | Ok(Some outcome,None,_) -> return ReconcileUnknown outcome.Reason
                | Ok(None,None,_) -> return ReconcileUnknown "executor-reconcile-terminal-readback-missing" }
        member _.Observe(session,token)=task {
            let! resolved=resolver.ResolveSession(session,token)
            match resolved with
            | Error reason -> return Error reason
            | Ok(intent,binding) ->
                let! invoked=invoke "observe" intent binding (ProviderSessionReference.value session) token
                return invoked|>Result.bind(fun (_,response,_)->match response with Some value->observation value|None->Error "executor-observation-missing") }
        member _.Cancel(session,token)=task {
            let! resolved=resolver.ResolveSession(session,token)
            match resolved with
            | Error reason -> return CancelUnknown reason
            | Ok(intent,binding) ->
                let! invoked=invoke "cancel" intent binding (ProviderSessionReference.value session) token
                match invoked with
                | Error reason -> return CancelUnknown reason
                | Ok(Some outcome,_,_) ->
                    return match outcome.Disposition with "accepted"->CancelAccepted|"refused"->CancelRefused outcome.Reason|_->CancelUnknown outcome.Reason
                | Ok(None,_,_) -> return CancelUnknown "executor-cancel-terminal-readback-missing" }
