#nowarn "3391"

module FS.GG.Coordination.InstalledOrdinarySettlementProviderTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Cli
open FS.GG.Coordination.GitHub

let private sha character = String.replicate 40 character

let private check name source =
    let value = JsonObject()
    value.Add("name", name)
    value.Add("appId", 15368L)
    value.Add("conclusion", "success")
    value.Add("sourceSha", source)
    value

let private fixture () =
    let source, head = sha "a", sha "b"
    let settlement = [ "contract-coherence / coherence"; "routine-eligibility" ]
    let gates =
        [ "contract-coherence / coherence"; "projection"; "roster-closure"; "drift"; "claim-generation"
          "Lint every shell file in the repo (pinned shellcheck)"; "claim-fence"; "architecture-map reconcile" ]
    let strings (values: string list) =
        let array = JsonArray()
        for value in values do array.Add value
        array
    let policy = JsonObject()
    policy.Add("policyId", "v2-ci-i1-ordinary-settlement-v1")
    let qualification = JsonObject()
    qualification.Add("requiredChecks", strings settlement)
    qualification.Add("requiredGateChecks", strings gates)
    policy.Add("qualification", qualification)
    let credential = JsonObject()
    credential.Add("installed", true)
    policy.Add("credentialJob", credential)
    let policyBytes = Encoding.UTF8.GetBytes(policy.ToJsonString())
    let digest = SHA256.HashData policyBytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let receipt = JsonObject()
    receipt.Add("schema", "fsgg.github.v2-ci-secret-free-predecessor-receipt/1")
    receipt.Add("status", "qualified")
    receipt.Add("policyId", "v2-ci-i1-ordinary-settlement-v1")
    receipt.Add("policySha256", digest)
    receipt.Add("sourceSha", source)
    receipt.Add("pullRequest", 17)
    receipt.Add("pullRequestNodeId", "PR_node")
    receipt.Add("pullRequestBaseSha", sha "c")
    receipt.Add("mergeCommitSha", source)
    receipt.Add("qualificationSha", head)
    receipt.Add("qualifiedTreeSha", sha "d")
    let required = JsonArray()
    for name in settlement do required.Add(check name head)
    receipt.Add("requiredChecks", required)
    let requiredGates = JsonArray()
    for name in gates do requiredGates.Add(check name head)
    receipt.Add("requiredGateChecks", requiredGates)
    receipt.Add("workflowRevision", source)
    receipt.Add("environment", "ordinary-v2")
    receipt.Add("operationClass", "ordinary-post-merge-delivery-settlement")
    receipt.Add("credentialAccess", false)
    receipt.Add("activation", true)
    receipt, policyBytes

[<Fact>]
let ``installed provider binds exact two settlement and eight gate facts`` () =
    let receipt, policy = fixture ()
    let result = InstalledOrdinarySettlementProvider.validateReceiptFacts "ordinary-v2" "v2-ci-i1-ordinary-settlement-v1" (Encoding.UTF8.GetBytes(receipt.ToJsonString())) policy
    Assert.True(Result.isOk result)

[<Fact>]
let ``installed provider refuses stale gate source and changed policy bytes`` () =
    let receipt, policy = fixture ()
    let gates = receipt["requiredGateChecks"].AsArray()
    gates[0].AsObject()["sourceSha"] <- sha "e"
    Assert.Equal(Error "preflight-receipt-binding",
                 InstalledOrdinarySettlementProvider.validateReceiptFacts "ordinary-v2" "v2-ci-i1-ordinary-settlement-v1" (Encoding.UTF8.GetBytes(receipt.ToJsonString())) policy)
    let receipt2, policy2 = fixture ()
    policy2[0] <- byte ((int policy2[0] + 1) % 255)
    Assert.Equal(Error "preflight-receipt-json",
                 InstalledOrdinarySettlementProvider.validateReceiptFacts "ordinary-v2" "v2-ci-i1-ordinary-settlement-v1" (Encoding.UTF8.GetBytes(receipt2.ToJsonString())) policy2)

