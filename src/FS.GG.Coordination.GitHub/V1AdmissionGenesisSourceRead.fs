namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Text.Json

[<RequireQualifiedAccess>]
module V1AdmissionGenesisSourceRead =
    let private exactProperties expected (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> List.distinct |> List.length)
            && Set.ofList names = Set.ofList expected

    let private timestamp value =
        DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-ddTHH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
        )

    let decode asOf (raw: ReadOnlyMemory<byte>) =
        try
            if raw.Length > 8192 then
                Error [ "genesis-source-evidence-size" ]
            else
                use document = JsonDocument.Parse raw
                let root = document.RootElement
                let fields =
                    [ "schema"; "observedAt"; "repository"; "repositoryId"; "sourceCommit"
                      "sourceTree"; "firstMainHead"; "secondMainHead"; "compareStatus"
                      "compareBase"; "compareHead"; "mergeBase" ]
                let text (name: string) = root.GetProperty(name).GetString()
                let observedAt = text "observedAt" |> timestamp
                let oid name = text name |> V1AdmissionRegistry.gitObjectId |> Result.defaultWith invalidOp
                let source = oid "sourceCommit"
                let tree = oid "sourceTree"
                let first = oid "firstMainHead"
                let second = oid "secondMainHead"
                let baseCommit = oid "compareBase"
                let headCommit = oid "compareHead"
                let mergeBase = oid "mergeBase"
                let status = text "compareStatus"
                if not (exactProperties fields root)
                   || text "schema" <> "fsgg.v1-admission-genesis-source-read/1"
                   || text "repository" <> "FS-GG/FS.GG.Coordination"
                   || root.GetProperty("repositoryId").GetInt64() <> 1346720714L
                   || observedAt > asOf
                   || asOf - observedAt > TimeSpan.FromMinutes 2.
                   || first <> second
                   || baseCommit <> source
                   || headCommit <> first
                   || not (Set.contains status (Set.ofList [ "ahead"; "behind"; "diverged"; "identical" ]))
                   || (status = "identical" && (source <> first || mergeBase <> source))
                   || (status = "ahead" && (source = first || mergeBase <> source)) then
                    Error [ "genesis-source-evidence-binding" ]
                else
                    Ok
                        { ObservedAt = observedAt
                          RepositoryId = 1346720714L
                          Commit = source
                          Tree = tree
                          IsOnMain = status = "identical" || status = "ahead" }
        with _ ->
            Error [ "genesis-source-evidence-invalid" ]
