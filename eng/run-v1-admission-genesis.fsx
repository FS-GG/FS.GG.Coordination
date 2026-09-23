#r "../src/FS.GG.Coordination.Protocol/bin/Release/net10.0/FS.GG.Coordination.Protocol.dll"
#r "../src/FS.GG.Coordination.Core/bin/Release/net10.0/FS.GG.Coordination.Core.dll"
#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Win32.SafeHandles
open FS.GG.Coordination.GitHub

// This runner is the only executable join of the typed installer and the scoped provider
// bridge. The Main host supplies a short-lived JWT on an inherited descriptor; no PEM is
// accepted here. A native reviewed workflow artifact and a trust-anchored signature are
// independently reread by V1AdmissionGenesisInstaller immediately before mutation.

let operationId = "fleet-v1-admission:fs-gg-production"
let authorityRemote = "https://github.com/FS-GG/FS.GG.Coordination.Authority.git"
let cutoverRef = "refs/heads/fsgg/v2/journal/cutover/d5"
let workflowPath = ".github/workflows/gs2-v1-admission-protected-authorization.yml"
let workflowSha256 = "07435f26a2e22b6bd597aa89ce83192b39c8ab19d7aeabd74c9a67e16be4adf3"
let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let eng = __SOURCE_DIRECTORY__
let utf8 = UTF8Encoding(false, true)

let refuse reason = failwith reason
let unwrap result = result |> Result.defaultWith (String.concat "," >> refuse)
let oid text = V1AdmissionRegistry.gitObjectId text |> Result.defaultWith refuse
let oidText = V1AdmissionRegistry.gitObjectIdValue
let sha256 (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let arguments = fsi.CommandLineArgs |> Array.skip 1
let mode, options =
    if arguments.Length < 1 || arguments.Length % 2 <> 1 then
        refuse "usage: run-v1-admission-genesis.fsx <prepare|signing-payload|apply> --source-commit SHA --trust-anchor PATH [mode options]"
    let pairs = arguments[1..] |> Array.chunkBySize 2 |> Array.map (fun pair -> pair[0], pair[1])
    if pairs |> Array.exists (fun (name, _) -> not (name.StartsWith("--", StringComparison.Ordinal)))
       || pairs |> Array.map fst |> Array.distinct |> Array.length <> pairs.Length then
        refuse "genesis-runner-arguments"
    arguments[0], Map.ofArray pairs
let option name =
    options |> Map.tryFind name |> Option.defaultWith (fun () -> refuse ("genesis-runner-missing-" + name))
let allowed names =
    if options |> Map.toSeq |> Seq.exists (fun (name, _) -> not (List.contains name names)) then
        refuse "genesis-runner-extra-option"
let sourceCommit = option "--source-commit" |> oid
let trustPath = option "--trust-anchor" |> Path.GetFullPath

let runProgram executable arguments timeout =
    let start = ProcessStartInfo(executable)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.WorkingDirectory <- repoRoot
    for argument in arguments do start.ArgumentList.Add argument
    use child = Process.Start start
    if isNull child then refuse "genesis-runner-process-start"
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit timeout) then
        child.Kill(true)
        refuse "genesis-runner-process-timeout"
    let result = stdout.GetAwaiter().GetResult()
    let _ = stderr.GetAwaiter().GetResult() // Do not echo provider bodies or credentials.
    if child.ExitCode <> 0 || result.Length > 2000000 then
        refuse "genesis-runner-native-read-unavailable"
    result

let collect script parameters =
    let path = Path.Combine(Path.GetTempPath(), "fsgg-v1-admission-read-" + Guid.NewGuid().ToString("N"))
    if Directory.Exists path || File.Exists path then refuse "genesis-runner-temp-collision"
    let directory = Directory.CreateDirectory path
    runProgram "chmod" [ "700"; path ] 10000 |> ignore
    try
        let path = Path.Combine(directory.FullName, "evidence.json")
        runProgram "python3" ([ Path.Combine(eng, script); "--output"; path ] @ parameters) 120000
        |> ignore
        let bytes = File.ReadAllBytes path
        if bytes.Length > 65536 then refuse "genesis-runner-evidence-size"
        bytes
    finally
        directory.Delete true

let absent () = collect "github-v1-admission-git-read.py" []
let installed commit =
    collect "github-v1-admission-git-read.py" [ "--expect-installed-commit"; oidText commit ]
