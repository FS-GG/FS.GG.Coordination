namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text.Json

/// A protected-host port. Every read must be a fresh authenticated GET from GitHub, using the
/// installed workflow's credential. It must reject redirects, partial responses and cached data.
/// A local/mock implementation is useful for tests but carries no installation authority.
type IMigrationSandboxSeedNativeProvenanceRead =
    abstract ReadRunAttempt: runId:int64 * attempt:int -> Result<byte array, string>
    abstract ReadGitBlob: commitSha:string * path:string -> Result<byte array, string>
    abstract ReadApprovedArtifact: sourceSha256:string -> Result<byte array, string>
    abstract ReadRuleset: rulesetId:int64 -> Result<byte array, string>
    /// None is permitted only after an independent authenticated native absence proof.
    /// An HTTP 404, redaction, or plan-entitlement error must return Error.
    abstract ReadClassicProtection: branch:string -> Result<byte array option, string>

/// This adapter has no default transport or ambient-token constructor. The protected workflow
/// must install the read port after its native credential and ruleset entitlement are proved.
type MigrationSandboxSeedNativeProvenanceVerifier(read: IMigrationSandboxSeedNativeProvenanceRead) =
    let sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let hex length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))

    let bounded (bytes: byte array) = not (isNull bytes) && bytes.Length > 0 && bytes.Length <= 1024 * 1024
    let boundedArtifact (bytes: byte array) =
        not (isNull bytes) && bytes.Length > 0 && bytes.Length <= 64 * 1024 * 1024

    let parse bytes =
        if not (bounded bytes) then invalidOp "native-read-size"
        let document = JsonDocument.Parse bytes
        let rec unique (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                if names.Length <> (names |> Set.ofList |> Set.count) then invalidOp "duplicate-json-member"
                element.EnumerateObject() |> Seq.iter (fun item -> unique item.Value)
            | JsonValueKind.Array -> element.EnumerateArray() |> Seq.iter unique
            | _ -> ()
        unique document.RootElement
        document

    let stringAt (name: string) (value: JsonElement) = value.GetProperty(name).GetString()
    let intAt (name: string) (value: JsonElement) = value.GetProperty(name).GetInt64()

    let requireRead result =
        match result with
        | Ok bytes when bounded bytes -> bytes
        | _ -> invalidOp "native-read-unavailable"

    let ruleset expectedName expectedId expectedDigest expectedBypass expectedRules =
        let bytes = read.ReadRuleset expectedId |> requireRead
        if sha256 bytes <> expectedDigest then invalidOp "ruleset-bytes-mismatch"
        use document = parse bytes
        let root = document.RootElement
        if intAt "id" root <> expectedId
           || stringAt "name" root <> expectedName
           || stringAt "source_type" root <> "Repository"
           || stringAt "source" root <> "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
           || stringAt "target" root <> "branch"
           || stringAt "enforcement" root <> "active" then invalidOp "ruleset-identity"
        let refs = root.GetProperty("conditions").GetProperty("ref_name")
        let includes = refs.GetProperty("include").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
        let excludes = refs.GetProperty("exclude").EnumerateArray() |> Seq.toList
        if includes <> [ "refs/heads/gs2-09-7/*/seed-journal" ] || not excludes.IsEmpty then
            invalidOp "ruleset-selector"
        let bypass = root.GetProperty("bypass_actors").EnumerateArray() |> Seq.toList
        if bypass.Length <> expectedBypass then invalidOp "ruleset-bypass"
        if expectedBypass = 1 then
            let actor = bypass.Head
            if intAt "actor_id" actor <> 4166418L
               || stringAt "actor_type" actor <> "Integration"
               || stringAt "bypass_mode" actor <> "always" then invalidOp "ruleset-bypass"
        let kinds =
            root.GetProperty("rules").EnumerateArray()
            |> Seq.map (fun item -> stringAt "type" item) |> Seq.toList
        if kinds <> expectedRules then invalidOp "ruleset-rules"

    interface IMigrationSandboxSeedInstalledProvenanceVerifier with
        member _.VerifyExact evidence =
            try
                if isNull (box read) || not (bounded evidence.BindingBytes)
                   || not (bounded evidence.PolicyReadbackBytes)
                   || evidence.WorkflowRunId <= 0L || evidence.WorkflowRunAttempt <= 0
                   || not (hex 40 evidence.WorkflowSha)
                   || not (hex 64 evidence.ApprovedArtifactSourceSha256) then false
                else
                    use binding = parse evidence.BindingBytes
                    use policy = parse evidence.PolicyReadbackBytes
                    let source = binding.RootElement.GetProperty "source"
                    let policyRoot = policy.RootElement
                    let ruleIds = policyRoot.GetProperty "rulesets"
                    let run = read.ReadRunAttempt(evidence.WorkflowRunId, evidence.WorkflowRunAttempt) |> requireRead
                    use runDocument = parse run
                    let runRoot = runDocument.RootElement
                    let repo = runRoot.GetProperty "repository"
                    let actor = runRoot.GetProperty "actor"
                    let runMatches =
                        intAt "id" runRoot = evidence.WorkflowRunId
                        && intAt "run_attempt" runRoot = int64 evidence.WorkflowRunAttempt
                        && stringAt "head_sha" runRoot = evidence.WorkflowSha
                        && stringAt "event" runRoot = "workflow_dispatch"
                        && stringAt "path" runRoot = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                        && intAt "id" repo > 0L
                        && stringAt "full_name" repo = "FS-GG/.github"
                        && intAt "id" actor > 0L
                    let sourceMatches =
                        intAt "runId" source = evidence.WorkflowRunId
                        && intAt "runAttempt" source = int64 evidence.WorkflowRunAttempt
                        && stringAt "workflowSha" source = evidence.WorkflowSha
                        && stringAt "approvedArtifactSourceSha256" source = evidence.ApprovedArtifactSourceSha256
                    if not (runMatches && sourceMatches)
                       || (match read.ReadApprovedArtifact evidence.ApprovedArtifactSourceSha256 with
                           | Ok bytes when boundedArtifact bytes -> sha256 bytes <> evidence.ApprovedArtifactSourceSha256
                           | _ -> true) then false
                    else
                        let workflow = read.ReadGitBlob(evidence.WorkflowSha, ".github/workflows/github-substrate-v2-sandbox-qualification.yml") |> requireRead
                        let builder = read.ReadGitBlob(evidence.WorkflowSha, "scripts/gs2-09-7-seed-execution-binding.py") |> requireRead
                        let checkout = source.GetProperty("protectedCheckout")
                        if stringAt "checkoutHead" checkout <> evidence.WorkflowSha
                           || stringAt "path" (checkout.GetProperty "workflow") <> ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                           || stringAt "path" (checkout.GetProperty "builder") <> "scripts/gs2-09-7-seed-execution-binding.py"
                           || stringAt "sha256" (checkout.GetProperty "workflow") <> sha256 workflow
                           || stringAt "sha256" (checkout.GetProperty "builder") <> sha256 builder then false
                        else
                            let writerId = intAt "writerId" ruleIds
                            let integrityId = intAt "integrityId" ruleIds
                            if writerId <= 0L || integrityId <= 0L || writerId = integrityId then false
                            else
                                ruleset "gs2-09-7-seed-journal-writer" writerId
                                    (stringAt "writerSha256" ruleIds) 1 [ "creation"; "update" ]
                                ruleset "gs2-09-7-seed-journal-integrity" integrityId
                                    (stringAt "integritySha256" ruleIds) 0 [ "deletion"; "non_fast_forward" ]
                                let nonce = stringAt "runNonce" source
                                let branch = $"gs2-09-7/{nonce}/seed-journal"
                                match read.ReadClassicProtection branch with
                                | Ok None -> true
                                | _ -> false
            with _ -> false
