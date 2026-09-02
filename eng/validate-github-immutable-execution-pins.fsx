#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubImmutableExecutionPinsQualification.fs"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts

let defaultRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let root =
    fsi.CommandLineArgs
    |> Array.skip 1
    |> Array.filter (fun value -> value <> "--")
    |> Array.tryFind Directory.Exists
    |> Option.map Path.GetFullPath
    |> Option.defaultValue defaultRoot
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-4")
let corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "corpus.json"))).AsObject()
let expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "independent-expectations.json"))).AsObject()
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let texts (node: JsonObject) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
let sha256Bytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let sha256File path = File.ReadAllBytes(path) |> sha256Bytes
let optionalText (node: JsonObject) (name: string) = if isNull node[name] then None else Some(text node name)
let referenceKind = function "action" -> ThirdPartyAction | "workflow" -> ReusableWorkflow | value -> failwith $"unsupported reference kind {value}"

let workflow (node: JsonNode) =
    let value = node.AsObject()
    let path = text value "path"
    { Path = path; Sha256 = text value "sha256"
      References = value["references"].AsArray() |> Seq.map (fun item ->
          let reference = item.AsObject()
          { WorkflowPath = path; TargetRepository = text reference "repository"
            TargetPath = optionalText reference "path"; Revision = text reference "revision"
            Kind = referenceKind (text reference "kind") }) |> List.ofSeq }

let publication (node: JsonNode) =
    let value = node.AsObject()
    { Repository = text value "repository"; Path = text value "path"; Revision = text value "revision"
      ContentSha256 = text value "contentSha256"; WorkflowCall = value["workflowCall"].GetValue<bool>() }

let updater (node: JsonNode) =
    let value = node.AsObject()
    { Name = text value "name"; Automated = value["automated"].GetValue<bool>()
      PullRequestOnly = value["pullRequestOnly"].GetValue<bool>(); DirectPush = value["directPush"].GetValue<bool>()
      PolicyRepository = text value "policyRepository"; PolicyRevision = text value "policyRevision"
      PolicyPath = text value "policyPath"; PolicySha256 = text value "policySha256"
      OwnedManagers = texts value "ownedManagers" }

let updaterConfiguration (node: JsonNode) =
    let value = node.AsObject()
    { Path = text value "path"; Sha256 = text value "sha256"; Authority = text value "authority"
      PullRequestOnly = value["pullRequestOnly"].GetValue<bool>(); DirectPush = value["directPush"].GetValue<bool>() }

let snapshot =
    { SchemaVersion = corpus["schemaVersion"].GetValue<int>(); Repository = text corpus "repository"
      SourceRevision = text corpus "sourceRevision"; PrerequisiteReceiptDigest = text corpus "prerequisiteReceiptDigest"
      Complete = corpus["complete"].GetValue<bool>()
      Workflows = corpus["workflows"].AsArray() |> Seq.map workflow |> List.ofSeq
      Publications = corpus["publications"].AsArray() |> Seq.map publication |> List.ofSeq
      RequiredUpdaterConfigurationPaths = texts corpus "updaterConfigurationPaths"
      RequiredUpdaterInvocationSelectors = texts corpus "updaterInvocationSelectors"
      UpdaterConfigurations = corpus["updaterConfigurations"].AsArray() |> Seq.map updaterConfiguration |> List.ofSeq
      Updaters = corpus["updaters"].AsArray() |> Seq.map updater |> List.ofSeq
      RequiredManagers = texts corpus "requiredManagers" }

let compile candidate = GitHubImmutableExecutionPinsQualification.compile candidate
let refused candidate = compile candidate |> Result.isError
let report = compile snapshot |> Result.defaultWith (failwithf "immutable execution pins baseline refused: %A")

if fsi.CommandLineArgs |> Array.contains "--mint" then
    printfn "%s" report.Seal
