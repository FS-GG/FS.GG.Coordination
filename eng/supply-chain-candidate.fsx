open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Xml.Linq

let schema = "fsgg.coordination.supply-chain-candidate/1"
let verificationSchema = "fsgg.coordination.supply-chain-verification/1"
let packageId = "FS.GG.Coordination.Protocol"
let channel = "github-packages-candidate"
let githubPackagesSource = "https://nuget.pkg.github.com/FS-GG/index.json"
let packageProject = "src/FS.GG.Coordination.Protocol/FS.GG.Coordination.Protocol.fsproj"
let packageLock = "src/FS.GG.Coordination.Protocol/packages.lock.json"
let versionPattern = Regex("^0\\.0\\.0-gs2-03-7\\.([0-9a-f]{12})$", RegexOptions.CultureInvariant)
let shaPattern = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
let jsonOptions = JsonSerializerOptions(WriteIndented = false)

let fail message = raise (InvalidOperationException message)

let sha256Bytes (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let sha256File path = File.ReadAllBytes path |> sha256Bytes

let writeJson path value =
    let bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions)
    use stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
    stream.Write(bytes)
    stream.WriteByte(byte '\n')

let readJson path = JsonNode.Parse(File.ReadAllBytes path)

let require condition message = if not condition then fail message

let canonicalFullPath path = Path.GetFullPath path

let run workingDirectory executable arguments environment =
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- workingDirectory
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    arguments |> List.iter info.ArgumentList.Add
    environment |> List.iter (fun (name, value) -> info.Environment[name] <- value)
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then
        let renderedArguments = String.concat " " arguments
        fail $"command failed ({child.ExitCode}): {executable} {renderedArguments}\n{output}{error}"
    output.Trim()

let copyDirectory source target =
    Directory.CreateDirectory target |> ignore
    for file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
        let relative = Path.GetRelativePath(source, file)
        let destination = Path.Combine(target, relative)
        Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
        File.Copy(file, destination, true)

let parseArgs (arguments: string array) =
    let rec loop index values =
        if index >= arguments.Length then values
        elif not (arguments[index].StartsWith("--", StringComparison.Ordinal)) then
            fail $"unexpected argument: {arguments[index]}"
        elif index + 1 >= arguments.Length then
            fail $"missing value for {arguments[index]}"
        else
            loop (index + 2) (values |> Map.add arguments[index] arguments[index + 1])
    loop 0 Map.empty

let required name (values: Map<string, string>) =
    values |> Map.tryFind name |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.defaultWith (fun () -> fail $"required option is missing: {name}")

let optional name (values: Map<string, string>) =
    values |> Map.tryFind name |> Option.filter (String.IsNullOrWhiteSpace >> not)

let requireIdentity (candidate: string) (version: string) (selectedChannel: string) =
    require (shaPattern.IsMatch candidate) "candidate must be a lowercase 40-character Git SHA"
    let matched = versionPattern.Match version
    require matched.Success "package version must be the GS2-03.7 prerelease identity"
    require (matched.Groups[1].Value = candidate.Substring(0, 12)) "package version does not bind the candidate SHA"
    require (selectedChannel = channel) "publication channel is not the allowed GitHub Packages candidate channel"

let zipEntries packagePath =
    use archive = ZipFile.OpenRead packagePath
    archive.Entries
    |> Seq.filter (fun entry -> not (entry.FullName.EndsWith("/", StringComparison.Ordinal)))
    |> Seq.map (fun entry ->
        use stream = entry.Open()
        use memory = new MemoryStream()
        stream.CopyTo memory
        let bytes = memory.ToArray()
        {| path = entry.FullName.Replace('\\', '/'); size = bytes.LongLength; sha256 = sha256Bytes bytes |})
    |> Seq.sortBy _.path
    |> Seq.toArray

