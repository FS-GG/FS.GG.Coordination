module FS.GG.Coordination.RoadmapWorkTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private bytes (value: string) = Encoding.UTF8.GetBytes value |> ReadOnlyMemory<byte>
let private sha (value: string) = SHA256.HashData(Encoding.UTF8.GetBytes value) |> Convert.ToHexString |> _.ToLowerInvariant()
let private revision = String.replicate 40 "a"
let private sourceRevision = String.replicate 40 "b"
let private artifactDigest = String.replicate 64 "c"
let private roadmap = "- [ ] **GS2-01.5 — Establish custom CI.** Prior.\n- [ ] **GS2-01.6 — Create the work skill.** Selected.\n"

let private unitWithContract (canonical: string) =
    canonical.Substring(0, canonical.Length - 1) + sprintf ",\"contractSha256\":\"%s\"}" (sha canonical)

let private unit5 =
    """{"exitGate":"accepted","gateCommands":[],"id":"GS2-01.5","owner":"FS.GG.Coordination","permissionCeiling":["local"],"prerequisites":[],"qGates":[],"title":"Establish custom CI"}"""
    |> unitWithContract

let private unit6 =
    """{"exitGate":"stop here","gateCommands":["skill-structure"],"id":"GS2-01.6","owner":"FS.GG.Coordination","permissionCeiling":["local only"],"prerequisites":["GS2-01.5"],"qGates":["Q0"],"title":"Create the work skill"}"""
    |> unitWithContract

let private unit5Contract =
    use document = JsonDocument.Parse unit5
    document.RootElement.GetProperty("contractSha256").GetString()

let private indexText =
    sprintf
        """{"schema":"fsgg.coordination.roadmap-index/1","roadmap":{"repository":"FS-GG/.github","revision":"%s","path":"docs/github-substrate-v2-roadmap.md","sha256":"%s"},"units":[%s,%s]}"""
        revision
        (sha roadmap)
        unit5
        unit6

let private receiptWith state unitContract digestOverride =
    let canonical =
        sprintf
            """{"acceptedAt":"2026-08-27T00:00:00Z","artifacts":[{"name":"merge","sha256":"%s"}],"schema":"fsgg.coordination.unit-acceptance/1","sourceRevision":"%s","state":"%s","unitContractSha256":"%s","unitId":"GS2-01.5"}"""
            artifactDigest sourceRevision state unitContract
    let digest = defaultArg digestOverride (sha canonical)
    sprintf
        """{"schema":"fsgg.coordination.unit-acceptance/1","unitId":"GS2-01.5","state":"%s","unitContractSha256":"%s","sourceRevision":"%s","artifacts":[{"name":"merge","sha256":"%s"}],"acceptedAt":"2026-08-27T00:00:00Z","digest":"%s"}"""
        state unitContract sourceRevision artifactDigest digest

let private validReceipt = receiptWith "accepted" unit5Contract None |> bytes
let private codes (findings: RoadmapWorkFinding list) = findings |> List.map _.Code |> Set.ofList

[<Fact>]
let ``known unit inspection is bound to exact roadmap bytes`` () =
    match RoadmapWork.inspect (bytes indexText) (bytes roadmap) "GS2-01.6" with
    | Ok inspection ->
        Assert.Equal(revision, inspection.RoadmapRevision)
        Assert.True(inspection.Unit.Prerequisites = [ "GS2-01.5" ])
        Assert.True(inspection.Unit.QGates = [ "Q0" ])
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

[<Fact>]
let ``changed roadmap and unknown unit are refused distinctly`` () =
    match RoadmapWork.inspect (bytes indexText) (bytes (roadmap + "changed")) "GS2-01.6" with
    | Ok _ -> Assert.Fail("changed roadmap was accepted")
    | Error findings -> Assert.Contains("RW-ROADMAP-DIGEST", codes findings)
    match RoadmapWork.inspect (bytes indexText) (bytes roadmap) "GS2-01.7" with
    | Ok _ -> Assert.Fail("unknown unit was accepted")
    | Error findings -> Assert.Contains("RW-UNIT-UNKNOWN", codes findings)

[<Fact>]
let ``accepted exact prerequisite is ready`` () =
    match RoadmapWork.checkPrerequisites (bytes indexText) (bytes roadmap) [ validReceipt ] "GS2-01.6" with
    | Ok status -> Assert.True(status.Ready); Assert.Single(status.AcceptedReceiptDigests) |> ignore
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

