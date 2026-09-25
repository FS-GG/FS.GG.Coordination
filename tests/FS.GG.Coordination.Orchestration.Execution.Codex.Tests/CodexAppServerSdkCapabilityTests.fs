namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type CodexAppServerSdkCapabilityTests() =
    let fixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-server", "capability-schema.json")
        |> File.ReadAllBytes
    let inspect bytes = CodexAppServerSdkCapability.inspect "codex-cli 0.156.1" bytes
    let replace oldText newText =
        Encoding.UTF8.GetString(fixture).Replace(oldText, newText, StringComparison.Ordinal)
        |> Encoding.UTF8.GetBytes
    let withServerRoute methodName target =
        let schema = JsonNode.Parse(Encoding.UTF8.GetString fixture)
        let definitions = schema["definitions"]
        let notification = definitions["ServerNotification"]
        let routes = (notification["oneOf"]).AsArray()
        let route =
            JsonNode.Parse
                (sprintf
                    """{"properties":{"method":{"enum":["%s"],"type":"string"},"params":{"$ref":"#/definitions/%s"}},"required":["method","params"],"type":"object"}"""
                    methodName target)
        routes.Add route
        Encoding.UTF8.GetBytes(schema.ToJsonString())
    let withDefinitionType (name: string) (kind: string) =
        let schema = JsonNode.Parse(Encoding.UTF8.GetString fixture)
        let definitions = schema["definitions"]
        let definition = (definitions[name]).AsObject()
        definition["type"] <- JsonValue.Create(kind)
        Encoding.UTF8.GetBytes(schema.ToJsonString())

    [<Fact>]
    member _.``pinned authored schema yields no native usage or direct attachment authority``() =
        match inspect fixture with
        | Ok report ->
            Assert.Equal("codex-cli 0.156.1", report.CliVersion)
            Assert.Equal("absent-from-turn-completed", report.TurnCompletedUsage)
            Assert.Equal("last-and-cumulative-snapshots", report.ThreadUsageShape)
            Assert.Equal("internal-one-upstream-response", report.RawResponseUsageScope)
            Assert.Equal("running-thread-rejoin-described", report.RunningThreadResumeDescription)
            Assert.Equal("not-authenticated-by-schema", report.CurrentDirectSessionAttachment)
            Assert.Equal("not-established", report.NativeCompletedTurnUsageVerdict)
        | Error code -> failwithf "unexpected schema refusal %s" code

    [<Fact>]
    member _.``foreign object kind in usage-relevant definition requires review``() =
        for definition in [ "TurnCompletedNotification"; "ThreadTokenUsage" ] do
            Assert.Equal(
                Error "app-server-sdk-schema-drift",
                inspect (withDefinitionType definition "array")
            )

    [<Fact>]
    member _.``explicit object kind preserves pinned diagnostic``() =
        for definition in [ "TurnCompletedNotification"; "ThreadTokenUsage" ] do
            match inspect (withDefinitionType definition "object") with
            | Ok report -> Assert.Equal("not-established", report.NativeCompletedTurnUsageVerdict)
            | Error code -> failwithf "object definition refused: %s" code

    [<Fact>]
    member _.``new usage field on turn completed requires review instead of acceptance``() =
        let changed = replace "\"status\":{}}},\"ThreadTokenUsageUpdatedNotification\""
                              "\"status\":{},\"usage\":{}}},\"ThreadTokenUsageUpdatedNotification\""
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect changed)

    [<Fact>]
    member _.``renamed snapshot counters or changed raw-response scope requires review``() =
        let changedSchema = JsonNode.Parse(Encoding.UTF8.GetString fixture)
        let definitions = changedSchema["definitions"]
        let usageDefinition = definitions["ThreadTokenUsage"]
        let usage = usageDefinition["properties"].AsObject()
        let last = usage["last"].DeepClone()
        usage.Remove("last") |> ignore
        usage["latestTurn"] <- last
        let changedSnapshot = Encoding.UTF8.GetBytes(changedSchema.ToJsonString())
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect changedSnapshot)
        let changedRaw = replace "Internal-only notification containing the exact usage from one upstream Responses API completion."
                                 "Public completed-turn usage notification."
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect changedRaw)

    [<Fact>]
    member _.``snapshot counters cannot become non-usage values``() =
        let schema = JsonNode.Parse(Encoding.UTF8.GetString fixture)
        for counter in [ "last"; "total" ] do
            let changed = JsonNode.Parse(schema.ToJsonString())
            let definitions = changed["definitions"]
            let usage = definitions["ThreadTokenUsage"]
            let fields = usage["properties"].AsObject()
            fields[counter] <- JsonNode.Parse("""{"type":"string"}""")
            Assert.Equal(
                Error "app-server-sdk-schema-drift",
                inspect (Encoding.UTF8.GetBytes(changed.ToJsonString()))
            )

    [<Fact>]
    member _.``snapshot breakdown target and numeric counters require review``() =
        let schema = JsonNode.Parse(Encoding.UTF8.GetString fixture)
        let definitions = schema["definitions"].AsObject()
        definitions.Remove("TokenUsageBreakdown") |> ignore
        Assert.Equal(
            Error "app-server-sdk-schema-drift",
            inspect (Encoding.UTF8.GetBytes(schema.ToJsonString()))
        )
        let untyped = JsonNode.Parse(Encoding.UTF8.GetString fixture)
        let untypedDefinitions = untyped["definitions"]
        let untypedTarget = untypedDefinitions["TokenUsageBreakdown"].AsObject()
        untypedTarget.Remove("type") |> ignore
        Assert.Equal(
            Error "app-server-sdk-schema-drift",
            inspect (Encoding.UTF8.GetBytes(untyped.ToJsonString()))
        )
        let changed = JsonNode.Parse(Encoding.UTF8.GetString fixture)
        let target = changed["definitions"]["TokenUsageBreakdown"]
        let properties = target["properties"].AsObject()
        properties["totalTokens"] <- JsonNode.Parse("""{"type":"string","format":"int64"}""")
        Assert.Equal(
            Error "app-server-sdk-schema-drift",
            inspect (Encoding.UTF8.GetBytes(changed.ToJsonString()))
        )
        let oversized = JsonNode.Parse(Encoding.UTF8.GetString fixture)
        let oversizedTarget = oversized["definitions"]["TokenUsageBreakdown"]
        let oversizedProperties = oversizedTarget["properties"]
        let cacheWrites = oversizedProperties["cacheWriteInputTokens"].AsObject()
        cacheWrites["default"] <- JsonNode.Parse("18446744073709551616")
        Assert.Equal(
            Error "app-server-sdk-schema-drift",
            inspect (Encoding.UTF8.GetBytes(oversized.ToJsonString()))
        )

    [<Fact>]
    member _.``raw response usage cannot become a string or foreign union``() =
        for replacement in
            [ """{"type":"string"}"""
              """{"anyOf":[{"$ref":"#/definitions/ForeignUsage"},{"type":"null"}]}"""
              """{"allOf":[{"$ref":"#/definitions/TokenUsageBreakdown"}]}""" ] do
            let changed = JsonNode.Parse(Encoding.UTF8.GetString fixture)
            let definitions = changed["definitions"]
            let raw = definitions["RawResponseCompletedNotification"]
            let properties = raw["properties"].AsObject()
            properties["usage"] <- JsonNode.Parse(replacement)
            Assert.Equal(
                Error "app-server-sdk-schema-drift",
                inspect (Encoding.UTF8.GetBytes(changed.ToJsonString()))
            )

    [<Fact>]
    member _.``running-thread resume description alone never authenticates this session``() =
        let changed = replace "If thread_id identifies a running thread, app-server rejoins that thread."
                              "Resume a thread."
        match inspect changed with
        | Ok report ->
            Assert.Equal("running-thread-rejoin-undetermined", report.RunningThreadResumeDescription)
            Assert.Equal("not-authenticated-by-schema", report.CurrentDirectSessionAttachment)
        | Error code -> failwithf "unexpected schema refusal %s" code

    [<Fact>]
    member _.``wrong CLI version and malformed or duplicate keys refuse``() =
        Assert.Equal(
            Error "app-server-sdk-version-unpinned",
            CodexAppServerSdkCapability.inspect "codex-cli 0.156.2" fixture
        )
        Assert.Equal(
            Error "app-server-sdk-schema-json-invalid",
            inspect (Encoding.UTF8.GetBytes "{bad-json")
        )
        let duplicate = replace "\"required\":[\"threadId\",\"turn\"],\"properties\""
                                "\"required\":[\"threadId\",\"turn\"],\"required\":[\"threadId\",\"turn\"],\"properties\""
        Assert.Equal(Error "app-server-sdk-schema-duplicate-key", inspect duplicate)

    [<Fact>]
    member _.``missing definitions or turn reference refuses``() =
        let missing = Encoding.UTF8.GetBytes "{}"
        Assert.Equal(Error "app-server-sdk-schema-invalid", inspect missing)
        let foreign = replace "#/definitions/Turn" "#/definitions/OtherTurn"
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect foreign)
        let composed = replace "\"status\":{}}},\"ThreadTokenUsageUpdatedNotification\""
                               "\"status\":{}},\"allOf\":[{}]},\"ThreadTokenUsageUpdatedNotification\""
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect composed)
        let repeatedRequired = replace "\"required\":[\"id\",\"items\",\"status\"]"
                                       "\"required\":[\"id\",\"items\",\"status\",\"status\"]"
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect repeatedRequired)

    [<Fact>]
    member _.``notification route cannot redirect turn completed to a usage-bearing definition``() =
        let redirected =
            replace "#/definitions/TurnCompletedNotification"
                    "#/definitions/UsageBearingTurnCompletedNotification"
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect redirected)

    [<Fact>]
    member _.``added usage-bearing turn route requires review before no-usage claim``() =
        let added = withServerRoute "turn/usageCompleted" "UsageBearingTurnCompletedNotification"
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect added)

    [<Fact>]
    member _.``unrelated extra notification route does not invent usage``() =
        let added = withServerRoute "foreign/info" "ForeignResumeParams"
        match inspect added with
        | Ok report -> Assert.Equal("not-established", report.NativeCompletedTurnUsageVerdict)
        | Error code -> failwithf "unrelated route refused: %s" code

    [<Fact>]
    member _.``notification method aliases and compositional routing require review``() =
        let alias =
            replace "\"thread/tokenUsage/updated\"" "\"turn/completed\""
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect alias)
        let composed =
            replace "\"ServerNotification\":{\"properties\""
                    "\"ServerNotification\":{\"allOf\":[{}],\"properties\""
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect composed)

    [<Fact>]
    member _.``foreign protocol root cannot inherit selected definitions``() =
        let changed = replace "\"title\":\"CodexAppServerProtocolV2\""
                              "\"title\":\"ForeignProtocol\""
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect changed)

    [<Fact>]
    member _.``resume method cannot route to an unused foreign definition``() =
        let redirected =
            replace "#/definitions/ThreadResumeParams" "#/definitions/ForeignResumeParams"
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect redirected)

    [<Fact>]
    member _.``duplicate resume method requires review``() =
        let alias = replace "\"foreign/other\"" "\"thread/resume\""
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect alias)

    [<Fact>]
    member _.``compositional client request routing requires review``() =
        let composed =
            replace "\"ClientRequest\":{\"oneOf\""
                    "\"ClientRequest\":{\"allOf\":[{}],\"oneOf\""
        Assert.Equal(Error "app-server-sdk-schema-drift", inspect composed)
