#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubV1EffectFenceQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-v1-effect-fence.fsx -- <root>"
let path relative = Path.Combine(root, relative)
let bytes relative = File.ReadAllBytes(path relative)
let sha256 relative = bytes relative |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let read relative = File.ReadAllText(path relative)
let text (node: JsonElement) (name: string) = node.GetProperty(name).GetString()
let evidenceRoot = "evidence/github-substrate-v2/gs2-08-4"
let bindingDocument = JsonDocument.Parse(read $"{evidenceRoot}/source-binding.json")
let casesDocument = JsonDocument.Parse(read $"{evidenceRoot}/independent-cases.json")
let binding = bindingDocument.RootElement
let writer = binding.GetProperty("writerCensusGate")
let receiver = binding.GetProperty("receiverCensusGate")

if text binding "schema" <> "fsgg.github-substrate.v1-effect-fence-source-binding/1" then failwith "source binding schema differs"
if text binding "roadmapRevision" <> "3719b6cfc6f2d766ad56f930b56f025e20c4b2cc"
   || text binding "roadmapSha256" <> "9c49a0efd1440d8a71130758be39394ae4cdd67f3d10b9cb6cb71998154c1a17" then failwith "roadmap binding differs"
if text binding "acceptedEpochReceiptDigest" <> "49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31" then failwith "accepted epoch prerequisite differs"
if text writer "commandSha256" <> "4e22685574c841ad50ed600497e8174832374964cff62ba6d25440d52467521b"
   || text receiver "commandSha256" <> "fbff7ac12feadedf1fc5a440848bfecfd4ac799e1c8bc45cc15fed666b68d0e9" then failwith "GS2-08.3 gate command dependency differs"
if sha256 "evidence/github-substrate-v2/gs2-08-3/source-binding.json" <> text writer "sourceBindingSha256"
   || sha256 "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json" <> text writer "censusSha256"
   || sha256 "evidence/github-substrate-v2/gs2-08-3/receiver-source/source-binding.json" <> text receiver "sourceBindingSha256"
   || sha256 "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-v1-writer-receiver-census.json" <> text receiver "censusSha256" then failwith "GS2-08.3 retained evidence dependency differs"

let cases =
    casesDocument.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.map (fun value ->
        { Name = text value "name"; FreshReadCount = value.GetProperty("freshReadCount").GetInt32()
          VerifiedBeforeEffect = value.GetProperty("verifiedBeforeEffect").GetBoolean()
          EffectCount = value.GetProperty("effectCount").GetInt32(); Outcome = text value "outcome" })
    |> List.ofSeq
let result = GitHubV1EffectFenceQualification.qualify cases
let findings = String.concat "," result.Findings
if not result.Findings.IsEmpty then failwith $"independent fence controls failed: {findings}"

let adapter = read "src/FS.GG.Coordination.GitHub/V1EffectFenceAdapter.fs"
let signature = read "src/FS.GG.Coordination.GitHub/V1EffectFenceAdapter.fsi"
let protocol = read "src/FS.GG.Coordination.Protocol/Protocol.md"
for required in [ "reader.ReadFresh()"; "effect.ApplyOnce request"; "ShardedJournalAdapter.address Cutover"; "cache-cannot-authorize"; "stale-claim-generation"; "stale-operation-generation"; "V1RefusedBeforeEffect" ] do
    if not(adapter.Contains required) then failwith $"runtime boundary omitted {required}"
for required in [ "FreshEpochReader"; "V1EffectPort"; "V1EffectOutcome" ] do
    if not(signature.Contains required) then failwith $"public runtime contract omitted {required}"
if not(protocol.Contains "pure def v1EffectMayProceed") then failwith "canonical v1EffectMayProceed semantic source missing"
for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Authorization:"; "ApplyAuthorized" ] do
    if adapter.Contains forbidden || signature.Contains forbidden then failwith $"runtime boundary contains forbidden production/provider token {forbidden}"
let scope = text binding "scope"
for forbiddenClaim in [ "installed"; "activated"; "accepted" ] do
    if scope.Contains($"{forbiddenClaim}=true", StringComparison.OrdinalIgnoreCase) then failwith $"forbidden completion claim {forbiddenClaim}"
printfn "GITHUB_V1_EFFECT_FENCE_OK cases=%d controls=%d writerGate=%s receiverGate=%s" cases.Length result.Controls.Length (text writer "commandSha256") (text receiver "commandSha256")