let canonicalizePackage packagePath =
    let entries =
        use archive = ZipFile.OpenRead packagePath
        archive.Entries
        |> Seq.map (fun entry ->
            use stream = entry.Open()
            use memory = new MemoryStream()
            stream.CopyTo memory
            entry.FullName.Replace('\\', '/'), entry.ExternalAttributes, memory.ToArray())
        |> Seq.sortWith (fun (left, _, _) (right, _, _) -> StringComparer.Ordinal.Compare(left, right))
        |> Seq.toArray
    let canonical = packagePath + ".canonical"
    use file = new FileStream(canonical, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    use archive = new ZipArchive(file, ZipArchiveMode.Create, false, UTF8Encoding(false))
    for name, attributes, bytes in entries do
        let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
        entry.ExternalAttributes <- attributes
        if not (name.EndsWith("/", StringComparison.Ordinal)) then
            use target = entry.Open()
            target.Write bytes
    archive.Dispose()
    file.Dispose()
    File.Move(canonical, packagePath, true)

let occurrenceCount (needle: string) (text: string) =
    text.Split(needle, StringSplitOptions.None).Length - 1

let validateWorkflowText (workflow: string) =
    require (not (String.IsNullOrWhiteSpace workflow)) "candidate workflow is empty or unreadable"
    let normalized = workflow.Replace("\r\n", "\n")
    let lineCount pattern = Regex.Matches(normalized, pattern, RegexOptions.Multiline ||| RegexOptions.CultureInvariant).Count
    require (lineCount "^  workflow_dispatch:$" = 1) "candidate workflow must be manual-only"
    for forbiddenTrigger in [ "push"; "pull_request"; "schedule"; "repository_dispatch" ] do
        require (lineCount $"^  {Regex.Escape forbiddenTrigger}:" = 0) $"candidate workflow enables forbidden trigger: {forbiddenTrigger}"
    let publicationCommands = Regex.Matches(normalized, "\\b(?:dotnet[ \\t\\r\\n]+)?nuget[ \\t\\r\\n]+push\\b", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant).Count
    require (publicationCommands = 1) "candidate workflow must contain exactly one active NuGet publication command"
    let compact = Regex.Replace(normalized, "\\s+", " ").Trim()
    let expectedPublication = "dotnet nuget push \"$CANDIDATE_OUTPUT/FS.GG.Coordination.Protocol.${{ steps.identity.outputs.version }}.nupkg\" --api-key \"${{ secrets.GITHUB_TOKEN }}\" --source https://nuget.pkg.github.com/FS-GG/index.json --skip-duplicate"
    require (occurrenceCount expectedPublication compact = 1) "candidate publication invocation is not exactly bound to the allowed endpoint and arguments"
    require (occurrenceCount "--source" compact = 1) "candidate workflow has an ambiguous publication source argument"
    require (occurrenceCount "https://nuget.pkg.github.com/FS-GG/index.json" normalized = 1) "candidate workflow has an ambiguous publication endpoint"
    require (not (normalized.Contains("nuget.org", StringComparison.OrdinalIgnoreCase))) "candidate workflow references nuget.org"
    require (not (normalized.Contains("continue-on-error", StringComparison.OrdinalIgnoreCase))) "candidate workflow can bypass a failed publication control"
    require (not (Regex.IsMatch(normalized, "^[ ]+if:[ ]*(false|\\$\\{\\{[ ]*false[ ]*\\}\\})[ ]*$", RegexOptions.Multiline ||| RegexOptions.IgnoreCase))) "candidate workflow disables a step"
    require (occurrenceCount "git fetch --no-tags origin +refs/heads/main:refs/remotes/origin/main" normalized = 1) "candidate workflow does not fetch protected-main authority"
    require (occurrenceCount "--protected-ref refs/remotes/origin/main" normalized = 1) "candidate workflow does not bind preparation to protected-main ancestry"
    require (occurrenceCount "packages: write" normalized = 1) "candidate workflow package permission is missing or ambiguous"
    require (not (normalized.Contains("gh release", StringComparison.OrdinalIgnoreCase))) "candidate workflow creates a release"
    require (not (Regex.IsMatch(normalized, "^[ ]+git tag(?:[ ]|$)", RegexOptions.Multiline))) "candidate workflow creates a tag"
    require (not (Regex.IsMatch(normalized, "^[ ]+environment:", RegexOptions.Multiline))) "candidate workflow targets a deployment environment"

let validateWorkflow repo =
    let path = Path.Combine(repo, ".github", "workflows", "candidate-supply-chain.yml")
    require (File.Exists path) "candidate workflow does not exist"
    validateWorkflowText (File.ReadAllText path)

let nuspecDependencies packagePath =
    use archive = ZipFile.OpenRead packagePath
    let nuspecs = archive.Entries |> Seq.filter (fun entry -> entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)) |> Seq.toArray
    require (nuspecs.Length = 1) "candidate package must contain exactly one nuspec"
    use stream = nuspecs[0].Open()
    let document = XDocument.Load stream
    document.Descendants()
    |> Seq.filter (fun element -> element.Name.LocalName = "dependency")
    |> Seq.map (fun element ->
        let attribute name = element.Attributes() |> Seq.find (fun item -> item.Name.LocalName = name) |> _.Value
        {| id = attribute "id"; version = attribute "version" |})
    |> Seq.distinct
    |> Seq.sortBy (fun dependency -> dependency.id, dependency.version)
    |> Seq.toArray

