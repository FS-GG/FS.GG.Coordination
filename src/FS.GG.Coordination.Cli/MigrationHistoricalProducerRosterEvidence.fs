namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type MigrationHistoricalProducerRole =
    | ExecutableWriter
    | AgentAuthoringProtocol

type MigrationHistoricalProducerSourceEvidence =
    {
        Family: string
        Role: MigrationHistoricalProducerRole
        Repository: string
        Revision: string
        Path: string
        BlobOid: string
        BlobSha256: string
    }

type MigrationHistoricalProducerNativePage =
    {
        Repository: string
        SubjectKind: string
        SubjectNumber: int
        RequestedUri: string
        NextUri: string option
        PayloadBytes: byte array
    }

type MigrationHistoricalProducerFamilyEvidence =
    {
        Family: string
        ProducerStatus: string
        SurvivorStatus: string
        SurvivorCommentIds: int64 list
        HistoricalCompleteness: string
        LossDisposition: string
    }

type MigrationHistoricalProducerSurvivorEvidence =
    {
        Family: string
        Repository: string
        SubjectKind: string
        SubjectNumber: int
        CommentId: int64
    }

type MigrationHistoricalProducerRosterPlan =
    {
        CutoffUtc: DateTimeOffset
        Sources: MigrationHistoricalProducerSourceEvidence list
        PriorCensusStates: (string * string) list
        Survivors: MigrationHistoricalProducerSurvivorEvidence list
        Families: MigrationHistoricalProducerFamilyEvidence list
        UnresolvedProducerFamilies: string list
        UnresolvedHistoryFamilies: string list
        ClaimEventAuthorityAvailable: bool
        Fingerprint: string
    }