let source () =
    collect "github-v1-admission-source-read.py" [ "--commit"; oidText sourceCommit ]
let approval runId =
    collect "github-v1-admission-protected-read.py" [ "--run-id"; string runId ]
let cutoverHead () =
    let result = runProgram "git" [ "ls-remote"; "--refs"; authorityRemote; cutoverRef ] 30000
    let fields = result.TrimEnd('\n').Split('\t')
    if fields.Length <> 2 || fields[1] <> cutoverRef then refuse "genesis-runner-cutover-head"
    oid fields[0]

let initial = absent ()
let evidence = V1AdmissionGenesisGitRead.decode (ReadOnlyMemory initial) |> unwrap
let plan =
    V1AdmissionGenesisGitRead.verifyPlan DateTimeOffset.UtcNow operationId evidence |> unwrap
let sourceRead =
    source () |> ReadOnlyMemory |> V1AdmissionGenesisSourceRead.decode DateTimeOffset.UtcNow |> unwrap
if sourceRead.Commit <> sourceCommit || not sourceRead.IsOnMain then
    refuse "genesis-runner-source-not-merged"

let workflowRevision () =
    let refJson = runProgram "gh" [ "api"; "--method"; "GET"; "repos/FS-GG/.github/git/ref/heads/main" ] 30000
    use refDocument = JsonDocument.Parse refJson
    let current = refDocument.RootElement.GetProperty("object").GetProperty("sha").GetString() |> oid
    let revision =
        match options |> Map.tryFind "--workflow-revision" with
        | Some selected -> oid selected
        | None when mode = "prepare" -> current
        | None -> refuse "genesis-runner-workflow-revision-required"
    if revision <> current then
        let comparison =
            runProgram "gh" [ "api"; "--method"; "GET";
                              $"repos/FS-GG/.github/compare/{oidText revision}...{oidText current}" ] 30000
        use compareDocument = JsonDocument.Parse comparison
        let root = compareDocument.RootElement
        if root.GetProperty("status").GetString() <> "ahead"
           || root.GetProperty("base_commit").GetProperty("sha").GetString() <> oidText revision
           || root.GetProperty("head_commit").GetProperty("sha").GetString() <> oidText current
           || root.GetProperty("merge_base_commit").GetProperty("sha").GetString() <> oidText revision then
            refuse "genesis-runner-workflow-not-on-main"
    let contentJson =
        runProgram "gh" [ "api"; "--method"; "GET";
                          $"repos/FS-GG/.github/contents/{workflowPath}?ref={oidText revision}" ] 30000
    use contentDocument = JsonDocument.Parse contentJson
    let content = contentDocument.RootElement
    if content.GetProperty("encoding").GetString() <> "base64" then
        refuse "genesis-runner-workflow-encoding"
    let bytes = Convert.FromBase64String(content.GetProperty("content").GetString().Replace("\n", ""))
    if sha256 bytes <> workflowSha256 then refuse "genesis-runner-workflow-drift"
    revision

let intent =
    { SourceCommit = sourceCommit
      SourceTree = sourceRead.Tree
      WorkflowRevision = workflowRevision ()
      WorkflowSha256 = V1AdmissionRegistry.sha256Digest workflowSha256 |> Result.defaultWith refuse }
let intentSha256 = V1AdmissionGenesisAuthorization.canonicalIntent plan intent |> sha256
let commit = (V1AdmissionRegistry.genesisObjects plan).CommitObjectId

let writePublic path bytes =
    let full = Path.GetFullPath path
    use output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    output.Write(bytes, 0, bytes.Length)
    output.Flush true
    runProgram "chmod" [ "600"; full ] 10000 |> ignore

let trust () =
    let bytes = File.ReadAllBytes trustPath
    if bytes.Length > 8192 then refuse "genesis-runner-trust-size"
    bytes

if sha256 (trust ())
   <> (V1AdmissionRegistry.genesisTrustDigest plan |> V1AdmissionRegistry.sha256Value) then
    refuse "genesis-runner-trust-digest"

let nativeRun runId =
    approval runId
    |> ReadOnlyMemory
    |> V1AdmissionGenesisProtectedApproval.decodeNativeRead
    |> unwrap