let createPreparedEvidence repo candidate version commitTime packagePath output =
    requireIdentity candidate version channel
    require (File.Exists packagePath) "candidate package does not exist"
    require (File.Exists(Path.Combine(repo, packageLock))) "Protocol lock file does not exist"
    Directory.CreateDirectory output |> ignore
    let expectedName = $"{packageId}.{version}.nupkg"
    require (Path.GetFileName packagePath = expectedName) "candidate package filename does not bind package identity"
    let packageTarget = Path.Combine(output, expectedName)
    if canonicalFullPath packagePath <> canonicalFullPath packageTarget then File.Copy(packagePath, packageTarget, true)
    let packageDigest = sha256File packageTarget
    let entries = zipEntries packageTarget
    let dependencies = nuspecDependencies packageTarget
    let lockDigest = sha256File(Path.Combine(repo, packageLock))
    let spdxFiles =
        entries
        |> Array.mapi (fun index entry ->
            {| SPDXID = $"SPDXRef-File-{index + 1:D4}"; fileName = "./" + entry.path
               checksums = [| {| algorithm = "SHA256"; checksumValue = entry.sha256 |} |] |})
    let relationships =
        [| yield {| spdxElementId = "SPDXRef-DOCUMENT"; relationshipType = "DESCRIBES"; relatedSpdxElement = "SPDXRef-Package" |}
           for file in spdxFiles do
               yield {| spdxElementId = "SPDXRef-Package"; relationshipType = "CONTAINS"; relatedSpdxElement = file.SPDXID |} |]
    let sbom =
        {| spdxVersion = "SPDX-2.3"; dataLicense = "CC0-1.0"; SPDXID = "SPDXRef-DOCUMENT"
           name = $"{packageId}-{version}"; documentNamespace = $"https://github.com/FS-GG/FS.GG.Coordination/sbom/{packageDigest}"
           creationInfo = {| created = commitTime; creators = [| "Tool: FS.GG.Coordination supply-chain-candidate/1" |] |}
           packages =
             [| {| SPDXID = "SPDXRef-Package"; name = packageId; versionInfo = version; downloadLocation = "NOASSERTION"
                   filesAnalyzed = true; checksums = [| {| algorithm = "SHA256"; checksumValue = packageDigest |} |]
                   externalRefs = [| {| referenceCategory = "PACKAGE-MANAGER"; referenceType = "purl"; referenceLocator = $"pkg:nuget/{packageId}@{version}" |} |] |} |]
           files = spdxFiles; relationships = relationships
           fsgg = {| candidate = candidate; packageSize = FileInfo(packageTarget).Length; lockPath = packageLock; lockSha256 = lockDigest; dependencies = dependencies |} |}
    let sbomPath = Path.Combine(output, "sbom.spdx.json")
    writeJson sbomPath sbom
    let sbomDigest = sha256File sbomPath
    let provenance =
        {| ``_type`` = "https://in-toto.io/Statement/v1"
           subject = [| {| name = expectedName; digest = {| sha256 = packageDigest |} |} |]
           predicateType = "https://slsa.dev/provenance/v1"
           predicate =
             {| buildDefinition =
                  {| buildType = "https://github.com/FS-GG/FS.GG.Coordination/supply-chain-candidate/v1"
                     externalParameters = {| repository = "FS-GG/FS.GG.Coordination"; candidate = candidate; packageId = packageId; version = version; channel = channel; packInvocations = 1 |}
                     internalParameters = {| configuration = "Release"; project = packageProject |}
                     resolvedDependencies = [| {| uri = packageLock; digest = {| sha256 = lockDigest |} |}; {| uri = "spdx:sbom.spdx.json"; digest = {| sha256 = sbomDigest |} |} |] |}
                runDetails = {| builder = {| id = "https://github.com/FS-GG/FS.GG.Coordination/.github/workflows/candidate-supply-chain.yml" |}; metadata = {| invocationId = candidate; startedOn = commitTime; finishedOn = commitTime |} |} |} |}
    let provenancePath = Path.Combine(output, "provenance.intoto.json")
    writeJson provenancePath provenance
    let provenanceDigest = sha256File provenancePath
    let manifestWithoutDigest =
        {| schema = schema; repository = "FS-GG/FS.GG.Coordination"; candidate = candidate; commitTime = commitTime
           package = {| id = packageId; version = version; file = expectedName; size = FileInfo(packageTarget).Length; sha256 = packageDigest; packInvocations = 1 |}
           channel = {| id = channel; source = githubPackagesSource; stable = false; production = false |}
           sbom = {| file = "sbom.spdx.json"; schema = "SPDX-2.3"; sha256 = sbomDigest |}
           attestations = [| {| file = "provenance.intoto.json"; predicateType = "https://slsa.dev/provenance/v1"; sha256 = provenanceDigest |} |]
           inputs = [| {| path = packageLock; sha256 = lockDigest |} |]
           stages = [| "identity-bound"; "restored"; "built"; "packed-once"; "package-canonicalized"; "sbom-generated"; "provenance-generated"; "prepared-verified" |] |}
    let canonical = JsonSerializer.SerializeToUtf8Bytes(manifestWithoutDigest, jsonOptions)
    let manifest = {| payload = manifestWithoutDigest; selfSha256 = sha256Bytes canonical |}
    let manifestPath = Path.Combine(output, "candidate.json")
    writeJson manifestPath manifest
    manifestPath

