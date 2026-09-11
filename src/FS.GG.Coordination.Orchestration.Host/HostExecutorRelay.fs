namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open FS.GG.Coordination.Orchestration.Runner.Protocol

type ExecutorRelayRequest = { CommandId:Guid; Frames:byte array list }

type private RelayEntry =
    { Request:ExecutorRelayRequest
      Completion:TaskCompletionSource<Result<ExecutorTransportReadback,string>> }

type private CompletedRelayEntry =
    { RequestDigest:string
      Result:Result<ExecutorTransportReadback,string>
      ResultDigest:string }

/// Host-owned pull/response rendezvous. The SystemAdmin launcher initiates the
/// authenticated connection to Host, polls a bounded closed request, relays it to
/// `podman exec -i`, and posts the exact response frames. There is no listener or
/// persistent broker in the runner/container trust domain.
[<Sealed>]
type HostExecutorRelay(maximumPending:int,maximumAggregateBytes:int) =
    let syncRoot=obj()
    let pending=Dictionary<Guid,RelayEntry>()
    let order=Queue<Guid>()
    let completed=Dictionary<Guid,CompletedRelayEntry>()
    let completedOrder=Queue<Guid>()
    let available=new SemaphoreSlim(0)
    let commandId (frames:byte array list) =
        frames|>List.tryLast|>Option.bind(fun bytes->ExecutorWire.parseCommandV2 bytes|>Result.toOption)|>Option.map _.CommandId
    let bounded (frames:byte array list) =
        not(List.isEmpty frames) && frames.Length<=64
        && frames|>List.forall(fun value->not(isNull value)&&value.Length>0&&value.Length<=2*ExecutorWire.maximumContentBytes)
        && (frames|>List.sumBy(fun value->int64 value.Length))<=int64 maximumAggregateBytes
    let digest (frames:byte array list) =
        use hash=IncrementalHash.CreateHash HashAlgorithmName.SHA256
        frames|>List.iter(fun value->hash.AppendData value)
        hash.GetHashAndReset()|>Convert.ToHexString|>_.ToLowerInvariant()
    let sameFrames (left:byte array list) (right:byte array list) =
        left.Length=right.Length
        && List.forall2 (fun (left:byte array) (right:byte array)->left.AsSpan().SequenceEqual(right.AsSpan())) left right
    let remember id requestDigest result resultDigest =
        completed[id]<-{RequestDigest=requestDigest;Result=result;ResultDigest=resultDigest}
        completedOrder.Enqueue id
        while completed.Count>maximumPending do completed.Remove(completedOrder.Dequeue())|>ignore
    member _.Poll(cancellationToken:CancellationToken) = task {
        let mutable found=None
        while found.IsNone && not cancellationToken.IsCancellationRequested do
            do! available.WaitAsync cancellationToken
            lock syncRoot (fun () ->
                if order.Count>0 then
                    let id=order.Dequeue()
                    match pending.TryGetValue id with
                    | true,entry -> found<-Some entry.Request;order.Enqueue id;available.Release()|>ignore
                    | _ -> ())
        return found }
    member _.Complete(commandId:Guid,frames:byte array list) =
        if not(bounded frames) then Error "executor-relay-response-bounds-refused"
        else
            let result=Ok{Frames=frames}
            let resultDigest=digest frames
            lock syncRoot (fun () ->
                match pending.TryGetValue commandId with
                | true,entry ->
                    pending.Remove commandId|>ignore
                    remember commandId (digest entry.Request.Frames) result resultDigest
                    entry.Completion.TrySetResult result|>ignore
                    Ok()
                | _ ->
                    match completed.TryGetValue commandId with
                    | true,entry when entry.ResultDigest=resultDigest -> Ok()
                    | true,_ -> Error "executor-relay-response-identity-conflict"
                    | _ -> Error "executor-relay-command-not-pending")
    member _.Fail(commandId:Guid,reason:string) =
        if String.IsNullOrWhiteSpace reason || reason.Length>1024 then Error "executor-relay-failure-refused"
        else
            lock syncRoot (fun () ->
                match pending.TryGetValue commandId with
                | true,entry ->
                    let result=Error reason
                    pending.Remove commandId|>ignore
                    remember commandId (digest entry.Request.Frames) result ("error:"+reason)
                    entry.Completion.TrySetResult result|>ignore
                    Ok()
                | _ ->
                    match completed.TryGetValue commandId with
                    | true,entry when entry.ResultDigest=("error:"+reason) -> Ok()
                    | true,_ -> Error "executor-relay-response-identity-conflict"
                    | _ -> Error "executor-relay-command-not-pending")
    member _.PendingCount=lock syncRoot (fun ()->pending.Count)
    interface IAuthenticatedExecutorTransport with
        member _.Exchange(frames,cancellationToken)=task {
            if maximumPending<1 || maximumPending>64 || maximumAggregateBytes<1 || maximumAggregateBytes>150*1024*1024 || not(bounded frames) then return Error "executor-relay-request-bounds-refused"
            else
                match commandId frames with
                | None -> return Error "executor-relay-command-refused"
                | Some id ->
                    let entry={Request={CommandId=id;Frames=frames};Completion=TaskCompletionSource<Result<ExecutorTransportReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)}
                    let selected =
                        lock syncRoot (fun () ->
                            match completed.TryGetValue id with
                            | true,doneEntry when doneEntry.RequestDigest=digest frames -> Choice1Of3 doneEntry.Result
                            | true,_ -> Choice2Of3 "executor-relay-command-identity-conflict"
                            | _ ->
                                match pending.TryGetValue id with
                                | true,current when sameFrames current.Request.Frames frames -> Choice3Of3 current
                                | true,_ -> Choice2Of3 "executor-relay-command-identity-conflict"
                                | _ when pending.Count>=maximumPending -> Choice2Of3 "executor-relay-capacity-refused"
                                | _ -> pending.Add(id,entry);order.Enqueue id;available.Release()|>ignore;Choice3Of3 entry)
                    match selected with
                    | Choice1Of3 result -> return result
                    | Choice2Of3 error -> return Error error
                    | Choice3Of3 current ->
                        try return! current.Completion.Task.WaitAsync cancellationToken
                        with :? OperationCanceledException -> return Error "executor-relay-response-unknown" }