let receiptTimes (native: GenesisProtectedNativeRead) =
    use document = JsonDocument.Parse native.ArtifactBytes
    let root = document.RootElement
    let read (name: string) =
        DateTimeOffset.ParseExact(
            root.GetProperty(name).GetString(), "yyyy-MM-ddTHH:mm:ss'Z'",
            Globalization.CultureInfo.InvariantCulture,
            Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal)
    if root.GetProperty("genesisIntentSha256").GetString() <> intentSha256
       || root.GetProperty("runId").GetInt64() <> native.RunId
       || root.GetProperty("coordinationRevision").GetString() <> oidText sourceCommit
       || root.GetProperty("coordinationTree").GetString() <> oidText sourceRead.Tree
       || root.GetProperty("workflowRevision").GetString() <> oidText intent.WorkflowRevision then
        refuse "genesis-runner-artifact-binding"
    read "approvedAt", read "expiresAt"

if mode = "prepare" then
    allowed [ "--source-commit"; "--trust-anchor" ]
    printfn "{\"schema\":\"fsgg.v1-admission-genesis-preparation/1\",\"operationId\":\"%s\",\"sourceCommit\":\"%s\",\"sourceTree\":\"%s\",\"workflowRevision\":\"%s\",\"genesisIntentSha256\":\"%s\",\"genesisCommit\":\"%s\"}"
        operationId (oidText sourceCommit) (oidText sourceRead.Tree)
        (oidText intent.WorkflowRevision) intentSha256 (oidText commit)
elif mode = "signing-payload" then
    allowed [ "--source-commit"; "--trust-anchor"; "--workflow-revision"; "--run-id"; "--output" ]
    let runId = Int64.Parse(option "--run-id")
    let native = nativeRun runId
    let authorizedAt, expiresAt = receiptTimes native
    let anchor = trust ()
    use trustDocument = JsonDocument.Parse anchor
    let keyId = trustDocument.RootElement.GetProperty("authorizer").GetProperty("keyId").GetString()
    let unsigned: GenesisSignature =
        { KeyId = keyId; PublicKeyPem = ""; ProtectedRunId = runId
          AuthorizedAt = authorizedAt; ExpiresAt = expiresAt; Signature = Array.empty }
    let payload = V1AdmissionGenesisAuthorization.canonicalSignaturePayload plan intent unsigned
    writePublic (option "--output") payload
    printfn "GENESIS_SIGNING_PAYLOAD_PREPARED sha256=%s run=%d" (sha256 payload) runId
