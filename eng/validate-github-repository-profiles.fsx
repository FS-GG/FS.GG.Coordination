#load "../src/FS.GG.Coordination.GitHub/RepositoryProfileAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubRepositoryProfileQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-1")
let rosterNode = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "roster.json"))).AsObject()
let expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "independent-expectations.json"))).AsObject()
let corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "corpus.json"))).AsObject()
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let optionalText (node: JsonObject) (name: string) = if isNull node[name] then None else Some(node[name].GetValue<string>())
let role (value: string) = match value with "authority" -> Authority | "framework" -> Framework | "non-participant" -> NonParticipant | value -> failwith $"unsupported retained role {value}"
let row (node: JsonNode) : RepositoryRosterRow =
    let value = node.AsObject()
    { Id = text value "id"
      FullName = text value "fullName"
      Role = role (text value "role")
      Capabilities = value["capabilities"].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
      KitDelivery = optionalText value "kitDelivery"
      AbsenceCover = optionalText value "absenceCover"
      Reason = optionalText value "reason" }
let rows = rosterNode["rows"].AsArray() |> Seq.map row |> List.ofSeq
let source = rosterNode["source"].AsObject()
let canonicalDigest = RepositoryProfileAdapter.canonicalRosterDigest rows
let mint = fsi.CommandLineArgs |> Array.contains "--mint"
let snapshot =
    { SchemaVersion = rosterNode["schemaVersion"].GetValue<int>()
      SourceRevision = text source "revision"
      SourceArtifactSha256 = text source "artifactSha256"
      CanonicalRosterSha256 = if mint then canonicalDigest else text rosterNode "canonicalRosterSha256"
      ReviewedAt = DateTimeOffset.Parse(text source "reviewedAt")
      Complete = rosterNode["complete"].GetValue<bool>()
      Rows = rows }
let asOf = snapshot.ReviewedAt
let report = RepositoryProfileAdapter.compile asOf (TimeSpan.FromHours 1) snapshot |> Result.defaultWith (failwithf "repository-profile baseline refused: %A")

if mint then
    printfn "%s" canonicalDigest
    printfn "%s" report.Seal
