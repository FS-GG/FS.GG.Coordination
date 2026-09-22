namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type GitHubDiscoverySubject =
    { Identity: string; Revision: string; PayloadSha256: string }

type GitHubDiscoveryAuthorityRead =
    {
        Authority: string
        ObservedAt: DateTimeOffset
        PageCount: int
        ItemCount: int
        Terminal: bool
        NextCursor: string option
        HighWaterMark: string
        Subjects: GitHubDiscoverySubject list
    }

type GitHubDiscoveryPass =
    {
        SourceRevision: string
        StartedAt: DateTimeOffset
        CompletedAt: DateTimeOffset
        Authorities: GitHubDiscoveryAuthorityRead list
    }

type GitHubCompleteDiscovery =
    {
        SchemaVersion: int
        RoadmapRevision: string
        RoadmapSha256: string
        UnitContractSha256: string
        ReceiptDigests: string list
        First: GitHubDiscoveryPass
        Second: GitHubDiscoveryPass
        NormalizedDigest: string
        Seal: string
    }

[<RequireQualifiedAccess>]
type GitHubCompleteDiscoveryFinding =
    | InvalidDiscoveryField of string
    | InvalidDiscoveryAuthoritySet
    | InvalidDiscoveryAuthority of string
    | IncompleteDiscoveryPagination of string
    | InvalidDiscoveryHighWaterMark of string
    | InvalidDiscoverySubject of string
    | NonQuiescentDiscovery of firstDigest: string * secondDigest: string
    | AlteredDiscoverySeal

type GitHubCompleteDiscoveryControl =
    | DiscoveryPrerequisites
    | DiscoveryRoadmap
    | DiscoveryAuthorityPopulation
    | DiscoveryOpenAndClosedIssues
    | DiscoveryProjectItemsAndFields
    | DiscoveryHierarchyAndDependencies
    | DiscoveryClaimAndEventStreams
    | DiscoveryReviewDeliveryAndRelease
    | DiscoveryRepositorySettings
    | DiscoveryWorkflowPins
    | DiscoveryReceiverIdentities
    | DiscoveryTerminalPagination
    | DiscoveryHighWaterMarks
    | DiscoveryCanonicalSubjects
    | DiscoveryTwoPassQuiescence
    | DiscoveryUnknownSubjectRefusal
    | DiscoveryLostPageRefusal
    | DiscoveryReplay
    | DiscoveryNoMutation

type GitHubCompleteDiscoveryControlResult =
    { Control: GitHubCompleteDiscoveryControl; ControlPassed: bool; BaselineGreen: bool }

type GitHubCompleteDiscoveryQualificationFinding =
    { Code: string; ControlId: string; Message: string }