let payloadRoot (manifest: JsonNode) =
    let payload = manifest["payload"]
    require (not (isNull payload)) "manifest payload is missing"
    payload

let stringAt (node: JsonNode) (name: string) =
    let value = node[name]
    require (not (isNull value)) $"manifest field is missing: {name}"
    value.GetValue<string>()

let intAt (node: JsonNode) (name: string) =
    let value = node[name]
    require (not (isNull value)) $"manifest field is missing: {name}"
    value.GetValue<int>()

let verifyPrepared manifestPath =
    let manifest = readJson manifestPath
    let payload = payloadRoot manifest
    require (stringAt payload "schema" = schema) "unsupported candidate manifest schema"
    let candidate = stringAt payload "candidate"
    let package = payload["package"]
    let channelNode = payload["channel"]
    let version = stringAt package "version"
    requireIdentity candidate version (stringAt channelNode "id")
    require (intAt package "packInvocations" = 1) "candidate manifest does not prove exactly one pack invocation"
    require (not (channelNode["stable"].GetValue<bool>())) "candidate channel cannot be stable"
    require (not (channelNode["production"].GetValue<bool>())) "candidate channel cannot be production"
    require (stringAt channelNode "source" = githubPackagesSource) "candidate source is not the allowed endpoint"
    let directory = Path.GetDirectoryName(canonicalFullPath manifestPath)
    let verifyFile (node: JsonNode) =
        let path = Path.Combine(directory, stringAt node "file")
        require (File.Exists path) $"bound artifact does not exist: {path}"
        require (sha256File path = stringAt node "sha256") $"bound artifact digest mismatch: {path}"
        path
    let packagePath = verifyFile package
    require (FileInfo(packagePath).Length = package["size"].GetValue<int64>()) "candidate package length mismatch"
    let sbomPath = verifyFile payload["sbom"]
    let attestations = payload["attestations"].AsArray()
    require (attestations.Count >= 1) "candidate manifest has no attestation"
    for attestation in attestations do verifyFile attestation |> ignore
    let sbom = readJson sbomPath
    require (stringAt sbom "spdxVersion" = "SPDX-2.3") "unsupported SPDX version"
    require (stringAt sbom["fsgg"] "candidate" = candidate) "SBOM candidate does not match"
    let sbomPackage = (sbom["packages"].AsArray())[0]
    let sbomChecksum = (sbomPackage["checksums"].AsArray())[0]
    require (stringAt sbomChecksum "checksumValue" = stringAt package "sha256") "SBOM package digest does not match"
    let provenance = readJson(Path.Combine(directory, stringAt attestations[0] "file"))
    require (stringAt provenance "_type" = "https://in-toto.io/Statement/v1") "unsupported in-toto statement"
    let provenanceSubject = (provenance["subject"].AsArray())[0]
    let provenanceDigest = provenanceSubject["digest"]
    require (stringAt provenanceDigest "sha256" = stringAt package "sha256") "provenance subject does not match"
    let payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions)
    require (stringAt manifest "selfSha256" = sha256Bytes payloadBytes) "candidate manifest self digest mismatch"
    packagePath, candidate, version, stringAt package "sha256", stringAt payload "commitTime"

