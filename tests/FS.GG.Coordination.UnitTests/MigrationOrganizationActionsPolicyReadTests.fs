module FS.GG.Coordination.MigrationOrganizationActionsPolicyReadTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

let private options () =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="test-token"; UserAgent="org-actions-test"
      Owner="FS-GG"; Repository="unused"; ExpectedRepositoryId=0L }

let private reply headers body =
    Response { StatusCode=200; Headers=headers; Body=body; ETag=None
               RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }

let private identity () =
    reply Map.empty """{"id":42,"node_id":"ORG_42","login":"FS-GG","type":"Organization","url":"https://api.github.test/orgs/FS-GG"}"""

let private policy enabled allowed selectedUrl =
    let selected =
        selectedUrl
        |> Option.map (fun url -> sprintf ", \"selected_actions_url\":\"%s\"" url)
        |> Option.defaultValue ""
    reply Map.empty $"""{{"enabled_repositories":"{enabled}","allowed_actions":"{allowed}","sha_pinning_required":true{selected}}}"""

let private selectedUrl () = "https://api.github.test/organizations/42/actions/permissions/selected-actions"
let private selectedActions () =
    reply Map.empty """{"github_owned_allowed":true,"verified_allowed":false,"patterns_allowed":["actions/checkout@*"]}"""

let private repository id name =
    $"""{{"id":{id},"node_id":"REPO_{id}","name":"{name}","full_name":"FS-GG/{name}","owner":{{"id":42,"node_id":"ORG_42","login":"FS-GG"}}}}"""

let private firstPage () =
    let next = "https://api.github.test/orgs/FS-GG/actions/permissions/repositories?per_page=100&page=2"
    reply (Map [ "link", $"<{next}>; rel=\"next\"" ])
        $"""{{"total_count":2,"repositories":[{repository 101 "one"}]}}"""

let private secondPage () =
    reply Map.empty $"""{{"total_count":2,"repositories":[{repository 102 "two"}]}}"""

let private selectedPass () =
    [ identity (); policy "selected" "selected" (Some(selectedUrl ()))
      selectedActions (); firstPage (); secondPage () ]

[<Fact>]
let ``organization Actions policy reads exact two-pass selected allowlists and pages`` () =
    let pass = selectedPass ()
    let transport = FakeTransport(pass @ pass)
    match MigrationOrganizationActionsPolicyRead.read 42L (options ()) transport with
    | Error failure -> failwithf "organization policy refused: %s" failure
    | Ok proof ->
        Assert.Equal(42L, proof.First.OrganizationId)
        Assert.Equal("ORG_42", proof.First.OrganizationNodeId)
        Assert.Equal(MigrationOrganizationEnabledRepositories.Selected, proof.First.EnabledRepositories)
        Assert.Equal(MigrationOrganizationAllowedActions.Selected, proof.First.AllowedActions)
        Assert.True(proof.First.ShaPinningRequired)
        Assert.Equal(Some(selectedUrl ()), proof.First.SelectedActionsUrl)
        Assert.Equal(Some 2, proof.First.SelectedRepositories |> Option.map List.length)
        Assert.Equal(2, proof.First.SelectedRepositoryPages.Length)
        Assert.Equal(proof.First, proof.Second)
        Assert.Equal(10, transport.Requests.Length)
        for request in transport.Requests do
            match request with
            | Rest value -> Assert.Equal(Get, value.Method); Assert.True(value.Body.IsNone)
            | _ -> failwith "organization policy emitted GraphQL"
        let raw = proof.First.PolicyEvidence.RawBody |> Encoding.UTF8.GetBytes
                  |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
        Assert.Equal(raw, proof.First.PolicyEvidence.RawSha256)