module GitHubCompleteDiscoveryQualification =
    let expectedAuthorities =
        [
            "issues-open-and-relevant-closed"
            "project-items"
            "project-fields"
            "hierarchy-and-dependencies"
            "claim-and-event-streams"
            "review-delivery-release-records"
            "repository-settings"
            "workflow-pins"
            "receiver-identities"
        ]

    let requiredControls =
        [
            DiscoveryPrerequisites
            DiscoveryRoadmap
            DiscoveryAuthorityPopulation
            DiscoveryOpenAndClosedIssues
            DiscoveryProjectItemsAndFields
            DiscoveryHierarchyAndDependencies
            DiscoveryClaimAndEventStreams
            DiscoveryReviewDeliveryAndRelease
            DiscoveryRepositorySettings
            DiscoveryWorkflowPins
            DiscoveryReceiverIdentities
            DiscoveryTerminalPagination
            DiscoveryHighWaterMarks
            DiscoveryCanonicalSubjects
            DiscoveryTwoPassQuiescence
            DiscoveryUnknownSubjectRefusal
            DiscoveryLostPageRefusal
            DiscoveryReplay
            DiscoveryNoMutation
        ]

    let controlId =
        function
        | DiscoveryPrerequisites -> "discovery-prerequisites"
        | DiscoveryRoadmap -> "discovery-roadmap"
        | DiscoveryAuthorityPopulation -> "discovery-authority-population"
        | DiscoveryOpenAndClosedIssues -> "discovery-open-and-closed-issues"
        | DiscoveryProjectItemsAndFields -> "discovery-project-items-and-fields"
        | DiscoveryHierarchyAndDependencies -> "discovery-hierarchy-and-dependencies"
        | DiscoveryClaimAndEventStreams -> "discovery-claim-and-event-streams"
        | DiscoveryReviewDeliveryAndRelease -> "discovery-review-delivery-release"
        | DiscoveryRepositorySettings -> "discovery-repository-settings"
        | DiscoveryWorkflowPins -> "discovery-workflow-pins"
        | DiscoveryReceiverIdentities -> "discovery-receiver-identities"
        | DiscoveryTerminalPagination -> "discovery-terminal-pagination"
        | DiscoveryHighWaterMarks -> "discovery-high-water-marks"
        | DiscoveryCanonicalSubjects -> "discovery-canonical-subjects"
        | DiscoveryTwoPassQuiescence -> "discovery-two-pass-quiescence"
        | DiscoveryUnknownSubjectRefusal -> "discovery-unknown-subject-refusal"
        | DiscoveryLostPageRefusal -> "discovery-lost-page-refusal"
        | DiscoveryReplay -> "discovery-replay"
        | DiscoveryNoMutation -> "discovery-no-mutation"

    let private validText value = not (String.IsNullOrWhiteSpace value)

    let private isSha length value =
        validText value
        && value.Length = length
        && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private framed values = values |> List.map frame |> String.concat ""

    let private hash (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private unique values = List.length values = (values |> Set.ofList |> Set.count)

    let private validatePass (pass: GitHubDiscoveryPass) =
        let findings = ResizeArray<GitHubCompleteDiscoveryFinding>()

        if not (isSha 40 pass.SourceRevision) then
            findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryField "sourceRevision")

        if pass.StartedAt > pass.CompletedAt then
            findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryField "observationWindow")

        let authorityIds = pass.Authorities |> List.map _.Authority

        if authorityIds <> expectedAuthorities || not (unique authorityIds) then
            findings.Add GitHubCompleteDiscoveryFinding.InvalidDiscoveryAuthoritySet

        for authority in pass.Authorities do
            if not (List.contains authority.Authority expectedAuthorities) then
                findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryAuthority authority.Authority)

            if authority.ObservedAt < pass.StartedAt || authority.ObservedAt > pass.CompletedAt then
                findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryAuthority authority.Authority)

            if authority.PageCount < 1 || authority.ItemCount <> authority.Subjects.Length then
                findings.Add(GitHubCompleteDiscoveryFinding.IncompleteDiscoveryPagination authority.Authority)

            if not authority.Terminal || authority.NextCursor.IsSome then
                findings.Add(GitHubCompleteDiscoveryFinding.IncompleteDiscoveryPagination authority.Authority)

            if not (validText authority.HighWaterMark) then
                findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryHighWaterMark authority.Authority)

            let identities = authority.Subjects |> List.map _.Identity

            if identities <> List.sort identities || not (unique identities) then
                findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoverySubject authority.Authority)

            for subject in authority.Subjects do
                if not (validText subject.Identity && validText subject.Revision && isSha 64 subject.PayloadSha256) then
                    findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoverySubject authority.Authority)

        findings |> Seq.distinct |> Seq.toList

    let normalizedPassDigest pass =
        match validatePass pass with
        | _ :: _ as findings -> Error findings
        | [] ->
            pass.Authorities
            |> List.collect (fun authority ->
                [ authority.Authority; authority.HighWaterMark; string authority.PageCount; string authority.ItemCount ]
                @ (authority.Subjects
                   |> List.collect (fun subject -> [ subject.Identity; subject.Revision; subject.PayloadSha256 ])))
            |> framed
            |> hash
            |> Ok

    let private sealParts roadmapRevision roadmapSha unitSha receipts first second digest =
        [
            "fsgg.coordination.github-complete-discovery/1"
            roadmapRevision
            roadmapSha
            unitSha
            framed receipts
            first.SourceRevision
            first.StartedAt.ToUniversalTime().ToString("O")
            first.CompletedAt.ToUniversalTime().ToString("O")
            second.SourceRevision
            second.StartedAt.ToUniversalTime().ToString("O")
            second.CompletedAt.ToUniversalTime().ToString("O")
            digest
        ]
        |> framed
        |> hash

    let qualify
        (roadmapRevision: string)
        (roadmapSha256: string)
        (unitContractSha256: string)
        (receiptDigests: string list)
        (first: GitHubDiscoveryPass)
        (second: GitHubDiscoveryPass)
        =
        let findings = ResizeArray<GitHubCompleteDiscoveryFinding>()

        if not (isSha 40 roadmapRevision) then
            findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryField "roadmapRevision")

        if not (isSha 64 roadmapSha256) then
            findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryField "roadmapSha256")

        if not (isSha 64 unitContractSha256) then
            findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryField "unitContractSha256")

        if receiptDigests.Length <> 2 || receiptDigests <> List.sort receiptDigests || not (unique receiptDigests) || receiptDigests |> List.exists (isSha 64 >> not) then
            findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryField "receiptDigests")

        if first.SourceRevision <> second.SourceRevision || second.StartedAt < first.CompletedAt then
            findings.Add(GitHubCompleteDiscoveryFinding.InvalidDiscoveryField "passSequence")

        let firstDigest = normalizedPassDigest first
        let secondDigest = normalizedPassDigest second

        match firstDigest with
        | Error passFindings -> passFindings |> List.iter findings.Add
        | Ok _ -> ()

        match secondDigest with
        | Error passFindings -> passFindings |> List.iter findings.Add
        | Ok _ -> ()

        match firstDigest, secondDigest with
        | Ok one, Ok two when one <> two -> findings.Add(GitHubCompleteDiscoveryFinding.NonQuiescentDiscovery(one, two))
        | _ -> ()

        match findings |> Seq.distinct |> Seq.toList, firstDigest with
        | [], Ok digest ->
            let seal = sealParts roadmapRevision roadmapSha256 unitContractSha256 receiptDigests first second digest
            Ok
                {
                    SchemaVersion = 1
                    RoadmapRevision = roadmapRevision
                    RoadmapSha256 = roadmapSha256
                    UnitContractSha256 = unitContractSha256
                    ReceiptDigests = receiptDigests
                    First = first
                    Second = second
                    NormalizedDigest = digest
                    Seal = seal
                }
        | refusal, _ -> Error refusal

    let verify expectedSeal discovery =
        let actual =
            sealParts
                discovery.RoadmapRevision
                discovery.RoadmapSha256
                discovery.UnitContractSha256
                discovery.ReceiptDigests
                discovery.First
                discovery.Second
                discovery.NormalizedDigest

        if discovery.SchemaVersion = 1 && isSha 64 expectedSeal && actual = discovery.Seal && actual = expectedSeal then
            match normalizedPassDigest discovery.First, normalizedPassDigest discovery.Second with
            | Ok first, Ok second when first = second && first = discovery.NormalizedDigest -> Ok discovery
            | Ok first, Ok second -> Error [ GitHubCompleteDiscoveryFinding.NonQuiescentDiscovery(first, second) ]
            | Error findings, _
            | _, Error findings -> Error findings
        else
            Error [ GitHubCompleteDiscoveryFinding.AlteredDiscoverySeal ]

    let validateControls generated independent =
        let validate lane rows =
            let ids = rows |> List.map (fun row -> row.Control)
            [
                if ids <> requiredControls || not (unique ids) then
                    yield { Code = "DISCOVERY-CONTROLS"; ControlId = lane; Message = "control inventory differs" }

                for row in rows do
                    if not (row.ControlPassed && row.BaselineGreen) then
                        yield { Code = "DISCOVERY-CONTROL-RED"; ControlId = controlId row.Control; Message = $"{lane} control did not pass" }
            ]

        match validate "generated" generated @ validate "independent" independent with
        | [] -> Ok()
        | findings -> Error findings