[<Fact>]
let ``rehearsal receipt has a separate environment and pending outcomes are nonzero`` () =
    let receipt, policy = fixture ()
    receipt["environment"] <- "ordinary-v2-rehearsal"
    receipt["policyId"] <- "v2-ci-i1-ordinary-settlement-rehearsal-v1"
    let policyNode = JsonNode.Parse(policy).AsObject()
    policyNode["policyId"] <- "v2-ci-i1-ordinary-settlement-rehearsal-v1"
    let rehearsalPolicy = Encoding.UTF8.GetBytes(policyNode.ToJsonString())
    let rehearsalDigest = SHA256.HashData rehearsalPolicy |> Convert.ToHexString |> _.ToLowerInvariant()
    receipt["policySha256"] <- rehearsalDigest
    Assert.True(
        InstalledOrdinarySettlementProvider.validateReceiptFacts
            "ordinary-v2-rehearsal" "v2-ci-i1-ordinary-settlement-rehearsal-v1"
            (Encoding.UTF8.GetBytes(receipt.ToJsonString())) rehearsalPolicy
        |> Result.isOk)
    Assert.Equal(0, OrdinarySettlementCommand.outcomeExitCode (SettlementSucceeded(String.replicate 64 "a")))
    Assert.Equal(0, OrdinarySettlementCommand.outcomeExitCode (SettlementAlreadyComplete(String.replicate 64 "a")))
    Assert.Equal(3, OrdinarySettlementCommand.outcomeExitCode (SettlementPending "unknown"))
    Assert.Equal(3, OrdinarySettlementCommand.outcomeExitCode (SettlementInterrupted "cut"))
    Assert.Equal(Ok(), InstalledOrdinarySettlementProvider.validateRehearsalFaultSetting true "none")
    Assert.Equal(Ok(), InstalledOrdinarySettlementProvider.validateRehearsalFaultSetting true "lost-ref-reply")
    Assert.Equal(Ok(), InstalledOrdinarySettlementProvider.validateRehearsalFaultSetting true "ref-conflict")
    Assert.Equal(Error "unsupported-rehearsal-fault", InstalledOrdinarySettlementProvider.validateRehearsalFaultSetting true "crash")
    Assert.Equal(Error "production-refuses-rehearsal-fault", InstalledOrdinarySettlementProvider.validateRehearsalFaultSetting false "ref-conflict")

[<Fact>]
let ``rehearsal epoch contract binds the isolated sandbox identity`` () =
    let profile = OrdinarySettlementAuthorityProfiles.rehearsal
    let eventBytes =
        Encoding.UTF8.GetBytes
            "{\"fleetId\":\"fs-gg-v2-rehearsal\",\"phase\":\"OpenV2\",\"schema\":\"fsgg.github-substrate.epoch-event/1\"}"
    let eventDigest = SHA256.HashData eventBytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let headBytes =
        Encoding.UTF8.GetBytes
            $"{{\"aggregateDigest\":\"2ad14cd9bc05b8290320fbadd752f873464ee7c256926cd1bb4a32fdf23abdc5\",\"aggregateId\":\"fleet-cutover:fs-gg-v2-rehearsal\",\"eventDigest\":\"{eventDigest}\",\"generation\":1,\"journalKind\":\"cutover\",\"shard\":\"2a\"}}"
    let commit = "4f02add98e091cd268979468f9c15ffe59435d43"
    Assert.Equal(
        Ok("OpenV2", 1L, commit),
        InstalledOrdinarySettlementProvider.validateEpochDocuments profile commit headBytes eventBytes)
    eventBytes[0] <- byte '{' + 1uy
    Assert.Equal(
        Error "epoch-document",
        InstalledOrdinarySettlementProvider.validateEpochDocuments profile commit headBytes eventBytes)