let cleanGitCandidate repo expected protectedRef =
    let head = run repo "git" [ "rev-parse"; "HEAD" ] []
    require (head = expected) "checked-out HEAD does not match expected candidate"
    let status = run repo "git" [ "status"; "--porcelain" ] []
    require (String.IsNullOrWhiteSpace status) "candidate checkout must be clean before packaging"
    run repo "git" [ "cat-file"; "-e"; expected + "^{commit}" ] [] |> ignore
    match protectedRef with
    | Some reference ->
        run repo "git" [ "rev-parse"; "--verify"; reference + "^{commit}" ] [] |> ignore
        run repo "git" [ "merge-base"; "--is-ancestor"; expected; reference ] [] |> ignore
    | None -> ()
    run repo "git" [ "show"; "-s"; "--format=%cI"; expected ] []

let prepare values =
    let repo = required "--repo" values |> canonicalFullPath
    let candidate = required "--candidate" values
    let version = required "--version" values
    let output = required "--output" values |> canonicalFullPath
    requireIdentity candidate version channel
    validateWorkflow repo
    let commitTime = cleanGitCandidate repo candidate (optional "--protected-ref" values)
    require (not (Directory.Exists output) || Directory.GetFileSystemEntries(output).Length = 0) "output directory must be empty"
    Directory.CreateDirectory output |> ignore
    let buildRoot = Path.Combine(output, "isolated-build")
    let intermediate = Path.Combine(buildRoot, "obj") + string Path.DirectorySeparatorChar
    let binaries = Path.Combine(buildRoot, "bin") + string Path.DirectorySeparatorChar
    Directory.CreateDirectory buildRoot |> ignore
    let deterministicProperties =
        [ "-p:ContinuousIntegrationBuild=true"
          "-p:Deterministic=true"
          "-p:DeterministicSourcePaths=true"
          $"-p:PathMap={repo}=/_/%2C{buildRoot}=/_build/"
          $"-p:BaseIntermediateOutputPath={intermediate}"
          $"-p:BaseOutputPath={binaries}" ]
    run repo "dotnet" ([ "restore"; packageProject; "--locked-mode" ] @ deterministicProperties) [] |> ignore
    run repo "dotnet" ([ "build"; packageProject; "--configuration"; "Release"; "--no-restore"; "--warnaserror" ] @ deterministicProperties) [] |> ignore
    let packOutput = Path.Combine(output, "pack")
    Directory.CreateDirectory packOutput |> ignore
    run repo "dotnet" ([ "pack"; packageProject; "--configuration"; "Release"; "--no-build"; "--no-restore"; "--output"; packOutput; "-p:IsPackable=true"; $"-p:PackageVersion={version}"; $"-p:RepositoryCommit={candidate}"; "-p:RepositoryBranch=main" ] @ deterministicProperties) [] |> ignore
    let packages = Directory.GetFiles(packOutput, "*.nupkg", SearchOption.TopDirectoryOnly)
    require (packages.Length = 1) "exactly one candidate package must be produced"
    canonicalizePackage packages[0]
    let manifest = createPreparedEvidence repo candidate version commitTime packages[0] output
    verifyPrepared manifest |> ignore
    Directory.Delete(packOutput, true)
    Directory.Delete(buildRoot, true)
    printfn "SUPPLY_CHAIN_PREPARED manifest=%s" manifest

