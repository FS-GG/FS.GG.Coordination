namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Pilot
open FS.GG.Coordination.Orchestration.Runner.Protocol

type MainRoutePreparation =
    { Snapshot:PlanningSnapshot; Budget:SubscriptionExecutionBudget; Reservation:Reservation
      Route:HostedRoutePlan; Readback:HostedRouteReadback; Runner:RunnerEnrollment; SessionId:SessionId
      LaunchIntent:LaunchIntent; Binding:ExecutorRouteBinding; InputManifest:ExecutorInputManifest
      InputBytes:byte array; WorkspaceManifest:ExecutorWorkspaceManifest; ExecutionReservation:SubscriptionReservation }

type MainRouteStatus =
    { Admitted:bool; Ready:bool; DispatchEnabled:bool; Mode:string
      Sequence:int64; Generation:int64; UnknownOperations:int; Findings:string list }

type MainRouteControl =
    { CommandId:Guid; ExpectedSequence:int64; ExpectedGeneration:int64; PrincipalId:string
      IssuedAt:DateTimeOffset; ExpiresAt:DateTimeOffset; Reason:string; Action:string }

type MainRouteControlReceipt =
    { Sequence:int64; Action:string; RequestPersisted:bool
      ProcessTerminationObserved:bool option; Detail:string }

type IMainRouteAdmissionHandler =
    abstract Admit:byte array * CancellationToken -> Task<Result<unit,string>>
    abstract Status:CancellationToken -> Task<Result<MainRouteStatus,string>>
    abstract Control:MainRouteControl * CancellationToken -> Task<Result<MainRouteControlReceipt,string>>

[<CLIMutable>]
type MainRouteAdmissionWire =
    { Schema:string; CommandId:Guid; ProjectId:Guid; WorkflowRevision:int64; CanonicalSha256:string; CapturedAt:DateTimeOffset
      RouteId:Guid; JobClass:string; AttemptId:Guid; CandidateId:Guid; RepositoryNodeId:string; BranchRef:string; ClaimResourceId:string
      ClaimOperationId:Guid; ProcessOperationId:Guid; CandidateOperationId:Guid; BranchOperationId:Guid; PullRequestOperationId:Guid; MergeOperationId:Guid; ReadbackOperationId:Guid
      Generation:int64; SelectedAt:DateTimeOffset; RouteProviderRevision:string; RouteEvidenceSha256:string; RouteObservedAt:DateTimeOffset
      RunnerId:Guid; RunnerPrincipalId:string; RunnerFingerprintSha256:string; RunnerExpiresAt:DateTimeOffset; SessionId:Guid
      ReservationId:Guid; ReservationExpiresAt:DateTimeOffset; BudgetMaximumRuntimeSeconds:int64; BudgetExecutionDeadline:DateTimeOffset
      LaunchBase64:string; RouteBindingBase64:string; InputManifestBase64:string
      InputBase64:string; WorkspaceManifestBase64:string; ExecutionReservationBase64:string }

