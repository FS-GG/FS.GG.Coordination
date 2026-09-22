module FS.GG.Coordination.GitHubCompleteDiscoveryArchitectureTests

open System.IO
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubCompleteDiscoveryQualification

let private root = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``GS2-09-1 authority population is closed and roadmap-derived`` () =
    Assert.Equal<string list>(
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
        ],
        expectedAuthorities
    )

    let roadmap = File.ReadAllText(Path.Combine(root, "docs/architecture/github-complete-discovery.md"))
    expectedAuthorities |> List.iter (fun authority -> Assert.Contains($"`{authority}`", roadmap))

[<Fact>]
let ``GS2-09-1 has no production mutation implementation`` () =
    let source =
        File.ReadAllText(
            Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubCompleteDiscoveryQualification.fs")
        )

    for forbidden in [ "HttpClient"; "Octokit"; "mutation {"; "POST "; "PATCH "; "DELETE " ] do
        Assert.DoesNotContain(forbidden, source)