[<Fact>]
let ``missing duplicate rejected stale and tampered receipts fail closed`` () =
    let assertCode expected receipts =
        match RoadmapWork.checkPrerequisites (bytes indexText) (bytes roadmap) receipts "GS2-01.6" with
        | Ok _ -> Assert.Fail($"{expected} was accepted")
        | Error findings -> Assert.Contains(expected, codes findings)
    assertCode "RW-PREREQUISITE-MISSING" []
    assertCode "RW-RECEIPT-DUPLICATE" [ validReceipt; validReceipt ]
    assertCode "RW-RECEIPT-STATE" [ receiptWith "rejected" unit5Contract None |> bytes ]
    assertCode "RW-RECEIPT-STALE" [ receiptWith "accepted" (String.replicate 64 "d") None |> bytes ]
    assertCode "RW-RECEIPT-TAMPERED" [ receiptWith "accepted" unit5Contract (Some(String.replicate 64 "e")) |> bytes ]

[<Fact>]
let ``candidate manifest is deterministic and never claims acceptance`` () =
    let candidate = { Commit = String.replicate 40 "1"; Tree = String.replicate 40 "2" }
    let artifact = { Name = "skill"; Path = ".agents/skills/github-substrate-v2-work/SKILL.md"; Bytes = bytes "skill" }
    let create () = RoadmapWork.createManifest (bytes indexText) (bytes roadmap) [ validReceipt ] "GS2-01.6" candidate "2026-08-27T01:00:00Z" [ artifact ]
    match create (), create () with
    | Ok first, Ok second ->
        Assert.Equal<byte>(first, second)
        use document = JsonDocument.Parse first
        Assert.Equal("candidate", document.RootElement.GetProperty("state").GetString())
        let mutable absent = Unchecked.defaultof<JsonElement>
        Assert.False(document.RootElement.TryGetProperty("accepted", &absent))
    | Error findings, _ | _, Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

[<Fact>]
let ``manifest refuses traversal and validation refuses changed candidate`` () =
    let candidate = { Commit = String.replicate 40 "1"; Tree = String.replicate 40 "2" }
    let escaped = { Name = "escape"; Path = "../secret"; Bytes = bytes "secret" }
    match RoadmapWork.createManifest (bytes indexText) (bytes roadmap) [ validReceipt ] "GS2-01.6" candidate "2026-08-27T01:00:00Z" [ escaped ] with
    | Ok _ -> Assert.Fail("path traversal was accepted")
    | Error findings -> Assert.Contains("RW-PATH", codes findings)
    let artifact = { Name = "skill"; Path = "skill.md"; Bytes = bytes "skill" }
    let manifest = RoadmapWork.createManifest (bytes indexText) (bytes roadmap) [ validReceipt ] "GS2-01.6" candidate "2026-08-27T01:00:00Z" [ artifact ] |> Result.defaultWith (fun _ -> failwith "fixture")
    let changed = { candidate with Tree = String.replicate 40 "3" }
    match RoadmapWork.validateManifest (bytes indexText) (bytes roadmap) [ validReceipt ] "GS2-01.6" changed (ReadOnlyMemory<byte>(manifest)) with
    | Ok _ -> Assert.Fail("changed candidate was accepted")
    | Error findings -> Assert.Contains("RW-MANIFEST-MISMATCH", codes findings)

[<Fact>]
let ``unknown qualification gate and JSON members are refused`` () =
    let unknownGate = indexText.Replace("\"Q0\"", "\"Q99\"")
    match RoadmapWork.inspect (bytes unknownGate) (bytes roadmap) "GS2-01.6" with
    | Ok _ -> Assert.Fail("unknown Q gate was accepted")
    | Error findings -> Assert.Contains("RW-Q-GATE", codes findings)
    let unknownMember = indexText.Replace("\"schema\":", "\"surprise\":true,\"schema\":")
    match RoadmapWork.inspect (bytes unknownMember) (bytes roadmap) "GS2-01.6" with
    | Ok _ -> Assert.Fail("unknown member was accepted")
    | Error findings -> Assert.Contains("RW-JSON-UNKNOWN", codes findings)
