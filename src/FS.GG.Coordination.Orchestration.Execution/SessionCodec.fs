namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Serialization

[<RequireQualifiedAccess>]
module SessionEventCodec =
    let schema = "fsgg.orchestration.execution-session-event/1"
    let maximumBytes = 256 * 1024
    [<CLIMutable>]
    type ReferenceWire = { Kind:string; Reference:string; Digest:string }
    [<CLIMutable>]
    type UsageWire = { Name:string; State:string; Value:Nullable<int64>; UnitName:string; Provenance:string }
    [<CLIMutable>]
    type ObservationWire =
        { Provider:string; AdapterVersion:string; ProviderSessionReference:string
          RequestedModel:string; RequestedEffort:string; Lifecycle:string
          Output:ReferenceWire array; LifecycleReferences:ReferenceWire array; Usage:UsageWire array
          CostState:string; CostAmount:Nullable<decimal>; CostCurrency:string; CostProvenance:string
          CandidateId:Guid; CandidateHeadSha:string; CandidateTreeSha:string; ObservedAt:DateTimeOffset }
    [<CLIMutable>]
    type EventWire =
        { Schema:string; Kind:string; IntentBase64:string; AttemptNumber:int; At:DateTimeOffset
          Observation:ObservationWire }
    let private options =
        let value=JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=12)
        value.PropertyNameCaseInsensitive<-false
        value.UnmappedMemberHandling<-JsonUnmappedMemberHandling.Disallow
        value
    let private digest (bytes:byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let private refWire (item:OutputReference) : ReferenceWire = {Kind=item.Kind;Reference=item.Reference;Digest=defaultArg item.Digest null}
    let private refValue (item:ReferenceWire) : OutputReference = {Kind=item.Kind;Reference=item.Reference;Digest=Option.ofObj item.Digest}
    let private usageWire name = function
        | UsageKnown(value,unitName,provenance) -> {Name=name;State="known";Value=Nullable value;UnitName=unitName;Provenance=provenance}
        | UsageUnknown provenance -> {Name=name;State="unknown";Value=Nullable();UnitName=null;Provenance=provenance}
        | UsageNotApplicable provenance -> {Name=name;State="not-applicable";Value=Nullable();UnitName=null;Provenance=provenance}
    let private usageValue item =
        match item.State with
        | "known" when item.Value.HasValue && item.Value.Value>=0L && not(String.IsNullOrWhiteSpace item.UnitName) -> Ok(UsageKnown(item.Value.Value,item.UnitName,item.Provenance))
        | "unknown" when not item.Value.HasValue && isNull item.UnitName -> Ok(UsageUnknown item.Provenance)
        | "not-applicable" when not item.Value.HasValue && isNull item.UnitName -> Ok(UsageNotApplicable item.Provenance)
        | _ -> Error "execution-usage-refused"
    let private lifecycleText = function Starting->"starting"|Running->"running"|Cancelling->"cancelling"|Succeeded->"succeeded"|Failed->"failed"|Cancelled->"cancelled"|DeadlineExceeded->"deadline-exceeded"|OutcomeUnknown->"outcome-unknown"
    let private lifecycleValue = function "starting"->Some Starting|"running"->Some Running|"cancelling"->Some Cancelling|"succeeded"->Some Succeeded|"failed"->Some Failed|"cancelled"->Some Cancelled|"deadline-exceeded"->Some DeadlineExceeded|"outcome-unknown"->Some OutcomeUnknown|_->None
    let private validText maximum (text:string) = not(String.IsNullOrWhiteSpace text) && text=text.Trim() && text.Length<=maximum
    let private validDigestOrNull (text:string) = isNull text || (text.Length=64 && text |> Seq.forall(fun c->Char.IsAsciiHexDigit c && not(Char.IsUpper c)))
    let private observationWire (value:SessionObservation) : ObservationWire =
        let costState,costAmount,costCurrency,costProvenance =
            match value.Usage.Cost with
            | CostKnown(amount,currency,provenance) -> "known",Nullable amount,currency,provenance
            | CostUnknown provenance -> "unknown",Nullable(),null,provenance
            | CostNotApplicable provenance -> "not-applicable",Nullable(),null,provenance
        let candidateId,head,tree = match value.Candidate with Some (c:CandidateReference)->c.CandidateId,c.HeadSha,c.TreeSha|None->Guid.Empty,null,null
        { Provider=value.Provider.Provider;AdapterVersion=value.Provider.AdapterVersion
          ProviderSessionReference=ProviderSessionReference.value value.Session
          RequestedModel=defaultArg value.Resolved.Model null;RequestedEffort=defaultArg value.Resolved.Effort null
          Lifecycle=lifecycleText value.Lifecycle;Output=value.Output |> List.map refWire |> List.toArray
          LifecycleReferences=value.LifecycleReferences |> List.map refWire |> List.toArray
          Usage=value.Usage.Values |> Map.toArray |> Array.map(fun (name,v)->usageWire name v)
          CostState=costState;CostAmount=costAmount;CostCurrency=costCurrency;CostProvenance=costProvenance
          CandidateId=candidateId;CandidateHeadSha=head;CandidateTreeSha=tree;ObservedAt=value.ObservedAt }
    let private observationValue (value:ObservationWire) : Result<SessionObservation,string> =
        if isNull(box value) || isNull value.Output || isNull value.LifecycleReferences || isNull value.Usage
           || value.Output.Length>64 || value.LifecycleReferences.Length>64 || value.Usage.Length>64 then Error "execution-observation-bounds-refused"
        elif not(validText 128 value.Provider && validText 128 value.AdapterVersion)
             || (value.Output |> Array.append value.LifecycleReferences |> Array.exists(fun item->not(validText 64 item.Kind && validText 2048 item.Reference && validDigestOrNull item.Digest)))
             || (value.Usage |> Array.exists(fun item->not(validText 64 item.Name && validText 256 item.Provenance)))
             || ((value.Usage |> Array.map _.Name |> Set.ofArray).Count<>value.Usage.Length)
             || (value.CandidateId<>Guid.Empty && not(validText 128 value.CandidateHeadSha && validText 128 value.CandidateTreeSha))
             || not(validText 256 value.CostProvenance) then Error "execution-observation-shape-refused"
        else
            let usage =
                value.Usage |> Array.fold(fun state item -> state |> Result.bind(fun acc -> usageValue item |> Result.map(fun v->Map.add item.Name v acc))) (Ok Map.empty)
            let cost = match value.CostState with |"known" when value.CostAmount.HasValue && value.CostAmount.Value>=0M && not(String.IsNullOrWhiteSpace value.CostCurrency)->Ok(CostKnown(value.CostAmount.Value,value.CostCurrency,value.CostProvenance))|"unknown" when not value.CostAmount.HasValue && isNull value.CostCurrency->Ok(CostUnknown value.CostProvenance)|"not-applicable" when not value.CostAmount.HasValue && isNull value.CostCurrency->Ok(CostNotApplicable value.CostProvenance)|_->Error "execution-cost-refused"
            match ProviderSessionReference.create value.ProviderSessionReference,lifecycleValue value.Lifecycle,usage,cost with
            | Ok session,Some lifecycle,Ok values,Ok normalizedCost ->
                let candidate = if value.CandidateId=Guid.Empty then None else Some{CandidateId=value.CandidateId;HeadSha=value.CandidateHeadSha;TreeSha=value.CandidateTreeSha}
                Ok {Provider={Provider=value.Provider;AdapterVersion=value.AdapterVersion};Session=session;Resolved={Model=Option.ofObj value.RequestedModel;Effort=Option.ofObj value.RequestedEffort};Lifecycle=lifecycle
                    Output=value.Output |> Array.map refValue |> Array.toList;LifecycleReferences=value.LifecycleReferences |> Array.map refValue |> Array.toList
                    Usage={Values=values;Cost=normalizedCost};Candidate=candidate;ObservedAt=value.ObservedAt}
            | _ -> Error "execution-observation-refused"
    let encode eventValue =
        let emptyObservation=Unchecked.defaultof<ObservationWire>
        let wire = match eventValue with
                   | LaunchIntentRecorded intent -> {Schema=schema;Kind="launch-intent";IntentBase64=Convert.ToBase64String(ExecutionProtocol.encode intent);AttemptNumber=0;At=DateTimeOffset.MinValue;Observation=emptyObservation}
                   | LaunchAttemptRecorded(number,at) -> {Schema=schema;Kind="launch-attempt";IntentBase64=null;AttemptNumber=number;At=at;Observation=emptyObservation}
                   | StartObserved observation -> {Schema=schema;Kind="start-observed";IntentBase64=null;AttemptNumber=0;At=DateTimeOffset.MinValue;Observation=observationWire observation}
                   | ObservationRecorded observation -> {Schema=schema;Kind="observation";IntentBase64=null;AttemptNumber=0;At=DateTimeOffset.MinValue;Observation=observationWire observation}
                   | CancelRequested at -> {Schema=schema;Kind="cancel-requested";IntentBase64=null;AttemptNumber=0;At=at;Observation=emptyObservation}
        JsonSerializer.SerializeToUtf8Bytes(wire,options)
    let identity eventValue = encode eventValue |> digest
    let decode (bytes:byte array) =
        if isNull bytes || bytes.Length=0 || bytes.Length>maximumBytes then Error "execution-event-size-refused"
        else
            try
                use document=JsonDocument.Parse(ReadOnlyMemory bytes,JsonDocumentOptions(MaxDepth=12))
                let rec duplicateFree (element:JsonElement) =
                    match element.ValueKind with
                    | JsonValueKind.Object ->
                        let names=element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                        names.Length=(Set.ofList names).Count && element.EnumerateObject() |> Seq.forall(fun property->duplicateFree property.Value)
                    | JsonValueKind.Array -> element.EnumerateArray() |> Seq.forall duplicateFree
                    | _ -> true
                let wire=JsonSerializer.Deserialize<EventWire>(ReadOnlySpan bytes,options)
                if not(duplicateFree document.RootElement) then Error "execution-event-duplicate-property-refused"
                elif isNull(box wire) || wire.Schema<>schema then Error "execution-event-schema-refused"
                else
                    match wire.Kind with
                    | "launch-intent" when wire.AttemptNumber=0 && wire.At=DateTimeOffset.MinValue && isNull(box wire.Observation) ->
                        try Convert.FromBase64String wire.IntentBase64 |> ExecutionProtocol.decode |> Result.map LaunchIntentRecorded
                        with :? FormatException -> Error "execution-event-payload-refused"
                    | "launch-attempt" when isNull wire.IntentBase64 && isNull(box wire.Observation) && wire.AttemptNumber>0 -> Ok(LaunchAttemptRecorded(wire.AttemptNumber,wire.At))
                    | "start-observed" when isNull wire.IntentBase64 -> observationValue wire.Observation |> Result.map StartObserved
                    | "observation" when isNull wire.IntentBase64 -> observationValue wire.Observation |> Result.map ObservationRecorded
                    | "cancel-requested" when isNull wire.IntentBase64 && isNull(box wire.Observation) && wire.AttemptNumber=0 -> Ok(CancelRequested wire.At)
                    | _ -> Error "execution-event-shape-refused"
            with :? JsonException -> Error "execution-event-json-refused"
