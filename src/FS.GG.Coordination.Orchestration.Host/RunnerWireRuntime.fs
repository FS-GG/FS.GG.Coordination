namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Runner.Protocol

type RunnerWireStore = { WorkItems:IJournalStore; Candidates:ICandidateStore }

[<RequireQualifiedAccess>]
module RunnerWireRuntime =
    let private derivedCommandId (seed:Guid) suffix =
        let bytes=Encoding.UTF8.GetBytes($"{seed:D}:{suffix}") |> SHA256.HashData
        Guid(ReadOnlySpan(bytes,0,16))

    let private activeAttempt (state:State) sessionId =
        state.Attempts |> Map.tryPick(fun _ attempt -> if Id.sessionValue attempt.SessionId=sessionId then Some attempt else None)

    let private validateIdentity now workItemId sessionId runnerId principal fingerprint generation expectedRevision issuedAt expiresAt (state:State) =
        let persistenceId=WorkItemIdentity.persistenceId workItemId
        if issuedAt>now || expiresAt<now || expiresAt<=issuedAt then Error "runner-envelope-expired-or-invalid"
        elif generation<>Id.generationValue state.Generation then Error "runner-authority-stale"
        elif state.Control<>Running || not state.ReadbackCurrent then Error "runner-dispatch-paused-or-readback-stale"
        elif state.Budget |> Option.exists(fun budget -> budget.Deadline>=now && expiresAt<=budget.Deadline) |> not then Error "runner-budget-or-deadline-refused"
        else
            match activeAttempt state sessionId with
            | Some attempt when Id.runnerValue attempt.Runner.RunnerId=runnerId
                                && attempt.Runner.PrincipalId=principal
                                && attempt.Runner.FingerprintSha256=fingerprint
                                && attempt.Runner.ExpiresAt>=expiresAt
                                && attempt.Status=Active
                                && state.Sessions |> Map.tryFind attempt.SessionId |> Option.exists(fun session -> not session.Closed) -> Ok(persistenceId,attempt)
            | _ -> Error "runner-enrollment-mismatch"

    let private recover (store:RunnerWireStore) workItemId token = HostedWriterJournal.recover store.WorkItems workItemId token

    let private priorReceipt (state:State) commandId expectedRevision generation principal sessionId issuedAt expiresAt command =
        let envelope=
            { CommandId=Id.command commandId;ProtocolVersion=Id.protocolVersion 1 0;ExpectedRevision=Id.revision expectedRevision
              ExpectedGeneration=Id.generation generation;PrincipalId=principal;SessionId=Some(Id.session sessionId)
              IssuedAt=issuedAt;ExpiresAt=expiresAt;Command=command }
        match Map.tryFind envelope.CommandId state.CommandReceipts with
        | Some receipt when receipt.BodySha256=canonicalEnvelopeSha256 envelope -> Some true
        | Some _ -> Some false
        | None -> None

    let private apply (clock:TimeProvider) store workItemId principal sessionId commandId expectedRevision issuedAt expiresAt command token = task {
        let! current=recover store workItemId token
        match current with
        | Error failures -> return Error(sprintf "%A" failures)
        | Ok recovered ->
            let envelope=
                { CommandId=Id.command commandId;ProtocolVersion=Id.protocolVersion 1 0
                  ExpectedRevision=Id.revision expectedRevision;ExpectedGeneration=recovered.State.Generation
                  PrincipalId=principal;SessionId=Some(Id.session sessionId);IssuedAt=issuedAt;ExpiresAt=expiresAt;Command=command }
            let! result=HostedWriterJournal.decideAndAppend clock store.WorkItems workItemId envelope token
            match result with
            | Ok(decision,_) when decision.Receipt.Disposition=ReceiptDisposition.Accepted || decision.Receipt.Disposition=ReceiptDisposition.Duplicate -> return Ok()
            | Ok(decision,_) -> return Error decision.Receipt.Detail
            | Error reason -> return Error reason }

    let private assignmentFrom state persistenceId (attempt:Attempt) clientSequence =
        match state.HostedRoute,state.Budget,state.Sessions |> Map.tryFind attempt.SessionId with
        | Some route,Some budget,Some session ->
            match Map.tryFind route.ProcessOperationId state.Operations with
            | Some(Dispatching intent) when intent.Kind=DispatchRunner && route.AttemptId=attempt.AttemptId ->
                let value=
                    { Schema=RunnerWire.assignmentSchema;WorkItemPersistenceId=persistenceId;RouteId=route.RouteId
                      AttemptId=Id.attemptValue attempt.AttemptId;CandidateId=Id.candidateValue route.CandidateId
                      SessionId=Id.sessionValue attempt.SessionId;RunnerId=Id.runnerValue attempt.Runner.RunnerId
                      PrincipalId=attempt.Runner.PrincipalId;FingerprintSha256=attempt.Runner.FingerprintSha256
                      Generation=Id.generationValue state.Generation;WorkflowRevision=Id.revisionValue route.WorkflowRevision
                      ClientSequence=clientSequence;ServerSequence=session.LastServerSequence;PayloadSha256=intent.PayloadSha256
                      AssignmentSha256="";ExpiresAt=attempt.Runner.ExpiresAt;Deadline=budget.Deadline }
                Ok {value with AssignmentSha256=RunnerWire.assignmentDigest value}
            | _ -> Error "runner-dispatch-intent-not-current"
        | _ -> Error "runner-route-budget-or-session-missing"

    let poll (clock:TimeProvider) store workItemId (request:RunnerPollRequest) token = task {
        let now=clock.GetUtcNow()
        let! recovered=recover store workItemId token
        match recovered with
        | Error failures -> return Error(sprintf "%A" failures)
        | Ok current when request.Schema<>RunnerWire.pollSchema || request.WorkItemPersistenceId<>current.PersistenceId -> return Error "runner-poll-schema-or-work-item-refused"
        | Ok current ->
            match validateIdentity now workItemId request.SessionId request.RunnerId request.PrincipalId request.FingerprintSha256 request.Generation request.ExpectedRevision request.IssuedAt request.ExpiresAt current.State with
            | Error reason -> return Error reason
            | Ok(_,attempt) ->
                let! accepted=apply clock store workItemId request.PrincipalId request.SessionId request.CommandId request.ExpectedRevision request.IssuedAt request.ExpiresAt (AcceptRunnerMessage(Id.session request.SessionId,request.ClientSequence,true)) token
                match accepted with
                | Error reason -> return Error reason
                | Ok() ->
                    let! refreshed=recover store workItemId token
                    match refreshed with
                    | Error failures -> return Error(sprintf "%A" failures)
                    | Ok value -> return assignmentFrom value.State value.PersistenceId attempt request.ClientSequence }

    let private assignmentBeforeClientMessage state persistenceId attempt clientSequence = assignmentFrom state persistenceId attempt (clientSequence-1L)

    let private acknowledgedAssignment assignmentSha (state:State) =
        state.HostedRoute
        |> Option.bind(fun route -> Map.tryFind route.ProcessOperationId state.HostedEffectReadbacks)
        |> Option.exists(fun readback -> readback.Exists && readback.ProviderRevision=$"runner-ack:{assignmentSha}")

    let private validCandidateRequest now (request:RunnerCandidateRequest) =
        let validGitObject value =
            not(String.IsNullOrWhiteSpace value) && (value.Length=40 || value.Length=64)
            && value |> Seq.forall Char.IsAsciiHexDigit
        RunnerWire.validSha256 request.ContentSha256
        && RunnerWire.validSha256 request.ManifestSha256
        && validGitObject request.BaselineSha
        && validGitObject request.HeadSha
        && validGitObject request.TreeSha
        && Set.contains request.MediaType (Set.ofList ["application/vnd.git.bundle";"application/zip";"application/zstd"])
        && request.SizeBytes>=0L && request.SizeBytes<=104857600L
        && request.RetainUntil>now && request.RetainUntil<=now.AddDays 90.

    let acknowledge (clock:TimeProvider) store workItemId (request:RunnerAckRequest) token = task {
        let now=clock.GetUtcNow()
        let! recovered=recover store workItemId token
        match recovered with
        | Error failures -> return Error(sprintf "%A" failures)
        | Ok current when request.Schema<>RunnerWire.ackSchema || request.WorkItemPersistenceId<>current.PersistenceId || not(RunnerWire.validSha256 request.AssignmentSha256) -> return Error "runner-ack-schema-or-work-item-refused"
        | Ok current ->
            let runnerCommand=AcceptRunnerMessage(Id.session request.SessionId,request.ClientSequence,false)
            match priorReceipt current.State request.CommandId request.ExpectedRevision request.Generation request.PrincipalId request.SessionId request.IssuedAt request.ExpiresAt runnerCommand with
            | Some false -> return Error "runner-command-identity-conflict"
            | commandReceipt ->
              if commandReceipt=Some true && acknowledgedAssignment request.AssignmentSha256 current.State then return Ok()
              else
                match validateIdentity now workItemId request.SessionId request.RunnerId request.PrincipalId request.FingerprintSha256 request.Generation request.ExpectedRevision request.IssuedAt request.ExpiresAt current.State with
                | Error reason -> return Error reason
                | Ok(_,attempt) ->
                    let assignmentValid =
                        if commandReceipt=Some true then
                            acknowledgedAssignment request.AssignmentSha256 current.State
                            || (current.State.HostedRoute |> Option.exists(fun route ->
                                Map.tryFind route.ProcessOperationId current.State.Operations
                                |> Option.exists(function Dispatching _ | NeedsObservation _ -> true | _ -> false)))
                        else assignmentBeforeClientMessage current.State current.PersistenceId attempt request.ClientSequence
                             |> Result.exists(fun assignment -> assignment.AssignmentSha256=request.AssignmentSha256)
                    if not assignmentValid then return Error "runner-assignment-digest-mismatch"
                    else
                        let! accepted =
                            match commandReceipt with
                            | Some true -> Task.FromResult(Ok())
                            | _ -> apply clock store workItemId request.PrincipalId request.SessionId request.CommandId request.ExpectedRevision request.IssuedAt request.ExpiresAt runnerCommand token
                        match accepted with
                        | Error reason -> return Error reason
                        | Ok() ->
                            let! refreshed=recover store workItemId token
                            match refreshed with
                            | Error failures -> return Error(sprintf "%A" failures)
                            | Ok current ->
                                if acknowledgedAssignment request.AssignmentSha256 current.State then return Ok()
                                else
                                    let route=current.State.HostedRoute.Value
                                    let readback=
                                        { OperationId=route.ProcessOperationId;RouteId=route.RouteId;AttemptId=route.AttemptId;CandidateId=route.CandidateId
                                          RepositoryNodeId=route.RepositoryNodeId;ProviderResourceId=string(Id.attemptValue route.AttemptId)
                                          CandidateHeadSha=None;ResultSha=None;ProviderRevision=$"runner-ack:{request.AssignmentSha256}"
                                          Generation=route.Generation;WorkflowRevision=route.WorkflowRevision;ObservedAt=request.IssuedAt;Exists=true }
                                    let commandId=derivedCommandId request.CommandId "dispatch-readback"
                                    return! apply clock store workItemId request.PrincipalId request.SessionId commandId (Id.revisionValue current.State.Revision) request.IssuedAt request.ExpiresAt (RecordHostedEffectReadback(route.ProcessOperationId,readback)) token }

    let submitCandidate (clock:TimeProvider) store workItemId (request:RunnerCandidateRequest) token = task {
        let now=clock.GetUtcNow()
        let! recovered=recover store workItemId token
        match recovered with
        | Error failures -> return Error(sprintf "%A" failures)
        | Ok current when request.Schema<>RunnerWire.candidateSchema || request.WorkItemPersistenceId<>current.PersistenceId || not(RunnerWire.validSha256 request.AssignmentSha256) || not(validCandidateRequest now request) -> return Error "runner-candidate-schema-or-work-item-refused"
        | Ok current ->
            let runnerCommand=AcceptRunnerMessage(Id.session request.SessionId,request.ClientSequence,false)
            match priorReceipt current.State request.CommandId request.ExpectedRevision request.Generation request.PrincipalId request.SessionId request.IssuedAt request.ExpiresAt runnerCommand with
            | Some false -> return Error "runner-command-identity-conflict"
            | commandReceipt ->
              match validateIdentity now workItemId request.SessionId request.RunnerId request.PrincipalId request.FingerprintSha256 request.Generation request.ExpectedRevision request.IssuedAt request.ExpiresAt current.State with
              | Error reason -> return Error reason
              | Ok(_,attempt) ->
                match current.State.HostedRoute with
                | None -> return Error "runner-route-missing"
                | Some route when Id.candidateValue route.CandidateId<>request.CandidateId -> return Error "runner-candidate-identity-mismatch"
                | Some route when current.State.HostedEffectReadbacks |> Map.tryFind route.ProcessOperationId |> Option.exists(fun readback -> readback.ProviderRevision=$"runner-ack:{request.AssignmentSha256}") |> not -> return Error "runner-assignment-digest-mismatch"
                | Some route ->
                    match Map.tryFind route.CandidateOperationId current.State.Operations with
                    | Some(Dispatching intent) | Some(NeedsObservation(intent,_)) | Some(OperationState.Settled(intent,Applied _)) when intent.Kind=StoreCandidate ->
                        let bytes = try Convert.FromBase64String request.ContentBase64 with _ -> Array.empty
                        let candidate=
                            { CandidateId=route.CandidateId;BaselineSha=request.BaselineSha;HeadSha=request.HeadSha;TreeSha=request.TreeSha
                              ManifestSha256=request.ManifestSha256;ContentSha256=request.ContentSha256;MediaType=request.MediaType
                              SizeBytes=request.SizeBytes;RetainUntil=request.RetainUntil;Location=ContentAddressedObject($"sha256/{request.ContentSha256}") }
                        if bytes.LongLength<>request.SizeBytes || RunnerWire.sha256 bytes<>request.ContentSha256 then return Error "runner-candidate-bytes-refused"
                        else
                            let! stored=store.Candidates.Put({Candidate=candidate;Bytes=bytes},token)
                            let receipt=
                                match stored with
                                | Ok receipt | Error(Existing receipt) -> Some receipt
                                | _ -> None
                            match receipt with
                            | None -> return Error(sprintf "runner-candidate-storage-refused:%A" stored)
                            | Some receipt ->
                                let! readBack=store.Candidates.Read(route.CandidateId,token)
                                match readBack with
                                | Error reason -> return Error($"runner-candidate-readback-refused:{reason}")
                                | Ok durable when durable.Bytes<>bytes || durable.Candidate<>candidate -> return Error "runner-candidate-readback-mismatch"
                                | Ok _ ->
                                    let! accepted =
                                        match commandReceipt with
                                        | Some true -> Task.FromResult(Ok())
                                        | _ -> apply clock store workItemId request.PrincipalId request.SessionId request.CommandId request.ExpectedRevision request.IssuedAt request.ExpiresAt runnerCommand token
                                    match accepted with
                                    | Error reason -> return Error reason
                                    | Ok() ->
                                        let readback=
                                            { OperationId=route.CandidateOperationId;RouteId=route.RouteId;AttemptId=route.AttemptId;CandidateId=route.CandidateId
                                              RepositoryNodeId=route.RepositoryNodeId;ProviderResourceId=string request.CandidateId
                                              CandidateHeadSha=Some request.HeadSha;ResultSha=Some request.ContentSha256
                                              ProviderRevision=$"candidate:{receipt.StorageReceiptSha256}";Generation=route.Generation
                                              WorkflowRevision=route.WorkflowRevision;ObservedAt=request.IssuedAt;Exists=true }
                                        let! afterClient=recover store workItemId token
                                        let currentAfterClient=afterClient |> Result.map(fun value -> value.State) |> Result.toOption
                                        let readbackAlreadyAccepted =
                                            currentAfterClient |> Option.bind(fun state -> Map.tryFind route.CandidateOperationId state.HostedEffectReadbacks)
                                            |> Option.exists(fun existing -> existing.CandidateHeadSha=Some request.HeadSha && existing.ResultSha=Some request.ContentSha256 && existing.ProviderRevision=readback.ProviderRevision)
                                        let! effectRecorded =
                                            if readbackAlreadyAccepted then Task.FromResult(Ok())
                                            else
                                                let revisionAfterClient=currentAfterClient |> Option.map(fun state -> Id.revisionValue state.Revision) |> Option.defaultValue -1L
                                                apply clock store workItemId request.PrincipalId request.SessionId (derivedCommandId request.CommandId "candidate-readback") revisionAfterClient request.IssuedAt request.ExpiresAt (RecordHostedEffectReadback(route.CandidateOperationId,readback)) token
                                        match effectRecorded with
                                        | Error reason -> return Error reason
                                        | Ok() ->
                                            let! afterEffect=recover store workItemId token
                                            let stateAfterEffect=afterEffect |> Result.map(fun value -> value.State) |> Result.toOption
                                            match stateAfterEffect |> Option.bind(fun state -> Map.tryFind route.CandidateId state.Candidates) with
                                            | Some existing when existing=candidate -> return Ok()
                                            | Some _ -> return Error "runner-candidate-identity-conflict"
                                            | None ->
                                                let revisionAfterEffect=stateAfterEffect |> Option.map(fun state -> Id.revisionValue state.Revision) |> Option.defaultValue -1L
                                                return! apply clock store workItemId request.PrincipalId request.SessionId (derivedCommandId request.CommandId "candidate-record") revisionAfterEffect request.IssuedAt request.ExpiresAt (RecordCandidate(candidate,receipt)) token
                    | _ -> return Error "runner-candidate-intent-not-current" }
