namespace FS.GG.Coordination.Orchestration.Runner.Protocol

open System

[<CLIMutable>]
type ExecutorCommand =
    { Schema:string; CommandId:Guid; BodySha256:string; Kind:string
      WorkItemPersistenceId:string; RouteOperationId:Guid; AssignmentId:Guid; AttemptId:Guid; CandidateId:Guid
      Generation:int64; ExpectedRevision:int64; Deadline:DateTimeOffset; MaximumRuntimeSeconds:int64; MaximumAttempts:int
      Workspace:string; RequestedModel:string; RequestedEffort:string; InputDigest:string; ExecutorBinding:string
      ContentOffset:int64; ContentLength:int }
[<CLIMutable>]
type ExecutorReceipt =
    { Schema:string; CommandId:Guid; BodySha256:string; Disposition:string; DurableRevision:int64; ProcessCreationObserved:bool; Detail:string }
[<CLIMutable>]
type ExecutorContent = { Schema:string; CommandId:Guid; InputDigest:string; Offset:int64; Final:bool; ContentBase64:string }
[<CLIMutable>]
type ExecutorInputManifest = { Schema:string; InputDigest:string; MediaType:string; SizeBytes:int64; ChunkBytes:int }
[<CLIMutable>]
type ExecutorReference = { Kind:string; Reference:string; Digest:string }
[<CLIMutable>]
type ExecutorUsage = { Name:string; State:string; Value:Nullable<int64>; UnitName:string; Provenance:string }
[<CLIMutable>]
type ExecutorResponse =
    { Schema:string; CommandId:Guid; BodySha256:string; Kind:string
      Provider:string; AdapterVersion:string; AuthenticationState:string; AuthenticationProvenance:string; SupportsResume:bool
      ProviderSessionReference:string; Lifecycle:string; RequestedModel:string; RequestedEffort:string; ResolvedModel:string; ResolvedEffort:string
      Output:ExecutorReference array; LifecycleReferences:ExecutorReference array; Usage:ExecutorUsage array
      InvocationCostState:string; InvocationCostAmount:Nullable<decimal>; InvocationCostCurrency:string; InvocationCostProvenance:string
      BroaderCostState:string; BroaderCostAmount:Nullable<decimal>; BroaderCostCurrency:string; BroaderCostProvenance:string
      CandidateId:Guid; CandidateHeadSha:string; CandidateTreeSha:string; ObservedAt:DateTimeOffset; Detail:string }

