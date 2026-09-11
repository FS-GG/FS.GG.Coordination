namespace FS.GG.Coordination.Orchestration.Runner.Protocol

open System
open System.Text.Json

[<CLIMutable>]
type ExecutorCommand =
    { Schema:string; CommandId:Guid; BodySha256:string; Kind:string
      WorkItemPersistenceId:string; RouteOperationId:Guid; AssignmentId:Guid; AttemptId:Guid
      CandidateId:Guid; Generation:int64; ExpectedRevision:int64; Deadline:DateTimeOffset
      InputDigest:string; ExecutorBinding:string; ContentOffset:int64; ContentLength:int }

[<CLIMutable>]
type ExecutorReceipt =
    { Schema:string; CommandId:Guid; BodySha256:string; Disposition:string
      DurableRevision:int64; ProcessCreationObserved:bool; Detail:string }

[<CLIMutable>]
type ExecutorContent =
    { Schema:string; CommandId:Guid; InputDigest:string; Offset:int64; Final:bool; ContentBase64:string }

[<RequireQualifiedAccess>]
module ExecutorWire =
    let commandSchema = "fsgg.orchestration.executor-command/1"
    let receiptSchema = "fsgg.orchestration.executor-receipt/1"
    let contentSchema = "fsgg.orchestration.executor-content/1"
    let maximumControlBytes = 32 * 1024
    let maximumContentBytes = 1024 * 1024
    let private commandKinds = set ["readiness";"launch";"observe";"reconcile";"cancel";"content-read"]
    let private dispositions = set ["persisted";"duplicate";"conflict";"refused";"observed"]
    let private commandProperties = set ["schema";"commandId";"bodySha256";"kind";"workItemPersistenceId";"routeOperationId";"assignmentId";"attemptId";"candidateId";"generation";"expectedRevision";"deadline";"inputDigest";"executorBinding";"contentOffset";"contentLength"]
    let private receiptProperties = set ["schema";"commandId";"bodySha256";"disposition";"durableRevision";"processCreationObserved";"detail"]
    let private contentProperties = set ["schema";"commandId";"inputDigest";"offset";"final";"contentBase64"]
    let private validText maximum (value:string) = not(String.IsNullOrWhiteSpace value) && value=value.Trim() && value.Length<=maximum
    let private validGuid value = value<>Guid.Empty
    let private closed<'T> properties maximumBytes (bytes:byte array) =
        RunnerWire.deserializeClosed<'T> properties maximumBytes bytes
    let commandDigest (value:ExecutorCommand) =
        RunnerWire.serialize { value with BodySha256="" } |> RunnerWire.sha256
    let encodeCommand (value:ExecutorCommand) = RunnerWire.serialize value
    let parseCommand bytes =
        closed<ExecutorCommand> commandProperties maximumControlBytes bytes
        |> Result.bind(fun value ->
            if value.Schema<>commandSchema then Error "executor-command-schema-refused"
            elif not(validGuid value.CommandId && validGuid value.RouteOperationId && validGuid value.AssignmentId && validGuid value.AttemptId)
                 || value.AssignmentId<>value.RouteOperationId then Error "executor-command-identity-refused"
            elif not(RunnerWire.validSha256 value.BodySha256) || commandDigest value<>value.BodySha256 then Error "executor-command-digest-refused"
            elif not(commandKinds.Contains value.Kind) then Error "executor-command-kind-refused"
            elif value.Generation<0L || value.ExpectedRevision<0L || value.Deadline=DateTimeOffset.MinValue then Error "executor-command-authority-refused"
            elif not(validText 512 value.WorkItemPersistenceId && validText 256 value.ExecutorBinding) then Error "executor-command-binding-refused"
            elif not(RunnerWire.validSha256 value.InputDigest) then Error "executor-command-input-refused"
            elif value.ContentOffset<0L || value.ContentLength<0 || value.ContentLength>maximumContentBytes then Error "executor-command-content-bounds-refused"
            else Ok value)
    let encodeReceipt (value:ExecutorReceipt) = RunnerWire.serialize value
    let parseReceipt bytes =
        closed<ExecutorReceipt> receiptProperties maximumControlBytes bytes
        |> Result.bind(fun value ->
            if value.Schema<>receiptSchema || not(validGuid value.CommandId) || not(RunnerWire.validSha256 value.BodySha256)
               || not(dispositions.Contains value.Disposition) || value.DurableRevision<0L || isNull value.Detail || value.Detail.Length>1024
            then Error "executor-receipt-refused" else Ok value)
    let encodeContent (value:ExecutorContent) = RunnerWire.serialize value
    let parseContent bytes =
        closed<ExecutorContent> contentProperties (2*maximumContentBytes) bytes
        |> Result.bind(fun value ->
            if value.Schema<>contentSchema || not(validGuid value.CommandId) || not(RunnerWire.validSha256 value.InputDigest) || value.Offset<0L
            then Error "executor-content-refused"
            else
                try
                    let content=Convert.FromBase64String value.ContentBase64
                    if content.Length>maximumContentBytes then Error "executor-content-size-refused" else Ok(value,content)
                with :? FormatException -> Error "executor-content-base64-refused")