let writeConsumerConfig path feed =
    File.WriteAllText(path,
        $"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="{feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="FS.GG.Coordination.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
""", UTF8Encoding(false))

let verifyServed values =
    let repo = required "--repo" values |> canonicalFullPath
    let manifestPath = required "--manifest" values |> canonicalFullPath
    let served = required "--served" values |> canonicalFullPath
    let output = required "--output" values |> canonicalFullPath
    let servedUrl = required "--served-url" values
    let packagePath, candidate, version, expectedDigest, commitTime = verifyPrepared manifestPath
    require (File.Exists served) "served package does not exist"
    require (sha256File served = expectedDigest) "served package digest does not match prepared candidate"
    require (File.ReadAllBytes(packagePath).AsSpan().SequenceEqual(File.ReadAllBytes(served).AsSpan())) "served package is not byte-for-byte identical"
    require (servedUrl.StartsWith("https://nuget.pkg.github.com/fs-gg/download/", StringComparison.Ordinal)) "served URL is outside the allowed channel"
    Directory.CreateDirectory output |> ignore
    let feed = Path.Combine(output, "served-feed")
    Directory.CreateDirectory feed |> ignore
    File.Copy(served, Path.Combine(feed, Path.GetFileName served), true)
    let consumers =
        [| "supply-chain-consumer-a", "FS.GG.Coordination.Protocol:1"
           "supply-chain-consumer-b", "Coordination candidate schema:1" |]
    let results = ResizeArray<_>()
    for fixture, expectedOutput in consumers do
        let consumer = Path.Combine(output, fixture)
        copyDirectory (Path.Combine(repo, "tests", "fixtures", fixture)) consumer
        let config = Path.Combine(consumer, "NuGet.Config")
        writeConsumerConfig config feed
        let packages = Path.Combine(output, "nuget", fixture)
        let dotnetHome = Path.Combine(output, "dotnet", fixture)
        Directory.CreateDirectory packages |> ignore
        Directory.CreateDirectory dotnetHome |> ignore
        let environment = [ "NUGET_PACKAGES", packages; "DOTNET_CLI_HOME", dotnetHome; "NUGET_HTTP_CACHE_PATH", Path.Combine(output, "http", fixture) ]
        let project = Directory.GetFiles(consumer, "*.fsproj", SearchOption.TopDirectoryOnly) |> Array.exactlyOne
        run consumer "dotnet" [ "restore"; project; "--configfile"; config; $"-p:CoordinationCandidateVersion={version}"; "--force-evaluate" ] environment |> ignore
        let cachedPackage = Path.Combine(packages, packageId.ToLowerInvariant(), version, $"{packageId.ToLowerInvariant()}.{version}.nupkg")
        require (File.Exists cachedPackage) "clean consumer cache does not retain the served candidate nupkg"
        require (sha256File cachedPackage = expectedDigest) "clean consumer did not use the served exact package"
        run consumer "dotnet" [ "build"; project; "--configuration"; "Release"; "--no-restore"; "--warnaserror"; $"-p:CoordinationCandidateVersion={version}" ] environment |> ignore
        let actual = run consumer "dotnet" [ "run"; "--project"; project; "--configuration"; "Release"; "--no-build"; "--no-restore"; $"-p:CoordinationCandidateVersion={version}" ] environment
        require (actual = expectedOutput) $"clean consumer output mismatch for {fixture}"
        results.Add {| fixture = fixture; output = actual; packageSha256 = sha256File cachedPackage |}
    let manifestDigest = sha256File manifestPath
    let verification =
        {| ``_type`` = "https://in-toto.io/Statement/v1"
           subject = [| {| name = Path.GetFileName served; digest = {| sha256 = expectedDigest |} |} |]
           predicateType = "https://github.com/FS-GG/FS.GG.Coordination/supply-chain-verification/v1"
           predicate = {| candidate = candidate; version = version; channel = channel; servedUrl = servedUrl; preparedManifestSha256 = manifestDigest; byteForByte = true; consumers = results.ToArray(); verifiedAt = commitTime |} |}
    let attestationPath = Path.Combine(output, "verification.intoto.json")
    writeJson attestationPath verification
    let receipt =
        {| schema = verificationSchema; candidate = candidate; version = version; channel = channel; servedUrl = servedUrl
           packageSha256 = expectedDigest; preparedManifestSha256 = manifestDigest; verificationAttestationSha256 = sha256File attestationPath
           comparisons = [| "length"; "sha256"; "byte-for-byte" |]; consumers = results.ToArray(); status = "verified" |}
    let receiptPath = Path.Combine(output, "verification.json")
    writeJson receiptPath receipt
    printfn "SUPPLY_CHAIN_SERVED_VERIFIED receipt=%s" receiptPath

let createFakePackage (path: string) (version: string) =
    use file = new FileStream(path, FileMode.Create, FileAccess.Write)
    use archive = new ZipArchive(file, ZipArchiveMode.Create)
    let add (name: string) (content: string) =
        let entry = archive.CreateEntry(name, CompressionLevel.NoCompression)
        entry.LastWriteTime <- DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
        use writer = new StreamWriter(entry.Open(), UTF8Encoding(false))
        writer.Write content
    add $"{packageId}.nuspec" $"<package><metadata><id>{packageId}</id><version>{version}</version><dependencies><group targetFramework=\"net10.0\"><dependency id=\"FSharp.Core\" version=\"[10.1.302, )\" /></group></dependencies></metadata></package>"
    add "lib/net10.0/FS.GG.Coordination.Protocol.dll" "deterministic-fixture"

let expectRefusal (action: unit -> unit) =
    try action (); false with :? InvalidOperationException -> true

let selfTest values =
    let repo = required "--repo" values |> canonicalFullPath
    validateWorkflow repo
    let scratch = Path.Combine(Path.GetTempPath(), "fsgg-supply-chain-selftest-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory scratch |> ignore
    try
        let candidate = String.replicate 40 "a"
        let version = "0.0.0-gs2-03-7." + candidate.Substring(0, 12)
        let package = Path.Combine(scratch, $"{packageId}.{version}.nupkg")
        createFakePackage package version
        let secondPackage = Path.Combine(scratch, $"second-{packageId}.{version}.nupkg")
        createFakePackage secondPackage version
        use secondArchive = ZipFile.Open(secondPackage, ZipArchiveMode.Update)
        for entry in secondArchive.Entries do
            entry.LastWriteTime <- DateTimeOffset(2025, 5, 6, 7, 8, 10, TimeSpan.Zero)
        secondArchive.Dispose()
        canonicalizePackage package
        canonicalizePackage secondPackage
        require (File.ReadAllBytes(package).AsSpan().SequenceEqual(File.ReadAllBytes(secondPackage).AsSpan())) "canonical package bytes differ across ZIP metadata"
        let output = Path.Combine(scratch, "prepared")
        let manifest = createPreparedEvidence repo candidate version "2026-08-30T00:00:00Z" package output
        verifyPrepared manifest |> ignore
        let negative = ResizeArray<string>()
        let mutatedPackage = Path.Combine(output, Path.GetFileName package)
        File.AppendAllText(mutatedPackage, "tamper")
        if expectRefusal (fun () -> verifyPrepared manifest |> ignore) then negative.Add "package-tamper"
        File.Copy(package, mutatedPackage, true)
        let sbomPath = Path.Combine(output, "sbom.spdx.json")
        File.AppendAllText(sbomPath, "tamper")
        if expectRefusal (fun () -> verifyPrepared manifest |> ignore) then negative.Add "sbom-tamper"
        createPreparedEvidence repo candidate version "2026-08-30T00:00:00Z" package output |> ignore
        if expectRefusal (fun () -> requireIdentity candidate version "nuget-org") then negative.Add "channel-substitution"
        if expectRefusal (fun () -> requireIdentity candidate "1.0.0" channel) then negative.Add "stable-version"
        let node = readJson manifest
        let payloadNode = node["payload"]
        let packageNode = payloadNode["package"]
        packageNode["packInvocations"] <- JsonValue.Create(2)
        writeJson manifest node
        if expectRefusal (fun () -> verifyPrepared manifest |> ignore) then negative.Add "repack-count"
        let workflow = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "candidate-supply-chain.yml"))
        if expectRefusal (fun () -> validateWorkflowText (workflow + "\n      - run: dotnet nuget push candidate.nupkg --source https://api.nuget.org/v3/index.json\n")) then negative.Add "workflow-channel-substitution"
        if expectRefusal (fun () -> validateWorkflowText (workflow.Replace("      - name: Publish only", "      - continue-on-error: true\n      - name: Publish only"))) then negative.Add "workflow-bypass"
        if expectRefusal (fun () -> validateWorkflowText "") then negative.Add "workflow-unreadable"
        if expectRefusal (fun () -> validateWorkflowText (workflow.Replace("          --protected-ref refs/remotes/origin/main\n", ""))) then negative.Add "workflow-unprotected"
        if expectRefusal (fun () -> validateWorkflowText (workflow + "\n      - run: dotnet nuget push candidate.nupkg --source \"$UNTRUSTED_SOURCE\"\n")) then negative.Add "workflow-dynamic-source"
        let detachedSource = workflow.Replace("--source https://nuget.pkg.github.com/FS-GG/index.json", "--source \"$UNTRUSTED_SOURCE\"") + "\nenv:\n  PUBLISH_POLICY_NOTE: https://nuget.pkg.github.com/FS-GG/index.json\n"
        if expectRefusal (fun () -> validateWorkflowText detachedSource) then negative.Add "workflow-detached-source"
        require (negative.Count = 11) "self-test did not exercise every negative control"
        printfn "SUPPLY_CHAIN_SELFTEST_OK positive=1 negative=%d cases=%s" negative.Count (String.concat "," negative)
    finally
        if Directory.Exists scratch then Directory.Delete(scratch, true)

let usage () =
    fail "usage: supply-chain-candidate.fsx <prepare|verify|verify-served|selftest> --name value ..."

try
    let arguments = fsi.CommandLineArgs |> Array.skip 1
    require (arguments.Length > 0) "command is required"
    let command = arguments[0]
    let values = arguments |> Array.skip 1 |> parseArgs
    match command with
    | "prepare" -> prepare values
    | "verify" ->
        let manifest = required "--manifest" values
        verifyPrepared manifest |> ignore
        printfn "SUPPLY_CHAIN_VERIFIED manifest=%s" (canonicalFullPath manifest)
    | "verify-served" -> verifyServed values
    | "selftest" -> selfTest values
    | _ -> usage ()
with error ->
    eprintfn "SUPPLY_CHAIN_REFUSED %s" error.Message
    exit 3
