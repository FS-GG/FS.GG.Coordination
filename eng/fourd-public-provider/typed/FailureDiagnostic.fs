namespace FS.GG.FourD.Typed

module FailureDiagnostic =
    type Observation = {
        Callsite: string
        Category: string
        Token: string option
        CleanupComplete: bool
    }

    type Projection = {
        Ready: bool
        FailureCode: string option
        CleanupComplete: bool
    }

    let private toolCallsites = Set ["tool-git"; "tool-node"; "tool-openssl"]
    let private callsites = Set.union toolCallsites (Set [
        "capacity"; "public-placement"; "public-tools"; "admission"; "typed-admission"
        "source-recipient"; "source-release-metadata"; "source-download"; "source-outer-capsule"
        "source-unseal"; "source-plaintext"; "source-reconstruction"; "source-validation"
        "typed-cleanup"; "source-receipt"; "source-cleanup"; "unknown"
    ])
    let private capsuleTokens = Set [
        "json-size-refused"; "json-duplicate-key-refused"; "json-refused"; "base64-refused"
        "snapshot-path-refused"; "descriptor-shape-refused"; "descriptor-purpose-refused"
        "descriptor-binding-refused"; "descriptor-identity-refused"; "descriptor-digest-refused"
        "descriptor-size-refused"; "descriptor-identifier-refused"; "capsule-shape-refused"
        "capsule-binding-refused"; "capsule-aad-refused"; "snapshot-shape-refused"
        "snapshot-binding-refused"; "snapshot-commit-refused"; "snapshot-files-refused"
        "snapshot-file-shape-refused"; "snapshot-order-refused"; "snapshot-mode-refused"
        "snapshot-size-refused"; "snapshot-total-refused"; "snapshot-file-digest-refused"
        "snapshot-inventory-refused"; "git-reconstruction-refused"; "git-reconstruction-timeout"
        "git-worktree-refused"; "git-blob-refused"; "git-tree-refused"; "git-commit-tree-refused"
        "git-commit-refused"; "git-cleanliness-refused"
    ]
    let private refusalTokens = Set [
        "capacity-refused"; "public-placement-refused"; "public-tool-source-refused"; "known-host-refused"
        "setup-dotnet-bytes-refused"; "execution-context-refused"; "admission-missing"; "admission-partial"
        "admission-json-refused"; "admission-shape-refused"; "admission-binding-refused"
        "admission-identifier-refused"; "admission-nonce-refused"; "admission-capsule-shape-refused"
        "admission-capsule-transport-refused"; "admission-capsule-identifier-refused"
        "admission-capsule-digest-refused"; "admission-capsule-name-refused"; "admission-capsule-size-refused"
        "admission-descriptor-digest-refused"; "admission-digest-refused"; "admission-time-refused"
        "source-recipient-key-format-refused"; "typed-policy-admission-refused"; "source-recipient-key-refused"
        "source-release-readback-refused"; "source-release-binding-refused"; "source-download-refused"
        "source-download-redirect-refused"; "source-download-timeout"; "source-download-overflow"
        "source-download-binding-refused"; "source-acquisition-timeout"; "source-unseal-refused"
        "source-held-file-refused"; "source-plaintext-refused"; "source-plaintext-binding-refused"
        "child-scope-refused"; "cancelled"; "source-binding-refused"; "source-cleanliness-refused"
        "source-index-refused"; "source-symlink-refused"; "source-lfs-refused"; "typed-policy-cleanup-refused"
        "private-root-refused"; "private-write-refused"; "source-cleanup-refused"
        "source-git-tool-refused"; "source-node-tool-refused"; "source-openssl-tool-refused"
    ]

    let private osCode callsite =
        match callsite with
        | "tool-git" -> "source-git-tool-refused"
        | "tool-node" -> "source-node-tool-refused"
        | "tool-openssl" -> "source-openssl-tool-refused"
        | "source-reconstruction" -> "git-reconstruction-refused"
        | "source-unseal" -> "source-unseal-refused"
        | "source-release-metadata" -> "source-release-readback-refused"
        | "source-download" -> "source-download-refused"
        | "source-recipient" -> "source-recipient-key-refused"
        | _ -> "qualification-refused"

    let project observation =
        if not (callsites.Contains observation.Callsite) then Error "typed-failure-callsite-refused"
        else
            match observation.Category, observation.Token with
            | "available", None when toolCallsites.Contains observation.Callsite ->
                Ok { Ready=true; FailureCode=None; CleanupComplete=observation.CleanupComplete }
            | "capsule-refusal", Some token when capsuleTokens.Contains token ->
                Ok { Ready=false; FailureCode=Some token; CleanupComplete=observation.CleanupComplete }
            | "closed-refusal", Some token when refusalTokens.Contains token ->
                Ok { Ready=false; FailureCode=Some token; CleanupComplete=observation.CleanupComplete }
            | ("os-not-found" | "os-permission" | "os-timeout" | "os-io"), None ->
                Ok { Ready=false; FailureCode=Some(osCode observation.Callsite); CleanupComplete=observation.CleanupComplete }
            | "cleanup-failed", None ->
                Ok { Ready=false; FailureCode=Some "source-cleanup-refused"; CleanupComplete=false }
            | "unexpected", None ->
                Ok { Ready=false; FailureCode=Some "qualification-refused"; CleanupComplete=observation.CleanupComplete }
            | _ -> Error "typed-failure-observation-refused"
