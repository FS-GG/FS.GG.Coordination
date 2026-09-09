module FS.GG.Coordination.GitHubLedgerProtectionProviderCodecTests

open System
open System.IO
open Xunit
open FS.GG.Coordination.GitHub

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read name = File.ReadAllBytes(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2",name)) |> ReadOnlyMemory<byte>

[<Fact>]
let ``first capture is explicitly uninitialized and second binds its actual bytes`` () =
    let first = LedgerProtectionProviderCodec.decode (read "live-capture-pass1.json") |> Result.defaultWith (failwithf "%A")
    let second = LedgerProtectionProviderCodec.decode (read "live-capture-pass2.json") |> Result.defaultWith (failwithf "%A")
    Assert.Equal(LedgerCaptureContinuity.Uninitialized, first.Continuity)
    Assert.True(LedgerProtectionProviderCodec.qualifiedObservation first |> Result.isError)
    Assert.Equal(LedgerCaptureContinuity.Matched, second.Continuity)
    Assert.Equal(Some "e119eb24a1e6567e78887684fd0b990302bb45add1a98e9090b0f081f6206cc5", second.PreviousEvidenceSha256)
    Assert.Equal(first.NormalizedSetSha256, second.NormalizedSetSha256)
    Assert.Equal(first.RawSetSha256, second.RawSetSha256)
    Assert.Equal(CurrentPreInstall, LedgerProtectionConformance.classify second.CapturedAt (TimeSpan.FromMinutes 5.0) second.Conformance)

[<Fact>]
let ``matched live capture compiles through the provider adapter without apply authority`` () =
    let capture = LedgerProtectionProviderCodec.decode (read "live-capture-pass2.json") |> Result.defaultWith (failwithf "%A")
    let observation = LedgerProtectionProviderCodec.qualifiedObservation capture |> Result.defaultWith (failwithf "%A")
    Assert.Equal(Some capture.RawSetSha256, observation.RawSetSha256)
    Assert.Equal(Some capture.NormalizedSetSha256, observation.NormalizedSetSha256)
    let normalized = LedgerProtectionProviderAdapter.normalize capture.CapturedAt (TimeSpan.FromMinutes 5.0) observation |> Result.defaultWith (failwithf "%A")
    Assert.Equal(Some capture.RawSetSha256, normalized.ProviderRawSetSha256)
    Assert.Equal(Some capture.NormalizedSetSha256, normalized.ProviderNormalizedSetSha256)
    let plan = LedgerProtectionProviderAdapter.compile capture.CapturedAt (TimeSpan.FromMinutes 5.0) observation |> Result.defaultWith (failwithf "%A")
    Assert.False(plan.ApplyAuthorized)
    Assert.Equal("refs/heads/fsgg/v2/journal/cutover/d5",plan.FleetRef)
    Assert.Contains(plan.ProductionBlockers, fun value -> value.Contains("identity is missing"))
    Assert.Contains(plan.ProductionBlockers, fun value -> value.Contains("control issue identity is unbound"))

[<Fact>]
let ``normalized tamper refuses before provider compilation`` () =
    let bytes = File.ReadAllText(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2/live-capture-pass2.json"))
    let tampered = bytes.Replace("v2-journal-writer","v2-journal-writer-tampered") |> Text.Encoding.UTF8.GetBytes |> ReadOnlyMemory<byte>
    Assert.True(LedgerProtectionProviderCodec.decode tampered |> Result.isError)

[<Fact>]
let ``declared raw-set tamper refuses even when normalized content is unchanged`` () =
    let bytes = File.ReadAllText(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2/live-capture-pass2.json"))
    let tampered = bytes.Replace("2684f8a6d3e2530d62a62f8d49127c7e61e9f389cf6c416edd8a80ba2d9cdfe5", String.replicate 64 "0") |> Text.Encoding.UTF8.GetBytes |> ReadOnlyMemory<byte>
    Assert.True(LedgerProtectionProviderCodec.decode tampered |> Result.isError)

[<Fact>]
let ``capture gaps refuse qualification`` () =
    let bytes = File.ReadAllText(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2/live-capture-pass2.json"))
    let tampered = bytes.Replace("\"gaps\":[]", "\"gaps\":[\"provider-read-refused\"]") |> Text.Encoding.UTF8.GetBytes |> ReadOnlyMemory<byte>
    Assert.True(LedgerProtectionProviderCodec.decode tampered |> Result.isError)