[<RequireQualifiedAccess>]
module ExecutorWire =
    let commandSchema="fsgg.orchestration.executor-command/1"
    let receiptSchema="fsgg.orchestration.executor-receipt/1"
    let contentSchema="fsgg.orchestration.executor-content/1"
    let inputManifestSchema="fsgg.orchestration.executor-input-manifest/1"
    let responseSchema="fsgg.orchestration.executor-response/1"
    let maximumControlBytes=32*1024
    let maximumContentBytes=1024*1024
    let private commandKinds=set ["readiness";"launch";"observe";"reconcile";"cancel";"content-read"]
    let private dispositions=set ["persisted";"duplicate";"conflict";"refused";"observed"]
    let private commandProperties=set ["schema";"commandId";"bodySha256";"kind";"workItemPersistenceId";"routeOperationId";"assignmentId";"attemptId";"candidateId";"generation";"expectedRevision";"deadline";"maximumRuntimeSeconds";"maximumAttempts";"workspace";"requestedModel";"requestedEffort";"inputDigest";"executorBinding";"contentOffset";"contentLength"]
    let private receiptProperties=set ["schema";"commandId";"bodySha256";"disposition";"durableRevision";"processCreationObserved";"detail"]
    let private contentProperties=set ["schema";"commandId";"inputDigest";"offset";"final";"contentBase64"]
    let private manifestProperties=set ["schema";"inputDigest";"mediaType";"sizeBytes";"chunkBytes"]
    let private responseProperties=set ["schema";"commandId";"bodySha256";"kind";"provider";"adapterVersion";"authenticationState";"authenticationProvenance";"supportsResume";"providerSessionReference";"lifecycle";"requestedModel";"requestedEffort";"resolvedModel";"resolvedEffort";"output";"lifecycleReferences";"usage";"invocationCostState";"invocationCostAmount";"invocationCostCurrency";"invocationCostProvenance";"broaderCostState";"broaderCostAmount";"broaderCostCurrency";"broaderCostProvenance";"candidateId";"candidateHeadSha";"candidateTreeSha";"observedAt";"detail"]
    let private validText maximum (value:string)=not(String.IsNullOrWhiteSpace value)&&value=value.Trim()&&value.Length<=maximum
    let private validGuid value=value<>Guid.Empty
    let private closed<'T> properties maximumBytes bytes=RunnerWire.deserializeClosed<'T> properties maximumBytes bytes
    let commandDigest (value:ExecutorCommand)=RunnerWire.serialize {value with BodySha256=""}|>RunnerWire.sha256
    let encodeCommand (value:ExecutorCommand)=RunnerWire.serialize value
    let parseCommand bytes=
        closed<ExecutorCommand> commandProperties maximumControlBytes bytes
        |> Result.bind(fun value->
            if value.Schema<>commandSchema then Error "executor-command-schema-refused"
            elif not(validGuid value.CommandId&&validGuid value.RouteOperationId&&validGuid value.AssignmentId&&validGuid value.AttemptId&&validGuid value.CandidateId)||value.AssignmentId<>value.RouteOperationId then Error "executor-command-identity-refused"
            elif not(RunnerWire.validSha256 value.BodySha256)||commandDigest value<>value.BodySha256 then Error "executor-command-digest-refused"
            elif not(commandKinds.Contains value.Kind) then Error "executor-command-kind-refused"
            elif value.Generation<0L||value.ExpectedRevision<1L||value.Deadline=DateTimeOffset.MinValue||value.MaximumRuntimeSeconds<1L||value.MaximumRuntimeSeconds>1800L||value.MaximumAttempts<>1 then Error "executor-command-authority-refused"
            elif not(validText 512 value.WorkItemPersistenceId&&validText 256 value.ExecutorBinding&&validText 4096 value.Workspace) then Error "executor-command-binding-refused"
            elif not(RunnerWire.validSha256 value.InputDigest) then Error "executor-command-input-refused"
            elif value.ContentOffset<0L||value.ContentLength<0||value.ContentLength>maximumContentBytes then Error "executor-command-content-bounds-refused"
            else Ok value)
    let encodeReceipt (value:ExecutorReceipt)=RunnerWire.serialize value
    let parseReceipt bytes=closed<ExecutorReceipt> receiptProperties maximumControlBytes bytes|>Result.bind(fun value->if value.Schema=receiptSchema&&validGuid value.CommandId&&RunnerWire.validSha256 value.BodySha256&&dispositions.Contains value.Disposition&&value.DurableRevision>=0L&&not(isNull value.Detail)&&value.Detail.Length<=1024 then Ok value else Error "executor-receipt-refused")
    let encodeContent (value:ExecutorContent)=RunnerWire.serialize value
    let parseContent bytes=closed<ExecutorContent> contentProperties (2*maximumContentBytes) bytes|>Result.bind(fun value->if value.Schema<>contentSchema||not(validGuid value.CommandId)||not(RunnerWire.validSha256 value.InputDigest)||value.Offset<0L||isNull value.ContentBase64 then Error "executor-content-refused" else try let content=Convert.FromBase64String value.ContentBase64 in if content.Length>maximumContentBytes then Error "executor-content-size-refused" else Ok(value,content) with :? FormatException->Error "executor-content-base64-refused")
    let encodeInputManifest (value:ExecutorInputManifest)=RunnerWire.serialize value
    let parseInputManifest bytes=closed<ExecutorInputManifest> manifestProperties maximumControlBytes bytes|>Result.bind(fun value->if value.Schema=inputManifestSchema&&RunnerWire.validSha256 value.InputDigest&&validText 128 value.MediaType&&value.SizeBytes>=0L&&value.SizeBytes<=16L*1024L*1024L&&value.ChunkBytes>0&&value.ChunkBytes<=maximumContentBytes then Ok value else Error "executor-input-manifest-refused")
    let encodeResponse (value:ExecutorResponse)=RunnerWire.serialize value
    let parseResponse bytes=
        closed<ExecutorResponse> responseProperties maximumControlBytes bytes
        |> Result.bind(fun value->
            let validRefs (items:ExecutorReference array)=not(isNull items)&&items.Length<=64&&items|>Array.forall(fun item->not(isNull(box item))&&validText 64 item.Kind&&validText 2048 item.Reference&&(isNull item.Digest||RunnerWire.validSha256 item.Digest))
            let validUsage=not(isNull value.Usage)&&value.Usage.Length<=64&&value.Usage|>Array.forall(fun item->not(isNull(box item))&&validText 64 item.Name&&validText 256 item.Provenance&&((item.State="observed"&&item.Value.HasValue&&item.Value.Value>=0L&&validText 64 item.UnitName)||((item.State="unknown"||item.State="not-applicable")&&not item.Value.HasValue&&isNull item.UnitName)))
            let validOptionalText maximum text=isNull text || validText maximum text
            let validCost state (amount:Nullable<decimal>) currency provenance =
                validText 256 provenance
                && match state with
                   | "known" -> amount.HasValue && amount.Value>=0M && validText 16 currency
                   | "unknown" | "not-applicable" -> not amount.HasValue && isNull currency
                   | _ -> false
            let kindOk=(set ["readiness";"session-observation";"refusal"]).Contains value.Kind
            let authOk=(set ["authenticated";"not-authenticated";"unknown"]).Contains value.AuthenticationState
            let lifecycleOk =
                match value.Kind with
                | "readiness" -> (set ["ready";"not-ready"]).Contains value.Lifecycle && isNull value.ProviderSessionReference && value.CandidateId=Guid.Empty && isNull value.CandidateHeadSha && isNull value.CandidateTreeSha
                | "refusal" -> value.Lifecycle="refused" && isNull value.ProviderSessionReference && value.CandidateId=Guid.Empty && isNull value.CandidateHeadSha && isNull value.CandidateTreeSha
                | "session-observation" ->
                    (set ["starting";"running";"cancelling";"succeeded";"failed";"cancelled";"deadline-exceeded";"outcome-unknown"]).Contains value.Lifecycle
                    && validText 512 value.ProviderSessionReference
                    && ((value.CandidateId=Guid.Empty && isNull value.CandidateHeadSha && isNull value.CandidateTreeSha)
                        || (value.CandidateId<>Guid.Empty && RunnerWire.validSha256 value.CandidateHeadSha && RunnerWire.validSha256 value.CandidateTreeSha))
                | _ -> false
            if value.Schema = responseSchema
               && validGuid value.CommandId
               && RunnerWire.validSha256 value.BodySha256
               && kindOk
               && validText 128 value.Provider
               && validText 128 value.AdapterVersion
               && validText 256 value.AuthenticationProvenance
               && authOk
               && lifecycleOk
               && validOptionalText 128 value.RequestedModel
               && validOptionalText 128 value.RequestedEffort
               && validOptionalText 128 value.ResolvedModel
               && validOptionalText 128 value.ResolvedEffort
               && validRefs value.Output
               && validRefs value.LifecycleReferences
               && validUsage
               && validCost value.InvocationCostState value.InvocationCostAmount value.InvocationCostCurrency value.InvocationCostProvenance
               && validCost value.BroaderCostState value.BroaderCostAmount value.BroaderCostCurrency value.BroaderCostProvenance
               && value.ObservedAt<>DateTimeOffset.MinValue
               && not (isNull value.Detail)
               && value.Detail.Length <= 1024 then Ok value
            else Error "executor-response-refused")