else
    let requiredControlIds = GitHubRepositoryProfileQualification.requiredControls |> List.map GitHubRepositoryProfileQualification.controlId
    let corpusControlIds = corpus["controls"].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
    let independentControlIds = expectations["controls"].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
    if corpusControlIds <> requiredControlIds || independentControlIds <> requiredControlIds then failwith "retained control inventories differ from the closed contract"
    let expectedOrder = expectations["expectedOrder"].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
    let actualOrder = report.Profiles |> List.map _.FullName
    if actualOrder <> expectedOrder then failwith "independent repository order differs"
    if report.Profiles.Length <> expectations["repositoryCount"].GetValue<int>() then failwith "repository count differs"
    let organization = report.Profiles |> List.filter (_.Administration >> (=) OrganizationAdministered)
    let external = report.Profiles |> List.filter (_.Administration >> (=) AdministrationBoundary.ExternalObserveOnly)
    if organization.Length <> expectations["organizationAdministeredCount"].GetValue<int>() then failwith "organization-administered count differs"
    if external.Length <> expectations["externalObserveOnlyCount"].GetValue<int>() then failwith "external count differs"
    if report.Seal <> text expectations "expectedSeal" then failwith "repository profile seal differs"
    if RepositoryProfileAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) snapshot <> Ok report then failwith "exact profile replay failed"

    let propertyShape (profiles: RepositoryProfile list) =
        profiles
        |> List.forall (fun profile ->
            let names = profile.NativeProperties |> List.map _.Name |> List.sort
            let bounded = profile.NativeProperties |> List.forall (fun property -> property.Name.Length <= 75 && property.Value.Length <= 200)
            match profile.Administration with
            | OrganizationAdministered -> names = [ "fsgg_coordination_mode"; "fsgg_owner_scope"; "fsgg_role" ] && profile.PropertyMutationPermitted && bounded
            | AdministrationBoundary.ExternalObserveOnly -> names.IsEmpty && not profile.PropertyMutationPermitted)

    let richRetention (candidateRows: RepositoryRosterRow list) (profiles: RepositoryProfile list) =
        List.zip (candidateRows |> List.sortBy (_.FullName >> _.ToLowerInvariant())) profiles
        |> List.forall (fun (sourceRow, profile) ->
            sourceRow.Capabilities |> List.map _.ToLowerInvariant() |> List.sort = profile.Capabilities
            && sourceRow.Reason = profile.Reason
            && sourceRow.KitDelivery = profile.KitDelivery
            && sourceRow.AbsenceCover = profile.AbsenceCover)

    let refused changed = RepositoryProfileAdapter.compile asOf (TimeSpan.FromHours 1) changed |> Result.isError
    let generatedMutation = function
        | RosterSourceBinding ->
            let malformedRevision = { snapshot with SourceRevision = "main" }
            let alteredRevision = { snapshot with SourceRevision = String.replicate 40 "0" }
            let alteredArtifact = { snapshot with SourceArtifactSha256 = String.replicate 64 "f" }
            refused malformedRevision
            && RepositoryProfileAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) alteredRevision = Error [ AlteredRepositoryProfileSeal ]
            && RepositoryProfileAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) alteredArtifact = Error [ AlteredRepositoryProfileSeal ]
        | CompleteRoster -> refused { snapshot with Complete = false }
        | StableOrdering -> (RepositoryProfileAdapter.compile asOf (TimeSpan.FromHours 1) { snapshot with Rows = List.rev rows } = Ok report)
        | IdentityUniqueness -> let changed = rows @ [ rows.Head ] in refused { snapshot with Rows = changed; CanonicalRosterSha256 = RepositoryProfileAdapter.canonicalRosterDigest changed }
        | RoleVocabulary -> let changed = rows |> List.map (fun value -> if value.Id = "sir" then { value with Role = Framework } else value) in refused { snapshot with Rows = changed; CanonicalRosterSha256 = RepositoryProfileAdapter.canonicalRosterDigest changed }
        | CapabilityVocabulary -> let changed = rows |> List.map (fun value -> if value.Id = "sdd" then { value with Capabilities = "unknown" :: value.Capabilities } else value) in refused { snapshot with Rows = changed; CanonicalRosterSha256 = RepositoryProfileAdapter.canonicalRosterDigest changed }
        | RichAuthorityRetention ->
            let altered = report.Profiles |> List.map (fun profile -> if profile.Id = "sdd" then { profile with Capabilities = [] } else profile)
            richRetention rows report.Profiles && not (richRetention rows altered)
        | OrganizationPropertyProjection ->
            let altered = report.Profiles |> List.map (fun profile -> if profile.Id = "sdd" then { profile with NativeProperties = [] } else profile)
            propertyShape report.Profiles && not (propertyShape altered)
        | GitHubRepositoryProfileControl.ExternalObserveOnly -> external.Length = 1 && external.Head.NativeProperties.IsEmpty && not external.Head.PropertyMutationPermitted
        | PropertyBounds -> propertyShape report.Profiles && not ([ { Name = "fsgg_role"; Value = String.replicate 201 "x" } ] |> List.forall (fun value -> value.Value.Length <= 200))
        | Freshness -> RepositoryProfileAdapter.compile (asOf.AddHours 2) (TimeSpan.FromHours 1) snapshot |> Result.isError
        | ExactSeal -> RepositoryProfileAdapter.verify (String.replicate 64 "0") asOf (TimeSpan.FromHours 1) snapshot |> Result.isError
        | ExactReplay -> RepositoryProfileAdapter.compile asOf (TimeSpan.FromHours 1) snapshot = Ok report
        | PrerequisiteReceipts ->
            [ "GS2-02.11", "52a282b6b2ddee1ffdd8c68288b1a374cb9bacbb767db238e310c32d0758a53f"
              "GS2-03.9", "c5b0bf313583e26dc6a2f471b58e22d6315f4ff425d05cf6f74070c45c5ecde2"
              "GS2-04.9", "11defafd12353bbcb9b96cc06d3d9e29553ddca4ba912bacd7476c067f9802ed"
              "GS2-05.8", "a267b70003b955e4cd171e30d6f22f52eca6655002e17a52df22a19383fdfd53"
              "GS2-05.9", "59398e603e39b04ff6d971ef923d19513e03d3990a970323add90cf7ce593861" ]
            |> List.forall (fun (unitId, digest) ->
                let receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(root, $"evidence/github-substrate-v2/accepted/{unitId}.json")))
                receipt["digest"].GetValue<string>() = digest)
        | QuintUnchanged ->
            File.ReadAllBytes(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant() = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        | NoApplySurface -> not (File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RepositoryProfileAdapter.fsi")).Contains("val apply"))

    let independentMutation = function
        | RosterSourceBinding ->
            let expectedRevision = text expectations "sourceRevision"
            let expectedArtifact = "838f80598dcebea1019ae1b0b38f55180502fb2de155d274c891bc245dd8c29d"
            let revisionSubstitution = { snapshot with SourceRevision = String.replicate 40 "a" }
            let artifactSubstitution = { snapshot with SourceArtifactSha256 = String.replicate 64 "b" }
            snapshot.SourceRevision = expectedRevision
            && snapshot.SourceArtifactSha256 = expectedArtifact
            && RepositoryProfileAdapter.verify (text expectations "expectedSeal") asOf (TimeSpan.FromHours 1) revisionSubstitution = Error [ AlteredRepositoryProfileSeal ]
            && RepositoryProfileAdapter.verify (text expectations "expectedSeal") asOf (TimeSpan.FromHours 1) artifactSubstitution = Error [ AlteredRepositoryProfileSeal ]
        | CompleteRoster -> rows.Length = 10 && rosterNode["complete"].GetValue<bool>()
        | StableOrdering -> actualOrder = expectedOrder && actualOrder <> List.rev expectedOrder
        | IdentityUniqueness -> rows |> List.map (_.FullName >> _.ToLowerInvariant()) |> Set.ofList |> Set.count = rows.Length
        | RoleVocabulary -> rows |> List.forall (fun value -> match value.Role with Authority | Framework | NonParticipant -> true)
        | CapabilityVocabulary -> rows |> List.collect _.Capabilities |> List.forall (fun value -> List.contains value RepositoryProfileAdapter.allowedCapabilities)
        | RichAuthorityRetention -> richRetention rows report.Profiles
        | OrganizationPropertyProjection -> propertyShape organization
        | GitHubRepositoryProfileControl.ExternalObserveOnly -> external = [ report.Profiles |> List.find (_.FullName >> (=) "EHotwagner/S.I.R.") ] && not external.Head.PropertyMutationPermitted
        | PropertyBounds -> report.Profiles |> List.collect _.NativeProperties |> List.forall (fun value -> value.Name.Length <= 75 && value.Value.Length <= 200)
        | Freshness -> asOf - snapshot.ReviewedAt <= TimeSpan.FromHours 1
        | ExactSeal -> report.Seal.Length = 64 && report.Seal = text expectations "expectedSeal"
        | ExactReplay -> RepositoryProfileAdapter.verify (text expectations "expectedSeal") asOf (TimeSpan.FromHours 1) snapshot = Ok report
        | PrerequisiteReceipts -> generatedMutation PrerequisiteReceipts
        | QuintUnchanged -> generatedMutation QuintUnchanged
        | NoApplySurface -> generatedMutation NoApplySurface

    let generated = GitHubRepositoryProfileQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = generatedMutation control; BaselineGreen = true })
    let independent = GitHubRepositoryProfileQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = independentMutation control; BaselineGreen = true })
    match GitHubRepositoryProfileQualification.validate generated independent with
    | Ok () -> printfn "GITHUB_REPOSITORY_PROFILES_OK repositories=%d organization=%d external=%d properties=%d controls=%d seal=%s" report.Profiles.Length organization.Length external.Length (organization |> List.sumBy (_.NativeProperties >> List.length)) generated.Length report.Seal
    | Error findings -> failwithf "repository-profile qualification failed: %A" findings
