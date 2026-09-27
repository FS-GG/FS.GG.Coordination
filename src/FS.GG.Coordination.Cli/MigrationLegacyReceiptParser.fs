namespace FS.GG.Coordination.Cli

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type MigrationLegacyReceiptLocation =
    | PullRequestComment
    | WorkItemComment

type MigrationLegacyReceiptParserSourceKind =
    | ReceiptProtectedWriterAndParser
    | ReceiptProtectedParserOnly

type MigrationLegacyReceiptParserSource =
    {
        Family: string
        Location: MigrationLegacyReceiptLocation
        Marker: string
        SourceKind: MigrationLegacyReceiptParserSourceKind
        SourceRepository: string
        SourceRevision: string
        ParserSourcePath: string
        ParserSourceSha256: string
        WriterSourcePath: string option
        WriterSourceSha256: string option
    }

type MigrationLegacyReceipt =
    | DeliveryReceipt of obligationId: string * head: string * evidence: string
    | DeliveryCompletion of item: string * pullRequest: int * mergeSha: string * digest: string
    | CompletionCorrection of item: string * destination: string * observedAt: DateTimeOffset * digest: string
    | LegacyDoneReceipt

type MigrationLegacyReceiptRoster =
    {
        Sources: MigrationLegacyReceiptParserSource list
        RosterComplete: bool
        MissingProtectedProducers: string list
        MissingReservedPrefixRegistry: bool
    }

