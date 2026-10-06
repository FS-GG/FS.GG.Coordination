namespace FS.GG.Coordination.PortableWorkspace.UnitTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Runtime.InteropServices
open Xunit
open FS.GG.Coordination.Cli

/// Observation/refusal source. No fixture is a protected grant, current installed
/// image proof, launched workload or native observation.
module PortableWorkspaceC2AdmissionTests =
    module C2 = PortableWorkspaceC2Phase
    module A = PortableWorkspaceC2Admission
    module Options = PortableWorkspaceC2RuntimeCommand

    let private fixture (name: string) : byte array =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "C2Fixtures", name + "-request.json"))
    let private get (value: Result<'a, 'b>) : 'a =
        match value with
        | Ok result -> result
        | Error failure -> failwithf "Unexpected refusal: %A" failure
    let private readRefuses (code: string) (result: Result<'a, A.Diagnostic list>) =
        match result with
        | Ok _ -> failwith "Expected bounded refusal, never opaque authority"
        | Error diagnostics ->
            Assert.InRange(diagnostics.Length, 1, 32)
            Assert.Contains(diagnostics, fun (row: A.Diagnostic) -> row.Code = code)
            for row in diagnostics do
                Assert.InRange(row.Field.Length, 1, 256)
                Assert.InRange(row.Code.Length, 1, 128)

    [<Theory>]
    [<InlineData("scaffold")>]
    [<InlineData("compiler")>]
    let ``description copies the six declared fields without enrollment`` (kind: string) =
        let original = fixture kind
        let request = C2.decodeRequest original |> get
        let description = C2.describeRequest request
        use parsed = JsonDocument.Parse(original)
        let field (name: string) = parsed.RootElement.GetProperty(name).GetString()
        Assert.Equal(field "enrollmentId", description.EnrollmentId)
        Assert.Equal(field "profileId", description.ProfileId)
        Assert.Equal(field "profileSha256", description.ProfileSha256)
        Assert.Equal(field "operationId", description.OperationId)
        Assert.Equal(field "idempotencyId", description.IdempotencyId)
        let expectedDigest = SHA256.HashData(original) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        Assert.Equal(expectedDigest, description.RequestSha256)
        // Public records may be copied/edited, but do not mutate the opaque request.
        let altered = { description with EnrollmentId = "caller-forged" }
        Assert.NotEqual(description.EnrollmentId, altered.EnrollmentId)
        Array.fill original 0 original.Length 0uy
        let exported = C2.encodeRequest request
        let exportedBefore = Array.copy exported
        Array.fill exported 0 exported.Length 0uy
        Assert.Equal(description, C2.describeRequest request)
        Assert.Equal<byte>(exportedBefore, C2.encodeRequest request)

    [<Fact>]
    let ``malformed bytes never become a described request`` () =
        let raw = fixture "scaffold"
        let text = Encoding.UTF8.GetString(raw)
        let duplicate = text.Replace("\"enrollmentId\":", "\"enrollmentId\":\"duplicate\",\"enrollmentId\":", StringComparison.Ordinal)
        Assert.NotEqual(text, duplicate)
        for bad in [ Encoding.UTF8.GetBytes duplicate; [| 0xffuy |]; Array.create 1048577 32uy ] do
            match C2.decodeRequest bad with
            | Error diagnostics -> Assert.InRange(diagnostics.Length, 1, 32)
            | Ok _ -> failwith "Malformed bytes must not create an opaque Request"

    [<Theory>]
    [<InlineData("relative.json")>]
    [<InlineData("/caller/request.json")>]
    [<InlineData("/var/lib/fsgg/portable-workspaces/c2/1000/requests/../request.json")>]
    [<InlineData("/var/lib/fsgg/portable-workspaces/c2/1001/requests/request.json")>]
    let ``literal request outside exact spool refuses before reading`` (path: string) =
        let invocation = Options.parse [| "execute-c2-phase"; "--request"; path |] |> get
        Assert.Equal(path, Options.requestPath invocation)
        match A.beginEntry () with
        | Ok entry -> A.readRequest entry invocation |> readRefuses "request-path-outside-spool"
        | Error diagnostics when not (OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture <> Architecture.X64 ->
            Assert.Contains(diagnostics, fun (row: A.Diagnostic) -> row.Code = "linux-x64-required")
        | Error diagnostics -> failwithf "Entry observation failed: %A" diagnostics

    // Exact API calls for later custody-backed fixture preparation. These helpers
    // accept only opaque producer outputs, never callback readers/boolean proofs.
    // Not Facts: actual linked/replaced/denied/expired fixtures are not provisioned.
    let private exerciseReadRefusal (entry: A.EntryContext) invocation code =
        A.readRequest entry invocation |> readRefuses code
    let private exerciseEnrollmentRefusal (entry: A.EntryContext) (request: A.CapturedRequest) code =
        A.resolveEnrollment entry request |> readRefuses code
    let private exerciseRoleRefusal entry enrollment request code =
        A.captureRoles entry enrollment request |> readRefuses code
    let private exerciseAdmissionRefusal entry enrollment request roles code =
        A.admit entry enrollment request roles |> readRefuses code
    let private exerciseCapturedCopy (request: A.CapturedRequest) =
        let identity = A.requestIdentity request
        let first = A.requestBytes request
        let original = Array.copy first
        Array.fill first 0 first.Length 0uy
        Assert.Equal(identity, A.requestIdentity request)
        Assert.Equal<byte>(original, A.requestBytes request)

    let private enrollmentRefuses code raw =
        A.decodeEnrollmentDeclaration raw |> readRefuses code

    [<Fact>]
    let ``closed enrollment rejects oversized malformed and duplicate declarations`` () =
        enrollmentRefuses "enrollment-size-refused" (Array.create 65537 32uy)
        enrollmentRefuses "enrollment-size-refused" [||]
        // Both violate exact closed membership before any physical read occurs.
        let unknown = Encoding.UTF8.GetBytes "{\"callerPath\":\"/caller/grant.json\"}"
        enrollmentRefuses "closed-members-required" unknown
        let repeated = Encoding.UTF8.GetBytes "{\"schema\":\"first\",\"schema\":\"second\"}"
        let equalBytes = unknown = repeated
        Assert.False(equalBytes)
        enrollmentRefuses "closed-members-required" repeated

    [<Fact>]
    let ``invalid UTF8 cannot become an enrollment declaration`` () =
        enrollmentRefuses "observation-refused" [| 0xffuy |]

    [<Theory>]
    [<InlineData("scaffold")>]
    [<InlineData("compiler")>]
    let ``read declaration digest covers exact canonical input array`` (kind: string) =
        let raw = fixture kind
        let request = C2.decodeRequest raw |> get
        let reads = C2.describeReads request
        use parsed = JsonDocument.Parse(raw)
        let arrayBytes = Encoding.UTF8.GetBytes(parsed.RootElement.GetProperty("inputs").GetRawText() + "\n")
        let expectedDigest = SHA256.HashData(arrayBytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        Assert.Equal(expectedDigest, reads.InputInventorySha256)
        Assert.Equal(parsed.RootElement.GetProperty("inputs").GetArrayLength(), reads.Inputs.Length)
        Assert.Equal(parsed.RootElement.GetProperty("consumer").GetProperty("source").GetString(), reads.ConsumerSource)
        let original = C2.encodeRequest request
        let changed = { reads with Inputs = []; ConsumerSource = "caller-edited" }
        Assert.NotEqual(reads, changed)
        Assert.Equal(reads, C2.describeReads request)
        Assert.Equal<byte>(original, C2.encodeRequest request)

    [<Theory>]
    [<InlineData("\"allowedUid\":1000", "\"allowedUid\":0", "fixed-profile-uid-root-required")>]
    [<InlineData("\"allowedUid\":1000", "\"allowedUid\":1001", "fixed-profile-uid-root-required")>]
    [<InlineData("\"fenceGeneration\":1", "\"fenceGeneration\":0", "fence-required")>]
    [<InlineData("\"sdk\":\"10.0.401\"", "\"sdk\":\"10.0.400\"", "fixed-sdk-runtime-required")>]
    [<InlineData("\"consumerRepository\":\"FS-GG/FS.GG.SDD\"", "\"consumerRepository\":\"FS-GG/FS.GG.Governance\"", "fixed-consumer-required")>]
    [<InlineData("\"workDeadlineMonotonicNs\":40000000000", "\"workDeadlineMonotonicNs\":50000000000", "original-monotonic-window-required")>]
    let ``shaped declarations do not excuse wrong UID fence image consumer or clock`` before after code =
        // Entirely synthetic/zero-digest schema vector, never read as a protected
        // file. Decoder success is only EnrollmentDeclaration, not TrustedEnrollment.
        let raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "C2Fixtures", "schema-only-unqualified.json"))
        A.decodeEnrollmentDeclaration raw |> get |> ignore
        let text = Encoding.UTF8.GetString(raw)
        let altered = text.Replace(before, after, StringComparison.Ordinal)
        Assert.NotEqual(text, altered)
        enrollmentRefuses code (Encoding.UTF8.GetBytes altered)
