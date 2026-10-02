namespace FS.GG.FourD.Typed

open System
open System.IO
open System.Text.Json

module Program =
    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let private readBounded path =
        let info = FileInfo path
        if not info.Exists || info.Length <= 0L || info.Length > 1048576L then invalidArg "input" "typed-input-refused"
        File.ReadAllBytes path
    let private rejectDuplicateProperties (bytes: byte array) =
        let mutable reader = Utf8JsonReader(ReadOnlySpan bytes, JsonReaderOptions(MaxDepth = 32))
        let scopes = Collections.Generic.Stack<Collections.Generic.HashSet<string>>()
        while reader.Read() do
            match reader.TokenType with
            | JsonTokenType.StartObject -> scopes.Push(Collections.Generic.HashSet(StringComparer.Ordinal))
            | JsonTokenType.EndObject -> scopes.Pop() |> ignore
            | JsonTokenType.PropertyName ->
                if scopes.Count = 0 || not (scopes.Peek().Add(reader.GetString())) then
                    invalidArg "input" "typed-json-duplicate-refused"
            | _ -> ()
    let private requiredString (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> invalidArg name "typed-string-refused"
    let private requiredInt (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
        | _ -> invalidArg name "typed-integer-refused"
    let private requiredInt64 (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt64()
        | _ -> invalidArg name "typed-integer-refused"
    let private requiredBool (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True -> true
        | true, value when value.ValueKind = JsonValueKind.False -> false
        | _ -> invalidArg name "typed-boolean-refused"
    let private parsePhase = function
        | "idle" -> Idle | "acquired" -> Acquired | "validated" -> Validated | "admitted" -> Admitted
        | "effect" -> Effect | "cleanup" -> Cleanup | "finished" -> Finished | "cleanup-failed" -> CleanupFailed
        | _ -> invalidArg "phase" "typed-phase-refused"
    let private phaseText = function
        | Idle -> "idle" | Acquired -> "acquired" | Validated -> "validated" | Admitted -> "admitted"
        | Effect -> "effect" | Cleanup -> "cleanup" | Finished -> "finished" | CleanupFailed -> "cleanup-failed"
    let private outcomeText = function NoneObserved -> "none" | Unknown -> "unknown" | Success -> "success" | Refused -> "refused"

    let private readState (root: JsonElement) =
        let expected = Set ["schema"; "phase"; "acquiredIdentity"; "validatedIdentity"; "admittedIdentity"
                            "currentIdentity"; "effectAcknowledged"; "outcome"; "owned"; "closed"; "cancelled"
                            "budgetRemaining"; "effectEligible"; "cleanupComplete"; "successful"]
        let actual = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        if actual <> expected then invalidArg "state" "typed-state-shape-refused"
        let strings (name: string) =
            let values = root.GetProperty(name).EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
            if values |> List.exists (fun value -> isNull value || not (Policy.validToken value))
               || values.Length <> (Set.ofList values).Count then invalidArg name "typed-resource-refused"
            Set.ofList values
        let optional (name: string) =
            let value=root.GetProperty name
            if value.ValueKind=JsonValueKind.Null then None
            elif value.ValueKind=JsonValueKind.String && Policy.validToken(value.GetString()) then Some(value.GetString())
            else invalidArg name "typed-identity-refused"
        let state = { Phase = parsePhase (requiredString root "phase"); AcquiredIdentity = optional "acquiredIdentity";
          ValidatedIdentity = optional "validatedIdentity"; AdmittedIdentity = optional "admittedIdentity";
          CurrentIdentity = optional "currentIdentity"; EffectAcknowledged = requiredBool root "effectAcknowledged";
          Outcome = (match requiredString root "outcome" with "success" -> Success | "unknown" -> Unknown | "refused" -> Refused | "none" -> NoneObserved | _ -> invalidArg "outcome" "typed-outcome-refused");
          Owned = strings "owned"; Closed = strings "closed"; Cancelled = requiredBool root "cancelled";
          BudgetRemaining = requiredInt root "budgetRemaining" }
        if requiredString root "schema" <> "fsgg.fourd.typed-operation-state/1"
           || requiredBool root "effectEligible" <> Policy.effectEligible state
           || requiredBool root "cleanupComplete" <> Policy.cleanupComplete state
           || requiredBool root "successful" <> Policy.successful state
           || not (Policy.validateState state) then
            invalidArg "state" "typed-state-refused"
        state

    let private writeState state =
        {| schema = "fsgg.fourd.typed-operation-state/1"; phase = phaseText state.Phase
           acquiredIdentity = Option.toObj state.AcquiredIdentity; validatedIdentity = Option.toObj state.ValidatedIdentity
           admittedIdentity = Option.toObj state.AdmittedIdentity; currentIdentity = Option.toObj state.CurrentIdentity
           effectAcknowledged = state.EffectAcknowledged; outcome = outcomeText state.Outcome
           owned = state.Owned |> Set.toArray; closed = state.Closed |> Set.toArray
           cancelled = state.Cancelled; budgetRemaining = state.BudgetRemaining
           effectEligible = Policy.effectEligible state; cleanupComplete = Policy.cleanupComplete state
           successful = Policy.successful state |}

    let private fields (root: JsonElement) = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
    let private requireFields expected root =
        if fields root <> expected then invalidArg "input" "typed-run-shape-refused"
    let private optionalString (root: JsonElement) (name: string) =
        let value = root.GetProperty name
        if value.ValueKind = JsonValueKind.Null then None
        elif value.ValueKind = JsonValueKind.String then Some(value.GetString())
        else invalidArg name "typed-string-refused"

    let private readExpected (root: JsonElement) =
        requireFields (Set ["rootJoinIdentity";"repository";"repositoryId";"runId";"workflowPath";"event";
                            "headBranch";"headSha";"originalActorId";"triggeringActorId";"producerSha256";"packetManifestSha256"]) root
        { RootJoinIdentity=requiredString root "rootJoinIdentity"; Repository=requiredString root "repository"
          RepositoryId=requiredInt64 root "repositoryId"; RunId=requiredInt64 root "runId"
          WorkflowPath=requiredString root "workflowPath"; Event=requiredString root "event"
          HeadBranch=requiredString root "headBranch"; HeadSha=requiredString root "headSha"
          OriginalActorId=requiredInt64 root "originalActorId"; TriggeringActorId=requiredInt64 root "triggeringActorId"
          ProducerSha256=requiredString root "producerSha256"; PacketManifestSha256=requiredString root "packetManifestSha256" }

    let private readSnapshot (root: JsonElement) =
        requireFields (Set ["repository";"repositoryId";"runId";"attempt";"workflowPath";"event";
                            "headBranch";"headSha";"originalActorId";"triggeringActorId";"status";"conclusion"]) root
        { Repository=requiredString root "repository"; RepositoryId=requiredInt64 root "repositoryId"
          RunId=requiredInt64 root "runId"; Attempt=requiredInt root "attempt"
          WorkflowPath=requiredString root "workflowPath"; Event=requiredString root "event"
          HeadBranch=requiredString root "headBranch"; HeadSha=requiredString root "headSha"
          OriginalActorId=requiredInt64 root "originalActorId"; TriggeringActorId=requiredInt64 root "triggeringActorId"
          Status=requiredString root "status"; Conclusion=optionalString root "conclusion" }

    // GitHub response bodies cross this trusted serialized boundary as bytes.  F# performs
    // duplicate-key rejection and the semantic projection so Python cannot erase evidence.
    let private readApiSnapshot (root: JsonElement) =
        let repository=root.GetProperty "repository"
        let actor=root.GetProperty "actor"
        let triggering=root.GetProperty "triggering_actor"
        { Repository=requiredString repository "full_name"; RepositoryId=requiredInt64 repository "id"
          RunId=requiredInt64 root "id"; Attempt=requiredInt root "run_attempt"
          WorkflowPath=requiredString root "path"; Event=requiredString root "event"
          HeadBranch=requiredString root "head_branch"; HeadSha=requiredString root "head_sha"
          OriginalActorId=requiredInt64 actor "id"; TriggeringActorId=requiredInt64 triggering "id"
          Status=requiredString root "status"; Conclusion=optionalString root "conclusion" }

    let private readJobs (root: JsonElement) =
        requireFields (Set ["totalCount";"pageCount";"hasNextPage";"jobs"]) root
        let jobs = root.GetProperty("jobs").EnumerateArray() |> Seq.map (fun item ->
            requireFields (Set ["id";"runId";"attempt";"headSha";"name";"status";"conclusion"]) item
            { Id=requiredInt64 item "id"; RunId=requiredInt64 item "runId"; Attempt=requiredInt item "attempt"
              HeadSha=requiredString item "headSha"; Name=requiredString item "name"; Status=requiredString item "status"
              Conclusion=optionalString item "conclusion" }) |> Seq.toList
        { TotalCount=requiredInt root "totalCount"; PageCount=requiredInt root "pageCount"
          HasNextPage=requiredBool root "hasNextPage"; Jobs=jobs }

    let private readApiJobs (root: JsonElement) =
        let jobs=root.GetProperty("jobs").EnumerateArray() |> Seq.map (fun item ->
            { Id=requiredInt64 item "id"; RunId=requiredInt64 item "run_id"; Attempt=requiredInt item "run_attempt"
              HeadSha=requiredString item "head_sha"; Name=requiredString item "name"; Status=requiredString item "status"
              Conclusion=optionalString item "conclusion" }) |> Seq.toList
        { TotalCount=requiredInt root "total_count"; PageCount=1; HasNextPage=false; Jobs=jobs }

    let private rerunState = function
        | "not-issued" -> NotIssued | "intent-recorded" -> IntentRecorded
        | "acknowledged" -> Acknowledged | "uncertain" -> Uncertain
        | _ -> invalidArg "rerun" "typed-run-state-refused"
    let private cancelState = function
        | "none" -> NoCancel | "intent" -> CancelIntent | "accepted" -> CancelAccepted | "refused" -> CancelRefused
        | _ -> invalidArg "cancel" "typed-run-state-refused"
    let private settlementState = function
        | "none" -> NoSettlement | "current-terminal" -> CurrentTerminal | "target-terminal" -> TargetTerminal
        | "jobs-terminal" -> JobsTerminal | "settled" -> Settled
        | _ -> invalidArg "settlement" "typed-run-state-refused"
    let private rerunText = function NotIssued->"not-issued"|IntentRecorded->"intent-recorded"|Acknowledged->"acknowledged"|Uncertain->"uncertain"
    let private cancelText = function NoCancel->"none"|CancelIntent->"intent"|CancelAccepted->"accepted"|CancelRefused->"refused"
    let private settlementText = function NoSettlement->"none"|CurrentTerminal->"current-terminal"|TargetTerminal->"target-terminal"|JobsTerminal->"jobs-terminal"|Settled->"settled"

    let private readRunState (root: JsonElement) =
        requireFields (Set ["schema";"expected";"rerun";"seenTarget2";"latestSequence";"elapsedSeconds";
                            "nextObservationAt";"targetConclusion";"cancel";"settlement";"resourcesRetired";
                            "resultAccepted";"stickyCancelled";"cleanupStarted";"cleanupFreshCurrent";"refusal"]) root
        if requiredString root "schema" <> "fsgg.fourd.root-run-state/1" then invalidArg "schema" "typed-schema-refused"
        { Expected=readExpected (root.GetProperty "expected"); Rerun=rerunState(requiredString root "rerun")
          SeenTarget2=requiredBool root "seenTarget2"; LatestSequence=requiredInt root "latestSequence"
          ElapsedSeconds=requiredInt root "elapsedSeconds"; NextObservationAt=requiredInt root "nextObservationAt"
          TargetConclusion=optionalString root "targetConclusion"; Cancel=cancelState(requiredString root "cancel")
          Settlement=settlementState(requiredString root "settlement"); ResourcesRetired=requiredBool root "resourcesRetired"
          ResultAccepted=requiredBool root "resultAccepted"; StickyCancelled=requiredBool root "stickyCancelled"
          CleanupStarted=requiredBool root "cleanupStarted"; CleanupFreshCurrent=requiredBool root "cleanupFreshCurrent"
          Refusal=optionalString root "refusal" }

    let private writeExpected expected =
        {| rootJoinIdentity=expected.RootJoinIdentity; repository=expected.Repository; repositoryId=expected.RepositoryId
           runId=expected.RunId; workflowPath=expected.WorkflowPath; ``event``=expected.Event
           headBranch=expected.HeadBranch; headSha=expected.HeadSha; originalActorId=expected.OriginalActorId
           triggeringActorId=expected.TriggeringActorId; producerSha256=expected.ProducerSha256
           packetManifestSha256=expected.PacketManifestSha256 |}
    let private writeRunState state =
        {| schema="fsgg.fourd.root-run-state/1"; expected=writeExpected state.Expected; rerun=rerunText state.Rerun
           seenTarget2=state.SeenTarget2; latestSequence=state.LatestSequence; elapsedSeconds=state.ElapsedSeconds
           nextObservationAt=state.NextObservationAt; targetConclusion=Option.toObj state.TargetConclusion
           cancel=cancelText state.Cancel; settlement=settlementText state.Settlement
           resourcesRetired=state.ResourcesRetired; resultAccepted=state.ResultAccepted
           stickyCancelled=state.StickyCancelled; cleanupStarted=state.CleanupStarted
           cleanupFreshCurrent=state.CleanupFreshCurrent; refusal=Option.toObj state.Refusal |}

    let private runEvent (expected: RunExpectedIdentity) (root: JsonElement) =
        let kind=requiredString root "kind"
        let elapsed=requiredInt root "elapsedSeconds"
        let readResponse (response: JsonElement) expectedAttempt =
            requireFields (Set ["httpStatus";"bodyBase64";"linkHeaders";"endpointRunId";"endpointAttempt"]) response
            let endpointAttempt = requiredInt response "endpointAttempt"
            if requiredInt64 response "endpointRunId" <> expected.RunId || endpointAttempt <> expectedAttempt then invalidArg "response" "typed-endpoint-identity-refused"
            let links=response.GetProperty("linkHeaders").EnumerateArray() |> Seq.map (fun x -> x.GetString()) |> Seq.toList
            if links |> List.exists (fun x -> isNull x || x.Length > 2048) then invalidArg "response" "typed-header-refused"
            let bytes = try Convert.FromBase64String(requiredString response "bodyBase64") with _ -> invalidArg "response" "typed-body-refused"
            if bytes.Length > 524288 then invalidArg "response" "typed-body-refused"
            rejectDuplicateProperties bytes
            requiredInt response "httpStatus", bytes, links
        let readObserved expected ctor =
            requireFields expected root
            let status,bytes,_=readResponse (root.GetProperty "response") (if kind="observe-current" then 0 else 2)
            let snapshot =
                if status<>200 then None
                else
                    use doc=JsonDocument.Parse(bytes)
                    Some(readApiSnapshot doc.RootElement)
            ctor(elapsed,requiredInt root "sequence",status,snapshot)
        match kind with
        | "record-rerun-intent" -> requireFields (Set ["kind";"elapsedSeconds"]) root; RecordRerunIntent elapsed
        | "record-rerun-result" -> requireFields (Set ["kind";"elapsedSeconds";"acknowledged"]) root; RecordRerunResult(elapsed,requiredBool root "acknowledged")
        | "observe-current" -> readObserved (Set ["kind";"elapsedSeconds";"sequence";"response"]) ObserveCurrentRun
        | "observe-target" -> readObserved (Set ["kind";"elapsedSeconds";"sequence";"response"]) ObserveTargetRun
        | "observe-jobs" ->
            requireFields (Set ["kind";"elapsedSeconds";"sequence";"response"]) root
            let status,bytes,links=readResponse (root.GetProperty "response") 2
            let jobs =
                if status<>200 then None else
                    use doc=JsonDocument.Parse(bytes)
                    let parsed=readApiJobs doc.RootElement
                    Some { parsed with HasNextPage = links |> List.exists (fun x -> x.Contains("rel=\"next\"",StringComparison.OrdinalIgnoreCase)) }
            ObserveTargetJobs(elapsed,requiredInt root "sequence",status,jobs)
        | "enter-cleanup" -> requireFields (Set ["kind";"elapsedSeconds";"signalCancelled"]) root; EnterCleanup(elapsed,requiredBool root "signalCancelled")
        | "record-signal-cancellation" -> requireFields (Set ["kind";"elapsedSeconds"]) root; RecordSignalCancellation elapsed
        | "record-cancel-result" -> requireFields (Set ["kind";"elapsedSeconds";"accepted"]) root; RecordCancelResult(elapsed,requiredBool root "accepted")
        | "record-resources-retired" ->
            requireFields (Set ["kind";"elapsedSeconds";"facts"]) root
            let value=root.GetProperty "facts"
            requireFields (Set ["secretCount";"releaseCount";"assetStatus";"releaseStatus";"tagStatus";
                                "privateKeyAbsent";"publicKeyAbsent";"failureCount"]) value
            RecordResourcesRetired(elapsed,{ SecretCount=requiredInt value "secretCount"; ReleaseCount=requiredInt value "releaseCount";
                AssetStatus=requiredString value "assetStatus"; ReleaseStatus=requiredString value "releaseStatus";
                TagStatus=requiredString value "tagStatus"; PrivateKeyAbsent=requiredBool value "privateKeyAbsent";
                PublicKeyAbsent=requiredBool value "publicKeyAbsent"; FailureCount=requiredInt value "failureCount" })
        | "record-result-readback" -> requireFields (Set ["kind";"elapsedSeconds";"sealedEvidence";"acknowledged"]) root; RecordResultReadback(elapsed,requiredBool root "sealedEvidence",requiredBool root "acknowledged")
        | "exhaust-deadline" -> requireFields (Set ["kind";"elapsedSeconds"]) root; ExhaustDeadline elapsed
        | _ -> invalidArg "kind" "typed-run-event-refused"

    let private actionFields = function
        | IssueRerun -> "issue-rerun",null,null | ObserveCurrent -> "observe-current",null,null
        | ObserveTargetAndJobs -> "observe-target-and-jobs",null,null
        | Wait seconds -> "wait",box seconds,null | CancelOnce -> "cancel-once",null,null
        | RetireResources -> "retire-resources",null,null
        | ContinueResultReadback -> "continue-result-readback",null,null
        | Complete -> "complete",null,null
        | FinishRefused -> "finish-refused",null,null
        | Refuse reason -> "refuse",null,reason

    let private observeRun input output =
        let bytes=readBounded input
        rejectDuplicateProperties bytes
        use doc=JsonDocument.Parse(bytes,JsonDocumentOptions(MaxDepth=32))
        let root=doc.RootElement
        requireFields (Set ["schema";"lifecycleState";"runState";"expected";"event"]) root
        if requiredString root "schema" <> "fsgg.fourd.root-run-request/1" then invalidArg "schema" "typed-schema-refused"
        let lifecycle=readState(root.GetProperty "lifecycleState")
        let expected=readExpected(root.GetProperty "expected")
        let stateValue=root.GetProperty "runState"
        let state =
            if stateValue.ValueKind=JsonValueKind.Null then RunObservation.initial expected lifecycle |> function Ok value->value|Error reason->invalidArg "runState" reason
            else
                let value=readRunState stateValue
                if value.Expected<>expected then invalidArg "expected" "run-identity-rebind-refused"
                value
        match RunObservation.reduce lifecycle state (runEvent expected (root.GetProperty "event")) with
        | Error reason ->
            File.WriteAllText(output,JsonSerializer.Serialize({|accepted=false;refusal=reason|},options)+"\n");2
        | Ok decision ->
            let action,waitSeconds,refusal=actionFields decision.Action
            File.WriteAllText(output,JsonSerializer.Serialize(
                {|schema="fsgg.fourd.root-run-decision/1";accepted=true;lifecycleState=writeState decision.Lifecycle
                  runState=writeRunState decision.Protocol;action=action;waitSeconds=waitSeconds;refusal=refusal|},options)+"\n");0

    let private observation (root: JsonElement) =
        let fields = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        let cost = requiredInt root "cost"
        match requiredString root "kind" with
        | "acquire" when fields = Set ["kind";"identity";"resource";"cost"] -> Acquire(requiredString root "identity", requiredString root "resource", cost)
        | "validate" when fields = Set ["kind";"identity";"cost"] -> Validate(requiredString root "identity", cost)
        | "admit" when fields = Set ["kind";"identity";"cost"] -> Admit(requiredString root "identity", cost)
        | "invalidate" when fields = Set ["kind";"identity";"cost"] -> Invalidate(requiredString root "identity", cost)
        | "begin-effect" when fields = Set ["kind";"resource";"acknowledged";"cost"] -> BeginEffect(requiredString root "resource", requiredBool root "acknowledged", cost)
        | "observe-success" when fields = Set ["kind";"cost"] -> ObserveSuccess cost
        | "begin-cleanup" when fields = Set ["kind";"cancelled";"cost"] -> BeginCleanup(requiredBool root "cancelled", cost)
        | "cancel" when fields = Set ["kind";"cost"] -> Cancel cost
        | "close" when fields = Set ["kind";"resource";"cost"] -> Close(requiredString root "resource", cost)
        | "finish" when fields = Set ["kind";"cost"] -> Finish cost
        | _ -> invalidArg "kind" "observation-refused"

    let private execute input output =
        let bytes = readBounded input
        rejectDuplicateProperties bytes
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 16))
        let root = doc.RootElement
        if requiredString root "schema" <> "fsgg.fourd.typed-operation-request/1" then invalidArg "input" "typed-schema-refused"
        let hasState = root.TryGetProperty("state") |> fst
        let requestFields = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        if requestFields <> (if hasState then Set ["schema";"state";"observation"] else Set ["schema";"budget";"observation"])
        then invalidArg "input" "typed-request-shape-refused"
        let state = if hasState then readState (root.GetProperty "state") else Policy.initial (requiredInt root "budget")
        match Policy.reduce state (observation (root.GetProperty "observation")) with
        | Error reason ->
            File.WriteAllText(output, JsonSerializer.Serialize({| accepted = false; refusal = reason |}, options) + "\n")
            2
        | Ok value ->
            File.WriteAllText(output, JsonSerializer.Serialize(writeState value, options) + "\n")
            0

    let private validateJoin input output =
        let bytes = readBounded input
        rejectDuplicateProperties bytes
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 32))
        let root = doc.RootElement
        let fields = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        if fields <> Set ["schema";"admission";"placementSha";"runId";"runAttempt";"observedSourceSha"
                          "observedSourceTree";"observedInventorySha256";"observedNow"] then
            invalidArg "input" "typed-join-request-shape-refused"
        if requiredString root "schema" <> "fsgg.fourd.typed-source-join-request/1" then invalidArg "input" "typed-schema-refused"
        let admission = root.GetProperty "admission"
        match Join.validate admission (requiredString root "placementSha") (requiredString root "runId")
                  (requiredString root "runAttempt") (requiredString root "observedSourceSha")
                  (requiredString root "observedSourceTree") (requiredString root "observedInventorySha256")
                  (requiredString root "observedNow") with
        | Error reason ->
            File.WriteAllText(output, JsonSerializer.Serialize({| accepted = false; refusal = reason |}, options) + "\n")
            2
        | Ok identity ->
            File.WriteAllText(output, JsonSerializer.Serialize(
                {| schema = "fsgg.fourd.typed-source-join/1"; accepted = true; identity = identity |}, options) + "\n")
            0

    let private validateRoot input output =
        let bytes = readBounded input
        rejectDuplicateProperties bytes
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 16))
        let root = doc.RootElement
        let fields = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        if fields <> Set ["schema";"context";"observed";"placementSha";"placementTree";"runId";"producerSha256";"packetManifestSha256"]
           || requiredString root "schema" <> "fsgg.fourd.typed-root-join-request/1" then
            invalidArg "input" "typed-root-request-refused"
        match Join.validateRoot (root.GetProperty "context") (root.GetProperty "observed") (requiredString root "placementSha")
                                (requiredString root "placementTree") (requiredString root "runId") with
        | Error reason ->
            File.WriteAllText(output, JsonSerializer.Serialize({| accepted=false; refusal=reason |}, options)+"\n"); 2
        | Ok identity ->
            let context=root.GetProperty "context"
            let producer=requiredString root "producerSha256"
            let packet=requiredString root "packetManifestSha256"
            let validDigest (value:string) = value.Length=64 && value |> Seq.forall(fun c->Char.IsDigit c || c>='a'&&c<='f')
            if not(validDigest producer && validDigest packet) then invalidArg "input" "typed-root-evidence-refused"
            let identity=Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(identity+":"+producer+":"+packet))).ToLowerInvariant()
            let binding =
                {| rootJoinIdentity=identity; repository="FS-GG/FS.GG.Coordination"; repositoryId=1346720714L
                   runId=Int64.Parse(requiredString root "runId"); workflowPath=".github/workflows/fourd-public-provider-qualification.yml"
                   ``event``="workflow_dispatch"; headBranch="qualification/fourd-native-20261001"
                   headSha=requiredString root "placementSha"
                   originalActorId=Int64.Parse(requiredString context "originalActorId")
                   triggeringActorId=Int64.Parse(requiredString context "triggeringActorId")
                   producerSha256=producer; packetManifestSha256=packet |}
            File.WriteAllText(output, JsonSerializer.Serialize(
                {| schema="fsgg.fourd.typed-root-join/1"; accepted=true; identity=identity; runBinding=binding |}, options)+"\n"); 0

    let private generateQuint output =
        let path = Path.GetFullPath output
        let parent = Path.GetDirectoryName path
        if String.IsNullOrWhiteSpace parent || not (Directory.Exists parent) || File.Exists path then
            invalidArg "output" "typed-quint-output-refused"
        use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        use writer = new StreamWriter(stream, Text.UTF8Encoding(false))
        writer.Write(FourDOperationModel.Source)
        writer.Flush()
        0

    [<EntryPoint>]
    let main argv =
        try
            if argv.Length = 2 && argv[0] = "generate-quint" then generateQuint argv[1]
            elif argv.Length <> 3 then 2
            elif argv[0] = "transition" then execute argv[1] argv[2]
            elif argv[0] = "validate-join" then validateJoin argv[1] argv[2]
            elif argv[0] = "validate-root" then validateRoot argv[1] argv[2]
            elif argv[0] = "observe-run" then observeRun argv[1] argv[2]
            else 2
        with _ -> 2
