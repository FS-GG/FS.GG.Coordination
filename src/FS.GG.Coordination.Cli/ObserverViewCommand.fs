namespace FS.GG.Coordination.Cli

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Orchestration.Observer

[<RequireQualifiedAccess>]
module ObserverViewCommand =
    let private usage () =
        eprintfn "usage: observer-view --events <base64-event-lines> [--format text|json]"
        2

    let private readEvents path =
        try
            let info = FileInfo path
            if not info.Exists then Error "event file does not exist"
            elif info.Length > 16L * 1024L * 1024L then Error "event file exceeds 16 MiB"
            else
                let lines = File.ReadAllLines path |> Array.filter (String.IsNullOrWhiteSpace >> not)
                if lines.Length > 10_000 then Error "event file exceeds 10000 events"
                else
                    let decoded =
                        lines
                        |> Array.mapi (fun index line ->
                        try
                            Convert.FromBase64String line
                            |> ObserverEventCodec.tryDecode
                            |> Result.mapError (fun reason -> $"event {index + 1}: {reason}")
                        with :? FormatException -> Error $"event {index + 1}: invalid-base64")
                    match decoded |> Array.tryPick (function Error reason -> Some reason | _ -> None) with
                    | Some reason -> Error reason
                    | None -> decoded |> Array.choose (function Ok value -> Some value | _ -> None) |> Array.toList |> Ok
        with exceptionValue -> Error $"event file unreadable: {exceptionValue.GetType().Name}"

    let private stage = function
        | Conversation -> "conversation"
        | Proposed -> "proposed"
        | Approved -> "approved"
        | DurableAcceptance -> "durable-acceptance"
        | EffectComplete -> "effect-complete"

    let private json view =
        let rows = JsonArray()
        for row in view.Rows do
            let value = JsonObject()
            value["stage"] <- JsonValue.Create(stage row.Stage)
            value["identity"] <- JsonValue.Create(row.Identity)
            value["detail"] <- JsonValue.Create(row.Detail)
            value["recordedAt"] <- JsonValue.Create(row.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            rows.Add value
        let root = JsonObject()
        root["schema"] <- JsonValue.Create("fsgg.orchestration.observer-export-view/1")
        root["authority"] <- JsonValue.Create("unverified-event-export")
        root["sessionId"] <- view.SessionId |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode) |> Option.defaultValue null
        root["sequence"] <- JsonValue.Create(view.Sequence)
        root["observationRevision"] <- view.ObservationRevision |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode) |> Option.defaultValue null
        root["rows"] <- rows
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    let private text view =
        seq {
            let session = view.SessionId |> Option.defaultValue "unopened"
            let observation = view.ObservationRevision |> Option.defaultValue "none"
            yield $"observer authority=unverified-event-export session={session} sequence={view.Sequence} observation={observation}"
            for row in view.Rows do
                yield $"{stage row.Stage}\t{row.Identity}\t{row.Detail}\t{row.RecordedAt.ToUniversalTime():O}"
        }
        |> String.concat Environment.NewLine

    let run arguments =
        let rec parse path format remaining =
            match remaining with
            | "--events" :: value :: tail -> parse (Some value) format tail
            | "--format" :: value :: tail when value = "text" || value = "json" -> parse path value tail
            | [] -> Ok(path, format)
            | _ -> Error()
        match parse None "text" (List.ofArray arguments) with
        | Error _ -> usage()
        | Ok(None, _) -> usage()
        | Ok(Some path, format) ->
            match readEvents path with
            | Error reason -> eprintfn "%s" reason; 1
            | Ok events ->
                try
                    let view = events |> Observer.replay |> ObserverProjection.render
                    printfn "%s" (if format = "json" then json view else text view)
                    0
                with exceptionValue ->
                    eprintfn "event history is invalid: %s" exceptionValue.Message
                    1
