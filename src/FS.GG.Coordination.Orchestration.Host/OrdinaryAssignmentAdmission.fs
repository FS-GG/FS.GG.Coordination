namespace FS.GG.Coordination.Orchestration.Host

open System
open System.IO
open System.Text.Json
open Microsoft.FSharp.Reflection
open FS.GG.Coordination.Core
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrdinaryAssignment
open FS.GG.Coordination.Orchestration.Runner.Protocol

/// An explicit schema-bound encoding of the complete domain evidence. No CLR type name is read from input.
module OrdinaryAssignmentAdmission =
    type Evidence = { Authority: AuthoritativeObligations; Snapshot: OrdinaryAssignment.Snapshot; Decision: Decision }
    let private flags=Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
    let private collection (t: Type) =
        if t.IsGenericType then
            let d=t.GetGenericTypeDefinition()
            if d=typedefof<Map<_,_>> then Some "Map" elif d=typedefof<Set<_>> then Some "Set" else None
        else None
    let private writeEvidence (value: Evidence) =
        use stream=new MemoryStream()
        use writer=new Utf8JsonWriter(stream)
        let rec write (t: Type) (v: obj) =
            if t=typeof<string> then writer.WriteStringValue(unbox<string> v)
            elif t=typeof<bool> then writer.WriteBooleanValue(unbox<bool> v)
            elif t=typeof<int> then writer.WriteNumberValue(unbox<int> v)
            elif t=typeof<int64> then writer.WriteNumberValue(unbox<int64> v)
            elif t=typeof<Guid> then writer.WriteStringValue(unbox<Guid> v)
            elif t=typeof<DateTimeOffset> then writer.WriteStringValue(unbox<DateTimeOffset> v)
            elif t=typeof<TimeSpan> then writer.WriteNumberValue((unbox<TimeSpan> v).Ticks)
            elif t.IsArray then
                writer.WriteStartArray()
                for x in unbox<Array> v do write (t.GetElementType()) x
                writer.WriteEndArray()
            elif collection t |> Option.isSome then
                writer.WriteStartArray()
                for x in v :?> System.Collections.IEnumerable do
                    if collection t=Some "Map" then
                        writer.WriteStartArray()
                        write (t.GetGenericArguments()[0]) (x.GetType().GetProperty("Key").GetValue x)
                        write (t.GetGenericArguments()[1]) (x.GetType().GetProperty("Value").GetValue x)
                        writer.WriteEndArray()
                    else write (t.GetGenericArguments()[0]) x
                writer.WriteEndArray()
            elif FSharpType.IsRecord(t,flags) then
                writer.WriteStartArray()
                for field in FSharpType.GetRecordFields(t,flags) do write field.PropertyType (field.GetValue v)
                writer.WriteEndArray()
            elif FSharpType.IsUnion(t,flags) then
                let case,fields=FSharpValue.GetUnionFields(v,t,flags)
                writer.WriteStartArray();writer.WriteNumberValue case.Tag
                for field,x in Array.zip (case.GetFields()) fields do write field.PropertyType x
                writer.WriteEndArray()
            else invalidOp "ordinary-evidence-type-refused"
        write typeof<Evidence> (box value)
        writer.Flush();stream.ToArray()
    let encodeEvidence = writeEvidence
    let decodeEvidence (bytes: byte array) =
        if isNull bytes || bytes.Length=0 || bytes.Length>OrdinaryAssignmentWire.maximumArtifactBytes then Error "ordinary-evidence-size-refused"
        else
          try
            use doc=JsonDocument.Parse(ReadOnlyMemory bytes,JsonDocumentOptions(MaxDepth=64))
            let mutable nodes=0
            let rec read (t: Type) (e: JsonElement) : obj =
                nodes<-nodes+1
                if nodes>100000 then invalidOp "ordinary-evidence-work-refused"
                let array () = if e.ValueKind<>JsonValueKind.Array then invalidOp "ordinary-evidence-array-refused" else e.EnumerateArray() |> Seq.toArray
                if t=typeof<string> then box(e.GetString())
                elif t=typeof<bool> then box(e.GetBoolean())
                elif t=typeof<int> then box(e.GetInt32())
                elif t=typeof<int64> then box(e.GetInt64())
                elif t=typeof<Guid> then box(e.GetGuid())
                elif t=typeof<DateTimeOffset> then box(e.GetDateTimeOffset())
                elif t=typeof<TimeSpan> then box(TimeSpan.FromTicks(e.GetInt64()))
                elif t.IsArray then
                    let values=array()
                    let result=Array.CreateInstance(t.GetElementType(),values.Length)
                    for i in 0..values.Length-1 do result.SetValue(read (t.GetElementType()) values[i],i)
                    box result
                elif collection t |> Option.isSome then
                    let values=array()
                    let args=t.GetGenericArguments()
                    let itemType=if collection t=Some "Map" then FSharpType.MakeTupleType args else args[0]
                    let typed=Array.CreateInstance(itemType,values.Length)
                    for i in 0..values.Length-1 do
                        let value=if collection t=Some "Map" then
                                      let pair=values[i].EnumerateArray() |> Seq.toArray
                                      if pair.Length<>2 then invalidOp "ordinary-map-entry-refused"
                                      FSharpValue.MakeTuple([|read args[0] pair[0];read args[1] pair[1]|],itemType)
                                  else read itemType values[i]
                        typed.SetValue(value,i)
                    let moduleType=typeof<Set<int>>.Assembly.GetType("Microsoft.FSharp.Collections."+(collection t |> Option.get)+"Module")
                    let methodInfo=moduleType.GetMethods() |> Array.find(fun m -> m.Name="OfSeq")
                    let result=methodInfo.MakeGenericMethod(args).Invoke(null,[|typed|])
                    let count=unbox<int>(result.GetType().GetProperty("Count").GetValue result)
                    if count<>values.Length then invalidOp "ordinary-collection-duplicate-refused"
                    result
                elif FSharpType.IsRecord(t,flags) then
                    let values=array()
                    let fields=FSharpType.GetRecordFields(t,flags)
                    if values.Length<>fields.Length then invalidOp "ordinary-record-fields-refused"
                    FSharpValue.MakeRecord(t,Array.map2(fun (p: Reflection.PropertyInfo) e -> read p.PropertyType e) fields values,flags)
                elif FSharpType.IsUnion(t,flags) then
                    let values=array()
                    if values.Length=0 then invalidOp "ordinary-union-refused"
                    let tag=values[0].GetInt32()
                    let case=FSharpType.GetUnionCases(t,flags) |> Array.tryFind(fun c -> c.Tag=tag) |> Option.defaultWith(fun () -> invalidOp "ordinary-union-case-refused")
                    let fields=case.GetFields()
                    if values.Length<>fields.Length+1 then invalidOp "ordinary-union-fields-refused"
                    FSharpValue.MakeUnion(case,Array.map2(fun (p: Reflection.PropertyInfo) e -> read p.PropertyType e) fields values[1..],flags)
                else invalidOp "ordinary-evidence-type-refused"
            Ok(unbox<Evidence>(read typeof<Evidence> doc.RootElement))
          with _ -> Error "ordinary-evidence-decode-refused"
    let verify (bytes: byte array) =
        OrdinaryAssignmentWire.parseArtifact bytes |> Result.bind(fun artifact ->
          try
            Convert.FromBase64String artifact.EvidenceBase64 |> decodeEvidence
            |> Result.bind(fun evidence ->
                OrdinaryAssignmentVerifier.verify evidence.Authority evidence.Snapshot evidence.Decision
                |> Result.mapError(fun e -> sprintf "ordinary-verifier-refused:%A" e)
                |> Result.bind(fun () ->
                    match evidence.Decision.Selected |> List.filter(fun a -> a.AssignmentSha256=artifact.AssignmentSha256) with
                    | [assignment] -> Ok(artifact,assignment)
                    | _ -> Error "ordinary-selected-assignment-refused"))
          with :? FormatException -> Error "ordinary-evidence-base64-refused")
    let validateProjection (artifact: OrdinaryAssignmentArtifact) (assignment: OrdinaryAssignment.Assignment) =
        let work=assignment.Specification.Requirements
        let spec=assignment.Specification
        let same (values: string array) expected = Set.ofArray values=expected && values.Length=expected.Count
        let identity=match work.Identity with Board w -> WorkItemIdentity.persistenceId w=artifact.WorkItemPersistenceId | _ -> false
        let repository=match work.Workspace.Repositories with [r] -> r.Identity=artifact.RepositoryBinding && r.Revision=artifact.BaselineObjectId | _ -> false
        if not identity || not repository || assignment.ExperimentEnrollment.IsSome
           || work.Generation<>Some(Id.generation artifact.Generation)
           || Id.attemptValue work.Attempt<>artifact.AttemptId || Id.reservationValue spec.Reservation<>artifact.ReservationId
           || work.SelectedAt<>artifact.RecordedAt || work.Workspace.Workspace<>artifact.Workspace
           || spec.Requested.Model<>artifact.Model || spec.Requested.Effort<>artifact.Effort
           || not(same artifact.AllowedPaths work.AllowedPaths && same artifact.Validations work.RequiredChecks
                  && same artifact.Context work.Context && same artifact.Tools work.Tools && same artifact.Effects work.AuthorizedEffects
                  && same artifact.Outputs work.RequiredOutputs && same artifact.Reviews work.RequiredReviews)
           || (work.Deadline |> Option.exists(fun d -> artifact.Deadline>d))
           || (work.Recovery |> Option.exists(fun r -> artifact.RecordedAt<>r.OriginalStartedAt || artifact.Deadline>r.OriginalDeadline || r.AttemptOrdinal<>1 || r.MaxAttempts<>1))
           || not work.Skills.IsEmpty || (work.Capabilities |> Set.exists(fun c -> c<>"git" && c<>"codex"))
           || (spec.Demand |> Map.exists(fun unit amount -> unit<>"task-slots" || amount<>Some 1L))
           || spec.Requested.Runtime<>"codex" || work.Context.IsEmpty
           || not(Set.isSubset work.RequiredChecks (set["git-diff-check";"prose-citations"]))
           || not(Set.isSubset work.Tools (set["git"])) || not(Set.isSubset work.AuthorizedEffects (set["workspace-edit"]))
           || not(Set.isSubset work.RequiredOutputs (set["candidate";"validation-evidence"]))
           || not(Set.isSubset work.RequiredReviews (set["independent-source-review"]))
           || artifact.AcceptanceOwner<>"Main candidate and independent review acceptance"
        then Error "ordinary-projection-refused" else Ok()

    /// Declarations are retained obligations. No review/output receipt is invented at launch.
    let outcomeReadiness bytes =
        verify bytes |> Result.bind(fun (artifact,assignment) ->
            validateProjection artifact assignment |> Result.bind(fun () ->
                Error "ordinary-deferred-review-output-readback-required:Main-candidate-and-independent-review-acceptance"))