elif mode = "apply" then
    allowed [ "--source-commit"; "--trust-anchor"; "--workflow-revision"; "--signature-envelope"; "--app-jwt-fd" ]
    let jwtFd = Int32.Parse(option "--app-jwt-fd")
    if jwtFd < 3 then refuse "genesis-runner-jwt-fd"
    let token =
        use handle = new SafeFileHandle(nativeint jwtFd, false)
        use stream = new FileStream(handle, FileAccess.Read)
        use buffer = new MemoryStream()
        let mutable next = stream.ReadByte()
        while next >= 0 && buffer.Length <= 8192 do
            buffer.WriteByte(byte next)
            next <- stream.ReadByte()
        if buffer.Length = 0L || buffer.Length > 8192L then refuse "genesis-runner-jwt-size"
        let bytes = buffer.ToArray()
        if bytes |> Array.exists (fun value -> value = 0uy || value = 10uy) then
            refuse "genesis-runner-jwt-shape"
        let value = Encoding.ASCII.GetString bytes
        Array.Clear bytes
        value
    let bridgeStart = ProcessStartInfo("python3")
    bridgeStart.ArgumentList.Add(Path.Combine(eng, "github-v1-admission-provider-bridge.py"))
    bridgeStart.UseShellExecute <- false
    bridgeStart.RedirectStandardInput <- true
    bridgeStart.RedirectStandardOutput <- true
    bridgeStart.RedirectStandardError <- true
    bridgeStart.WorkingDirectory <- repoRoot
    use bridge = Process.Start bridgeStart
    if isNull bridge then refuse "genesis-runner-bridge-start"
    bridge.StandardInput.WriteLine token
    bridge.StandardInput.Flush()
    if bridge.StandardOutput.ReadLine() <> "{\"ready\":true}" then
        refuse "genesis-runner-bridge-unavailable"
    let call value =
        bridge.StandardInput.WriteLine(JsonSerializer.Serialize value)
        bridge.StandardInput.Flush()
        let line = bridge.StandardOutput.ReadLine()
        if isNull line || line.Length > 32768 then refuse "genesis-runner-bridge-response"
        use response = JsonDocument.Parse line
        let root = response.RootElement
        if root.TryGetProperty("error") |> fst then
            refuse ("genesis-runner-bridge-" + root.GetProperty("error").GetString())
        root.GetProperty("ok").Clone()
    let objects = V1AdmissionRegistry.genesisObjects plan
    let binding =
        [| {| kind = "blob"; oid = oidText objects.EventObjectId; bytesBase64 = Convert.ToBase64String objects.EventBytes |}
           {| kind = "blob"; oid = oidText objects.HeadObjectId; bytesBase64 = Convert.ToBase64String objects.HeadBytes |}
           {| kind = "tree"; oid = oidText objects.TreeObjectId; bytesBase64 = Convert.ToBase64String objects.TreeBytes |}
           {| kind = "commit"; oid = oidText objects.CommitObjectId; bytesBase64 = Convert.ToBase64String objects.CommitBytes |} |]
    let address = V1AdmissionRegistry.genesisAddress plan
    call {| op = "bind"; ``ref`` = address.Ref; commit = oidText commit; objects = binding |} |> ignore
    let readRef name =
        try
            let value = (call {| op = "read_ref"; ``ref`` = name |}).GetProperty("oid")
            if value.ValueKind = JsonValueKind.Null then GenesisRefAbsent
            else GenesisRefAt(oid (value.GetString()))
        with _ -> GenesisRefUnknown "genesis-runner-ref-unknown"
    let rawNative: GenesisInstallerRawReaders =
        { Now = fun () -> DateTimeOffset.UtcNow
          ReadAbsentGit = fun () -> try Ok(absent ()) with _ -> Error "genesis-runner-absent-read"
          ReadInstalledGit = fun id -> try Ok(installed id) with _ -> Error "genesis-runner-installed-read"
          ReadCutoverHead = fun () -> try Ok(cutoverHead ()) with _ -> Error "genesis-runner-cutover-read"
          ReadRef = readRef
          ReadTrustAnchor = fun () -> try Ok(trust ()) with _ -> Error "genesis-runner-trust-read"
          ReadSource = fun () -> try Ok(source ()) with _ -> Error "genesis-runner-source-read"
          ReadProtection = fun () ->
              try
                  let result = call {| op = "protection" |}
                  Ok(utf8.GetBytes(result.GetProperty("snapshot").GetRawText()))
              with _ -> Error "genesis-runner-protection-read"
          ReadApproval = fun runId -> try Ok(approval runId) with _ -> Error "genesis-runner-approval-read" }
    let objectPort: GenesisInstallerObjectPort =
        { PutObject = fun kind id _ ->
              try
                  let result = call {| op = "put_object"; kind = kind; oid = oidText id |}
                  Ok(oid (result.GetProperty("oid").GetString()))
              with _ -> Error "genesis-runner-object-put"
          ReadObject = fun kind id ->
              try
                  let result = call {| op = "read_object"; kind = kind; oid = oidText id |}
                  Ok(Convert.FromBase64String(result.GetProperty("bytesBase64").GetString()))
              with _ -> Error "genesis-runner-object-read"
          CreateRefExpectedAbsent = fun name id ->
              try
                  call {| op = "create_ref"; ``ref`` = name; commit = oidText id |} |> ignore
                  Ok()
              with _ -> Error "genesis-runner-ref-create-unknown" }
    let signatureBytes = File.ReadAllBytes(option "--signature-envelope")
    let signature =
        V1AdmissionGenesisAuthorization.decodeEnvelope plan intent (ReadOnlyMemory signatureBytes)
        |> unwrap
    let port =
        V1AdmissionGenesisPortBinding.createRaw (ReadOnlyMemory initial) plan rawNative objectPort
        |> unwrap
    match V1AdmissionGenesisInstaller.apply port plan intent signature with
    | GenesisInstalled -> printfn "GENESIS_INSTALLED ref=%s commit=%s" address.Ref (oidText commit)
    | GenesisAlreadyInstalled -> printfn "GENESIS_ALREADY_INSTALLED ref=%s commit=%s" address.Ref (oidText commit)
    | GenesisInstallRefused reasons -> refuse ("GENESIS_INSTALL_REFUSED " + String.concat "," reasons)
    | GenesisInstallIndeterminate reasons -> refuse ("GENESIS_INSTALL_INDETERMINATE " + String.concat "," reasons)
else
    refuse "genesis-runner-mode"
