module FS.GG.Coordination.GitHubCompleteDiscoveryTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubCompleteDiscoveryQualification

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private get =
    function
    | Ok value -> value
    | Error findings -> failwithf "unexpected discovery refusal: %A" findings

let private firstStart = DateTimeOffset.Parse("2026-09-22T10:00:00Z")

let private pass (started: DateTimeOffset) =
    let completed = started.AddMinutes 2.

    {
        SourceRevision = String.replicate 40 "a"
        StartedAt = started
        CompletedAt = completed
        Authorities =
            expectedAuthorities
            |> List.map (fun authority ->
                {
                    Authority = authority
                    ObservedAt = started.AddMinutes 1.
                    PageCount = 2
                    ItemCount = 2
                    Terminal = true
                    NextCursor = None
                    HighWaterMark = $"{authority}:watermark:42"
                    Subjects =
                        [
                            { Identity = $"{authority}:1"; Revision = "41"; PayloadSha256 = sha $"{authority}:one" }
                            { Identity = $"{authority}:2"; Revision = "42"; PayloadSha256 = sha $"{authority}:two" }
                        ]
                })
    }

let private qualifyPasses first second =
    qualify
        (String.replicate 40 "b")
        (String.replicate 64 "c")
        (String.replicate 64 "d")
        ([ String.replicate 64 "1"; String.replicate 64 "2" ] |> List.sort)
        first
        second

[<Fact>]
let ``two complete quiescent reads qualify and replay`` () =
    let first = pass firstStart
    let second = pass (first.CompletedAt.AddSeconds 1.)
    let discovery = qualifyPasses first second |> get

    Assert.Equal(1, discovery.SchemaVersion)
    Assert.Equal(get (normalizedPassDigest first), discovery.NormalizedDigest)
    Assert.Equal(Ok discovery, verify discovery.Seal discovery)

[<Fact>]
let ``lost terminal page refuses qualification`` () =
    let first = pass firstStart
    let second = pass (first.CompletedAt.AddSeconds 1.)
    let authority = second.Authorities.Head

    let incomplete =
        { second with
            Authorities = { authority with Terminal = false; NextCursor = Some "next" } :: second.Authorities.Tail
        }

    match qualifyPasses first incomplete with
    | Ok _ -> Assert.Fail("incomplete pagination qualified")
    | Error findings ->
        Assert.Contains(
            GitHubCompleteDiscoveryFinding.IncompleteDiscoveryPagination authority.Authority,
            findings
        )

[<Fact>]
let ``subject added between reads prevents quiescent qualification`` () =
    let first = pass firstStart
    let second = pass (first.CompletedAt.AddSeconds 1.)
    let authority = second.Authorities.Head

    let changed =
        { second with
            Authorities =
                { authority with
                    ItemCount = 3
                    HighWaterMark = $"{authority.Authority}:watermark:43"
                    Subjects =
                        authority.Subjects
                        @ [ { Identity = $"{authority.Authority}:3"; Revision = "43"; PayloadSha256 = sha "new" } ]
                }
                :: second.Authorities.Tail
        }

    match qualifyPasses first changed with
    | Ok _ -> Assert.Fail("non-quiescent discovery qualified")
    | Error findings ->
        Assert.Contains(findings, function GitHubCompleteDiscoveryFinding.NonQuiescentDiscovery _ -> true | _ -> false)

[<Fact>]
let ``unknown authority and duplicate subject identity refuse`` () =
    let first = pass firstStart
    let second = pass (first.CompletedAt.AddSeconds 1.)
    let authority = second.Authorities.Head
    let duplicate = authority.Subjects.Head

    let invalid =
        { second with
            Authorities =
                { authority with
                    Authority = "unknown-authority"
                    ItemCount = 3
                    Subjects = duplicate :: duplicate :: authority.Subjects.Tail
                }
                :: second.Authorities.Tail
        }

    match qualifyPasses first invalid with
    | Ok _ -> Assert.Fail("unknown authority qualified")
    | Error findings ->
        Assert.Contains(GitHubCompleteDiscoveryFinding.InvalidDiscoveryAuthoritySet, findings)
        Assert.Contains(GitHubCompleteDiscoveryFinding.InvalidDiscoveryAuthority "unknown-authority", findings)

[<Fact>]
let ``qualification controls require the complete independent inventory`` () =
    let passing: GitHubCompleteDiscoveryControlResult list =
        requiredControls
        |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })

    Assert.Equal(Ok(), validateControls passing passing)
    Assert.True(validateControls passing passing.Tail |> Result.isError)