else
    let workflowDirectory = Path.Combine(root, ".github/workflows")
    let trackedWorkflows =
        Directory.EnumerateFiles(workflowDirectory)
        |> Seq.filter (fun path -> let extension = Path.GetExtension(path) in extension = ".yml" || extension = ".yaml")
        |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
        |> Set.ofSeq
    let declaredWorkflows = snapshot.Workflows |> List.map _.Path |> Set.ofList
    if trackedWorkflows <> declaredWorkflows then failwith $"workflow inventory is incomplete: tracked={trackedWorkflows.Count} declared={declaredWorkflows.Count}"
    for workflow in snapshot.Workflows do
        let path = Path.Combine(root, workflow.Path)
        if sha256File path <> workflow.Sha256 then failwith $"workflow digest differs: {workflow.Path}"

    let uses = Regex("(?m)^\\s*uses:\\s*([^\\s#]+)", RegexOptions.CultureInvariant)
    let observedReferences =
        [ for workflow in snapshot.Workflows do
              for matched in uses.Matches(File.ReadAllText(Path.Combine(root, workflow.Path))) do
                  let target = matched.Groups[1].Value
                  match GitHubImmutableExecutionPinsQualification.classifyReferenceLiteral target with
                  | Ok(kind, repository, path, revision) ->
                      yield workflow.Path, kind, repository, path, revision
                  | Error errors -> failwith $"execution reference is not immutable: {target}: {errors}" ]
        |> Set.ofSeq
    let declaredReferences =
        snapshot.Workflows
        |> List.collect (fun workflow -> workflow.References |> List.map (fun reference -> workflow.Path, reference.Kind, reference.TargetRepository, reference.TargetPath, reference.Revision))
        |> Set.ofList
    if observedReferences <> declaredReferences then failwith $"execution reference inventory differs: observed={observedReferences.Count} declared={declaredReferences.Count}"

    let trackedPaths =
        let startInfo = ProcessStartInfo("git")
        for argument in [ "-C"; root; "ls-files"; "-z" ] do startInfo.ArgumentList.Add argument
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        if child.ExitCode <> 0 then failwith $"tracked updater inventory failed: {error}"
        output.Split('\u0000', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
    let officialRenovatePaths =
        [ "renovate.json"; "renovate.jsonc"; "renovate.json5"
          ".github/renovate.json"; ".github/renovate.jsonc"; ".github/renovate.json5"
          ".gitlab/renovate.json"; ".gitlab/renovate.jsonc"; ".gitlab/renovate.json5"
          ".renovaterc"; ".renovaterc.json"; ".renovaterc.jsonc"; ".renovaterc.json5" ]
        |> Set.ofList
    let dependabotPaths = Set.ofList [ ".github/dependabot.yml"; ".github/dependabot.yaml" ]
    let packageRenovate = Regex("(?s)\\\"renovate\\\"\\s*:", RegexOptions.CultureInvariant)
    let configuredPaths = snapshot.RequiredUpdaterConfigurationPaths |> Set.ofList
    let invocationSelectors = snapshot.RequiredUpdaterInvocationSelectors
    if configuredPaths <> Set.union officialRenovatePaths dependabotPaths
       || officialRenovatePaths <> (texts expectations "officialRenovatePaths" |> Set.ofList)
       || invocationSelectors <> texts expectations "invocationSelectors" then
        failwith "independent updater discovery contract differs"
    let branchAutomerge =
        Regex("(?i)(automergeType[\\\"']?\\s*[:=]\\s*[\\\"']?(?:branch|direct)|RENOVATE_AUTOMERGE_TYPE\\s*[:=]\\s*[\\\"']?(?:branch|direct)|--automerge-type(?:=|\\s+)(?:branch|direct))", RegexOptions.CultureInvariant)
    let renovateEnvironment =
        Regex("(?i)\\bRENOVATE_[A-Z0-9_]+\\b", RegexOptions.CultureInvariant)
    let renovateCommand =
        Regex("(?im)(?:^|[;&|`(\\s])(?:npx\\s+)?renovate(?:\\s|$)|renovate/renovate", RegexOptions.CultureInvariant)
    let invocationSurface (path: string) =
        path.StartsWith(".github/workflows/", StringComparison.Ordinal)
        || path.StartsWith("scripts/", StringComparison.Ordinal)
        || ([ ".sh"; ".bash"; ".zsh"; ".ps1"; ".cmd"; ".bat" ]
            |> List.exists (fun extension -> path.EndsWith(extension, StringComparison.Ordinal)))
    let configurationTuple (path: string) (authority: string) =
        let fullPath = Path.Combine(root, path)
        let content = File.ReadAllText fullPath
        let directPush = branchAutomerge.IsMatch content
        path, sha256File fullPath, authority, not directPush, directPush
    let hasRenovateInvocation (content: string) =
        renovateEnvironment.IsMatch content
        || renovateCommand.IsMatch content
        || invocationSelectors |> List.exists (fun selector ->
            selector <> "RENOVATE_*"
            && selector <> "renovate-command"
            && selector <> "renovate-container"
            && content.Contains(selector, StringComparison.OrdinalIgnoreCase))
    let observedUpdaterConfigurations =
        trackedPaths
        |> Seq.choose (fun path ->
            let fullPath = Path.Combine(root, path)
            if configuredPaths.Contains path && dependabotPaths.Contains path then Some(configurationTuple path "dependabot")
            elif configuredPaths.Contains path then Some(configurationTuple path "renovate")
            elif path = "package.json" && packageRenovate.IsMatch(File.ReadAllText fullPath) then Some(configurationTuple path "renovate")
            elif invocationSurface path && (File.ReadAllText fullPath |> hasRenovateInvocation) then
                Some(configurationTuple path "renovate-local-invocation")
            else None)
        |> Set.ofSeq
    let declaredUpdaterConfigurations =
        snapshot.UpdaterConfigurations
        |> List.map (fun configuration -> configuration.Path, configuration.Sha256, configuration.Authority, configuration.PullRequestOnly, configuration.DirectPush)
        |> Set.ofList
    if observedUpdaterConfigurations <> declaredUpdaterConfigurations then
        failwith $"updater configuration inventory differs: observed={observedUpdaterConfigurations.Count} declared={declaredUpdaterConfigurations.Count}"

    let receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-06.3.json"))).AsObject()
    if text receipt "digest" <> snapshot.PrerequisiteReceiptDigest then failwith "accepted GS2-06.3 receipt differs"
    if text corpus "roadmapRevision" <> "7ab43852609563265291eec2b4010a829582d447" || text corpus "roadmapSha256" <> "9c8c87581bc0e7d1e9aac6d2691fdbf5f4e3db531c45879b1acc5b37669f0112" then failwith "accepted roadmap binding differs"
    let acceptedUpdater = snapshot.Updaters |> List.exactlyOne
    if acceptedUpdater.PolicyRepository <> "FS-GG/.github"
       || acceptedUpdater.PolicyRevision <> "7ab43852609563265291eec2b4010a829582d447"
       || acceptedUpdater.PolicyPath <> "renovate.json"
       || acceptedUpdater.PolicySha256 <> "fb9c4ec557a849a553881dbc9ac75ef6f1e98d7f6a43efeefeca67d4f9ec36fb"
       || not acceptedUpdater.PullRequestOnly || acceptedUpdater.DirectPush then
        failwith "accepted Renovate policy identity, content, or PR-only semantics differ"
    if report.Repository <> text expectations "repository" || report.WorkflowCount <> expectations["workflowCount"].GetValue<int>() || report.ReferenceCount <> expectations["referenceCount"].GetValue<int>() then failwith "baseline inventory differs"
    if observedReferences |> Seq.map (fun (_, kind, repository, _, revision) -> kind, repository, revision) |> Set.ofSeq |> Set.count <> expectations["distinctActionCount"].GetValue<int>() then failwith "distinct action inventory differs"
    if report.PublicationCount <> expectations["publicationCount"].GetValue<int>() || report.UpdaterConfigurationCount <> expectations["updaterConfigurationCount"].GetValue<int>() || report.AutomatedUpdater <> text expectations "automatedUpdater" || report.Managers <> texts expectations "managers" then failwith "publication or updater result differs"
    if report.Seal <> text expectations "expectedSeal" then failwith "baseline seal differs"
    if GitHubImmutableExecutionPinsQualification.verify report.Seal snapshot <> Ok report then failwith "exact replay failed"
    let expectedControls = GitHubImmutableExecutionPinsQualification.requiredControls |> List.map GitHubImmutableExecutionPinsQualification.controlId
    if texts expectations "controls" <> expectedControls then failwith "independent control inventory differs"

    let sha = String.replicate 40 "a"
    let digest = String.replicate 64 "b"
    let reusable =
        { WorkflowPath = snapshot.Workflows.Head.Path; TargetRepository = "FS-GG/.github"
          TargetPath = Some ".github/workflows/reusable.yml"; Revision = sha; Kind = ReusableWorkflow }
    let published =
        { Repository = "FS-GG/.github"; Path = ".github/workflows/reusable.yml"; Revision = sha
          ContentSha256 = digest; WorkflowCall = true }
    let dependabotConfiguration =
        { Path = ".github/dependabot.yml"; Sha256 = digest; Authority = "dependabot"
          PullRequestOnly = true; DirectPush = false }
    let unsafeRenovateConfiguration =
        { Path = "renovate.jsonc"; Sha256 = digest; Authority = "renovate"
          PullRequestOnly = false; DirectPush = true }
    let generatedMutation = function
        | ImmutablePinsPrerequisite -> refused { snapshot with PrerequisiteReceiptDigest = "invalid" }
        | ImmutablePinsCompleteness -> refused { snapshot with Complete = false }
        | ImmutablePinsSourceBinding -> refused { snapshot with SourceRevision = "main" }
        | ThirdPartyActionPins ->
            let head = snapshot.Workflows.Head
            let changed = { head.References.Head with Revision = "v4" }
            refused { snapshot with Workflows = { head with References = changed :: head.References.Tail } :: snapshot.Workflows.Tail }
        | ReusableWorkflowPins ->
            let head = snapshot.Workflows.Head
            let changed = { reusable with Revision = "main" }
            refused { snapshot with Workflows = { head with References = changed :: head.References } :: snapshot.Workflows.Tail; Publications = [ published ] }
        | LocalExecutionReferenceRejection ->
            match GitHubImmutableExecutionPinsQualification.classifyReferenceLiteral "./.github/workflows/reusable.yml" with
            | Error [ LocalExecutionReferenceNotImmutable ] -> true
            | _ -> false
        | WorkflowDigestBinding -> refused { snapshot with Workflows = { snapshot.Workflows.Head with Sha256 = "changed" } :: snapshot.Workflows.Tail }
        | PublicationIdentity -> refused { snapshot with Publications = [ { published with Repository = "invalid" } ] }
        | PublicationContent -> refused { snapshot with Publications = [ { published with ContentSha256 = "changed" } ] }
        | PublicationWorkflowCall -> refused { snapshot with Publications = [ { published with WorkflowCall = false } ] }
        | StablePinOrdering -> compile { snapshot with Workflows = List.rev snapshot.Workflows } = Ok report
        | UpdaterConfigurationInventory ->
            refused { snapshot with UpdaterConfigurations = [ dependabotConfiguration; unsafeRenovateConfiguration ] }
        | RenovateSoleUpdater -> refused { snapshot with Updaters = { snapshot.Updaters.Head with Name = "dependabot" } :: snapshot.Updaters }
        | RenovatePullRequestOnly -> refused { snapshot with Updaters = [ { snapshot.Updaters.Head with PullRequestOnly = false; DirectPush = true } ] }
        | RenovateOwnership -> refused { snapshot with RequiredManagers = [ "github-actions"; "regex" ] }
        | ExactPinsSeal -> GitHubImmutableExecutionPinsQualification.verify (String.replicate 64 "0") snapshot |> Result.isError
        | ExactPinsReplay -> GitHubImmutableExecutionPinsQualification.verify report.Seal snapshot = Ok report
        | QuintPinsUnchanged -> sha256File (Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")) = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        | NoPinsMutationSurface | NoWorkflowPublicationSurface ->
            let surface = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubImmutableExecutionPinsQualification.fsi"))
            [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "val publish"; "PATCH"; "POST"; "DELETE" ] |> List.forall (surface.Contains >> not)

    let independentMutation control =
        match control with
        | ImmutablePinsPrerequisite -> snapshot.PrerequisiteReceiptDigest = "eec15747e2e5c1cf0ae91fbf370eb82a3e6ea88d6fe3c0f2f738a556e63e5063"
        | ImmutablePinsCompleteness -> snapshot.Complete && trackedWorkflows = declaredWorkflows
        | ImmutablePinsSourceBinding -> snapshot.SourceRevision = "e25727a89ad0101188da74414669a556059d251e"
        | ThirdPartyActionPins -> observedReferences |> Seq.filter (fun (_, kind, _, _, _) -> kind = ThirdPartyAction) |> Seq.forall (fun (_, _, _, _, revision) -> revision.Length = 40 && revision |> Seq.forall Uri.IsHexDigit)
        | ReusableWorkflowPins -> observedReferences |> Seq.filter (fun (_, kind, _, _, _) -> kind = ReusableWorkflow) |> Seq.forall (fun (_, _, _, path, revision) -> path.IsSome && revision.Length = 40)
        | LocalExecutionReferenceRejection ->
            snapshot.Workflows
            |> List.collect (fun workflow ->
                uses.Matches(File.ReadAllText(Path.Combine(root, workflow.Path)))
                |> Seq.map (fun matched -> matched.Groups[1].Value)
                |> List.ofSeq)
            |> List.forall (fun literal ->
                not (literal.StartsWith("./", StringComparison.Ordinal))
                && GitHubImmutableExecutionPinsQualification.classifyReferenceLiteral literal |> Result.isOk)
        | WorkflowDigestBinding -> snapshot.Workflows |> List.forall (fun workflow -> sha256File (Path.Combine(root, workflow.Path)) = workflow.Sha256)
        | PublicationIdentity | PublicationContent | PublicationWorkflowCall -> snapshot.Publications.IsEmpty && observedReferences |> Seq.exists (fun (_, kind, _, _, _) -> kind = ReusableWorkflow) |> not
        | StablePinOrdering -> compile { snapshot with Workflows = List.rev snapshot.Workflows } = Ok report
        | UpdaterConfigurationInventory ->
            observedUpdaterConfigurations = declaredUpdaterConfigurations
            && observedUpdaterConfigurations |> Seq.forall (fun (_, _, authority, pullRequestOnly, directPush) -> authority = "renovate" && pullRequestOnly && not directPush)
        | RenovateSoleUpdater -> snapshot.Updaters |> List.filter _.Automated |> List.map _.Name = [ "renovate" ]
        | RenovatePullRequestOnly -> snapshot.Updaters.Head.PullRequestOnly && not snapshot.Updaters.Head.DirectPush
        | RenovateOwnership -> snapshot.Updaters.Head.OwnedManagers = [ "github-actions" ] && snapshot.RequiredManagers = [ "github-actions" ]
        | ExactPinsSeal -> report.Seal = text expectations "expectedSeal"
        | ExactPinsReplay -> GitHubImmutableExecutionPinsQualification.verify report.Seal snapshot = Ok report
        | QuintPinsUnchanged -> generatedMutation QuintPinsUnchanged
        | NoPinsMutationSurface | NoWorkflowPublicationSurface -> generatedMutation control

    let result control passed = { GitHubImmutableExecutionPinsControlResult.Control = control; ControlPassed = passed; BaselineGreen = true }
    let generated = GitHubImmutableExecutionPinsQualification.requiredControls |> List.map (fun control -> result control (generatedMutation control))
    let independent = GitHubImmutableExecutionPinsQualification.requiredControls |> List.map (fun control -> result control (independentMutation control))
    match GitHubImmutableExecutionPinsQualification.validate generated independent with
    | Ok () -> printfn "GITHUB_IMMUTABLE_EXECUTION_PINS_OK workflows=%d references=%d publications=%d updaterConfigurations=%d updater=%s controls=%d seal=%s" report.WorkflowCount report.ReferenceCount report.PublicationCount report.UpdaterConfigurationCount report.AutomatedUpdater expectedControls.Length report.Seal
    | Error findings -> failwithf "immutable execution pins qualification failed: %A" findings
