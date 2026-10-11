namespace FS.GG.Coordination.Orchestration.Execution.Tests
open System
open Xunit
open FS.GG.Coordination.Orchestration.Execution

module ProcessContractAdapterTests =
    let private key = { AssignmentId = Guid.NewGuid(); AttemptId = Guid.NewGuid(); Generation = Int64.MaxValue }
    let private intent =
        { Schema = ExecutionProtocol.launchSchema; Key = key; InputDigest = String.replicate 64 "c"
          Workspace = "/fixture"; Requested = { Model = None; Effort = None }
          Limits = { Deadline = DateTimeOffset.Parse "2026-10-11T00:00:00Z"; MaximumRuntime = TimeSpan.FromMinutes 1.; MaximumAttempts = 1 }
          RecordedAt = DateTimeOffset.Parse "2026-10-10T23:59:00Z" }
    [<Fact>]
    let ``Execution binding round trips without generation narrowing`` () =
        Assert.Equal(key,key |> ProcessContractAdapter.toBinding |> ProcessContractAdapter.ofBinding)
    [<Fact>]
    let ``local invocation fields do not replace durable input digest`` () =
        let invocation = Guid.NewGuid()
        match ProcessContractAdapter.bindInvocation intent invocation UInt64.MaxValue (String.replicate 64 "a") "ordinary" (String.replicate 64 "b") with
        | Error errors -> failwithf "Refused %A" errors
        | Ok bound ->
            Assert.Equal(invocation,bound.InvocationId)
            Assert.Equal(UInt64.MaxValue,bound.LaunchOrdinal)
            Assert.Equal(String.replicate 64 "a",bound.SourceDigest)
            Assert.Equal(String.replicate 64 "c",intent.InputDigest)
            Assert.Equal(Some(ProcessContractAdapter.toBinding key),bound.Execution)
    [<Theory>]
    [<InlineData("schema")>]
    [<InlineData("input")>]
    [<InlineData("source")>]
    [<InlineData("profile")>]
    [<InlineData("invocation")>]
    let ``invalid source profile schema and invocation joins refuse`` (field: string) =
        let intent = if field = "schema" then { intent with Schema = "other" } elif field = "input" then { intent with InputDigest = "bad" } else intent
        let source = if field = "source" then "bad" else String.replicate 64 "a"
        let profile = if field = "profile" then "" else "ordinary"
        let invocation = if field = "invocation" then Guid.Empty else Guid.NewGuid()
        Assert.True(ProcessContractAdapter.bindInvocation intent invocation 0UL source profile (String.replicate 64 "b") |> Result.isError)
