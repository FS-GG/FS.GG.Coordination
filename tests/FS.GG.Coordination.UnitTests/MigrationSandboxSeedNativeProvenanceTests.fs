module FS.GG.Coordination.MigrationSandboxSeedNativeProvenanceTests
open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.Cli
open Xunit
let bytes (text: string) = Encoding.UTF8.GetBytes text
let sha (raw: byte[]) = SHA256.HashData raw |> Convert.ToHexString |> _.ToLowerInvariant()
let workflow = bytes "workflow"
let builder = bytes "builder"
let artifact = bytes "approved artifact"
let head = String.replicate 40 "a"
let rule name id bypass kinds =
    bytes $"{{\"id\":{id},\"name\":\"{name}\",\"source_type\":\"Repository\",\"source\":\"FS-GG/FS.GG.GitHub.Substrate.Sandbox\",\"target\":\"branch\",\"enforcement\":\"active\",\"conditions\":{{\"ref_name\":{{\"include\":[\"refs/heads/gs2-09-7/*/seed-journal\"],\"exclude\":[]}}}},\"bypass_actors\":{bypass},\"rules\":{kinds}}}"
let writer = rule "gs2-09-7-seed-journal-writer" 7 "[{\"actor_id\":4166418,\"actor_type\":\"Integration\",\"bypass_mode\":\"always\"}]" "[{\"type\":\"creation\"},{\"type\":\"update\"}]"
let integrity = rule "gs2-09-7-seed-journal-integrity" 8 "[]" "[{\"type\":\"deletion\"},{\"type\":\"non_fast_forward\"}]"
let binding = bytes $"{{\"source\":{{\"runId\":7,\"runAttempt\":2,\"workflowSha\":\"{head}\",\"approvedArtifactSourceSha256\":\"{sha artifact}\",\"runNonce\":\"7-2-{head}\",\"protectedCheckout\":{{\"checkoutHead\":\"{head}\",\"workflow\":{{\"path\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"sha256\":\"{sha workflow}\"}},\"builder\":{{\"path\":\"scripts/gs2-09-7-seed-execution-binding.py\",\"sha256\":\"{sha builder}\"}}}}}}}}"
let policy = bytes $"{{\"rulesets\":{{\"writerId\":7,\"writerSha256\":\"{sha writer}\",\"integrityId\":8,\"integritySha256\":\"{sha integrity}\"}}}}"
let run = bytes $"{{\"id\":7,\"run_attempt\":2,\"head_sha\":\"{head}\",\"event\":\"workflow_dispatch\",\"path\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"repository\":{{\"id\":1,\"full_name\":\"FS-GG/.github\"}},\"actor\":{{\"id\":2}}}}"
type Read(runResponse: Result<byte[],string>, rulesResponse: Result<byte[],string>, classic: Result<byte[] option,string>) =
    interface IMigrationSandboxSeedNativeProvenanceRead with
        member _.ReadRunAttempt(_,_) = runResponse
        member _.ReadGitBlob(_,path) = Ok (if path.StartsWith(".github/") then workflow else builder)
        member _.ReadApprovedArtifact _ = Ok artifact
        member _.ReadRuleset id = if id = 7L then rulesResponse else Ok integrity
        member _.ReadClassicProtection _ = classic
let evidence = { BindingBytes=binding; PolicyReadbackBytes=policy; WorkflowRunId=7L; WorkflowRunAttempt=2; WorkflowSha=head; ApprovedArtifactSourceSha256=sha artifact }
let verify reader = (MigrationSandboxSeedNativeProvenanceVerifier(reader) :> IMigrationSandboxSeedInstalledProvenanceVerifier).VerifyExact evidence
[<Fact>]
let ``controlled native readback is exact and provider failures refuse`` () =
    Assert.True(verify (Read(Ok run, Ok writer, Ok None)))
    Assert.False(verify (Read(Ok run, Error "403 plan entitlement", Ok None)))
    Assert.False(verify (Read(Ok run, Ok writer, Error "ambiguous 404")))
    Assert.False(verify (Read(Error "missing attempt", Ok writer, Ok None)))

[<Fact>]
let ``missing evidence and transport cannot mint authority`` () =
    let invalid = { evidence with BindingBytes=[||] }
    let verifier = MigrationSandboxSeedNativeProvenanceVerifier(Unchecked.defaultof<IMigrationSandboxSeedNativeProvenanceRead>) :> IMigrationSandboxSeedInstalledProvenanceVerifier
    Assert.False(verifier.VerifyExact invalid)
