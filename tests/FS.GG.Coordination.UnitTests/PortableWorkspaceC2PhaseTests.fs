namespace FS.GG.Coordination.PortableWorkspace.UnitTests

open System
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Cli

/// Authored byte fixtures exercise the actual producer API once its body and
/// project entries are admitted. No fixture is an observed native receipt.
module PortableWorkspaceC2PhaseTests =
    module C2 = PortableWorkspaceC2Phase

    let private fixture (name: string) =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "C2Fixtures", name + ".json"))
    let private text bytes = Encoding.UTF8.GetString(bytes: byte array)
    let private bytes (value: string) = Encoding.UTF8.GetBytes value
    let private changed (original: byte array) (altered: byte array) =
        Assert.False((original = altered), "Negative mutation must change the actual bytes")
        altered
    let private change before after raw =
        bytes ((text raw).Replace(before, after, StringComparison.Ordinal)) |> changed raw
    // Existing properties are ordinal-sorted in the goldens. Mutations below
    // replace them in place; added output objects explicitly use ordinal order.
    let private mutateDocument (raw: byte array) (mutation: JsonNode -> unit) =
        let node = JsonNode.Parse(text raw)
        mutation node
        let options = JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
        bytes (node.ToJsonString(options) + "\n") |> changed raw
    let private get = function
        | Ok value -> value
        | Error diagnostics -> failwithf "Unexpected diagnostic: %A" diagnostics
    let private rejects (outcome: Result<'a, C2.Diagnostic list>) =
        match outcome with
        | Error diagnostics ->
            Assert.InRange(diagnostics.Length, 1, 32)
            for diagnostic in diagnostics do
                Assert.InRange(diagnostic.Field.Length, 1, 256)
                Assert.InRange(diagnostic.Code.Length, 1, 128)
        | Ok _ -> failwith "Expected refusal"

    // Independently supplied fixed data starts from the unchanged golden fixture.
    // Mutated requests are never used to reconstruct their expected selection.
    // This convenience reader attests neither enrollment nor physical inputs.
    let private selectionFromBytes (raw: byte array) (kind: string) : C2.DeclaredSelection =
        let node = JsonNode.Parse(text raw)
        let str (n: JsonNode) (key: string) = n[key].GetValue<string>()
        let number (n: JsonNode) (key: string) = n[key].GetValue<int64>()
        let strings (n: JsonNode) (key: string) =
            n[key].AsArray() |> Seq.map (fun (value: JsonNode) -> value.GetValue<string>()) |> Seq.toList
        let budget = node["budget"]
        let containerPaths =
            [ "archive", "/inputs/archive.nupkg"; "catalog", "/inputs/catalog.json"
              "policy", "/inputs/policy.json"; "compiler-source", "/inputs/compiler"
              "baseline", "/inputs/public-surface.baseline"; "restore-state", "/inputs/restore-state"
              "home", "/work/home"; "cli-home", "/work/cli-home"; "hive", "/work/hive"
              "packages", "/work/packages"; "compiler-work", "/work/compiler"
              "stage", (if kind = "scaffold" then "/output/project" else "/output")
              "handoff", "/handoff" ] |> Map.ofList
        let inputRows : C2.InputDeclaration list =
            node["inputs"].AsArray()
            |> Seq.map (fun row ->
                ({ Id = str row "id"; Role = str row "role"; HostPath = str row "hostPath"
                   RelativePath = str row "relativePath"; Bytes = number row "bytes"; Sha256 = str row "sha256" }: C2.InputDeclaration))
            |> Seq.toList
        let roles : C2.RolePath list =
            [ yield! (node["ownedRoots"].AsArray() |> Seq.map (fun row -> str row "role", str row "hostPath"))
              yield! (inputRows |> Seq.map (fun row ->
                  row.Role, (if row.Role = "compiler-source" then Path.GetDirectoryName row.HostPath else row.HostPath))) ]
            |> List.distinct
            |> List.map (fun (role, path) -> ({ Role = role; HostPath = path; ContainerPath = containerPaths[role] }: C2.RolePath))
        let steps : C2.StepDeclaration list =
            node["steps"].AsArray()
            |> Seq.map (fun row ->
                let translations : C2.Translation list =
                    row["translations"].AsArray()
                    |> Seq.map (fun item ->
                        ({ ArgumentIndex = item["argumentIndex"].GetValue<int>()
                           Role = str item "role"; Prefix = str item "prefix"; RelativePath = str item "relativePath" }: C2.Translation))
                    |> Seq.toList
                ({ StepId = str row "stepId"; Executable = str row "executable"
                   DeclaredArguments = strings row "declaredArguments"
                   ContainerArguments = strings row "containerArguments"
                   WorkingRole = str row "workingRole"; Translations = translations
                   Environment = row["environment"].AsObject() |> Seq.map (fun p -> p.Key, p.Value.GetValue<string>()) |> Map.ofSeq }: C2.StepDeclaration))
            |> Seq.toList
        { EnrollmentId = str node "enrollmentId"; ProfileId = str node "profileId"
          ProfileSha256 = str node "profileSha256"; ConsumerRepository = str (node["consumer"]) "repository"
          ConsumerSource = str (node["consumer"]) "source"; Inputs = inputRows; RolePaths = roles; Steps = steps
          Window =
            { PhaseStartedUtc = str budget "phaseStartedUtc"; NotAfterUtc = str budget "notAfterUtc"
              MaximumPhaseMs = number budget "maximumPhaseMs"; MaximumWorkMs = number budget "maximumWorkMs"
              CleanupReserveMs = number budget "cleanupReserveMs"; CallerFinishReserveMs = number budget "callerFinishReserveMs" }
          MaximumCapturedBytes = number budget "maximumCapturedBytes"
          MaximumHandoffBytes = number budget "maximumHandoffBytes"
          MaximumHandoffEntries = budget["maximumHandoffEntries"].GetValue<int>() }

    let private selection kind = selectionFromBytes (fixture (kind + "-request")) kind

    [<Fact>]
    let ``review source vectors and public equivalents call the same actual API`` () =
        // Root's separately selected local qualification supplies this pinned
        // private input directory. Ordinary hosted source checks use public data.
        // Absence of the variable is never evidence for private-vector coverage.
        let reviewRoot = Environment.GetEnvironmentVariable "FSGG_C2_REVIEW_VECTOR_ROOT"
        let exercise kind requestBytes resultBytes =
            let request = C2.decodeRequest requestBytes |> get
            Assert.Equal<byte>(requestBytes, C2.encodeRequest request)
            C2.bindDeclared (selectionFromBytes requestBytes kind) request |> get |> ignore
            let result = C2.decodeResult resultBytes |> get
            Assert.Equal<byte>(resultBytes, C2.encodeResult result)
            C2.validateResultJoin request result |> get
        if not (isNull reviewRoot) then
            Assert.True(Path.IsPathFullyQualified reviewRoot)
            for kind in [ "scaffold"; "compiler" ] do
                exercise kind
                    (File.ReadAllBytes(Path.Combine(reviewRoot, kind + "-request.json")))
                    (File.ReadAllBytes(Path.Combine(reviewRoot, kind + "-expected-refusal.json")))
        for kind in [ "scaffold"; "compiler" ] do
            exercise kind (fixture (kind + "-request")) (fixture (kind + "-refused-result"))

    [<Theory>]
    [<InlineData("scaffold")>]
    [<InlineData("compiler")>]
    let ``golden bytes roundtrip through compiled request and result APIs`` kind =
        let raw = fixture (kind + "-request")
        let request = C2.decodeRequest raw |> get
        Assert.Equal<byte>(raw, C2.encodeRequest request)
        let expectedDigest =
            System.Security.Cryptography.SHA256.HashData raw |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        Assert.Equal(expectedDigest, C2.requestSha256 request)
        C2.bindDeclared (selection kind) request |> get |> ignore
        for suffix in [ "refused-result"; "completed-result" ] do
            let resultBytes = fixture (kind + "-" + suffix)
            let document = C2.decodeResult resultBytes |> get
            Assert.Equal<byte>(resultBytes, C2.encodeResult document)
            C2.validateResultJoin request document |> get

    [<Fact>]
    let ``strict decoder rejects duplicate closed malformed and noncanonical bytes`` () =
        let raw = fixture "scaffold-request"
        let mutations =
            [ change "\"maximumPhaseMs\":60000" "\"maximumPhaseMs\":60000,\"maximumPhaseMs\":60000" raw
              change "\"budget\":{" "\"budget\":{\"unknown\":1," raw
              change "\"argumentIndex\":2" "\"argumentIndex\":2,\"argumentIndex\":2" raw
              change "\"HOME\":\"/work/home\"" "\"HOME\":\"/work/home\",\"HOME\":\"/work/home\"" raw
              change "\"maximumWorkMs\":40000" "\"maximumWorkMs\":null" raw
              change "\"maximumWorkMs\":40000" "\"maximumWorkMs\":40000.5" raw
              change "\"maximumWorkMs\":40000" "\"maximumWorkMs\":9223372036854775808" raw
              change "phase-request/1" "phase-request/2" raw
              change "é名" "\\u00e9\\u540d" raw
              Array.append [| 0xffuy |] raw
              Array.append raw [| 10uy |] ]
        for mutation in mutations do C2.decodeRequest (changed raw mutation) |> rejects

    [<Fact>]
    let ``binding preserves the literal raw slot and refuses argv environment and translation drift`` () =
        let raw = fixture "scaffold-request"
        let expected = selection "scaffold"
        Assert.Contains("Awkward é名!? $HOME ;", text raw)
        for before, after in
            [ "Awkward é名!? $HOME ;", "Awkward é名!? expanded ;"
              "\"new\",\"install\"", "\"install\",\"new\""
              "/usr/share/dotnet/dotnet", "/bin/sh"
              "\"DOTNET_MULTILEVEL_LOOKUP\":\"0\"", "\"DOTNET_MULTILEVEL_LOOKUP\":\"1\""
              "\"argumentIndex\":2", "\"argumentIndex\":1"
              "/output/project", "/output/../foreign"
              "62f0683fc6349494f05868abe782ecede2000cac", String.replicate 40 "a"
              "\"profileId\":\"c2-dotnet-scaffold-fixture/1\"", "\"profileId\":\"c2-dotnet-compiler-fixture/1\"" ] do
            match C2.decodeRequest (change before after raw) with
            | Error diagnostics -> Error diagnostics |> rejects
            | Ok request -> C2.bindDeclared expected request |> rejects

    [<Fact>]
    let ``original whole phase window includes all reserves and cannot renew`` () =
        let raw = fixture "scaffold-request"
        let expected = selection "scaffold"
        for before, after in
            [ "\"maximumWorkMs\":40000", "\"maximumWorkMs\":60000"
              "2000-01-01T00:00:00Z", "2000-01-01T00:00:01Z"
              "2000-01-01T00:01:00Z", "2000-01-01T00:02:00Z" ] do
            match C2.decodeRequest (change before after raw) with
            | Error diagnostics -> Error diagnostics |> rejects
            | Ok request -> C2.bindDeclared expected request |> rejects

    [<Fact>]
    let ``completed results require full ordered zero-exit timely sequence and retirement`` () =
        let request = fixture "scaffold-request" |> C2.decodeRequest |> get
        let raw = fixture "scaffold-completed-result"
        for before, after in
            [ "\"code\":0", "\"code\":1"
              "\"code\":0,\"state\":\"known\"", "\"reason\":\"interrupted\",\"state\":\"unknown\""
              "\"milliseconds\":1000", "\"milliseconds\":120000"
              "\"milliseconds\":1000,\"state\":\"known\"", "\"reason\":\"clock-unavailable\",\"state\":\"unknown\""
              "\"stepId\":\"template-create\"", "\"stepId\":\"template-install\""
              "\"helpers\":\"retired\"", "\"helpers\":\"unknown\""
              "\"cleanup\":\"complete\"", "\"cleanup\":\"pending\""
              "\"outputComplete\":true", "\"outputComplete\":false"
              "00000000-0000-4000-8000-000000000001", "00000000-0000-4000-8000-000000000003" ] do
            match C2.decodeResult (change before after raw) with
            | Error diagnostics -> Error diagnostics |> rejects
            | Ok document -> C2.validateResultJoin request document |> rejects

    [<Fact>]
    let ``late and unknown recovery observations are representable without execution permission`` () =
        let request = fixture "scaffold-request" |> C2.decodeRequest |> get
        let raw = fixture "scaffold-refused-result"
        for elapsed in
            [ "\"milliseconds\":120000,\"state\":\"known\""
              "\"reason\":\"clock-unavailable\",\"state\":\"unknown\"" ] do
            let changed = raw |> change "\"disposition\":\"refused\"" "\"disposition\":\"unknown\""
            let changed = change "\"milliseconds\":0,\"state\":\"known\"" elapsed changed
            let document = C2.decodeResult changed |> get
            C2.validateResultJoin request document |> get

    [<Fact>]
    let ``result join rejects install-only reversed and continued-after-unknown prefixes`` () =
        let request = fixture "scaffold-request" |> C2.decodeRequest |> get
        let completed = text (fixture "scaffold-completed-result")
        let install = "{\"duration\":{\"milliseconds\":100,\"state\":\"known\"},\"exit\":{\"code\":0,\"state\":\"known\"},\"stepId\":\"template-install\"}"
        let create = install.Replace("template-install", "template-create")
        let unknownInstall = install.Replace("\"code\":0,\"state\":\"known\"", "\"reason\":\"interrupted\",\"state\":\"unknown\"")
        Assert.Contains(install + "," + create, completed)
        for replacement in [ install; create + "," + install; unknownInstall + "," + create ] do
            let altered = change (install + "," + create) replacement (bytes completed)
            match C2.decodeResult altered with
            | Error diagnostics -> Error diagnostics |> rejects
            | Ok document -> C2.validateResultJoin request document |> rejects
        let changedDigest = change (C2.requestSha256 request) (String.replicate 64 "0") (bytes completed)
        C2.validateResultJoin request (C2.decodeResult changedDigest |> get) |> rejects
        // A non-completed document still may not continue after unknown/nonzero.
        for stopped in [ unknownInstall; install.Replace("\"code\":0", "\"code\":1") ] do
            let failedBytes = change "\"disposition\":\"completed\"" "\"disposition\":\"failed\"" (bytes completed)
            let prefixOnly = change (install + "," + create) stopped failedBytes
            C2.validateResultJoin request (C2.decodeResult prefixOnly |> get) |> get
            let continued = change (install + "," + create) (stopped + "," + create) failedBytes
            match C2.decodeResult continued with
            | Error diagnostics -> Error diagnostics |> rejects
            | Ok document -> C2.validateResultJoin request document |> rejects

    [<Fact>]
    let ``compiler declaration keeps baseline outside exact four-file source and claims no restore readiness`` () =
        let expected = selection "compiler"
        Assert.Equal<string>([| "Fixture.fsproj"; "Library.fs"; "Library.fsi"; "global.json" |],
                             expected.Inputs |> List.filter (fun row -> row.Role = "compiler-source") |> List.map _.RelativePath |> List.toArray)
        Assert.Single(expected.Inputs |> List.filter (fun row -> row.Role = "baseline")) |> ignore
        Assert.Empty(expected.Inputs |> List.filter (fun row -> row.Role = "restore-state"))
        let raw = fixture "compiler-request"
        C2.bindDeclared expected (C2.decodeRequest raw |> get) |> get |> ignore
        let changed = change "-p:OtherFlags=--sig:" "-p:OtherFlags=--exec:" raw
        match C2.decodeRequest changed with
        | Error diagnostics -> Error diagnostics |> rejects
        | Ok request -> C2.bindDeclared expected request |> rejects

    [<Fact>]
    let ``whole document caps refuse valid individual maxima without widening wire limits`` () =
        let oversizedRequest =
            mutateDocument (fixture "scaffold-request") (fun node ->
                for step in node["steps"].AsArray() do
                    for key in [ "declaredArguments"; "containerArguments" ] do
                        let arguments = JsonArray()
                        for _ in 1 .. 64 do arguments.Add(JsonValue.Create(String.replicate 4096 "x"))
                        step[key] <- arguments)
        let oversizedResult =
            mutateDocument (fixture "scaffold-refused-result") (fun node ->
                let stream = Convert.ToBase64String(Array.zeroCreate<byte> (1024 * 1024))
                node["stdoutBase64"] <- JsonValue.Create stream
                node["stderrBase64"] <- JsonValue.Create stream)
        Assert.True(oversizedRequest.Length > 1024 * 1024)
        Assert.True(oversizedResult.Length > 2 * 1024 * 1024)
        let documentLimit outcome =
            match outcome with
            | Ok _ -> failwith "Oversized whole document must refuse"
            | Error diagnostics ->
                Error diagnostics |> rejects
                Assert.Contains("document-byte-limit", diagnostics |> List.map _.Code)
        C2.decodeRequest oversizedRequest |> documentLimit
        C2.decodeResult oversizedResult |> documentLimit

    [<Fact>]
    let ``capture cap counts both decoded streams and permits exact aggregate boundary`` () =
        let request = fixture "scaffold-request" |> C2.decodeRequest |> get
        let resultBytes = fixture "scaffold-refused-result"
        let withStreams count =
            mutateDocument resultBytes (fun node ->
                let stream = Convert.ToBase64String(Array.zeroCreate<byte> count)
                node["stdoutBase64"] <- JsonValue.Create stream
                node["stderrBase64"] <- JsonValue.Create stream)
        let exactBoundary = withStreams (512 * 1024)
        let aggregateOverflow = withStreams (512 * 1024 + 1)
        Assert.True(aggregateOverflow.Length < 2 * 1024 * 1024)
        C2.validateResultJoin request (C2.decodeResult exactBoundary |> get) |> get
        // Each decoded stream fits by itself; their sum exceeds the selected cap.
        match C2.decodeResult aggregateOverflow with
        | Error diagnostics -> Error diagnostics |> rejects
        | Ok document -> C2.validateResultJoin request document |> rejects

    [<Fact>]
    let ``handoff aggregate bytes and entries use request bounds rather than per-row maxima`` () =
        let sourceRequest = fixture "scaffold-request"
        let narrowedRequestBytes =
            sourceRequest
            |> change "\"maximumHandoffBytes\":16777216" "\"maximumHandoffBytes\":8"
            |> change "\"maximumHandoffEntries\":1024" "\"maximumHandoffEntries\":2"
        let narrowedRequest = C2.decodeRequest narrowedRequestBytes |> get
        let resultBytes =
            fixture "scaffold-completed-result"
            |> change (C2.requestSha256 (C2.decodeRequest sourceRequest |> get)) (C2.requestSha256 narrowedRequest)
        let withOutputs sizes =
            mutateDocument resultBytes (fun node ->
                let outputs = JsonArray()
                for index, size in List.indexed sizes do
                    let output = JsonObject()
                    output.Add("bytes", JsonValue.Create(size: int))
                    output.Add("relativePath", JsonValue.Create(sprintf "output-%d.txt" index))
                    output.Add("sha256", JsonValue.Create(String.replicate 64 "a"))
                    outputs.Add output
                node["outputs"] <- outputs)
        C2.validateResultJoin narrowedRequest (C2.decodeResult (withOutputs [ 4; 4 ]) |> get) |> get
        // Individually bounded rows exceed either only total bytes or only count.
        for overflow in [ withOutputs [ 4; 5 ]; withOutputs [ 1; 1; 1 ] ] do
            match C2.decodeResult overflow with
            | Error diagnostics -> Error diagnostics |> rejects
            | Ok document -> C2.validateResultJoin narrowedRequest document |> rejects

    [<Theory>]
    [<InlineData("Zg== ")>]
    [<InlineData(" Zg==")>]
    [<InlineData("Zg==\n")>]
    [<InlineData("Zg")>]
    [<InlineData("Zh==")>]
    let ``result streams require canonical padded Base64 including zero unused bits`` encoded =
        let resultBytes = fixture "scaffold-refused-result"
        for key in [ "stdoutBase64"; "stderrBase64" ] do
            let canonical = mutateDocument resultBytes (fun node -> node[key] <- JsonValue.Create "Zg==")
            C2.decodeResult canonical |> get |> ignore
            let altered = mutateDocument resultBytes (fun node -> node[key] <- JsonValue.Create(encoded: string))
            C2.decodeResult altered |> rejects

    [<Fact>]
    let ``opaque decoded documents do not alias caller byte arrays`` () =
        let raw = fixture "scaffold-request"
        let request = C2.decodeRequest raw |> get
        let original = Array.copy raw
        raw[0] <- 0uy
        Assert.Equal<byte>(original, C2.encodeRequest request)
        let exported = C2.encodeRequest request
        exported[0] <- 0uy
        Assert.Equal<byte>(original, C2.encodeRequest request)
        let resultBytes = fixture "scaffold-refused-result"
        let result = C2.decodeResult resultBytes |> get
        let resultOriginal = Array.copy resultBytes
        resultBytes[0] <- 0uy
        let resultExport = C2.encodeResult result
        resultExport[0] <- 0uy
        Assert.Equal<byte>(resultOriginal, C2.encodeResult result)

    [<Fact>]
    let ``literal supplementary Unicode and line separator survive canonical encoding`` () =
        let raw = fixture "scaffold-request"
        let literal = change "é名" "é😀\u2028名" raw
        Assert.Equal<byte>(literal, C2.encodeRequest (C2.decodeRequest literal |> get))
        let escaped = change "é名" "\\ud800" raw
        C2.decodeRequest escaped |> rejects

    [<Fact>]
    let ``supplied declaration cannot widen fixed command or renew phase selection`` () =
        let request = fixture "scaffold-request" |> C2.decodeRequest |> get
        let expected = selection "scaffold"
        let changedCommand =
            { expected with Steps = expected.Steps |> List.map (fun step -> { step with Executable = "/bin/sh" }) }
        C2.bindDeclared changedCommand request |> rejects
        let renewed = { expected with Window = { expected.Window with NotAfterUtc = "2000-01-01T00:02:00Z" } }
        C2.bindDeclared renewed request |> rejects
        let oversizedSelection =
            { expected with
                Steps =
                    expected.Steps
                    |> List.map (fun step ->
                        { step with DeclaredArguments = [ String.replicate 4097 "x" ] }) }
        C2.bindDeclared oversizedSelection request |> rejects

    [<Theory>]
    [<InlineData("scaffold")>]
    [<InlineData("compiler")>]
    let ``fixed profile cannot omit handoff from both declaration and request`` kind =
        let withoutHandoff =
            mutateDocument (fixture (kind + "-request")) (fun node ->
                let remaining = JsonArray()
                for row in node["ownedRoots"].AsArray() do
                    if row["role"].GetValue<string>() <> "handoff" then remaining.Add(row.DeepClone())
                node["ownedRoots"] <- remaining)
        let request = C2.decodeRequest withoutHandoff |> get
        C2.bindDeclared (selectionFromBytes withoutHandoff kind) request |> rejects

    [<Theory>]
    [<InlineData("scaffold", 25000)>]
    [<InlineData("compiler", 50000)>]
    let ``completed step work cannot exceed work allocation inside controller cutoff`` kind durationMs =
        let request = fixture (kind + "-request") |> C2.decodeRequest |> get
        let documentBytes =
            mutateDocument (fixture (kind + "-completed-result")) (fun node ->
                node["elapsed"].["milliseconds"] <- JsonValue.Create 50000
                for step in node["steps"].AsArray() do
                    step["duration"].["milliseconds"] <- JsonValue.Create(durationMs: int))
        // Known total duration50s is inside55s return cutoff, but outside40s work.
        C2.validateResultJoin request (C2.decodeResult documentBytes |> get) |> rejects

    [<Fact>]
    let ``baseline host role cannot overlap compiler source even when declaration matches`` () =
        let raw = fixture "compiler-request"
        let overlapping = change "/fixture/inputs/compiler-baseline/public-surface.baseline"
                                 "/fixture/inputs/compiler/public-surface.baseline" raw
        C2.bindDeclared (selectionFromBytes overlapping "compiler") (C2.decodeRequest overlapping |> get) |> rejects

    [<Fact>]
    let ``owned root array order remains part of the declaration`` () =
        let raw = fixture "scaffold-request"
        let reordered =
            mutateDocument raw (fun node ->
                let roots = JsonArray()
                for row in node["ownedRoots"].AsArray() |> Seq.rev do roots.Add(row.DeepClone())
                node["ownedRoots"] <- roots)
        C2.bindDeclared (selection "scaffold") (C2.decodeRequest reordered |> get) |> rejects

    [<Theory>]
    [<InlineData(8)>]
    [<InlineData(10)>]
    [<InlineData(12)>]
    let ``empty literal parameter retains its slot and exact binding`` (index: int) =
        let raw = fixture "scaffold-request"
        let emptySlot =
            mutateDocument raw (fun node ->
                let step = (node["steps"].AsArray()).[1]
                for key in [ "declaredArguments"; "containerArguments" ] do
                    (step[key].AsArray()).[index] <- JsonValue.Create "")
        let request = C2.decodeRequest emptySlot |> get
        Assert.Equal<byte>(emptySlot, C2.encodeRequest request)
        let expected = selectionFromBytes emptySlot "scaffold"
        C2.bindDeclared expected request |> get |> ignore
        C2.bindDeclared (selection "scaffold") request |> rejects
        let changedSlot =
            mutateDocument emptySlot (fun node ->
                let step = (node["steps"].AsArray()).[1]
                (step["containerArguments"].AsArray()).[index] <- JsonValue.Create "changed")
        C2.bindDeclared expected (C2.decodeRequest changedSlot |> get) |> rejects
        let removedSlot =
            mutateDocument emptySlot (fun node ->
                for key in [ "declaredArguments"; "containerArguments" ] do
                    let step = (node["steps"].AsArray()).[1]
                    step[key].AsArray().RemoveAt(index))
        C2.bindDeclared expected (C2.decodeRequest removedSlot |> get) |> rejects

    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(7)>]
    [<InlineData(9)>]
    [<InlineData(11)>]
    [<InlineData(13)>]
    [<InlineData(5)>]
    let ``empty template option key or translated path cannot enable a command`` (index: int) =
        let altered =
            mutateDocument (fixture "scaffold-request") (fun node ->
                let step = (node["steps"].AsArray()).[1]
                for key in [ "declaredArguments"; "containerArguments" ] do
                    (step[key].AsArray()).[index] <- JsonValue.Create "")
        let request = C2.decodeRequest altered |> get
        C2.bindDeclared (selectionFromBytes altered "scaffold") request |> rejects

    [<Fact>]
    let ``empty executable and owned role paths remain refused by decoding`` () =
        let raw = fixture "scaffold-request"
        let executable = mutateDocument raw (fun node -> (node["steps"].AsArray()).[1].["executable"] <- JsonValue.Create "")
        C2.decodeRequest executable |> rejects
        let rolePath = mutateDocument raw (fun node -> (node["ownedRoots"].AsArray()).[0].["hostPath"] <- JsonValue.Create "")
        C2.decodeRequest rolePath |> rejects
