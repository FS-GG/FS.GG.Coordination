open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Diagnostics
open System.Security.Cryptography

let assertTrue value message = if not value then failwith message
let sha (bytes: byte[]) = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
let helper = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../../eng/portable-p4-input-manifest.fsx"))
let target = String.replicate 40 "a"
let source = "b06c18722b422213fe1c7cdd11cda1732605466f"
let tree = "51df48a04b6cb7971233723945e6c743e3a65147"
let roles = ["provider-input-join";"candidate-receipt";"profile";"receiver-archive";"coordination-package";"sdd-package";"templates-package";"descriptor";"runtime-archive";"image-archive";"image-receipt"]
let root = Path.Combine(Path.GetTempPath(), "p4-manifest-v2-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory(root) |> ignore
let assetsRoot = Path.Combine(root,"assets")
Directory.CreateDirectory(assetsRoot) |> ignore
let run command input output extra =
    let psi = ProcessStartInfo("dotnet")
    psi.ArgumentList.Add("fsi"); psi.ArgumentList.Add("--exec"); psi.ArgumentList.Add(helper); psi.ArgumentList.Add(command)
    for value in ["--input";input;"--output";output;"--expected-release-id";"55";"--expected-target";target;"--expected-helper";source;"--expected-helper-tree";tree;"--assets-root";assetsRoot] @ extra do psi.ArgumentList.Add(value)
    psi.RedirectStandardError <- true; psi.RedirectStandardOutput <- true; psi.UseShellExecute <- false
    use child = Process.Start(psi)
    assertTrue (child.WaitForExit(60000)) "helper timeout"
    let error = child.StandardError.ReadToEnd()
    child.ExitCode,error
let obj (pairs: (string * JsonNode) list) =
    let value = JsonObject()
    for key,item in pairs do value[key] <- item
    value
let arr (values: JsonNode seq) =
    let result = JsonArray()
    values |> Seq.iter result.Add
    result
let producers = arr [obj ["repository",JsonValue.Create("FS-GG/FS.GG.Coordination");"runId",JsonValue.Create(1);"runAttempt",JsonValue.Create(1);"workflowPath",JsonValue.Create(".github/workflows/provider.yml");"headSha",JsonValue.Create(String.replicate 40 "1");"artifactId",JsonValue.Create(2);"archiveSha256",JsonValue.Create(String.replicate 64 "2")]]
let roleNodes = ResizeArray<JsonNode>()
let roleReceipts = JsonObject()
let assetNodes = ResizeArray<JsonNode>()
for index,role in roles |> List.indexed do
    let id = 100 + index
    let path = "candidate/" + role + ".bin"
    let bytes = Text.Encoding.UTF8.GetBytes(role + "\n")
    let full = Path.Combine(assetsRoot,path)
    Directory.CreateDirectory(Path.GetDirectoryName(full)) |> ignore
    File.WriteAllBytes(full,bytes)
    let digest=sha bytes
    roleNodes.Add(obj ["path",JsonValue.Create(path);"role",JsonValue.Create(role);"assetId",JsonValue.Create(id);"name",JsonValue.Create(Path.GetFileName(path));"bytes",JsonValue.Create(bytes.Length);"sha256",JsonValue.Create(digest);"producer",JsonValue.Create(0)])
    roleReceipts[role] <- obj ["path",JsonValue.Create(full);"bytes",JsonValue.Create(bytes.Length);"sha256",JsonValue.Create(digest)]
    assetNodes.Add(obj ["id",JsonValue.Create(id);"name",JsonValue.Create(Path.GetFileName(path));"bytes",JsonValue.Create(bytes.Length);"sha256",JsonValue.Create(digest);"state",JsonValue.Create("uploaded")])
let manifest = obj [
    "schema",JsonValue.Create("fsgg.portable-p4-private-inputs/2"); "classification",JsonValue.Create("public-candidate-files-only")
    "release",obj ["repository",JsonValue.Create("FS-GG/FS.GG.GitHub.Substrate.Sandbox");"releaseId",JsonValue.Create(55);"tag",JsonValue.Create("portable-p4-python-private-inputs-20261001");"targetCommit",JsonValue.Create(target)]
    "coordinationSource",obj ["repository",JsonValue.Create("FS-GG/FS.GG.Coordination");"commit",JsonValue.Create(source);"tree",JsonValue.Create(tree)]
    "producers",producers; "files",arr roleNodes; "privateProviderFactsRef",null]
let sourcePath=Path.Combine(root,"source.json")
let canonicalPath=Path.Combine(root,"canonical.json")
File.WriteAllText(sourcePath,manifest.ToJsonString())
let constructCode,constructError=run "construct" sourcePath canonicalPath []
assertTrue (constructCode=0) constructError
let body=File.ReadAllBytes(canonicalPath)
let manifestId=111
assetNodes.Add(obj ["id",JsonValue.Create(manifestId);"name",JsonValue.Create("public-inputs-manifest.json");"bytes",JsonValue.Create(body.Length);"sha256",JsonValue.Create(sha body);"state",JsonValue.Create("uploaded")])
let canonicalManifest=JsonNode.Parse(body)
let transport=obj ["schema",JsonValue.Create("fsgg.portable-p4-manifest-transport/2");"releaseId",JsonValue.Create(55);"manifestAssetId",JsonValue.Create(manifestId);"manifestSha256",JsonValue.Create(sha body);"manifest",canonicalManifest;"assets",arr assetNodes;"roles",roleReceipts]
let transportPath=Path.Combine(root,"transport.json")
let acquiredPath=Path.Combine(root,"acquired.json")
File.WriteAllText(transportPath,transport.ToJsonString())
let transportCode,transportError=run "transport" transportPath acquiredPath ["--expected-manifest-asset-id";string manifestId;"--expected-manifest-sha256";sha body]
assertTrue (transportCode=0) transportError
let acquired=JsonNode.Parse(File.ReadAllBytes(acquiredPath)).AsObject()
assertTrue (acquired["manifestAssetId"].GetValue<int>()=manifestId) "external ID missing"
assertTrue (acquired["manifestSha256"].GetValue<string>()=sha body) "external SHA missing"
let acquiredManifest = acquired["manifest"].AsObject()
let acquiredRelease = acquiredManifest["release"].AsObject()
assertTrue (not(acquiredRelease.ContainsKey("manifestAssetId"))) "self ID embedded"
let hybrid=JsonNode.Parse(body).AsObject()
hybrid["release"].AsObject()["manifestAssetId"] <- JsonValue.Create(manifestId)
let hybridPath=Path.Combine(root,"hybrid.json")
File.WriteAllText(hybridPath,hybrid.ToJsonString())
let hybridCode,_=run "validate-manifest" hybridPath (Path.Combine(root,"hybrid-output.json")) []
assertTrue (hybridCode<>0) "hybrid v1/v2 accepted"
let acquiredCode,acquiredError=run "validate-acquired" acquiredPath (Path.Combine(root,"validated.json")) ["--expected-manifest-asset-id";string manifestId;"--expected-manifest-sha256";sha body]
assertTrue (acquiredCode=0) acquiredError
printfn "portable-p4 manifest v2: transport, acquired envelope, and hybrid refusal passed"
Directory.Delete(root,true)
