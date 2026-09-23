module FS.GG.Coordination.GitHubTypedTransformTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubTypedTransformQualification

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private revision character = String.replicate 40 character
let private digest character = String.replicate 64 character

let private obligations =
    requiredFamilies
    |> List.map (fun family -> { SubjectIdentity = "issue:1"; Family = family })

let private candidate identity =
    {
        TargetIdentity = identity
        TargetSchema = "github-v2/1"
        PayloadSha256 = sha $"payload:{identity}"
        MappingSha256 = sha $"mapping:{identity}"
    }

let private transforms =
    obligations
    |> List.mapi (fun index obligation ->
        let globalId = $"GLOBAL_{index:D2}"
        let decision =
            match index % 3 with
            | 0 ->
                GitHubTypedTransformDecision.Migrated
                    {
                        TargetIdentity = $"v2:{obligation.SubjectIdentity}:{familyId obligation.Family}"
                        GlobalId = globalId
                        TargetSchema = "github-v2/1"
                        PayloadSha256 = sha $"result:{index}"
                        MappingSha256 = sha $"mapping:{index}"
                    }
            | 1 ->
                GitHubTypedTransformDecision.Ambiguous
                    {
                        Reason = "two canonical targets remain"
                        Candidates = [ candidate $"candidate-a:{index}"; candidate $"candidate-b:{index}" ]
                        EvidenceSha256 = sha $"ambiguity:{index}"
                    }
            | _ ->
                GitHubTypedTransformDecision.Unsupported
                    {
                        Code = "UNSUPPORTED-SOURCE-SHAPE"
                        Reason = "source shape has no v2 representation"
                        EvidenceSha256 = sha $"unsupported:{index}"
                    }

        {
            SubjectIdentity = obligation.SubjectIdentity
            GlobalId = globalId
            Family = obligation.Family
            SourceSchema = "github-v1/1"
            SourceBytesSha256 = sha $"bytes:{index}"
            SourceValueSha256 = sha $"value:{index}"
            Decision = decision
        })

let private qualifyBaseline () =
    qualify
        "typed-transform-gs2-09-3-fixture"
        (revision "a")
        (digest "b")
        (digest "c")
        (digest "d")
        (digest "e")
        (digest "f")
        { Name = "github-v2-transformer"; Version = "1.0.0"; Sha256 = sha "transformer"; Bytes = 42L }
        obligations
        transforms
        (DateTimeOffset.Parse "2026-09-23T01:00:00Z")

let private get =
    function
    | Ok value -> value
    | Error findings -> failwithf "unexpected typed-transform refusal: %A" findings

let private refusal =
    function
    | Error findings -> findings
    | Ok _ -> failwith "invalid typed transform qualified"

[<Fact>]
let ``all typed transform families qualify and deterministically replay`` () =
    let first = qualifyBaseline () |> get
    let second = qualifyBaseline () |> get
    Assert.Equal(first, second)
    Assert.Equal(10, requiredFamilies.Length)
    Assert.Equal(Ok first, verify obligations first.Seal first)

[<Fact>]
let ``missing and reordered transform obligations refuse`` () =
    let baseline = qualifyBaseline () |> get
    let missing = { baseline with Transforms = baseline.Transforms.Tail }
    Assert.Contains(GitHubTypedTransformFinding.InvalidTransformPopulation, verify obligations baseline.Seal missing |> refusal)

    let reordered = { baseline with Obligations = List.rev baseline.Obligations; Transforms = List.rev baseline.Transforms }
    Assert.Contains(GitHubTypedTransformFinding.InvalidObligationPopulation, verify obligations baseline.Seal reordered |> refusal)

[<Fact>]
let ``malformed ambiguity and migrated global id mismatch refuse`` () =
    let baseline = qualifyBaseline () |> get
    let ambiguous = baseline.Transforms[1]

    let badAmbiguous =
        match ambiguous.Decision with
        | GitHubTypedTransformDecision.Ambiguous value ->
            { ambiguous with Decision = GitHubTypedTransformDecision.Ambiguous { value with Candidates = value.Candidates.Tail } }
        | _ -> failwith "fixture outcome differs"

    let changedAmbiguous = { baseline with Transforms = baseline.Transforms |> List.updateAt 1 badAmbiguous }
    Assert.Contains(GitHubTypedTransformFinding.InvalidAmbiguousTransform "issue:1:body-metadata", verify obligations baseline.Seal changedAmbiguous |> refusal)

    let migrated = baseline.Transforms.Head
    let badMigrated =
        match migrated.Decision with
        | GitHubTypedTransformDecision.Migrated value ->
            { migrated with Decision = GitHubTypedTransformDecision.Migrated { value with GlobalId = "DIFFERENT" } }
        | _ -> failwith "fixture outcome differs"

    let changedMigrated = { baseline with Transforms = badMigrated :: baseline.Transforms.Tail }
    Assert.Contains(GitHubTypedTransformFinding.InvalidMigratedTransform "issue:1:blockers", verify obligations baseline.Seal changedMigrated |> refusal)

[<Fact>]
let ``changed typed result and changed seal refuse`` () =
    let baseline = qualifyBaseline () |> get
    let unsupported = baseline.Transforms[2]
    let changed =
        match unsupported.Decision with
        | GitHubTypedTransformDecision.Unsupported value ->
            { unsupported with Decision = GitHubTypedTransformDecision.Unsupported { value with Reason = "changed" } }
        | _ -> failwith "fixture outcome differs"

    let altered = { baseline with Transforms = baseline.Transforms |> List.updateAt 2 changed }
    Assert.Equal(Error [ GitHubTypedTransformFinding.AlteredTransformDigest ], verify obligations baseline.Seal altered)
    Assert.Equal(Error [ GitHubTypedTransformFinding.AlteredTransformSeal ], verify obligations (digest "9") baseline)

[<Fact>]
let ``typed transform controls require complete green independent inventories`` () =
    let passing: GitHubTypedTransformControlResult list =
        requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })

    Assert.Equal(Ok(), validateControls passing passing)
    Assert.True(validateControls passing passing.Tail |> Result.isError)