[<RequireQualifiedAccess>]
module MigrationLegacyReceiptParser =
    let private sourceRevision = "ca6dd7bd5d14cd3c44f54c89ee87f602c3a3abce"

    let private source family location marker kind parserPath parserDigest writer =
        {
            Family = family
            Location = location
            Marker = marker
            SourceKind = kind
            SourceRepository = "FS-GG/.github"
            SourceRevision = sourceRevision
            ParserSourcePath = parserPath
            ParserSourceSha256 = parserDigest
            WriterSourcePath = writer |> Option.map fst
            WriterSourceSha256 = writer |> Option.map snd
        }

    let roster =
        {
            Sources =
                [
                    source
                        "delivery-receipt"
                        PullRequestComment
                        "<!-- fsgg:delivery-receipt "
                        ReceiptProtectedParserOnly
                        "src/FS.GG.Coord.Cli.Lifecycle/DeliveryApplication.fs"
                        "8ef49945f964c216f48ed751ede54dd26a53a069b6b747f16090d70ca602f1fb"
                        None
                    source
                        "typed-completion"
                        WorkItemComment
                        "<!-- fsgg:delivery-completion/v1 -->"
                        ReceiptProtectedWriterAndParser
                        "src/FS.GG.Coord.Core/Delivery.fs"
                        "88ee2d9cadc74e9ebca82565bbf207be4ddbbaa67c5edb252cd433c7a086357e"
                        (Some(
                            "src/FS.GG.Coord.GitHub/Writes.fs",
                            "194e5f22d99573c6de564c17148317125dfe277dbe8e45d839161329f7905c5b"
                        ))
                    source
                        "completion-correction"
                        WorkItemComment
                        "<!-- fsgg:completion-correction/v1 -->"
                        ReceiptProtectedWriterAndParser
                        "src/FS.GG.Coord.Core/Delivery.fs"
                        "88ee2d9cadc74e9ebca82565bbf207be4ddbbaa67c5edb252cd433c7a086357e"
                        (Some(
                            "src/FS.GG.Coord.GitHub/Writes.fs",
                            "194e5f22d99573c6de564c17148317125dfe277dbe8e45d839161329f7905c5b"
                        ))
                    source
                        "legacy-done-receipt"
                        WorkItemComment
                        "<!-- fsgg:done-receipt v=1 -->"
                        ReceiptProtectedParserOnly
                        "src/FS.GG.Coord.GitHub/Done.fs"
                        "148107a73abda2ee3f2db82a4aaaf951047b8d657b54be9352f07637757a3fef"
                        None
                ]
            RosterComplete = false
            MissingProtectedProducers = [ "delivery-receipt"; "intake-receipt"; "legacy-done-receipt" ]
            MissingReservedPrefixRegistry = true
        }

    let private sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private duplicateMembers (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.countBy _.Name
        |> Seq.exists (fun (_, count) -> count <> 1)

    let private objectHasExactly names (element: JsonElement) =
        element.ValueKind = JsonValueKind.Object
        && not (duplicateMembers element)
        && (element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = Set.ofList names

    let private nonempty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if
            element.TryGetProperty(name, &value)
            && value.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(value.GetString()))
        then
            Some(value.GetString())
        else
            None

    let private exactInt (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        let mutable parsed = 0

        if
            element.TryGetProperty(name, &value)
            && value.ValueKind = JsonValueKind.Number
            && value.TryGetInt32(&parsed)
        then
            Some parsed
        else
            None

    let private boolValue (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) then
            match value.ValueKind with
            | JsonValueKind.True -> Some true
            | JsonValueKind.False -> Some false
            | _ -> None
        else
            None

    let private parseTime (value: string) =
        let mutable parsed = DateTimeOffset.MinValue

        if DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &parsed) then
            Some parsed
        else
            None

    let private deliveryPattern =
        Regex(
            "^<!-- fsgg:delivery-receipt id=(?<id>[a-z0-9][a-z0-9_.-]*) head=(?<head>[0-9A-Za-z._-]+) evidence=(?<evidence>[^ ]+) -->$",
            RegexOptions.Compiled
        )

    let private leadingLine (body: string) =
        let first =
            body.Replace("\r\n", "\n").Split('\n')
            |> Array.tryFind (fun line -> line.Trim() <> "")

        match first with
        | Some line ->
            let indent =
                line |> Seq.takeWhile (fun c -> c = ' ' || c = '\t') |> Seq.toArray |> String

            if indent.Contains('\t') || indent.Length >= 4 then
                line
            else
                line.Trim()
        | None -> ""

    type private Run =
        {
            Id: int64
            Attempt: int
            Workflow: string
            Event: string
            Branch: string
            Sha: string
            Status: string
            Conclusion: string
            Url: string
        }

    let private parseRun (element: JsonElement) =
        let names =
            [
                "id"
                "attempt"
                "workflow"
                "event"
                "branch"
                "sha"
                "status"
                "conclusion"
                "url"
            ]

        let mutable idElement = Unchecked.defaultof<JsonElement>
        let mutable id = 0L
        let mutable conclusion = Unchecked.defaultof<JsonElement>

        if
            not (objectHasExactly names element)
            || not (element.TryGetProperty("id", &idElement) && idElement.TryGetInt64(&id))
            || not (
                element.TryGetProperty("conclusion", &conclusion)
                && conclusion.ValueKind = JsonValueKind.String
            )
        then
            None
        else
            match
                exactInt "attempt" element,
                nonempty "workflow" element,
                nonempty "event" element,
                nonempty "branch" element,
                nonempty "sha" element,
                nonempty "status" element,
                nonempty "url" element
            with
            | Some attempt, Some workflow, Some event, Some branch, Some sha, Some status, Some url ->
                Some
                    {
                        Id = id
                        Attempt = attempt
                        Workflow = workflow
                        Event = event
                        Branch = branch
                        Sha = sha
                        Status = status
                        Conclusion = conclusion.GetString()
                        Url = url
                    }
            | _ -> None

    let private parseCompletion (body: string) =
        let marker = "<!-- fsgg:delivery-completion/v1 -->"

        try
            use document = JsonDocument.Parse(body.Substring(marker.Length).Trim())
            let root = document.RootElement

            let names =
                [
                    "schema"
                    "item"
                    "pullRequest"
                    "mergeSha"
                    "mergeReachable"
                    "obligationReceipts"
                    "postMergeVerification"
                    "pendingBoardWrites"
                    "freshnessToken"
                    "actionKey"
                    "completedAt"
                    "digest"
                ]

            if not (objectHasExactly names root) then
                Error "legacy-receipt-completion-shape"
            else
                match
                    nonempty "schema" root,
                    nonempty "item" root,
                    exactInt "pullRequest" root,
                    nonempty "mergeSha" root,
                    boolValue "mergeReachable" root,
                    exactInt "pendingBoardWrites" root,
                    nonempty "freshnessToken" root,
                    nonempty "actionKey" root,
                    nonempty "completedAt" root,
                    nonempty "digest" root
                with
                | Some "fsgg.coord.delivery-completion/v1",
                  Some item,
                  Some pullRequest,
                  Some mergeSha,
                  Some true,
                  Some 0,
                  Some freshness,
                  Some actionKey,
                  Some completedText,
                  Some digest when pullRequest > 0 ->
                    match parseTime completedText with
                    | None -> Error "legacy-receipt-completion-time"
                    | Some completedAt ->
                        let obligationsElement = root.GetProperty("obligationReceipts")

                        let obligations =
                            if obligationsElement.ValueKind <> JsonValueKind.Array then
                                None
                            else
                                obligationsElement.EnumerateArray()
                                |> Seq.map (fun value ->
                                    if objectHasExactly [ "id"; "kind"; "evidence"; "headSha" ] value then
                                        match
                                            nonempty "id" value,
                                            nonempty "kind" value,
                                            nonempty "evidence" value,
                                            nonempty "headSha" value
                                        with
                                        | Some id, Some kind, Some evidence, Some head ->
                                            Some(id, kind, evidence, head)
                                        | _ -> None
                                    else
                                        None)
                                |> Seq.toList
                                |> fun rows ->
                                    if rows |> List.forall Option.isSome then
                                        Some(List.choose id rows)
                                    else
                                        None

                        match obligations with
                        | None -> Error "legacy-receipt-completion-obligations"
                        | Some obligations when
                            (obligations
                             |> List.map (fun (id, _, _, _) -> id)
                             |> List.distinct
                             |> List.length)
                            <> obligations.Length
                            ->
                            Error "legacy-receipt-completion-obligations"
                        | Some obligations ->
                            let verification = root.GetProperty("postMergeVerification")

                            let parsedVerification =
                                if verification.ValueKind = JsonValueKind.Null then
                                    Some None
                                elif objectHasExactly [ "mergeSha"; "defaultBranch"; "runs" ] verification then
                                    match nonempty "mergeSha" verification, nonempty "defaultBranch" verification with
                                    | Some verifiedMerge, Some branch when verifiedMerge = mergeSha ->
                                        let runsElement = verification.GetProperty("runs")

                                        if runsElement.ValueKind <> JsonValueKind.Array then
                                            None
                                        else
                                            let runs = runsElement.EnumerateArray() |> Seq.map parseRun |> Seq.toList

                                            let runs =
                                                if runs |> List.forall Option.isSome then
                                                    Some(List.choose id runs)
                                                else
                                                    None

                                            match runs with
                                            | Some runs when
                                                not runs.IsEmpty
                                                && runs
                                                   |> List.forall (fun run ->
                                                       run.Sha = mergeSha && run.Branch = branch && run.Event = "push")
                                                && runs
                                                   |> List.exists (fun run ->
                                                       run.Status = "completed" && run.Conclusion = "success")
                                                ->
                                                Some(Some(branch, runs))
                                            | _ -> None
                                    | _ -> None
                                else
                                    None

                            match parsedVerification with
                            | None -> Error "legacy-receipt-completion-verification"
                            | Some verification ->
                                let verificationTokens =
                                    match verification with
                                    | None -> []
                                    | Some(branch, runs) ->
                                        [
                                            "postMergeVerification"
                                            "verified"
                                            mergeSha
                                            branch
                                            yield!
                                                runs
                                                |> List.sortBy (fun run -> run.Id, run.Attempt)
                                                |> List.collect (fun run ->
                                                    [
                                                        string run.Id
                                                        string run.Attempt
                                                        run.Workflow
                                                        run.Event
                                                        run.Branch
                                                        run.Sha
                                                        run.Status
                                                        run.Conclusion
                                                        run.Url
                                                    ])
                                        ]

                                let digestTokens =
                                    [
                                        item
                                        string pullRequest
                                        mergeSha
                                        string true
                                        string 0
                                        freshness
                                        actionKey
                                        completedAt.ToUniversalTime().ToString("O")
                                        yield! verificationTokens
                                        yield!
                                            obligations
                                            |> List.sortBy (fun (id, kind, _, _) -> id, kind)
                                            |> List.collect (fun (id, kind, evidence, head) ->
                                                [ id; kind; evidence; head ])
                                    ]

                                if sha256 (String.concat "\n" digestTokens) <> digest then
                                    Error "legacy-receipt-completion-digest"
                                else
                                    Ok(Some(DeliveryCompletion(item, pullRequest, mergeSha, digest)))
                | _ -> Error "legacy-receipt-completion-fields"
        with _ ->
            Error "legacy-receipt-completion-json"

    let private parseCorrection (body: string) =
        let marker = "<!-- fsgg:completion-correction/v1 -->"

        try
            use document = JsonDocument.Parse(body.Substring(marker.Length).Trim())
            let root = document.RootElement

            if not (objectHasExactly [ "schema"; "item"; "destination"; "observedAt"; "digest" ] root) then
                Error "legacy-receipt-correction-shape"
            else
                match
                    nonempty "schema" root,
                    nonempty "item" root,
                    nonempty "destination" root,
                    nonempty "observedAt" root,
                    nonempty "digest" root
                with
                | Some "fsgg.coord.completion-correction/v1",
                  Some item,
                  Some destination,
                  Some observedText,
                  Some digest when destination = "In review" || destination = "Blocked" ->
                    match parseTime observedText with
                    | Some observedAt ->
                        let expected =
                            sha256 (
                                String.concat
                                    "\n"
                                    [
                                        "fsgg.coord.completion-correction/v1"
                                        item
                                        destination
                                        observedAt.ToUniversalTime().ToString("O")
                                    ]
                            )

                        if digest <> expected then
                            Error "legacy-receipt-correction-digest"
                        else
                            Ok(Some(CompletionCorrection(item, destination, observedAt, digest)))
                    | None -> Error "legacy-receipt-correction-time"
                | _ -> Error "legacy-receipt-correction-fields"
        with _ ->
            Error "legacy-receipt-correction-json"

    let tryParse location body =
        if isNull body then
            Error "legacy-receipt-body-null"
        else
            let line = leadingLine body

            if line.StartsWith("<!-- fsgg:delivery-receipt", StringComparison.Ordinal) then
                if location <> PullRequestComment then
                    Error "legacy-receipt-location"
                else
                    let matched = deliveryPattern.Match line

                    if not matched.Success then
                        Error "legacy-receipt-delivery-shape"
                    else
                        Ok(
                            Some(
                                DeliveryReceipt(
                                    matched.Groups.["id"].Value,
                                    matched.Groups.["head"].Value,
                                    matched.Groups.["evidence"].Value
                                )
                            )
                        )
            elif body.StartsWith("<!-- fsgg:delivery-completion/v1 -->", StringComparison.Ordinal) then
                if location <> WorkItemComment then
                    Error "legacy-receipt-location"
                else
                    parseCompletion body
            elif body.StartsWith("<!-- fsgg:completion-correction/v1 -->", StringComparison.Ordinal) then
                if location <> WorkItemComment then
                    Error "legacy-receipt-location"
                else
                    parseCorrection body
            elif body.StartsWith("<!-- fsgg:done-receipt", StringComparison.Ordinal) then
                if location <> WorkItemComment then
                    Error "legacy-receipt-location"
                elif body.StartsWith("<!-- fsgg:done-receipt v=1 -->", StringComparison.Ordinal) then
                    Ok(Some LegacyDoneReceipt)
                else
                    Error "legacy-receipt-done-shape"
            else
                Ok None
