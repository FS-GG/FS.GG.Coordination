module FS.GG.Coordination.CallableIsolatedOperationArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private bytes relative = File.ReadAllBytes(Path.Combine(root, relative))
let private text relative = File.ReadAllText(Path.Combine(root, relative))
let private sha256 (value: byte array) = SHA256.HashData(value) |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``isolated operation contract is source-bound sealed and unauthorized`` () =
    use contract = JsonDocument.Parse(bytes "eng/callable-cli-isolated-operation-contract.json")
    let value = contract.RootElement
    Assert.Equal("fsgg.coordination.callable-isolated-operation-contract/4", value.GetProperty("schema").GetString())
    Assert.Equal("prepared-not-authorized", value.GetProperty("state").GetString())
    Assert.False(value.GetProperty("authorized").GetBoolean())
    Assert.Equal(value.GetProperty("contractSha256").GetString(), AcceptanceReceiptDigest.canonicalBytesOmitting "contractSha256" value |> sha256)
    Assert.Equal(value.GetProperty("source").GetProperty("operationSourceSha256").GetString(), bytes "eng/callable-cli-isolated-operation.py" |> sha256)
    Assert.Equal("workflow-environment-grant-credential-and-capabilities-unavailable", value.GetProperty("authorization").GetProperty("currentStatus").GetString())
    Assert.Equal("forbidden-self-reference", value.GetProperty("authorization").GetProperty("grantPayloadArtifactCoordinates").GetString())
    Assert.Contains(value.GetProperty("forbidden").EnumerateArray(), fun item -> item.GetString() = "effect-before-protected-grant-and-fresh-capability-readback")

    use proposal = JsonDocument.Parse(bytes "eng/callable-cli-isolated-operation-proposal.json")
    let prepared = proposal.RootElement
    Assert.Equal(prepared.GetProperty("proposalSha256").GetString(), AcceptanceReceiptDigest.canonicalBytesOmitting "proposalSha256" prepared |> sha256)
    Assert.False(prepared.GetProperty("authorized").GetBoolean())
    Assert.Equal(value.GetProperty("contractSha256").GetString(), prepared.GetProperty("contract").GetProperty("sha256").GetString())

[<Fact>]
let ``preflight records refusals without inferring authority or compatibility`` () =
    use preflight = JsonDocument.Parse(bytes "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json")
    let value = preflight.RootElement
    Assert.Equal(value.GetProperty("evidenceSha256").GetString(), AcceptanceReceiptDigest.canonicalBytesOmitting "evidenceSha256" value |> sha256)
    Assert.Equal("refused-no-compatible-admitted-target", value.GetProperty("disposition").GetString())
    Assert.False(value.GetProperty("authorized").GetBoolean())
    let facts = value.GetProperty("facts")
    Assert.Equal(16, facts.GetProperty("accessibleRepositoryCount").GetInt32())
    Assert.Equal(0, facts.GetProperty("compatibleTargetCount").GetInt32())
    Assert.Equal(404, facts.GetProperty("proposedTargetHttpStatus").GetInt32())
    Assert.Equal(403, facts.GetProperty("privateSandboxRequiredStatusChecksHttpStatus").GetInt32())
    Assert.Contains("not-ownership-reservation-or-authority", facts.GetProperty("proposedTarget404Meaning").GetString(), StringComparison.Ordinal)

    use coverage = JsonDocument.Parse(bytes "evidence/github-substrate-v2/gs2-09-9/isolated-operation-coverage.json")
    let covered = coverage.RootElement
    Assert.Equal(covered.GetProperty("coverageSha256").GetString(), AcceptanceReceiptDigest.canonicalBytesOmitting "coverageSha256" covered |> sha256)
    Assert.False(covered.GetProperty("externalAcceptance").GetBoolean())
    Assert.True(covered.GetProperty("negativeControls").GetArrayLength() >= 30)

[<Fact>]
let ``live source admits only protected exact target operation and persists cleanup intent`` () =
    let source = text "eng/callable-cli-isolated-operation.py"
    for required in
        [
            "plan-cannot-self-authorize"
            "reviewer-membership-unproved"
            "creation-credential-scope"
            "operation-credential-scope"
            "creation-plan-not-canonical-contract"
            "grant-artifact-readback"
            "grant-artifact-content-binding"
            "grant-artifact-self-reference"
            "grant-artifact-envelope-binding"
            "grant-artifact-envelope-readback"
            "credential-role-set"
            "credential-token-role-alias"
            "retained-plan-missing"
            "retained-pull-request-identity"
            "journal-protection-conflict"
            "journalOperationId"
            "installed-advance-not-settled"
            "installed-replay-not-noop"
            "cleanup\": {\"state\": \"intent-persisted"
            "cleanup-pending-requires-readback"
            "github-outcome-unknown-requires-readback"
        ] do Assert.Contains(required, source, StringComparison.Ordinal)
    for forbidden in [ "gh api"; "git push"; "visibility\": \"private"; "billing"; "secrets." ] do
        Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("TemporaryDirectory", source, StringComparison.Ordinal)
    Assert.DoesNotContain("request(\"GET\", \"installation\")", source, StringComparison.Ordinal)
    Assert.Contains("app/installations/", source, StringComparison.Ordinal)
