namespace FS.GG.FourD.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsQuint

/// Compares Quint's independently executed journey with observations emitted by the actual
/// compiled native reducer. The Python fixture driver supplies acquired Git/OCI bytes.
module NativeProtocolTests =
    let private require test reason = if not test then invalidOp reason
    let private unwrap = function Ok value -> value | Error error -> invalidOp (sprintf "%A" error)
    let private variable name =
        let value=Environment.GetEnvironmentVariable name
        require (not(String.IsNullOrWhiteSpace value)) (name+" is required")
        value
    let private fingerprint name =
        let value=variable name
        require (value.Length=64 && value |> Seq.forall (fun c -> c>='0' && c<='9' || c>='a' && c<='f')) (name+" must be SHA-256")
        value
    let private state (json:JsonElement) =
        let fields=json.EnumerateObject() |> Seq.map (fun p ->
            let value=
                match p.Value.ValueKind with
                | JsonValueKind.String -> QuintReplayValue.Text(p.Value.GetString())
                | JsonValueKind.True -> QuintReplayValue.Boolean true
                | JsonValueKind.False -> QuintReplayValue.Boolean false
                | JsonValueKind.Number -> QuintReplayValue.Integer(string(p.Value.GetInt64()))
                | JsonValueKind.Array -> QuintReplayValue.Set(p.Value.EnumerateArray() |> Seq.map (fun v -> QuintReplayValue.Text(v.GetString())) |> Seq.toList)
                | _ -> invalidOp "native model projection must contain closed scalar facts"
            p.Name,value) |> Seq.toList
        let draft={Identity=String.replicate 64 "0";Bindings=["state",QuintReplayValue.Record fields]}
        {draft with Identity=QuintReplay.stateFingerprint draft |> unwrap}
    let run () =
        let environment={
            Seed="20261025";Bounds=["steps",50L]
            ToolFingerprint=fingerprint "FSGG_FOURD_QUINT_TOOL_SHA256"
            ProfileFingerprint=fingerprint "FSGG_FOURD_QUINT_PROFILE_SHA256"
            ContractFingerprint=fingerprint "FSGG_FOURD_QUINT_CONTRACT_SHA256"
            AdapterFingerprint=fingerprint "FSGG_FOURD_QUINT_ADAPTER_SHA256"
            ImplementationFingerprint=fingerprint "FSGG_FOURD_QUINT_IMPLEMENTATION_SHA256" }
        use actual=JsonDocument.Parse(File.ReadAllBytes(variable "FSGG_FOURD_NATIVE_QUINT_OBSERVATIONS"))
        let observations=actual.RootElement.EnumerateArray() |> Seq.mapi (fun index event ->
            let action=event.GetProperty("action").GetString()
            { Index=index+1;Action=action;Source={Path="eng/fourd-public-provider/typed/NativePolicy.fs";Line=1;Column=action.Length};Actual=state(event.GetProperty "state") }) |> Seq.toList
        require (observations.Length>0) "native observations are empty"
        let context={Environment=environment;Steps=observations |> List.map(fun o -> {Index=o.Index;Action=o.Action;Source=o.Source})}
        let root=JsonNode.Parse(File.ReadAllText(variable "FSGG_FOURD_NATIVE_QUINT_ITF")).AsObject()
        root["#meta"].AsObject().Remove("description") |> ignore
        root["#meta"].AsObject().Remove("timestamp") |> ignore
        let trace=QuintReplay.decodeItf context (root.ToJsonString()) |> unwrap
        require (QuintReplay.compare trace observations |> unwrap = QuintReplayResult.Equivalent) "native Quint/F# correspondence diverged"
        let last=List.last observations
        let bindings = last.Actual.Bindings |> List.map (fun (name,value) ->
            match value with
            | QuintReplayValue.Record fields ->
                name,QuintReplayValue.Record(fields |> List.map(fun (key,v) ->
                    if key="outcome" then
                        key,(match v with QuintReplayValue.Text "success" -> QuintReplayValue.Text "unknown" | _ -> QuintReplayValue.Text "success")
                    else key,v))
            | _ -> name,value)
        let changed={last.Actual with Bindings=bindings}
        let changed={changed with Identity=QuintReplay.stateFingerprint changed |> unwrap}
        let mutated=(observations |> List.take (observations.Length-1)) @ [{last with Actual=changed}]
        match QuintReplay.compare trace mutated |> unwrap with
        | QuintReplayResult.Diverged _ -> ()
        | _ -> invalidOp "native false-result mutation escaped correspondence"
        let archiveBindings=last.Actual.Bindings |> List.map(fun (name,value) ->
            match value with
            | QuintReplayValue.Record fields ->
                name,QuintReplayValue.Record(fields |> List.map(fun (key,v) ->
                    if key="archive" then key,(match v with QuintReplayValue.Integer "0" -> QuintReplayValue.Integer "1" | _ -> QuintReplayValue.Integer "0") else key,v))
            | _ -> name,value)
        let missingArchive={last.Actual with Bindings=archiveBindings}
        let missingArchive={missingArchive with Identity=QuintReplay.stateFingerprint missingArchive |> unwrap}
        let mutatedArchive=(observations |> List.take (observations.Length-1)) @ [{last with Actual=missingArchive}]
        match QuintReplay.compare trace mutatedArchive |> unwrap with
        | QuintReplayResult.Diverged _ -> ()
        | _ -> invalidOp "native archive-evidence mutation escaped correspondence"
        printfn "native correspondence: %d acquired reducer transitions; false-result and archive-evidence mutations diverge" observations.Length