[<Fact>]
let ``nonselected organization policy never reads conditional endpoints`` () =
    let pass = [ identity (); policy "all" "local_only" None ]
    let transport = FakeTransport(pass @ pass)
    match MigrationOrganizationActionsPolicyRead.read 42L (options ()) transport with
    | Error failure -> failwithf "simple policy refused: %s" failure
    | Ok proof ->
        Assert.Equal(MigrationOrganizationEnabledRepositories.All, proof.First.EnabledRepositories)
        Assert.Equal(MigrationOrganizationAllowedActions.LocalOnly, proof.First.AllowedActions)
        Assert.True(proof.First.SelectedActions.IsNone)
        Assert.True(proof.First.SelectedRepositories.IsNone)
        Assert.Empty(proof.First.SelectedRepositoryPages)
        Assert.Equal(4, transport.Requests.Length)

[<Fact>]
let ``organization policy refuses unavailable foreign and missing conditional identity`` () =
    let forbidden =
        Response { StatusCode=403; Headers=Map.empty; Body="forbidden"; ETag=None
                   RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
    let noAccess = FakeTransport [ forbidden ]
    Assert.Equal(Error "unknown:organization-actions-http-403",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) noAccess)
    let notFound =
        Response { StatusCode=404; Headers=Map.empty; Body="not found"; ETag=None
                   RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
    Assert.Equal(Error "unknown:organization-actions-http-404",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) (FakeTransport [ notFound ]))
    let foreign =
        reply Map.empty """{"id":43,"node_id":"ORG_43","login":"OTHER","type":"Organization","url":"https://api.github.test/orgs/OTHER"}"""
    let foreignRead = FakeTransport [ foreign ]
    Assert.Equal(Error "foreign:organization-identity",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) foreignRead)
    Assert.Equal(1, foreignRead.Requests.Length)
    let missingActions = FakeTransport [ identity (); policy "all" "selected" None ]
    Assert.Equal(Error "invalid:selected-actions-boundary",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) missingActions)
    let missingRepositories = FakeTransport [ identity (); policy "selected" "all" None; forbidden ]
    Assert.Equal(Error "unknown:organization-actions-http-403",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) missingRepositories)

[<Fact>]
let ``organization policy refuses unknown enum and escaped selected URL`` () =
    let unknown = FakeTransport [ identity (); policy "mystery" "all" None ]
    Assert.Equal(Error "invalid:organization-actions-policy",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) unknown)
    let escaped =
        FakeTransport [ identity (); policy "all" "selected"
                            (Some "https://api.evil.test/organizations/42/actions/permissions/selected-actions") ]
    Assert.Equal(Error "foreign:selected-actions-url",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) escaped)

[<Fact>]
let ``selected repository census refuses foreign pages duplicate identities and missing count`` () =
    let pass = selectedPass ()
    let foreignNext =
        reply (Map [ "link", "<https://api.evil.test/orgs/FS-GG/actions/permissions/repositories?per_page=100&page=2>; rel=\"next\"" ])
            $"""{{"total_count":2,"repositories":[{repository 101 "one"}]}}"""
    let foreign = FakeTransport(pass |> List.mapi (fun index item -> if index = 3 then foreignNext else item))
    Assert.Equal(Error "foreign:selected-repository-page",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) foreign)
    let duplicate =
        reply Map.empty $"""{{"total_count":2,"repositories":[{repository 101 "one"}]}}"""
    let repeated = FakeTransport(pass |> List.mapi (fun index item -> if index = 4 then duplicate else item))
    Assert.Equal(Error "duplicate:selected-repository",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) repeated)
    let short = reply Map.empty $"""{{"total_count":3,"repositories":[{repository 102 "two"}]}}"""
    let incomplete = FakeTransport(pass |> List.mapi (fun index item -> if index = 4 then short else item))
    Assert.Equal(Error "changed:selected-repository-count",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) incomplete)

[<Fact>]
let ``second pass drift refuses a plausible changed organization policy`` () =
    let first = [ identity (); policy "all" "all" None ]
    let second = [ identity (); policy "none" "all" None ]
    let transport = FakeTransport(first @ second)
    Assert.Equal(Error "changed:organization-actions-policy",
                 MigrationOrganizationActionsPolicyRead.read 42L (options ()) transport)
    Assert.Equal(4, transport.Requests.Length)