[<RequireQualifiedAccess>]
module MainRouteAdmission =
    let schema="fsgg.orchestration.main-route-admission/1"
    let private options=JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=8,PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow)
    let private properties=set["schema";"commandId";"projectId";"workflowRevision";"canonicalSha256";"capturedAt";"routeId";"jobClass";"attemptId";"candidateId";"repositoryNodeId";"branchRef";"claimResourceId";"claimOperationId";"processOperationId";"candidateOperationId";"branchOperationId";"pullRequestOperationId";"mergeOperationId";"readbackOperationId";"generation";"selectedAt";"routeProviderRevision";"routeEvidenceSha256";"routeObservedAt";"runnerId";"runnerPrincipalId";"runnerFingerprintSha256";"runnerExpiresAt";"sessionId";"reservationId";"reservationExpiresAt";"budgetMaximumRuntimeSeconds";"budgetExecutionDeadline";"launchBase64";"routeBindingBase64";"inputManifestBase64";"inputBase64";"workspaceManifestBase64";"executionReservationBase64"]
    let private decode64 maximum (value:string)=
        try let bytes=Convert.FromBase64String value in if bytes.Length=0||bytes.Length>maximum then Error "main-route-admission-content-bounds-refused" else Ok bytes
        with :? FormatException->Error "main-route-admission-base64-refused"
    let private sha value=not(String.IsNullOrWhiteSpace value)&&value.Length=64&&(value|>Seq.forall(fun c->Char.IsAsciiHexDigit c && not(Char.IsUpper c)))
    let encode commandId (value:MainRoutePreparation) =
        let wire =
            { Schema=schema; CommandId=commandId; ProjectId=Id.projectValue value.Snapshot.ProjectId
              WorkflowRevision=Id.revisionValue value.Snapshot.WorkflowRevision; CanonicalSha256=value.Snapshot.CanonicalSha256
              CapturedAt=value.Snapshot.CapturedAt; RouteId=value.Route.RouteId; JobClass=value.Route.JobClass
              AttemptId=Id.attemptValue value.Route.AttemptId; CandidateId=Id.candidateValue value.Route.CandidateId
              RepositoryNodeId=value.Route.RepositoryNodeId; BranchRef=value.Route.BranchRef; ClaimResourceId=value.Route.ClaimResourceId
              ClaimOperationId=Id.operationValue value.Route.ClaimOperationId; ProcessOperationId=Id.operationValue value.Route.ProcessOperationId
              CandidateOperationId=Id.operationValue value.Route.CandidateOperationId; BranchOperationId=Id.operationValue value.Route.BranchOperationId
              PullRequestOperationId=Id.operationValue value.Route.PullRequestOperationId; MergeOperationId=Id.operationValue value.Route.MergeOperationId
              ReadbackOperationId=Id.operationValue value.Route.ReadbackOperationId; Generation=Id.generationValue value.Route.Generation
              SelectedAt=value.Route.SelectedAt; RouteProviderRevision=value.Readback.ProviderRevision
              RouteEvidenceSha256=value.Readback.EvidenceSha256; RouteObservedAt=value.Readback.ObservedAt
              RunnerId=Id.runnerValue value.Runner.RunnerId; RunnerPrincipalId=value.Runner.PrincipalId
              RunnerFingerprintSha256=value.Runner.FingerprintSha256; RunnerExpiresAt=value.Runner.ExpiresAt
              SessionId=Id.sessionValue value.SessionId; ReservationId=Id.reservationValue value.Reservation.ReservationId
              ReservationExpiresAt=value.Reservation.ExpiresAt; BudgetMaximumRuntimeSeconds=int64 value.Budget.MaximumRuntime.TotalSeconds
              BudgetExecutionDeadline=value.Budget.ExecutionDeadline
              LaunchBase64=Convert.ToBase64String(ExecutionProtocol.encode value.LaunchIntent)
              RouteBindingBase64=Convert.ToBase64String(ExecutorWire.encodeRouteBinding value.Binding)
              InputManifestBase64=Convert.ToBase64String(ExecutorWire.encodeInputManifest value.InputManifest)
              InputBase64=Convert.ToBase64String value.InputBytes
              WorkspaceManifestBase64=Convert.ToBase64String(ExecutorWire.encodeWorkspaceManifest value.WorkspaceManifest)
              ExecutionReservationBase64=Convert.ToBase64String(SubscriptionAccountingCodec.encodeReservation value.ExecutionReservation) }
        JsonSerializer.SerializeToUtf8Bytes(wire,options)
    let decode (workItemId:WorkItemId) (principal:string) (bytes:byte array) =
        try
            if isNull bytes||bytes.Length=0||bytes.Length>2*1024*1024 then Error "main-route-admission-size-refused" else
            use document=JsonDocument.Parse(ReadOnlyMemory bytes,JsonDocumentOptions(MaxDepth=8))
            let names=if document.RootElement.ValueKind=JsonValueKind.Object then document.RootElement.EnumerateObject()|>Seq.map _.Name|>Seq.toList else []
            if names.Length<>properties.Count||Set.ofList names<>properties then Error "main-route-admission-shape-refused" else
            let wire=document.RootElement.Deserialize<MainRouteAdmissionWire>(options)
            if isNull(box wire)||wire.Schema<>schema||wire.CommandId=Guid.Empty||wire.ProjectId=Guid.Empty||wire.RouteId=Guid.Empty||wire.AttemptId=Guid.Empty||wire.CandidateId=Guid.Empty||wire.Generation<0L||wire.WorkflowRevision<1L
               ||wire.RunnerId=Guid.Empty||wire.SessionId=Guid.Empty||wire.ReservationId=Guid.Empty||wire.RunnerPrincipalId<>principal||not(sha wire.CanonicalSha256&&sha wire.RouteEvidenceSha256&&sha wire.RunnerFingerprintSha256)
               ||wire.SelectedAt=DateTimeOffset.MinValue||wire.RouteObservedAt<wire.SelectedAt||wire.CapturedAt=DateTimeOffset.MinValue||wire.ReservationExpiresAt<=wire.SelectedAt||wire.RunnerExpiresAt<=wire.SelectedAt
               ||wire.BudgetMaximumRuntimeSeconds<1L||wire.BudgetMaximumRuntimeSeconds>1800L||wire.BudgetExecutionDeadline<=wire.SelectedAt then Error "main-route-admission-authority-refused" else
            let combined=
                decode64 8192 wire.LaunchBase64
                |>Result.bind(fun launchBytes->ExecutionProtocol.decode launchBytes)
                |>Result.bind(fun launch->decode64 8192 wire.RouteBindingBase64|>Result.bind ExecutorWire.parseRouteBinding|>Result.map(fun binding->launch,binding))
                |>Result.bind(fun(launch,binding)->decode64 8192 wire.InputManifestBase64|>Result.bind ExecutorWire.parseInputManifest|>Result.map(fun input->launch,binding,input))
                |>Result.bind(fun(launch,binding,input)->decode64 (1024*1024) wire.InputBase64|>Result.map(fun content->launch,binding,input,content))
                |>Result.bind(fun(launch,binding,input,content)->decode64 8192 wire.WorkspaceManifestBase64|>Result.bind ExecutorWire.parseWorkspaceManifest|>Result.map(fun workspace->launch,binding,input,content,workspace))
                |>Result.bind(fun(launch,binding,input,content,workspace)->decode64 8192 wire.ExecutionReservationBase64|>Result.bind SubscriptionAccountingCodec.decodeReservation|>Result.map(fun reservation->launch,binding,input,content,workspace,reservation))
            combined|>Result.bind(fun(launch,binding,input,inputBytes,workspace,executionReservation)->
                let generation=Id.generation wire.Generation
                let revision=Id.revision wire.WorkflowRevision
                let route={RouteId=wire.RouteId;WorkItemId=workItemId;JobClass=wire.JobClass;AttemptId=Id.attempt wire.AttemptId;CandidateId=Id.candidate wire.CandidateId;RepositoryNodeId=wire.RepositoryNodeId;BranchRef=wire.BranchRef;ClaimResourceId=wire.ClaimResourceId;ClaimOperationId=Id.operation wire.ClaimOperationId;ProcessOperationId=Id.operation wire.ProcessOperationId;CandidateOperationId=Id.operation wire.CandidateOperationId;BranchOperationId=Id.operation wire.BranchOperationId;PullRequestOperationId=Id.operation wire.PullRequestOperationId;MergeOperationId=Id.operation wire.MergeOperationId;ReadbackOperationId=Id.operation wire.ReadbackOperationId;Generation=generation;WorkflowRevision=revision;SelectedAt=wire.SelectedAt}
                let budget={Schema=SubscriptionPilot.budgetSchema;AttemptLimit=1;MaximumRuntime=TimeSpan.FromSeconds(float wire.BudgetMaximumRuntimeSeconds);ExecutionDeadline=wire.BudgetExecutionDeadline;Usage=TokensUnknown "provider-has-not-reported-usage";Cost={InvocationState="not-applicable";InvocationProvenance="subscription-session";BroaderAttributionState="unknown";BroaderAttributionProvenance="subscription-cost-not-attributable-to-invocation"}}
                Ok {Snapshot={ProjectId=Id.project wire.ProjectId;WorkItemId=workItemId;WorkflowRevision=revision;CanonicalSha256=wire.CanonicalSha256;BoardMembershipIds=[];CapturedAt=wire.CapturedAt};Budget=budget
                    Reservation={ReservationId=Id.reservation wire.ReservationId;Generation=generation;ExpiresAt=wire.ReservationExpiresAt;RequiredClaimIds=set[wire.ClaimResourceId]};Route=route
                    Readback={RouteId=wire.RouteId;WorkItemId=workItemId;RepositoryNodeId=wire.RepositoryNodeId;ProviderRevision=wire.RouteProviderRevision;EvidenceSha256=wire.RouteEvidenceSha256;Generation=generation;WorkflowRevision=revision;ObservedAt=wire.RouteObservedAt}
                    Runner={RunnerId=Id.runner wire.RunnerId;PrincipalId=wire.RunnerPrincipalId;FingerprintSha256=wire.RunnerFingerprintSha256;Generation=generation;ExpiresAt=wire.RunnerExpiresAt};SessionId=Id.session wire.SessionId
                    LaunchIntent=launch;Binding=binding;InputManifest=input;InputBytes=inputBytes;WorkspaceManifest=workspace;ExecutionReservation=executionReservation})
        with :? JsonException->Error "main-route-admission-json-refused"
             | :? ArgumentException->Error "main-route-admission-value-refused"
