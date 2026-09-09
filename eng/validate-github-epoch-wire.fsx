#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
module Q = GitHubEpochWireQualification

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-epoch-wire.fsx -- <root>"
let read relative = File.ReadAllText(Path.Combine(root, relative))
let strings (name: string) (node: JsonElement) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let controls relative = use doc = JsonDocument.Parse(read relative) in strings "controls" doc.RootElement, strings "cases" doc.RootElement
let expected = [ "state-inventory"; "forward-transition"; "rollback-transition"; "no-post-open-v1"; "operating-admission"; "preparing-table"; "freeze-fence"; "epoch-generation"; "claim-generation"; "operation-generation"; "manifest"; "fleet-layout"; "genesis-trust"; "parent-tag"; "fresh-read"; "cache"; "strict-fields"; "unreadable-contradictory"; "lost-response"; "issue-projection" ]
let generatedIds, generatedCases = controls "evidence/github-substrate-v2/gs2-08-1/generated-controls.json"
let independentIds, independentCases = controls "evidence/github-substrate-v2/gs2-08-1/independent-controls.json"
if generatedIds <> expected || independentIds <> expected || generatedCases.Length <> expected.Length || independentCases.Length <> expected.Length || generatedCases = independentCases then
    failwith "generated and independent inventories are not independently complete"
let digest c = String.replicate 64 c
let authority phase =
    { Schema = Q.schema; FleetId = Q.fleetId; Repository = Q.repository; RepositoryId = Q.repositoryId
      Ref = Q.epochRef; Tag = Q.tagPrefix + Q.phaseName phase + "/" + digest "a"; GenesisCommit = digest "1"
      TrustAnchorSha256 = digest "2"; ManifestSha256 = digest "3"; Phase = phase; Commit = digest "4"
      Parent = Some(digest "5"); Generation = 7L; Complete = true; Fresh = true; CacheUsedAsAuthority = false
      UnknownFields = []; DuplicateFields = [] }
let fence writer =
    { Writer = writer; ExpectedManifestSha256 = digest "3"; ExpectedEpochCommit = digest "4"; ExpectedEpochGeneration = 7L
      ExpectedClaimGeneration = Some 11L; CurrentClaimGeneration = Some 11L; ExpectedOperationGeneration = 13L
      CurrentOperationGeneration = 13L; OperationId = "operation-17"; EligibleIncumbent = true }
let refused = function AdmissionRefused _ -> true | _ -> false
let applied =
    { Read = AuthorityObserved; OperationId = Some "operation-17"; EpochCommit = Some(digest "4"); EpochGeneration = Some 7L
      EffectDigest = Some(digest "8"); PartialEffect = false }
let check = function
    | "state-inventory" -> Q.requiredPhases.Length = 11 && not(List.contains "RetiringV1" (List.map Q.phaseName Q.requiredPhases))
    | "forward-transition" -> Q.legalTransition OpenV2 ObservingV2 && Q.legalTransition ObservingV2 ContractingV1 && Q.legalTransition ContractingV1 OperatingV2
    | "rollback-transition" -> Q.legalTransition VerifiedV2 RollingBack && Q.legalTransition RollingBack OperatingV1 && not(Q.legalTransition RollingBack Preparing)
    | "no-post-open-v1" -> not(Q.legalTransition OpenV2 OperatingV1)
    | "operating-admission" -> Q.admit AuthorityObserved (authority OperatingV1) (fence NewOrdinaryV1) = AdmissionAuthorized
    | "preparing-table" -> Q.admit AuthorityObserved (authority Preparing) (fence IncumbentV1Effect) = AdmissionAuthorized && Q.admit AuthorityObserved (authority Preparing) (fence NewOrdinaryV1) |> refused
    | "freeze-fence" -> [ FreezeRequested; Frozen; SwitchedV2; VerifiedV2; OpenV2; ObservingV2; ContractingV1; OperatingV2 ] |> List.forall (fun phase -> Q.admit AuthorityObserved (authority phase) (fence IncumbentV1Effect) |> refused)
    | "epoch-generation" -> Q.admit AuthorityObserved (authority OperatingV1) { fence NewOrdinaryV1 with ExpectedEpochGeneration = 6L } |> refused
    | "claim-generation" -> Q.admit AuthorityObserved (authority OperatingV1) { fence NewOrdinaryV1 with CurrentClaimGeneration = Some 12L } |> refused
    | "operation-generation" -> Q.admit AuthorityObserved (authority OperatingV1) { fence NewOrdinaryV1 with CurrentOperationGeneration = 14L } |> refused
    | "manifest" -> Q.admit AuthorityObserved (authority OperatingV1) { fence NewOrdinaryV1 with ExpectedManifestSha256 = digest "9" } |> refused
    | "fleet-layout" -> Q.validateAuthority AuthorityObserved { authority OperatingV1 with FleetId = "caller" } |> List.contains "wrong-fleet"
    | "genesis-trust" -> Q.validateAuthority AuthorityObserved { authority OperatingV1 with TrustAnchorSha256 = "" } |> List.contains "wrong-trust-anchor"
    | "parent-tag" -> Q.validateAuthority AuthorityObserved { authority OperatingV1 with Parent = None; Tag = "" } |> List.contains "missing-parent-or-genesis"
    | "fresh-read" -> Q.admit AuthorityObserved { authority OperatingV1 with Fresh = false } (fence NewOrdinaryV1) |> refused
    | "cache" -> Q.admit AuthorityObserved { authority OperatingV1 with CacheUsedAsAuthority = true } (fence NewOrdinaryV1) |> refused
    | "strict-fields" -> Q.validateAuthority AuthorityObserved { authority OperatingV1 with UnknownFields = ["x"]; DuplicateFields = ["phase"] } |> List.contains "unknown-fields"
    | "unreadable-contradictory" -> (match Q.admit AuthorityUnreadable (authority OperatingV1) (fence NewOrdinaryV1) with AdmissionIndeterminate _ -> true | _ -> false) && Q.admit AuthorityContradictory (authority OperatingV1) (fence NewOrdinaryV1) |> refused
    | "lost-response" -> Q.settleLostResponse (fence NewOrdinaryV1) applied = SettlementKnownApplied && Q.settleLostResponse (fence NewOrdinaryV1) { applied with OperationId = Some "other" } = SettlementIndeterminate
    | "issue-projection" -> let a = authority ObservingV2 in let p = Q.projectIssue a in not p.Authoritative && Q.validateProjection a p
    | _ -> false
for control in expected do if not(check control) then failwith $"control failed: {control}"
let protocol = read "src/FS.GG.Coordination.Protocol/Protocol.md"
let binding = read "src/FS.GG.Coordination.Protocol/Generated/Protocol.Generated.fs"
for required in [ "fsgg.github-substrate.epoch-wire/1"; "fleet-cutover:fs-gg-production"; "OpenV2>ObservingV2>ContractingV1>OperatingV2" ] do
    if not(protocol.Contains required) || not(binding.Contains required) then failwith $"generated protocol binding omitted {required}"
printfn "GITHUB_EPOCH_WIRE_OK states=%d transitions=%d controls=%d" Q.requiredPhases.Length Q.requiredTransitions.Length expected.Length