[<RequireQualifiedAccess>]
module MigrationHistoricalProducerRosterEvidence =
    let private legacyDoneRevision = "e356b91a5235cb4b0a31c786a31331d6065254bf"
    let private deliveryRevision = "45b93a1182f154536c36646b56ff4625c2ab0ad5"

    let expectedSources =
        [
            {
                Family = "legacy-done-receipt"
                Role = MigrationHistoricalProducerRole.ExecutableWriter
                Repository = "FS-GG/.github"
                Revision = legacyDoneRevision
                Path = "src/FS.GG.Coord.GitHub/Writes.fs"
                BlobOid = "75f0d809cddff0b92a9cce56605d7b9f3a9e66f5"
                BlobSha256 = "f4b19a05b722f6b3a457c8a4f48586a28471032f40e245058ef2f696621d2627"
            }
            {
                Family = "legacy-done-receipt"
                Role = MigrationHistoricalProducerRole.ExecutableWriter
                Repository = "FS-GG/.github"
                Revision = legacyDoneRevision
                Path = "src/FS.GG.Coord.Cli/Client.fs"
                BlobOid = "9e883859a8c2933a1018c1084c886593ce71eb50"
                BlobSha256 = "38005516e5be881ada6127156f026bf285be011813177418417cdd6b8342f99d"
            }
            {
                Family = "legacy-done-receipt"
                Role = MigrationHistoricalProducerRole.ExecutableWriter
                Repository = "FS-GG/.github"
                Revision = legacyDoneRevision
                Path = "src/FS.GG.Coord.GitHub/Done.fs"
                BlobOid = "2c5188e949fb0c64b50cbcbec54aad1fda1298c5"
                BlobSha256 = "6c03422d163b8ae2ced3cc749f6a34df2f72f7ec8f6561d1ef422f609c4b98b6"
            }
            {
                Family = "delivery-receipt"
                Role = MigrationHistoricalProducerRole.AgentAuthoringProtocol
                Repository = "FS-GG/.github"
                Revision = deliveryRevision
                Path = ".agents/skills/pnext-item/SKILL.md"
                BlobOid = "78240759d1eb9022328b5d061f768cfdd57f21c0"
                BlobSha256 = "3e73eb76410db4f3900c215cd5f1ecc80beb7ac0329b010f675997be59e0323f"
            }
        ]

    let private expectedSurvivors =
        [
            "legacy-done-receipt", "FS-GG/FS.GG.Coordination", "issue", 274, 5522351065L
            "legacy-done-receipt", "FS-GG/FS.GG.Net", "issue", 71, 5301936886L
            "delivery-receipt", "FS-GG/.github", "pull-request", 2214, 5178100645L
            "delivery-receipt", "FS-GG/.github", "pull-request", 2879, 5386930796L
            "delivery-receipt", "FS-GG/.github", "pull-request", 3231, 5552693865L
        ]

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private frame (value: string) =
        $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private fingerprint values =
        values |> Seq.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> sha256

    let private refuse reason =
        Error $"historical-producer-roster-refused:{reason}"

    let private baseUri repository number =
        $"https://api.github.com/repos/{repository}/issues/{number}/comments?per_page=100"

    let private pageNumber (uri: Uri) =
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun item ->
            match item.Split('=', 2) with
            | [| "page"; value |] ->
                let mutable parsed = 0

                if
                    Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, &parsed)
                    && parsed > 0
                then
                    Some parsed
                else
                    None
            | _ -> None)
        |> Option.defaultValue 1

    let private validUri repository number expectedPage (value: string) =
        let mutable uri = Unchecked.defaultof<Uri>

        Uri.TryCreate(value, UriKind.Absolute, &uri)
        && uri.Scheme = Uri.UriSchemeHttps
        && uri.Host = "api.github.com"
        && uri.AbsolutePath = $"/repos/{repository}/issues/{number}/comments"
        && uri.Query =
            (if expectedPage = 1 then
                 "?per_page=100"
             else
                 $"?per_page=100&page={expectedPage}")
        && pageNumber uri = expectedPage

    let private parsePage cutoff (page: MigrationHistoricalProducerNativePage) =
        try
            use document = JsonDocument.Parse page.PayloadBytes

            if document.RootElement.ValueKind <> JsonValueKind.Array then
                None
            else
                document.RootElement.EnumerateArray()
                |> Seq.map (fun row ->
                    if row.ValueKind <> JsonValueKind.Object then
                        invalidOp "row"

                    let id = row.GetProperty("id").GetInt64()
                    let node = row.GetProperty("node_id").GetString()
                    let body = row.GetProperty("body").GetString()
                    let created = row.GetProperty("created_at").GetDateTimeOffset().ToUniversalTime()

                    if id <= 0L || String.IsNullOrWhiteSpace node || isNull body || created > cutoff then
                        invalidOp "row"

                    id, body, created)
                |> Seq.toList
                |> Some
        with
        | :? JsonException
        | :? InvalidOperationException
        | :? KeyNotFoundException
        | :? FormatException -> None

    let private validatePass cutoff (pages: MigrationHistoricalProducerNativePage list) =
        let groups =
            pages
            |> List.groupBy (fun (page: MigrationHistoricalProducerNativePage) ->
                page.Repository, page.SubjectKind, page.SubjectNumber)

        let expectedSubjects =
            expectedSurvivors
            |> List.map (fun (_, repo, kind, number, _) -> repo, kind, number)
            |> Set.ofList

        if (groups |> List.map fst |> Set.ofList) <> expectedSubjects then
            refuse "native-subject-population"
        else
            groups
            |> List.fold
                (fun state ((repository, kind, number), stream) ->
                    state
                    |> Result.bind (fun accumulated ->
                        let ordered = stream

                        let rec walk
                            expectedPage
                            (remaining: MigrationHistoricalProducerNativePage list)
                            rows
                            pageProofs
                            =
                            match remaining with
                            | [] -> Ok(rows, pageProofs)
                            | page :: tail ->
                                let nextExpected = expectedPage + 1

                                if
                                    page.SubjectKind <> kind
                                    || not (validUri repository number expectedPage page.RequestedUri)
                                    || (expectedPage = 1 && page.RequestedUri <> baseUri repository number)
                                then
                                    refuse "native-request-identity"
                                else
                                    match parsePage cutoff page with
                                    | None -> refuse "native-page-shape"
                                    | Some parsed ->
                                        match page.NextUri, tail with
                                        | None, [] ->
                                            walk
                                                nextExpected
                                                []
                                                (rows @ parsed)
                                                (pageProofs @ [ page.RequestedUri, sha256 page.PayloadBytes ])
                                        | Some next, _ :: _ when validUri repository number nextExpected next ->
                                            match tail with
                                            | following :: _ when following.RequestedUri = next ->
                                                walk
                                                    nextExpected
                                                    tail
                                                    (rows @ parsed)
                                                    (pageProofs @ [ page.RequestedUri, sha256 page.PayloadBytes ])
                                            | _ -> refuse "native-page-chain"
                                        | _ -> refuse "native-page-chain"

                        walk 1 ordered [] []
                        |> Result.bind (fun (rows, pageProofs) ->
                            let ids = rows |> List.map (fun (id, _, _) -> id)

                            if ids.Length <> (ids |> Set.ofList |> Set.count) then
                                refuse "native-duplicate-comment"
                            else
                                Ok(accumulated @ [ (repository, kind, number), rows, pageProofs ]))))
                (Ok [])

    let private markerMatches family (body: string) =
        match family with
        | "legacy-done-receipt" -> body.Contains("<!-- fsgg:done-receipt v=1 -->", StringComparison.Ordinal)
        | "delivery-receipt" -> body.Contains("<!-- fsgg:delivery-receipt ", StringComparison.Ordinal)
        | _ -> false

    let prepare
        (proposal: MigrationHistoricalLossProposal)
        (sourceReads: MigrationHistoricalProducerSourceEvidence list)
        firstPass
        secondPass
        =
        let roster = MigrationLegacyReceiptParser.roster

        let expectedFamilies =
            [ "delivery-receipt"; "intake-receipt"; "legacy-done-receipt" ]

        let priorStates =
            proposal.Families
            |> List.map (fun family -> family.Family, family.ObservationState)

        let validPriorFamily (family: MigrationHistoricalLossFamilyCensus) =
            let expectedState =
                if family.Observations.IsEmpty then
                    "observed-zero"
                else
                    $"observed-{family.Observations.Length}"

            family.ObservationState = expectedState
            && family.ProducerGap = $"protected-producer-unavailable:{family.Family}"
            && family.HistoryGap = $"historical-completeness-unresolved:{family.Family}"
            && family.Observations
               |> List.forall (fun observation -> observation.Family = family.Family)

        if sourceReads <> expectedSources then
            refuse "protected-source-identity"
        elif roster.RosterComplete || roster.MissingProtectedProducers <> expectedFamilies then
            refuse "source-roster-baseline"
        elif
            proposal.CutoffUtc.Offset <> TimeSpan.Zero
            || proposal.UnresolvedProducerFamilies <> expectedFamilies
            || proposal.UnresolvedHistoryFamilies <> expectedFamilies
            || (proposal.Families |> List.map _.Family) <> expectedFamilies
            || not (proposal.Families |> List.forall validPriorFamily)
        then
            refuse "census-baseline-or-cutoff"
        else
            validatePass proposal.CutoffUtc firstPass
            |> Result.bind (fun first ->
                validatePass proposal.CutoffUtc secondPass
                |> Result.bind (fun second ->
                    if first <> second then
                        refuse "two-pass-native-drift"
                    else
                        let survivors =
                            expectedSurvivors
                            |> List.map (fun (family, repository, kind, number, commentId) ->
                                let found =
                                    first
                                    |> List.tryFind (fun ((repo, subjectKind, subjectNumber), _, _) ->
                                        repo = repository && subjectKind = kind && subjectNumber = number)
                                    |> Option.bind (fun (_, comments, _) ->
                                        comments |> List.tryFind (fun (id, _, _) -> id = commentId))

                                match found with
                                | Some(_, body, _) when markerMatches family body ->
                                    Ok
                                        {
                                            Family = family
                                            Repository = repository
                                            SubjectKind = kind
                                            SubjectNumber = number
                                            CommentId = commentId
                                        }
                                | _ -> refuse $"survivor-missing-or-wrong-family:{commentId}")

                        match
                            survivors
                            |> List.tryPick (function
                                | Error error -> Some error
                                | _ -> None)
                        with
                        | Some error -> Error error
                        | None ->
                            let found =
                                survivors
                                |> List.choose (function
                                    | Ok value -> Some value
                                    | _ -> None)

                            let family family producer =
                                let ids =
                                    found
                                    |> List.choose (fun survivor ->
                                        if survivor.Family = family then
                                            Some survivor.CommentId
                                        else
                                            None)
                                    |> List.sort

                                {
                                    Family = family
                                    ProducerStatus = producer
                                    SurvivorStatus = $"observed-nonzero:{ids.Length}"
                                    SurvivorCommentIds = ids
                                    HistoricalCompleteness = $"historical-completeness-unresolved:{family}"
                                    LossDisposition = $"loss-disposition-unapproved:{family}"
                                }

                            let families =
                                [
                                    family "delivery-receipt" "protected-agent-authoring-protocol-recovered"
                                    {
                                        Family = "intake-receipt"
                                        ProducerStatus = "protected-producer-unavailable:intake-receipt"
                                        SurvivorStatus = priorStates |> List.find (fst >> (=) "intake-receipt") |> snd
                                        SurvivorCommentIds = []
                                        HistoricalCompleteness = "historical-completeness-unresolved:intake-receipt"
                                        LossDisposition = "loss-disposition-unapproved:intake-receipt"
                                    }
                                    family "legacy-done-receipt" "protected-executable-producer-recovered"
                                ]

                            let digest =
                                fingerprint (
                                    seq {
                                        yield "fsgg.migration-historical-producer-roster-evidence/1"
                                        yield proposal.CutoffUtc.ToString("O")

                                        for source in expectedSources do
                                            yield source.Family
                                            yield string source.Role
                                            yield source.Repository
                                            yield source.Revision
                                            yield source.Path
                                            yield source.BlobOid
                                            yield source.BlobSha256

                                        for page in firstPass do
                                            yield page.Repository
                                            yield page.SubjectKind
                                            yield string page.SubjectNumber
                                            yield page.RequestedUri
                                            yield defaultArg page.NextUri ""
                                            yield sha256 page.PayloadBytes

                                        for family in families do
                                            yield family.Family
                                            yield family.ProducerStatus
                                            yield family.SurvivorStatus
                                            yield family.HistoricalCompleteness
                                            yield family.LossDisposition

                                            for id in family.SurvivorCommentIds do
                                                yield string id

                                        for survivor in found do
                                            yield survivor.Family
                                            yield survivor.Repository
                                            yield survivor.SubjectKind
                                            yield string survivor.SubjectNumber
                                            yield string survivor.CommentId
                                    }
                                )

                            Ok
                                {
                                    CutoffUtc = proposal.CutoffUtc
                                    Sources = expectedSources
                                    PriorCensusStates = priorStates
                                    Survivors = found
                                    Families = families
                                    UnresolvedProducerFamilies = [ "intake-receipt" ]
                                    UnresolvedHistoryFamilies = expectedFamilies
                                    ClaimEventAuthorityAvailable = false
                                    Fingerprint = digest
                                }))
