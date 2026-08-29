# GS2-03.1 canonical coordination protocol

This document is the sole authored source for the coordination protocol baseline. Every behavioral
fact is inside a named Quint block. The generated `.qnt`, compiled contract, and F# bindings are
projections and must never be edited independently.

GS2-02.1 established vocabulary and stable integration identities; GS2-02.2 added the closed,
revision-aware authority catalogue; GS2-02.3 added observation outcomes and knowledge semantics.
GS2-02.4 added lifecycle intent and derived status; GS2-02.5 added native relation algebra; GS2-02.6
added typed retained protocol streams; GS2-02.7 added the closed mutation algebra; GS2-02.8 added
ordered resumable durable plans. This unit adds closed desired-state families and pure inspect, plan,
apply-intent, and verify classification for GitHub configuration. This unit adds a closed catalogue
of deterministic compiled-contract output families and typed qualification of their identity,
content, completeness, support, freshness, and order. This unit binds canonical typed-effect behavior
to an exact five-part version tuple and stable semantic-diff projection. GS2-03.1 adds the closed
qualification-manifest identity and its candidate, input-set, freshness, independence, result, and
review bindings. No network writer or production mutation authority is defined here.

```quint protocol.qnt +=
module CoordinationProtocol {
  type VocabularyEntry = { id: str, kind: str, family: str }
  type PropertyEntry = { id: str, kind: str, subjects: Set[str] }
  type RelationshipEntry = { id: str, kind: str, fromId: str, toId: str }
  type VerificationEntry = {
    id: str,
    kind: str,
    verificationKind: str,
    subjectIds: Set[str],
    boundIds: Set[str],
  }
  type BoundEntry = { id: str, kind: str, minimum: int, maximum: int }
  type CompatibilityEntry = { id: str, kind: str, surface: str, requirement: str, detail: str }
  type AuthorityBinding = {
    id: str,
    kind: str,
    family: str,
    revisionKind: str,
    revisionValue: str,
    completenessContract: str,
    evidenceRelationship: str,
  }
  type ObservationOutcome = {
    id: str,
    kind: str,
    knowledgeClass: str,
    retryClass: str,
    terminal: bool,
  }
  type AuthorityObservation = {
    outcomeId: str,
    authorityId: str,
    family: str,
    revisionKind: str,
    revisionValue: str,
    completenessContract: str,
    evidenceRelationship: str,
    complete: bool,
    contradictory: bool,
    retryAfterPresent: bool,
  }
  type LifecycleIntent = {
    id: str,
    kind: str,
    schedulingClass: str,
    terminal: bool,
  }
  type LifecycleFacts = {
    claimOutcomeId: str,
    claimPresent: bool,
    blockerOutcomeId: str,
    blocked: bool,
    pullRequestOutcomeId: str,
    pullRequestOpen: bool,
    reviewOutcomeId: str,
    reviewAccepted: bool,
    deliveryOutcomeId: str,
    delivered: bool,
  }
  type NativeRelationKind = { id: str, kind: str, direction: str }
  type NativeRelationEdge = { relationKindId: str, sourceId: str, targetId: str }
  type ProtocolStreamKind = { id: str, kind: str, family: str }
  type ProtocolPayloadKind = {
    id: str,
    kind: str,
    streamKindId: str,
    retentionClass: str,
    durableCheckpoint: bool,
  }
  type ProtocolEnvelope = {
    streamKindId: str,
    streamId: str,
    subjectId: str,
    generation: int,
    sequence: int,
    eventId: str,
    predecessorEventId: str,
    payloadKindId: str,
    retentionClass: str,
    durableCheckpoint: bool,
  }
  type MutationKind = {
    id: str,
    kind: str,
    targetKind: str,
    payloadKind: str,
    revisionRequirement: str,
  }
  type MutationOutcome = {
    id: str,
    kind: str,
    finality: str,
    effectClass: str,
    retryClass: str,
  }
  type MutationIntent = {
    operationId: str,
    subjectId: str,
    mutationKindId: str,
    targetKind: str,
    payloadKind: str,
    expectedRevision: int,
    idempotencyKey: str,
    payloadDigest: str,
    compensatesOperationId: str,
  }
  type MutationResult = {
    intent: MutationIntent,
    outcomeId: str,
    resultingRevision: int,
  }
  type DurablePlanStep = {
    planId: str,
    stepId: str,
    predecessorStepId: str,
    sequence: int,
    causationId: str,
    correlationId: str,
    compensationBoundaryId: str,
    intent: MutationIntent,
  }
  pure val vocabularyCatalogue = Set(
    { id: "SubjectVocabulary", kind: "subject", family: "subjects" },
    { id: "AuthorityVocabulary", kind: "authority", family: "authorities" },
    { id: "CodecVocabulary", kind: "codec", family: "codecs" },
    { id: "CommandVocabulary", kind: "command", family: "commands" },
    { id: "EventVocabulary", kind: "event", family: "events" },
    { id: "MutationVocabulary", kind: "mutation", family: "mutations" },
    { id: "ProjectionVocabulary", kind: "projection", family: "projections" },
    { id: "ObservationPlanVocabulary", kind: "observationPlan", family: "observation-plans" },
    { id: "SettingsProfileVocabulary", kind: "settingsProfile", family: "settings-profiles" },
    { id: "EvidenceObligationVocabulary", kind: "evidence", family: "evidence-obligations" },
    { id: "VersionIdentityVocabulary", kind: "versionIdentity", family: "version-identities" }
  )

  pure val authorityCatalogue = Set(
    { id: "AUTH-NativeGitHub", kind: "authorityBinding", family: "native-github", revisionKind: "github-object-version", revisionValue: "node-id-and-updated-at", completenessContract: "complete-required-fields", evidenceRelationship: "REL-AUTH-NativeGitHub-Evidence" },
    { id: "AUTH-RepositoryRegistry", kind: "authorityBinding", family: "repository-registry", revisionKind: "registry-document-sha256", revisionValue: "canonical-document-digest", completenessContract: "complete-required-fields", evidenceRelationship: "REL-AUTH-RepositoryRegistry-Evidence" },
    { id: "AUTH-ProtocolStream", kind: "authorityBinding", family: "protocol-stream", revisionKind: "protocol-contract-sha256", revisionValue: "compiled-contract-digest", completenessContract: "complete-required-fields", evidenceRelationship: "REL-AUTH-ProtocolStream-Evidence" },
    { id: "AUTH-GitLedger", kind: "authorityBinding", family: "git-ledger", revisionKind: "git-commit-sha", revisionValue: "commit-object-id", completenessContract: "complete-required-fields", evidenceRelationship: "REL-AUTH-GitLedger-Evidence" },
    { id: "AUTH-Actions", kind: "authorityBinding", family: "actions", revisionKind: "workflow-run-attempt", revisionValue: "run-id-and-attempt", completenessContract: "complete-required-fields", evidenceRelationship: "REL-AUTH-Actions-Evidence" },
    { id: "AUTH-PackageFeed", kind: "authorityBinding", family: "package-feed", revisionKind: "package-content-sha256", revisionValue: "package-bytes-digest", completenessContract: "complete-required-fields", evidenceRelationship: "REL-AUTH-PackageFeed-Evidence" },
    { id: "AUTH-ClassifiedExternal", kind: "authorityBinding", family: "classified-external", revisionKind: "classified-external-revision", revisionValue: "declared-source-revision", completenessContract: "complete-required-fields", evidenceRelationship: "REL-AUTH-ClassifiedExternal-Evidence" }
  )

  pure val observationOutcomeCatalogue = Set(
    { id: "OBS-Observed", kind: "observationOutcome", knowledgeClass: "positive", retryClass: "none", terminal: true },
    { id: "OBS-ProvenAbsent", kind: "observationOutcome", knowledgeClass: "negative", retryClass: "none", terminal: true },
    { id: "OBS-Contradictory", kind: "observationOutcome", knowledgeClass: "contradictory", retryClass: "resolve-contradiction", terminal: false },
    { id: "OBS-Unreadable", kind: "observationOutcome", knowledgeClass: "none", retryClass: "repair-read", terminal: false },
    { id: "OBS-Unsupported", kind: "observationOutcome", knowledgeClass: "none", retryClass: "none", terminal: true },
    { id: "OBS-Unauthorized", kind: "observationOutcome", knowledgeClass: "none", retryClass: "repair-authorization", terminal: false },
    { id: "OBS-Incomplete", kind: "observationOutcome", knowledgeClass: "none", retryClass: "complete-evidence", terminal: false },
    { id: "OBS-Stale", kind: "observationOutcome", knowledgeClass: "none", retryClass: "refresh-revision", terminal: false },
    { id: "OBS-RateLimited", kind: "observationOutcome", knowledgeClass: "none", retryClass: "authority-window", terminal: false }
  )

  pure val lifecycleIntentCatalogue = Set(
    { id: "INTENT-Backlog", kind: "lifecycleIntent", schedulingClass: "backlog", terminal: false },
    { id: "INTENT-Ready", kind: "lifecycleIntent", schedulingClass: "ready", terminal: false },
    { id: "INTENT-Paused", kind: "lifecycleIntent", schedulingClass: "paused", terminal: false },
    { id: "INTENT-Cancelled", kind: "lifecycleIntent", schedulingClass: "cancelled", terminal: true }
  )

  pure val nativeRelationKindCatalogue = Set(
    { id: "REL-ParentChild", kind: "nativeRelationKind", direction: "parent-to-child" },
    { id: "REL-Blocks", kind: "nativeRelationKind", direction: "blocker-to-blocked" }
  )

  pure val protocolStreamKindCatalogue = Set(
    { id: "STREAM-Claim", kind: "protocolStreamKind", family: "claim-lease-touch-set" },
    { id: "STREAM-OperationLock", kind: "protocolStreamKind", family: "operation-lock-election" },
    { id: "STREAM-Review", kind: "protocolStreamKind", family: "review" },
    { id: "STREAM-Delivery", kind: "protocolStreamKind", family: "delivery" },
    { id: "STREAM-OperationReceipt", kind: "protocolStreamKind", family: "operation-receipt" }
  )

  pure val protocolPayloadKindCatalogue = Set(
    { id: "PAYLOAD-Claim", kind: "protocolPayloadKind", streamKindId: "STREAM-Claim", retentionClass: "ephemeral", durableCheckpoint: false },
    { id: "PAYLOAD-Lease", kind: "protocolPayloadKind", streamKindId: "STREAM-Claim", retentionClass: "ephemeral", durableCheckpoint: false },
    { id: "PAYLOAD-TouchSet", kind: "protocolPayloadKind", streamKindId: "STREAM-Claim", retentionClass: "ephemeral", durableCheckpoint: false },
    { id: "PAYLOAD-OperationLock", kind: "protocolPayloadKind", streamKindId: "STREAM-OperationLock", retentionClass: "ephemeral", durableCheckpoint: false },
    { id: "PAYLOAD-Election", kind: "protocolPayloadKind", streamKindId: "STREAM-OperationLock", retentionClass: "durable", durableCheckpoint: true },
    { id: "PAYLOAD-Review", kind: "protocolPayloadKind", streamKindId: "STREAM-Review", retentionClass: "durable", durableCheckpoint: true },
    { id: "PAYLOAD-Delivery", kind: "protocolPayloadKind", streamKindId: "STREAM-Delivery", retentionClass: "durable", durableCheckpoint: true },
    { id: "PAYLOAD-OperationReceipt", kind: "protocolPayloadKind", streamKindId: "STREAM-OperationReceipt", retentionClass: "durable", durableCheckpoint: true }
  )

  pure val mutationKindCatalogue = Set(
    { id: "MUT-Create", kind: "mutationKind", targetKind: "subject", payloadKind: "create", revisionRequirement: "absent" },
    { id: "MUT-Append", kind: "mutationKind", targetKind: "stream", payloadKind: "append", revisionRequirement: "exact" },
    { id: "MUT-AddEdge", kind: "mutationKind", targetKind: "relation", payloadKind: "edge", revisionRequirement: "exact" },
    { id: "MUT-RemoveEdge", kind: "mutationKind", targetKind: "relation", payloadKind: "edge", revisionRequirement: "exact" },
    { id: "MUT-Set", kind: "mutationKind", targetKind: "field", payloadKind: "scalar", revisionRequirement: "exact" },
    { id: "MUT-Clear", kind: "mutationKind", targetKind: "field", payloadKind: "scalar", revisionRequirement: "exact" },
    { id: "MUT-Transition", kind: "mutationKind", targetKind: "lifecycle", payloadKind: "transition", revisionRequirement: "exact" },
    { id: "MUT-Compensate", kind: "mutationKind", targetKind: "mutation", payloadKind: "compensation", revisionRequirement: "exact" }
  )

  pure val mutationOutcomeCatalogue = Set(
    { id: "MOUT-Applied", kind: "mutationOutcome", finality: "terminal", effectClass: "applied", retryClass: "none" },
    { id: "MOUT-Idempotent", kind: "mutationOutcome", finality: "terminal", effectClass: "no-op", retryClass: "none" },
    { id: "MOUT-Rejected", kind: "mutationOutcome", finality: "terminal", effectClass: "refused", retryClass: "new-authority-or-intent" },
    { id: "MOUT-RevisionConflict", kind: "mutationOutcome", finality: "terminal", effectClass: "conflict", retryClass: "refresh-and-new-intent" },
    { id: "MOUT-RateLimited", kind: "mutationOutcome", finality: "uncertain", effectClass: "unknown", retryClass: "authority-window" },
    { id: "MOUT-Unavailable", kind: "mutationOutcome", finality: "uncertain", effectClass: "unknown", retryClass: "availability-window" },
    { id: "MOUT-TimedOut", kind: "mutationOutcome", finality: "uncertain", effectClass: "unknown", retryClass: "observe-or-exact-replay" },
    { id: "MOUT-Incomplete", kind: "mutationOutcome", finality: "uncertain", effectClass: "unknown", retryClass: "complete-observation" }
  )

  pure val durablePlanDispositionCatalogue = Set(
    { id: "PDISP-Advance", kind: "durablePlanDisposition", receiptClass: "terminal-success", nextAction: "next-step" },
    { id: "PDISP-ReceiptReread", kind: "durablePlanDisposition", receiptClass: "uncertain", nextAction: "reread-receipt" },
    { id: "PDISP-Replan", kind: "durablePlanDisposition", receiptClass: "terminal-refusal-no-applied-boundary", nextAction: "compile-new-plan" },
    { id: "PDISP-Compensate", kind: "durablePlanDisposition", receiptClass: "terminal-refusal-applied-boundary", nextAction: "compensate-reverse" }
  )

  pure val desiredStateSpecificationCatalogue = Set(
    { id: "DSTATE-Specification", kind: "desiredStateSpecification", authorityClass: "revision-bound",
      executionClass: "pure-intent-no-writer",
      issueSchemaContract: "issue-type|issue-field|field-type|allowed-value",
      repositoryPropertiesContract: "property-schema|property-value",
      projectsContract: "project-field|project-view|project-workflow|project-visibility|project-membership-policy",
      repositoryProfileContract: "ruleset|merge-queue|merge-policy|actions-policy|branch-deletion-policy",
      workflowPinsContract: "reusable-workflow-pin|action-pin",
      releasesContract: "release-environment|immutable-release|tag-protection|trusted-publisher",
      permissionsContract: "repository-visibility|team-access|workflow-permission|environment-protection",
      securitySupplyChainContract: "vulnerability-policy|secret-policy|dependency-policy|sbom-policy|attestation-policy",
      phaseContract: "DSPH-Inspect>DSPH-Plan>(DSPH-Apply|DSPH-Verify)>DSPH-Verify",
      phaseAuthorityContract: "subject|profile|family|content|authority-revision|plan-outcome|apply-receipt",
      refusalContract: "unsupported|unauthorized|incomplete|stale|identity-mismatch" }
  )

  pure val compiledOutputSpecificationCatalogue = Set(
    { id: "COUT-Specification", kind: "compiledOutputSpecification",
      familyContract: "1:schemas|2:command-metadata|3:permission-census|4:mutation-census|5:settings-plans|6:projection-views|7:semantic-diff|8:diagrams|9:model-test-inventory",
      identityContract: "family|ordinal|source|behavior|source-version|extractor-version|quint-version|profile-version|schema-version|contract|content",
      qualificationContract: "supported|complete|fresh|qualification-manifest:candidate|input-set|environment|results|reviewers|independent-cases|independent-review",
      projectionViewFormats: "markdown|json", normalizationAuthority: "typed-effect-json",
      refusalContract: "missing|duplicate|substituted|unsupported|incomplete|reordered|stale", versionContract: "fsgg.quint.literate-source/1|quint-specification-v1@FS.GG.SDD.Artifacts/1.5.0|sha256:939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f|fsgg-quint-profile/2|fsgg.quint.compiled-contract/v2", semanticDiffContract: "ordinal|json-pointer|value-sha256" }
  )

  pure val relationshipCatalogue = Set(
    { id: "REL-Subject-Evidence", kind: "verifiedBy", fromId: "SubjectVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-Authority-Evidence", kind: "verifiedBy", fromId: "AuthorityVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-Codec-Evidence", kind: "verifiedBy", fromId: "CodecVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-Command-Evidence", kind: "verifiedBy", fromId: "CommandVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-Event-Evidence", kind: "verifiedBy", fromId: "EventVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-Mutation-Evidence", kind: "verifiedBy", fromId: "MutationVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-Projection-Evidence", kind: "verifiedBy", fromId: "ProjectionVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-ObservationPlan-Evidence", kind: "verifiedBy", fromId: "ObservationPlanVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-SettingsProfile-Evidence", kind: "verifiedBy", fromId: "SettingsProfileVocabulary", toId: "EvidenceObligationVocabulary" },
    { id: "REL-VersionIdentity-Evidence", kind: "verifiedBy", fromId: "VersionIdentityVocabulary", toId: "EvidenceObligationVocabulary" }
    ,{ id: "REL-AUTH-NativeGitHub-Evidence", kind: "verifiedBy", fromId: "AUTH-NativeGitHub", toId: "EvidenceObligationVocabulary" }
    ,{ id: "REL-AUTH-RepositoryRegistry-Evidence", kind: "verifiedBy", fromId: "AUTH-RepositoryRegistry", toId: "EvidenceObligationVocabulary" }
    ,{ id: "REL-AUTH-ProtocolStream-Evidence", kind: "verifiedBy", fromId: "AUTH-ProtocolStream", toId: "EvidenceObligationVocabulary" }
    ,{ id: "REL-AUTH-GitLedger-Evidence", kind: "verifiedBy", fromId: "AUTH-GitLedger", toId: "EvidenceObligationVocabulary" }
    ,{ id: "REL-AUTH-Actions-Evidence", kind: "verifiedBy", fromId: "AUTH-Actions", toId: "EvidenceObligationVocabulary" }
    ,{ id: "REL-AUTH-PackageFeed-Evidence", kind: "verifiedBy", fromId: "AUTH-PackageFeed", toId: "EvidenceObligationVocabulary" }
    ,{ id: "REL-AUTH-ClassifiedExternal-Evidence", kind: "verifiedBy", fromId: "AUTH-ClassifiedExternal", toId: "EvidenceObligationVocabulary" }
  )

  pure val boundCatalogue = Set(
    { id: "BOUND-VocabularyCardinality", kind: "bound", minimum: 11, maximum: 11 },
    { id: "BOUND-AuthorityCardinality", kind: "bound", minimum: 7, maximum: 7 },
    { id: "BOUND-ObservationOutcomeCardinality", kind: "bound", minimum: 9, maximum: 9 },
    { id: "BOUND-LifecycleIntentCardinality", kind: "bound", minimum: 4, maximum: 4 },
    { id: "BOUND-NativeRelationKindCardinality", kind: "bound", minimum: 2, maximum: 2 },
    { id: "BOUND-ProtocolStreamKindCardinality", kind: "bound", minimum: 5, maximum: 5 },
    { id: "BOUND-ProtocolPayloadKindCardinality", kind: "bound", minimum: 8, maximum: 8 },
    { id: "BOUND-MutationKindCardinality", kind: "bound", minimum: 8, maximum: 8 },
    { id: "BOUND-MutationOutcomeCardinality", kind: "bound", minimum: 8, maximum: 8 },
    { id: "BOUND-DurablePlanDispositionCardinality", kind: "bound", minimum: 4, maximum: 4 },
    { id: "BOUND-TraceSteps", kind: "bound", minimum: 0, maximum: 4 }
  )

  pure val verificationCatalogue = Set(
    { id: "VERIFY-VocabularyBaseline", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "SubjectVocabulary", "AuthorityVocabulary", "CodecVocabulary", "CommandVocabulary",
        "EventVocabulary", "MutationVocabulary", "ProjectionVocabulary", "ObservationPlanVocabulary",
        "SettingsProfileVocabulary", "EvidenceObligationVocabulary", "VersionIdentityVocabulary"
      ), boundIds: Set("BOUND-VocabularyCardinality", "BOUND-TraceSteps") },
    { id: "VERIFY-AuthorityBindings", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "AUTH-NativeGitHub", "AUTH-RepositoryRegistry", "AUTH-ProtocolStream", "AUTH-GitLedger",
        "AUTH-Actions", "AUTH-PackageFeed", "AUTH-ClassifiedExternal"
      ), boundIds: Set("BOUND-AuthorityCardinality", "BOUND-TraceSteps") },
    { id: "VERIFY-ObservationOutcomes", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "OBS-Observed", "OBS-ProvenAbsent", "OBS-Contradictory", "OBS-Unreadable", "OBS-Unsupported",
        "OBS-Unauthorized", "OBS-Incomplete", "OBS-Stale", "OBS-RateLimited"
      ), boundIds: Set("BOUND-ObservationOutcomeCardinality", "BOUND-TraceSteps") }
    ,{ id: "VERIFY-LifecycleIntent", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "INTENT-Backlog", "INTENT-Ready", "INTENT-Paused", "INTENT-Cancelled"
      ), boundIds: Set("BOUND-LifecycleIntentCardinality", "BOUND-TraceSteps") }
    ,{ id: "VERIFY-NativeRelations", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "REL-ParentChild", "REL-Blocks"
      ), boundIds: Set("BOUND-NativeRelationKindCardinality", "BOUND-TraceSteps") }
    ,{ id: "VERIFY-ProtocolStreams", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "STREAM-Claim", "STREAM-OperationLock", "STREAM-Review", "STREAM-Delivery", "STREAM-OperationReceipt",
        "PAYLOAD-Claim", "PAYLOAD-Lease", "PAYLOAD-TouchSet", "PAYLOAD-OperationLock", "PAYLOAD-Election",
        "PAYLOAD-Review", "PAYLOAD-Delivery", "PAYLOAD-OperationReceipt"
      ), boundIds: Set("BOUND-ProtocolStreamKindCardinality", "BOUND-ProtocolPayloadKindCardinality", "BOUND-TraceSteps") }
    ,{ id: "VERIFY-MutationAlgebra", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "MUT-Create", "MUT-Append", "MUT-AddEdge", "MUT-RemoveEdge", "MUT-Set", "MUT-Clear", "MUT-Transition", "MUT-Compensate",
        "MOUT-Applied", "MOUT-Idempotent", "MOUT-Rejected", "MOUT-RevisionConflict",
        "MOUT-RateLimited", "MOUT-Unavailable", "MOUT-TimedOut", "MOUT-Incomplete"
      ), boundIds: Set("BOUND-MutationKindCardinality", "BOUND-MutationOutcomeCardinality", "BOUND-TraceSteps") }
    ,{ id: "VERIFY-DurablePlans", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "PDISP-Advance", "PDISP-ReceiptReread", "PDISP-Replan", "PDISP-Compensate",
        "PAYLOAD-OperationReceipt", "MUT-Compensate"
      ), boundIds: Set("BOUND-DurablePlanDispositionCardinality", "BOUND-TraceSteps") }
  )

  pure val compatibilityCatalogue = Set(
    { id: "COMPAT-Profile2", kind: "compatibility", surface: "fsgg-quint-profile/2", requirement: "exact", detail: "Consumer-defined structural profile; profile 1 remains frozen." }
  )

  pure val propertyCatalogue = Set(
    { id: "AcceptedVocabularyIsQualified", kind: "invariant", subjects: Set("EvidenceObligationVocabulary") },
    { id: "VocabularyCanBeAccepted", kind: "example", subjects: Set("SubjectVocabulary") },
    { id: "AuthorityCatalogueIsClosed", kind: "invariant", subjects: Set("AuthorityVocabulary") },
    { id: "AcceptedAuthoritiesAreQualified", kind: "invariant", subjects: Set("AuthorityVocabulary", "EvidenceObligationVocabulary") },
    { id: "AuthorityCanBeAccepted", kind: "example", subjects: Set("AUTH-NativeGitHub") },
    { id: "ObservationOutcomeCatalogueIsClosed", kind: "invariant", subjects: Set("ObservationPlanVocabulary") },
    { id: "AcceptedObservationKnowledgeIsQualified", kind: "invariant", subjects: Set("ObservationPlanVocabulary", "EvidenceObligationVocabulary") },
    { id: "ProvenAbsenceCanBeAccepted", kind: "example", subjects: Set("OBS-ProvenAbsent") },
    { id: "FailureOutcomesDoNotBecomeAbsence", kind: "invariant", subjects: Set(
        "OBS-Contradictory", "OBS-Unreadable", "OBS-Unsupported", "OBS-Unauthorized", "OBS-Incomplete", "OBS-Stale", "OBS-RateLimited"
      ) },
    { id: "LifecycleIntentCatalogueIsClosed", kind: "invariant", subjects: Set(
        "INTENT-Backlog", "INTENT-Ready", "INTENT-Paused", "INTENT-Cancelled"
      ) },
    { id: "HumanIntentIsObservationIndependent", kind: "invariant", subjects: Set(
        "INTENT-Backlog", "INTENT-Ready", "ObservationPlanVocabulary"
      ) },
    { id: "LifecycleStatusIsDerived", kind: "invariant", subjects: Set(
        "INTENT-Ready", "OBS-Observed", "OBS-ProvenAbsent"
      ) },
    { id: "UnknownLifecycleFactsFailClosed", kind: "invariant", subjects: Set(
        "OBS-Contradictory", "OBS-Unreadable", "OBS-Unsupported", "OBS-Unauthorized", "OBS-Incomplete", "OBS-Stale", "OBS-RateLimited"
      ) }
    ,{ id: "NativeRelationKindCatalogueIsClosed", kind: "invariant", subjects: Set("REL-ParentChild", "REL-Blocks") }
    ,{ id: "NativeRelationEdgesAreValid", kind: "invariant", subjects: Set("REL-ParentChild", "REL-Blocks") }
    ,{ id: "RelationChangesPreserveUnrelatedEdges", kind: "invariant", subjects: Set("REL-ParentChild", "REL-Blocks") }
    ,{ id: "RelationChangesPreserveLifecycleIntent", kind: "invariant", subjects: Set(
        "REL-ParentChild", "REL-Blocks", "INTENT-Backlog", "INTENT-Ready", "INTENT-Paused", "INTENT-Cancelled"
      ) }
    ,{ id: "RelationObservationFailuresDoNotBecomeAbsence", kind: "invariant", subjects: Set(
        "REL-ParentChild", "REL-Blocks", "OBS-Contradictory", "OBS-Unreadable", "OBS-Unsupported", "OBS-Unauthorized", "OBS-Incomplete", "OBS-Stale", "OBS-RateLimited"
      ) }
    ,{ id: "ProtocolStreamCataloguesAreClosed", kind: "invariant", subjects: Set(
        "STREAM-Claim", "STREAM-OperationLock", "STREAM-Review", "STREAM-Delivery", "STREAM-OperationReceipt"
      ) }
    ,{ id: "ProtocolEnvelopesAreValidAndOrdered", kind: "invariant", subjects: Set(
        "PAYLOAD-Claim", "PAYLOAD-Lease", "PAYLOAD-TouchSet", "PAYLOAD-OperationLock", "PAYLOAD-Election",
        "PAYLOAD-Review", "PAYLOAD-Delivery", "PAYLOAD-OperationReceipt"
      ) }
    ,{ id: "DurableProtocolCheckpointsArePreserved", kind: "invariant", subjects: Set(
        "PAYLOAD-Election", "PAYLOAD-Review", "PAYLOAD-Delivery", "PAYLOAD-OperationReceipt"
      ) }
    ,{ id: "ProtocolStreamChangesPreservePriorSemantics", kind: "invariant", subjects: Set(
        "STREAM-Claim", "INTENT-Backlog", "REL-ParentChild", "REL-Blocks"
      ) }
    ,{ id: "ProtocolStreamObservationFailuresDoNotBecomeAbsence", kind: "invariant", subjects: Set(
        "AUTH-ProtocolStream", "OBS-Contradictory", "OBS-Unreadable", "OBS-Unsupported", "OBS-Unauthorized", "OBS-Incomplete", "OBS-Stale", "OBS-RateLimited"
      ) }
    ,{ id: "MutationCataloguesAreClosed", kind: "invariant", subjects: Set(
        "MutationVocabulary"
      ) }
    ,{ id: "MutationResultsAreBound", kind: "invariant", subjects: Set(
        "MutationVocabulary", "MOUT-Applied", "MOUT-Idempotent", "MOUT-Rejected", "MOUT-RevisionConflict"
      ) }
    ,{ id: "UncertainMutationOutcomesStayUnknown", kind: "invariant", subjects: Set(
        "MOUT-RateLimited", "MOUT-Unavailable", "MOUT-TimedOut", "MOUT-Incomplete"
      ) }
    ,{ id: "CompensationRequiresAppliedPredecessor", kind: "invariant", subjects: Set(
        "MUT-Compensate", "MOUT-Applied"
      ) }
    ,{ id: "DurablePlansAreOrderedAndResumable", kind: "invariant", subjects: Set(
        "PDISP-Advance", "PDISP-ReceiptReread", "PDISP-Replan", "PDISP-Compensate", "PAYLOAD-OperationReceipt"
      ) }
    ,{ id: "DurablePlanCompensationIsBoundaryBound", kind: "invariant", subjects: Set(
        "MUT-Compensate", "PDISP-Compensate"
      ) }
  )

  pure val nativeGitHubObservation = {
    outcomeId: "OBS-Observed",
    authorityId: "AUTH-NativeGitHub",
    family: "native-github",
    revisionKind: "github-object-version",
    revisionValue: "node-id-and-updated-at",
    completenessContract: "complete-required-fields",
    evidenceRelationship: "REL-AUTH-NativeGitHub-Evidence",
    complete: true,
    contradictory: false,
    retryAfterPresent: false,
  }

  pure def observationAuthorityShapeIsBound(observation: AuthorityObservation): bool =
    authorityCatalogue.exists(binding => and {
      binding.id == observation.authorityId,
      binding.family == observation.family,
      binding.revisionKind == observation.revisionKind,
      binding.completenessContract == observation.completenessContract,
      binding.evidenceRelationship == observation.evidenceRelationship,
    })

  pure def authorityObservationEvidenceIsQualified(observation: AuthorityObservation): bool = and {
    observation.complete,
    not(observation.contradictory),
    observationAuthorityShapeIsBound(observation),
    authorityCatalogue.exists(binding => binding.id == observation.authorityId and binding.revisionValue == observation.revisionValue),
  }

  pure def observationHasOutcome(observation: AuthorityObservation, outcomeId: str): bool =
    observation.outcomeId == outcomeId and observationOutcomeCatalogue.exists(outcome => outcome.id == outcomeId)

  pure def observationContributesPositiveKnowledge(observation: AuthorityObservation): bool = and {
    observationHasOutcome(observation, "OBS-Observed"),
    authorityObservationEvidenceIsQualified(observation),
    not(observation.retryAfterPresent),
  }

  pure def observationContributesNegativeKnowledge(observation: AuthorityObservation): bool = and {
    observationHasOutcome(observation, "OBS-ProvenAbsent"),
    authorityObservationEvidenceIsQualified(observation),
    not(observation.retryAfterPresent),
  }

  pure def observationContributesKnowledge(observation: AuthorityObservation): bool =
    observationContributesPositiveKnowledge(observation) or observationContributesNegativeKnowledge(observation)

  pure def observationIsRetryableFailure(observation: AuthorityObservation): bool = and {
    observationAuthorityShapeIsBound(observation),
    observation.retryAfterPresent,
    observationOutcomeCatalogue.exists(outcome => and {
      outcome.id == observation.outcomeId,
      outcome.knowledgeClass == "none",
      outcome.retryClass != "none",
      not(outcome.terminal),
    }),
  }

  pure def authorityObservationIsQualified(observation: AuthorityObservation): bool =
    observationContributesPositiveKnowledge(observation)

  pure val emptyLifecycleFacts = {
    claimOutcomeId: "OBS-ProvenAbsent",
    claimPresent: false,
    blockerOutcomeId: "OBS-ProvenAbsent",
    blocked: false,
    pullRequestOutcomeId: "OBS-ProvenAbsent",
    pullRequestOpen: false,
    reviewOutcomeId: "OBS-ProvenAbsent",
    reviewAccepted: false,
    deliveryOutcomeId: "OBS-ProvenAbsent",
    delivered: false,
  }

  pure val claimedLifecycleFacts = {
    ...emptyLifecycleFacts,
    claimOutcomeId: "OBS-Observed",
    claimPresent: true,
  }

  pure def lifecycleFactIsKnowledge(outcomeId: str, present: bool): bool =
    if (outcomeId == "OBS-Observed") present else outcomeId == "OBS-ProvenAbsent" and not(present)

  pure def lifecycleFactsAreKnowledge(facts: LifecycleFacts): bool = and {
    lifecycleFactIsKnowledge(facts.claimOutcomeId, facts.claimPresent),
    lifecycleFactIsKnowledge(facts.blockerOutcomeId, facts.blocked),
    lifecycleFactIsKnowledge(facts.pullRequestOutcomeId, facts.pullRequestOpen),
    lifecycleFactIsKnowledge(facts.reviewOutcomeId, facts.reviewAccepted),
    lifecycleFactIsKnowledge(facts.deliveryOutcomeId, facts.delivered),
  }

  pure def lifecycleIntentExists(intentId: str): bool =
    lifecycleIntentCatalogue.exists(intent => intent.id == intentId)

  pure def deriveLifecycleStatus(intentId: str, facts: LifecycleFacts): str =
    if (not(lifecycleIntentExists(intentId)) or not(lifecycleFactsAreKnowledge(facts))) "indeterminate"
    else if (intentId == "INTENT-Cancelled") "cancelled"
    else if (facts.delivered) "delivered"
    else if (facts.reviewAccepted) "accepted"
    else if (facts.blocked) "blocked"
    else if (facts.pullRequestOpen) "in-review"
    else if (facts.claimPresent) "claimed"
    else if (intentId == "INTENT-Ready") "ready"
    else if (intentId == "INTENT-Paused") "paused"
    else "backlog"

  pure def nativeRelationKindExists(relationKindId: str): bool =
    nativeRelationKindCatalogue.exists(relationKind => relationKind.id == relationKindId)

  pure def nativeRelationEdgeIsValid(edge: NativeRelationEdge): bool = and {
    nativeRelationKindExists(edge.relationKindId),
    edge.sourceId != edge.targetId,
  }

  pure def relationObservationContributesKnowledge(outcomeId: str, edges: Set[NativeRelationEdge]): bool =
    if (outcomeId == "OBS-Observed") edges.forall(nativeRelationEdgeIsValid)
    else outcomeId == "OBS-ProvenAbsent" and edges == Set()

  pure val parentChildEdge = { relationKindId: "REL-ParentChild", sourceId: "subject-parent", targetId: "subject-child" }
  pure val blockingEdge = { relationKindId: "REL-Blocks", sourceId: "subject-blocker", targetId: "subject-blocked" }

  pure def protocolStreamKindExists(streamKindId: str): bool =
    protocolStreamKindCatalogue.exists(streamKind => streamKind.id == streamKindId)

  pure def protocolPayloadMatchesEnvelope(envelope: ProtocolEnvelope): bool =
    protocolPayloadKindCatalogue.exists(payload => and {
      payload.id == envelope.payloadKindId,
      payload.streamKindId == envelope.streamKindId,
      payload.retentionClass == envelope.retentionClass,
      payload.durableCheckpoint == envelope.durableCheckpoint,
    })

  pure def protocolEnvelopeShapeIsValid(envelope: ProtocolEnvelope): bool = and {
    protocolStreamKindExists(envelope.streamKindId),
    protocolPayloadMatchesEnvelope(envelope),
    envelope.streamId != "",
    envelope.subjectId != "",
    envelope.generation > 0,
    envelope.sequence > 0,
    envelope.eventId != "",
    envelope.predecessorEventId != envelope.eventId,
    if (envelope.sequence == 1) envelope.predecessorEventId == "" else envelope.predecessorEventId != "",
  }

  pure def protocolAppendHasPredecessor(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =
    if (envelope.sequence == 1) {
      if (envelope.generation == 1) true
      else events.exists(previous => and {
        previous.streamKindId == envelope.streamKindId,
        previous.streamId == envelope.streamId,
        previous.subjectId == envelope.subjectId,
        previous.generation == envelope.generation - 1,
        previous.durableCheckpoint,
      })
    } else events.exists(previous => and {
      previous.streamKindId == envelope.streamKindId,
      previous.streamId == envelope.streamId,
      previous.subjectId == envelope.subjectId,
      previous.generation == envelope.generation,
      previous.sequence == envelope.sequence - 1,
      previous.eventId == envelope.predecessorEventId,
    })

  pure def retainedProtocolEnvelopeHasPredecessor(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =
    if (envelope.sequence == 1) {
      if (envelope.generation == 1) true
      else events.exists(previous => and {
        previous.streamKindId == envelope.streamKindId,
        previous.streamId == envelope.streamId,
        previous.subjectId == envelope.subjectId,
        previous.generation == envelope.generation - 1,
        previous.durableCheckpoint,
      })
    } else or {
      events.exists(previous => and {
        previous.streamKindId == envelope.streamKindId,
        previous.streamId == envelope.streamId,
        previous.subjectId == envelope.subjectId,
        previous.generation == envelope.generation,
        previous.sequence == envelope.sequence - 1,
        previous.eventId == envelope.predecessorEventId,
      }),
      envelope.durableCheckpoint or events.exists(checkpoint => and {
        checkpoint.streamKindId == envelope.streamKindId,
        checkpoint.streamId == envelope.streamId,
        checkpoint.subjectId == envelope.subjectId,
        checkpoint.generation == envelope.generation,
        checkpoint.sequence > envelope.sequence,
        checkpoint.durableCheckpoint,
      }),
    }

  pure def protocolEnvelopeIdentityIsConsistent(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =
    events.filter(existing => existing.eventId == envelope.eventId).forall(existing => existing == envelope)

  pure def protocolEnvelopeSequenceIsUnique(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =
    events.filter(existing => and {
      existing.streamKindId == envelope.streamKindId,
      existing.streamId == envelope.streamId,
      existing.subjectId == envelope.subjectId,
      existing.generation == envelope.generation,
      existing.sequence == envelope.sequence,
    }).forall(existing => existing == envelope)

  pure def protocolEnvelopeIsOrdered(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool = and {
    retainedProtocolEnvelopeHasPredecessor(envelope, events),
    protocolEnvelopeIdentityIsConsistent(envelope, events),
    protocolEnvelopeSequenceIsUnique(envelope, events),
  }

  pure def protocolAppendIsValid(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =
    if (events.contains(envelope)) true
    else and {
      protocolEnvelopeShapeIsValid(envelope),
      events.forall(existing => existing.eventId != envelope.eventId),
      events.forall(existing => not(and {
        existing.streamKindId == envelope.streamKindId,
        existing.streamId == envelope.streamId,
        existing.subjectId == envelope.subjectId,
        existing.generation == envelope.generation,
        existing.sequence == envelope.sequence,
      })),
      protocolAppendHasPredecessor(envelope, events),
      if (envelope.sequence == 1) events.forall(existing => not(and {
        existing.streamKindId == envelope.streamKindId,
        existing.streamId == envelope.streamId,
        existing.subjectId == envelope.subjectId,
        existing.generation >= envelope.generation,
      })) else true,
    }

  pure def ephemeralEnvelopeMayBeCompacted(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool = and {
    envelope.retentionClass == "ephemeral",
    not(envelope.durableCheckpoint),
    events.exists(checkpoint => and {
      checkpoint.streamKindId == envelope.streamKindId,
      checkpoint.streamId == envelope.streamId,
      checkpoint.subjectId == envelope.subjectId,
      checkpoint.generation == envelope.generation,
      checkpoint.sequence > envelope.sequence,
      checkpoint.durableCheckpoint,
      checkpoint.retentionClass == "durable",
    }),
  }

  pure def protocolStreamObservationContributesKnowledge(outcomeId: str, events: Set[ProtocolEnvelope]): bool =
    if (outcomeId == "OBS-Observed") events.forall(event => and {
      protocolEnvelopeShapeIsValid(event),
      protocolEnvelopeIsOrdered(event, events),
    }) else outcomeId == "OBS-ProvenAbsent" and events == Set()

  pure val claimEnvelope = {
    streamKindId: "STREAM-Claim", streamId: "claim:subject-work", subjectId: "subject-work",
    generation: 1, sequence: 1, eventId: "claim-event-1", predecessorEventId: "",
    payloadKindId: "PAYLOAD-Claim", retentionClass: "ephemeral", durableCheckpoint: false,
  }

  pure val leaseEnvelope = {
    ...claimEnvelope,
    sequence: 2, eventId: "lease-event-2", predecessorEventId: "claim-event-1", payloadKindId: "PAYLOAD-Lease",
  }

  pure val reviewCheckpointEnvelope = {
    streamKindId: "STREAM-Review", streamId: "review:subject-work", subjectId: "subject-work",
    generation: 1, sequence: 1, eventId: "review-event-1", predecessorEventId: "",
    payloadKindId: "PAYLOAD-Review", retentionClass: "durable", durableCheckpoint: true,
  }

  pure val operationLockEnvelope = {
    streamKindId: "STREAM-OperationLock", streamId: "operation-lock:receiver", subjectId: "subject-work",
    generation: 1, sequence: 1, eventId: "operation-lock-event-1", predecessorEventId: "",
    payloadKindId: "PAYLOAD-OperationLock", retentionClass: "ephemeral", durableCheckpoint: false,
  }

  pure val electionCheckpointEnvelope = {
    ...operationLockEnvelope,
    sequence: 2, eventId: "election-event-2", predecessorEventId: "operation-lock-event-1",
    payloadKindId: "PAYLOAD-Election", retentionClass: "durable", durableCheckpoint: true,
  }

  pure def mutationOutcomeIsTerminal(outcomeId: str): bool =
    mutationOutcomeCatalogue.exists(outcome => outcome.id == outcomeId and outcome.finality == "terminal")

  pure def mutationOutcomeIsUncertain(outcomeId: str): bool =
    mutationOutcomeCatalogue.exists(outcome => and {
      outcome.id == outcomeId,
      outcome.finality == "uncertain",
      outcome.effectClass == "unknown",
    })

  pure def mutationKindMatchesIntent(intent: MutationIntent): bool =
    mutationKindCatalogue.exists(mutationKind => and {
      mutationKind.id == intent.mutationKindId,
      mutationKind.targetKind == intent.targetKind,
      mutationKind.payloadKind == intent.payloadKind,
      if (mutationKind.revisionRequirement == "absent") intent.expectedRevision == 0
      else intent.expectedRevision > 0,
    })

  pure def mutationIntentShapeIsValid(intent: MutationIntent): bool = and {
    mutationKindMatchesIntent(intent),
    intent.operationId != "",
    intent.subjectId != "",
    intent.idempotencyKey != "",
    intent.payloadDigest != "",
    if (intent.mutationKindId == "MUT-Compensate") and {
      intent.compensatesOperationId != "",
      intent.compensatesOperationId != intent.operationId,
    } else and {
      intent.compensatesOperationId == "",
    },
  }

  pure def mutationResultOutcomeIsValid(result: MutationResult): bool = and {
    if (result.outcomeId == "MOUT-Applied" or result.outcomeId == "MOUT-Idempotent")
      result.resultingRevision > result.intent.expectedRevision
    else if (result.outcomeId == "MOUT-Rejected" or result.outcomeId == "MOUT-RevisionConflict")
      result.resultingRevision == result.intent.expectedRevision
    else mutationOutcomeIsUncertain(result.outcomeId) and result.resultingRevision == 0,
  }

  pure def mutationIntentsConflict(left: MutationIntent, right: MutationIntent): bool = and {
    left != right,
    left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey,
  }

  pure def mutationResultMayFollow(previous: MutationResult, current: MutationResult): bool = and {
    previous.intent == current.intent,
    mutationResultOutcomeIsValid(previous),
    mutationResultOutcomeIsValid(current),
    if (mutationOutcomeIsTerminal(previous.outcomeId))
      if (previous.outcomeId == "MOUT-Applied") and {
        current.outcomeId == "MOUT-Idempotent",
        current.resultingRevision == previous.resultingRevision,
      } else previous == current
    else mutationOutcomeIsUncertain(previous.outcomeId),
  }

  pure def compensationIntentIsValid(
    intent: MutationIntent,
    original: MutationResult,
    existingCompensations: Set[MutationIntent],
  ): bool = and {
    intent.mutationKindId == "MUT-Compensate",
    mutationIntentShapeIsValid(intent),
    mutationIntentShapeIsValid(original.intent),
    mutationResultOutcomeIsValid(original),
    original.intent.operationId == intent.compensatesOperationId,
    original.intent.subjectId == intent.subjectId,
    original.intent.mutationKindId != "MUT-Compensate",
    original.outcomeId == "MOUT-Applied",
    original.resultingRevision == intent.expectedRevision,
    not(existingCompensations.exists(existing => and {
      existing.compensatesOperationId == intent.compensatesOperationId,
      existing != intent,
    })),
  }

  pure def mutationOutcomeForRevision(expectedRevision: int, observedRevision: int): str =
    if (expectedRevision == observedRevision) "MOUT-Applied" else "MOUT-RevisionConflict"

  pure val createIntent = {
    operationId: "operation-create-1", subjectId: "subject-created", mutationKindId: "MUT-Create",
    targetKind: "subject", payloadKind: "create", expectedRevision: 0,
    idempotencyKey: "key-create-1", payloadDigest: "digest-create-1",
    compensatesOperationId: "",
  }

  pure val createAppliedResult = {
    intent: createIntent, outcomeId: "MOUT-Applied", resultingRevision: 1,
  }

  pure val compensateCreateIntent = {
    operationId: "operation-compensate-1", subjectId: createIntent.subjectId, mutationKindId: "MUT-Compensate",
    targetKind: "mutation", payloadKind: "compensation", expectedRevision: 1,
    idempotencyKey: "key-compensate-1", payloadDigest: "digest-compensate-1",
    compensatesOperationId: createIntent.operationId,
  }

  pure val closedLifecycleIntentCatalogue = and {
    lifecycleIntentCatalogue.map(intent => intent.id) == Set(
      "INTENT-Backlog", "INTENT-Ready", "INTENT-Paused", "INTENT-Cancelled"
    ),
    lifecycleIntentCatalogue.filter(intent => intent.terminal).map(intent => intent.id) == Set("INTENT-Cancelled"),
  }

  pure val closedNativeRelationKindCatalogue = and {
    nativeRelationKindCatalogue.map(relationKind => relationKind.id) == Set("REL-ParentChild", "REL-Blocks"),
    nativeRelationKindCatalogue.map(relationKind => relationKind.direction) == Set("parent-to-child", "blocker-to-blocked"),
  }

  pure val closedProtocolStreamCatalogues = and {
    protocolStreamKindCatalogue.map(streamKind => streamKind.id) == Set(
      "STREAM-Claim", "STREAM-OperationLock", "STREAM-Review", "STREAM-Delivery", "STREAM-OperationReceipt"
    ),
    protocolPayloadKindCatalogue.map(payload => payload.id) == Set(
      "PAYLOAD-Claim", "PAYLOAD-Lease", "PAYLOAD-TouchSet", "PAYLOAD-OperationLock", "PAYLOAD-Election",
      "PAYLOAD-Review", "PAYLOAD-Delivery", "PAYLOAD-OperationReceipt"
    ),
    protocolPayloadKindCatalogue.filter(payload => payload.retentionClass == "ephemeral").map(payload => payload.id) == Set(
      "PAYLOAD-Claim", "PAYLOAD-Lease", "PAYLOAD-TouchSet", "PAYLOAD-OperationLock"
    ),
    protocolPayloadKindCatalogue.filter(payload => payload.durableCheckpoint).map(payload => payload.id) == Set(
      "PAYLOAD-Election", "PAYLOAD-Review", "PAYLOAD-Delivery", "PAYLOAD-OperationReceipt"
    ),
  }

  pure val closedAuthorityCatalogue = and {
    authorityCatalogue.map(binding => binding.id) == Set(
      "AUTH-NativeGitHub", "AUTH-RepositoryRegistry", "AUTH-ProtocolStream", "AUTH-GitLedger",
      "AUTH-Actions", "AUTH-PackageFeed", "AUTH-ClassifiedExternal"
    ),
    authorityCatalogue.map(binding => binding.family) == Set(
      "native-github", "repository-registry", "protocol-stream", "git-ledger",
      "actions", "package-feed", "classified-external"
    ),
  }

  pure val closedObservationOutcomeCatalogue = and {
    observationOutcomeCatalogue.map(outcome => outcome.id) == Set(
      "OBS-Observed", "OBS-ProvenAbsent", "OBS-Contradictory", "OBS-Unreadable", "OBS-Unsupported",
      "OBS-Unauthorized", "OBS-Incomplete", "OBS-Stale", "OBS-RateLimited"
    ),
    observationOutcomeCatalogue.filter(outcome => outcome.knowledgeClass == "positive").map(outcome => outcome.id) == Set("OBS-Observed"),
    observationOutcomeCatalogue.filter(outcome => outcome.knowledgeClass == "negative").map(outcome => outcome.id) == Set("OBS-ProvenAbsent"),
    observationOutcomeCatalogue.filter(outcome => outcome.knowledgeClass == "contradictory").map(outcome => outcome.id) == Set("OBS-Contradictory"),
  }


  var evidenceObserved: bool
  var acceptedVocabulary: Set[str]
  var authorityObservation: AuthorityObservation
  var authorityObservationAvailable: bool
  var acceptedAuthorityObservations: Set[AuthorityObservation]
  var acceptedObservationKnowledge: Set[AuthorityObservation]
  var humanIntentId: str
  var authorizedHumanIntentId: str
  var lifecycleFacts: LifecycleFacts
  var lifecycleStatus: str
  var lifecycleStatusCurrent: bool
  var nativeRelationEdges: Set[NativeRelationEdge]
  var authorizedNativeRelationEdges: Set[NativeRelationEdge]
  var protocolStreamEvents: Set[ProtocolEnvelope]
  var authorizedDurableProtocolCheckpoints: Set[ProtocolEnvelope]

  action init = all {
    evidenceObserved' = false,
    acceptedVocabulary' = Set(),
    authorityObservation' = { ...nativeGitHubObservation, complete: false },
    authorityObservationAvailable' = false,
    acceptedAuthorityObservations' = Set(),
    acceptedObservationKnowledge' = Set(),
    humanIntentId' = "INTENT-Backlog",
    authorizedHumanIntentId' = "INTENT-Backlog",
    lifecycleFacts' = emptyLifecycleFacts,
    lifecycleStatus' = "backlog",
    lifecycleStatusCurrent' = true,
    nativeRelationEdges' = Set(),
    authorizedNativeRelationEdges' = Set(),
    protocolStreamEvents' = Set(),
    authorizedDurableProtocolCheckpoints' = Set(),
  }

  action observeProtocolEvidence: bool = all {
    evidenceObserved' = true,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action acceptVocabularyIdentity(vocabularyId: str): bool = all {
    vocabularyCatalogue.exists(entry => entry.id == vocabularyId),
    evidenceObserved,
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary.union(Set(vocabularyId)),
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action observeAuthority(observation: AuthorityObservation): bool = all {
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = observation,
    authorityObservationAvailable' = true,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action acceptObservedAuthority: bool = all {
    authorityObservationAvailable,
    authorityObservationIsQualified(authorityObservation),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations.union(Set(authorityObservation)),
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action acceptObservationKnowledge: bool = all {
    authorityObservationAvailable,
    observationContributesKnowledge(authorityObservation),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge.union(Set(authorityObservation)),
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action setHumanIntent(intentId: str): bool = all {
    lifecycleIntentExists(intentId),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = intentId,
    authorizedHumanIntentId' = intentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = false,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action observeLifecycleFacts(facts: LifecycleFacts): bool = all {
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = facts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = false,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action refreshLifecycleStatus: bool = all {
    lifecycleFactsAreKnowledge(lifecycleFacts),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = deriveLifecycleStatus(humanIntentId, lifecycleFacts),
    lifecycleStatusCurrent' = true,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action addNativeRelation(edge: NativeRelationEdge): bool = all {
    nativeRelationEdgeIsValid(edge),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges.union(Set(edge)),
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges.union(Set(edge)),
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action removeNativeRelation(edge: NativeRelationEdge): bool = all {
    nativeRelationEdgeIsValid(edge),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges.filter(existing => existing != edge),
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges.filter(existing => existing != edge),
    protocolStreamEvents' = protocolStreamEvents,
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action appendProtocolEnvelope(envelope: ProtocolEnvelope): bool = all {
    protocolAppendIsValid(envelope, protocolStreamEvents),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents.union(Set(envelope)),
    authorizedDurableProtocolCheckpoints' =
      if (envelope.durableCheckpoint) authorizedDurableProtocolCheckpoints.union(Set(envelope))
      else authorizedDurableProtocolCheckpoints,
  }

  action compactEphemeralProtocolEnvelope(envelope: ProtocolEnvelope): bool = all {
    protocolStreamEvents.contains(envelope),
    ephemeralEnvelopeMayBeCompacted(envelope, protocolStreamEvents),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
    acceptedObservationKnowledge' = acceptedObservationKnowledge,
    humanIntentId' = humanIntentId,
    authorizedHumanIntentId' = authorizedHumanIntentId,
    lifecycleFacts' = lifecycleFacts,
    lifecycleStatus' = lifecycleStatus,
    lifecycleStatusCurrent' = lifecycleStatusCurrent,
    nativeRelationEdges' = nativeRelationEdges,
    authorizedNativeRelationEdges' = authorizedNativeRelationEdges,
    protocolStreamEvents' = protocolStreamEvents.filter(existing => existing != envelope),
    authorizedDurableProtocolCheckpoints' = authorizedDurableProtocolCheckpoints,
  }

  action step = any {
    observeProtocolEvidence,
    acceptVocabularyIdentity("SubjectVocabulary"),
    observeAuthority(nativeGitHubObservation),
    acceptObservedAuthority,
    acceptObservationKnowledge,
    setHumanIntent("INTENT-Ready"),
    observeLifecycleFacts(claimedLifecycleFacts),
    refreshLifecycleStatus,
    addNativeRelation(parentChildEdge),
    addNativeRelation(blockingEdge),
    removeNativeRelation(parentChildEdge),
    appendProtocolEnvelope(claimEnvelope),
    appendProtocolEnvelope(leaseEnvelope),
    appendProtocolEnvelope(reviewCheckpointEnvelope),
    appendProtocolEnvelope(operationLockEnvelope),
    appendProtocolEnvelope(electionCheckpointEnvelope),
    compactEphemeralProtocolEnvelope(operationLockEnvelope),
  }

  val acceptedVocabularyIsQualified = acceptedVocabulary == Set() or evidenceObserved
  val acceptedAuthoritiesAreQualified = and {
    closedAuthorityCatalogue,
    closedObservationOutcomeCatalogue,
    acceptedAuthorityObservations.forall(authorityObservationIsQualified),
    acceptedObservationKnowledge.forall(observationContributesKnowledge),
  }
  val humanIntentIsObservationIndependent = and {
    closedLifecycleIntentCatalogue,
    lifecycleIntentExists(humanIntentId),
    humanIntentId == authorizedHumanIntentId,
  }
  val lifecycleStatusIsDerived =
    not(lifecycleStatusCurrent) or lifecycleStatus == deriveLifecycleStatus(humanIntentId, lifecycleFacts)
  val nativeRelationEdgesAreValid = and {
    closedNativeRelationKindCatalogue,
    nativeRelationEdges.forall(nativeRelationEdgeIsValid),
  }
  val relationChangesPreserveUnrelatedEdges = nativeRelationEdges == authorizedNativeRelationEdges
  val protocolEnvelopesAreValidAndOrdered = and {
    closedProtocolStreamCatalogues,
    protocolStreamEvents.forall(event => and {
      protocolEnvelopeShapeIsValid(event),
      protocolEnvelopeIsOrdered(event, protocolStreamEvents),
    }),
  }
  val durableProtocolCheckpointsArePreserved =
    protocolStreamEvents.filter(event => event.durableCheckpoint) == authorizedDurableProtocolCheckpoints
}
```

The executable witness records evidence before accepting the subject vocabulary identity. Removing
the evidence guard must make the invariant red in the bounded negative control.

The profile-2 compiler has a fixed 4,096-node typed graph ceiling. The executable suite therefore
uses compact, independently named witnesses for every durable-plan law, while the validator separately
inverts each material binding and preserves the earlier bounded invariants.

```quint-test
module CoordinationProtocolTests {
  import CoordinationProtocol.*

  // GS2-03.4 independent black-box oracles. These expectations are deliberately hand-authored
  // against public protocol behavior and invoke the canonical functions directly; they are not
  // generated from the compiled contract or from the implementation of those functions.
  run oracleClaimExclusion = and {
    mutationIntentsConflict(createIntent, { ...createIntent, operationId: "operation-rival" }),
    not(mutationIntentsConflict(createIntent, createIntent)),
  }

  run oracleStaleProjection = and {
    deriveLifecycleStatus("INTENT-Ready", claimedLifecycleFacts) == "claimed",
    deriveLifecycleStatus("INTENT-Ready", emptyLifecycleFacts) == "ready",
  }

  run oracleDependencyConcurrency = and {
    mutationOutcomeForRevision(12, 13) == "MOUT-RevisionConflict",
    mutationOutcomeForRevision(12, 12) == "MOUT-Applied",
  }

  run oraclePartialOperation = and {
    durablePlanDispositionFor(planCreateStep, planUncertainCheckpoint.receipt, Set()) == "PDISP-ReceiptReread",
    not(durablePlanMayAdvance(planUncertainCheckpoint)),
  }

  run oracleOldClientFencing = and {
    not(deterministicVersionsAreSupported({ ...supportedDeterministicVersions,
      sourceVersion: "fsgg.quint.literate-source/0" })),
    deterministicVersionsAreSupported(supportedDeterministicVersions),
  }

  run oracleLedgerTamper = and {
    not(retainedProtocolEnvelopeHasPredecessor(leaseEnvelope, Set(leaseEnvelope, reviewCheckpointEnvelope))),
    retainedProtocolEnvelopeHasPredecessor(leaseEnvelope, Set(claimEnvelope, leaseEnvelope)),
  }

  run oracleExactHeadReview = and {
    qualificationManifestIsBound(canonicalQualificationManifest),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      reviewCandidateSha: "candidate-stale" })),
  }

  run oraclePostMergeVerification = and {
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      resultCandidateSha: "merge-unverified" })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      resultsComplete: false })),
  }

  run oracleDualFeedRecovery = and {
    desiredStatePlanOutcomeFor(desiredReleases, desiredReleases) == "DSPLAN-NoChange",
    desiredStatePlanOutcomeFor(desiredReleases,
      { ...desiredReleases, contentDigest: "one-feed-only" }) == "DSPLAN-Ready",
    desiredStateMayApply(desiredReleases,
      { ...desiredReleases, contentDigest: "one-feed-only" }),
  }

  run oracleAbstractionEquivalence = and {
    behavioralIdentityIsEquivalent(canonicalDeterministicIdentity, canonicalDeterministicIdentity),
    not(behavioralIdentityIsEquivalent(canonicalDeterministicIdentity,
      { ...canonicalDeterministicIdentity, behavioralSha256: "abstract-drift" })),
  }

  run oracleScaleEnvelope = and {
    boundCatalogue.forall(bound => and { bound.minimum >= 0, bound.maximum >= bound.minimum }),
    boundCatalogue.map(bound => bound.id).size() == 11,
  }

  type DeterministicVersionTuple = {
    sourceVersion: str, extractorVersion: str, quintVersion: str,
    profileVersion: str, schemaVersion: str,
  }

  type DeterministicIdentity = {
    sourceSha256: str, behavioralSha256: str, contractSha256: str,
    normalizationAuthority: str, versions: DeterministicVersionTuple,
    semanticDiffRows: List[str], supported: bool, complete: bool, fresh: bool,
  }

  pure val supportedDeterministicVersions = {
    sourceVersion: "fsgg.quint.literate-source/1",
    extractorVersion: "quint-specification-v1@FS.GG.SDD.Artifacts/1.5.0",
    quintVersion: "sha256:939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f",
    profileVersion: "fsgg-quint-profile/2",
    schemaVersion: "fsgg.quint.compiled-contract/v2",
  }

  pure def deterministicVersionsAreSupported(versions: DeterministicVersionTuple): bool =
    versions == supportedDeterministicVersions

  pure def deterministicIdentityIsQualified(identity: DeterministicIdentity): bool = and {
    identity.sourceSha256 != "", identity.behavioralSha256 != "", identity.contractSha256 != "",
    identity.normalizationAuthority == "typed-effect-json",
    deterministicVersionsAreSupported(identity.versions), identity.supported, identity.complete, identity.fresh,
  }

  pure def behavioralIdentityIsEquivalent(left: DeterministicIdentity, right: DeterministicIdentity): bool = and {
    deterministicIdentityIsQualified(left), deterministicIdentityIsQualified(right),
    left.behavioralSha256 == right.behavioralSha256,
    left.contractSha256 == right.contractSha256,
    left.versions == right.versions,
    left.semanticDiffRows == right.semanticDiffRows,
  }

  pure val canonicalDeterministicIdentity = {
    sourceSha256: "source-canonical", behavioralSha256: "behavior-canonical",
    contractSha256: "contract-canonical", normalizationAuthority: "typed-effect-json",
    versions: supportedDeterministicVersions,
    semanticDiffRows: List("1:/catalogue/0:row-a", "2:/properties/0:row-b"),
    supported: true, complete: true, fresh: true,
  }

  type QualificationInputEntry = {
    id: str, candidateSha: str, producer: str, digest: str, fresh: bool,
  }

  type QualificationManifest = {
    schema: str, candidateSha: str, inputSetSha256: str, candidateProducer: str,
    inputEntries: Set[QualificationInputEntry], generatedProducers: Set[str],
    independentProducers: Set[str], resultProducers: Set[str], reviewerPrincipals: Set[str],
    resultCandidateSha: str, resultInputSetSha256: str,
    reviewCandidateSha: str, reviewInputSetSha256: str,
    environmentClosed: bool, resultsComplete: bool, reviewsComplete: bool,
  }

  pure def qualificationManifestIsBound(manifest: QualificationManifest): bool = and {
    manifest.schema == "fsgg.coordination.qualification-manifest/1",
    manifest.candidateSha != "", manifest.inputSetSha256 != "",
    manifest.inputEntries.size() > 0,
    manifest.inputEntries.forall(entry => and {
      entry.id != "", entry.digest != "", entry.fresh,
      entry.candidateSha == manifest.candidateSha,
    }),
    manifest.resultCandidateSha == manifest.candidateSha,
    manifest.reviewCandidateSha == manifest.candidateSha,
    manifest.resultInputSetSha256 == manifest.inputSetSha256,
    manifest.reviewInputSetSha256 == manifest.inputSetSha256,
    manifest.independentProducers.size() > 0,
    manifest.reviewerPrincipals.size() > 0,
    manifest.independentProducers.forall(principal => and {
      principal != manifest.candidateProducer,
      not(manifest.generatedProducers.contains(principal)),
    }),
    manifest.reviewerPrincipals.forall(principal => and {
      principal != manifest.candidateProducer,
      not(manifest.resultProducers.contains(principal)),
    }),
    manifest.environmentClosed, manifest.resultsComplete, manifest.reviewsComplete,
  }

  pure val canonicalQualificationManifest = {
    schema: "fsgg.coordination.qualification-manifest/1",
    candidateSha: "candidate-a", inputSetSha256: "inputs-a", candidateProducer: "candidate-builder",
    inputEntries: Set(
      { id: "source", candidateSha: "candidate-a", producer: "candidate-builder", digest: "source-digest", fresh: true },
      { id: "generated", candidateSha: "candidate-a", producer: "case-generator", digest: "generated-digest", fresh: true },
      { id: "independent", candidateSha: "candidate-a", producer: "oracle-author", digest: "oracle-digest", fresh: true }
    ),
    generatedProducers: Set("case-generator"), independentProducers: Set("oracle-author"),
    resultProducers: Set("gate-runner"), reviewerPrincipals: Set("independent-critic"),
    resultCandidateSha: "candidate-a", resultInputSetSha256: "inputs-a",
    reviewCandidateSha: "candidate-a", reviewInputSetSha256: "inputs-a",
    environmentClosed: true, resultsComplete: true, reviewsComplete: true,
  }

  type CompiledOutputFamily = {
    id: str, ordinal: int, contentContract: str, formats: Set[str],
  }

  pure val compiledOutputFamilyCatalogue = Set(
    { id: "COUT-Schemas", ordinal: 1,
      contentContract: "authority|observation|lifecycle|relation|stream|mutation|durable-plan|desired-state|compiled-output",
      formats: Set("json-schema") },
    { id: "COUT-CommandMetadata", ordinal: 2, contentContract: "inspect|plan|apply-intent|verify", formats: Set("json") },
    { id: "COUT-PermissionCensus", ordinal: 3,
      contentContract: "organization-administration|repository-administration|project-administration|actions-administration|release-administration|security-administration",
      formats: Set("json") },
    { id: "COUT-MutationCensus", ordinal: 4,
      contentContract: "create|append|add-edge|remove-edge|set|clear|transition|compensate|applied|idempotent|rejected|revision-conflict|rate-limited|unavailable|timed-out|incomplete",
      formats: Set("json") },
    { id: "COUT-SettingsPlans", ordinal: 5,
      contentContract: "issue-schema|repository-properties|projects|repository-profile|workflow-pins|releases|permissions|security-supply-chain|inspect|plan|apply|verify",
      formats: Set("json") },
    { id: "COUT-ProjectionViews", ordinal: 6,
      contentContract: "catalogue|relationships|actions|verification|bounds|compatibility",
      formats: Set("markdown", "json") },
    { id: "COUT-SemanticDiff", ordinal: 7,
      contentContract: "family|identity|content|support|completeness|freshness|order", formats: Set("json") },
    { id: "COUT-Diagrams", ordinal: 8,
      contentContract: "authority-map|phase-flow|mutation-flow|stream-flow", formats: Set("mermaid") },
    { id: "COUT-ModelTestInventory", ordinal: 9,
      contentContract: "invariant|witness|negative-control|bounded-verification", formats: Set("json") }
  )

  pure val closedCompiledOutputFamilyCatalogue = and {
    compiledOutputFamilyCatalogue.map(family => family.id) == Set(
      "COUT-Schemas", "COUT-CommandMetadata", "COUT-PermissionCensus", "COUT-MutationCensus",
      "COUT-SettingsPlans", "COUT-ProjectionViews", "COUT-SemanticDiff", "COUT-Diagrams",
      "COUT-ModelTestInventory"
    ),
    compiledOutputFamilyCatalogue.map(family => family.ordinal) == Set(1, 2, 3, 4, 5, 6, 7, 8, 9),
    compiledOutputFamilyCatalogue.filter(family => family.id == "COUT-ProjectionViews")
      .forall(family => family.formats == Set("markdown", "json")),
  }

  type CompiledOutput = {
    familyId: str, ordinal: int, sourceIdentity: str, profileIdentity: str,
    contractIdentity: str, contentContract: str, formats: Set[str], contentDigest: str,
    supported: bool, complete: bool, fresh: bool,
  }

  type DesiredStateFact = {
    authorityId: str, authorityRevision: str, subjectId: str, profileId: str,
    familyId: str, targetKind: str, surfaceIds: Set[str], contentDigest: str, requiredPermission: str,
    outcomeId: str, complete: bool, supported: bool, permissionGranted: bool,
  }

  type DesiredStatePhaseAuthority = {
    subjectId: str, profileId: str, familyId: str, desiredContentDigest: str,
    authorityRevision: str, planOutcomeId: str, applyReceiptOutcomeId: str,
  }

  pure def compiledOutput(
    familyId: str, ordinal: int, contentContract: str, formats: Set[str], contentDigest: str
  ): CompiledOutput = {
    familyId: familyId, ordinal: ordinal, sourceIdentity: "literate-quint-authority",
    profileIdentity: "fsgg-quint-profile/2", contractIdentity: "compiled-output-contract/v1",
    contentContract: contentContract, formats: formats, contentDigest: contentDigest,
    supported: true, complete: true, fresh: true,
  }

  pure val completeCompiledOutputs = Set(
    compiledOutput("COUT-Schemas", 1,
      "authority|observation|lifecycle|relation|stream|mutation|durable-plan|desired-state|compiled-output",
      Set("json-schema"), "digest-schemas"),
    compiledOutput("COUT-CommandMetadata", 2, "inspect|plan|apply-intent|verify", Set("json"), "digest-command-metadata"),
    compiledOutput("COUT-PermissionCensus", 3,
      "organization-administration|repository-administration|project-administration|actions-administration|release-administration|security-administration",
      Set("json"), "digest-permission-census"),
    compiledOutput("COUT-MutationCensus", 4,
      "create|append|add-edge|remove-edge|set|clear|transition|compensate|applied|idempotent|rejected|revision-conflict|rate-limited|unavailable|timed-out|incomplete",
      Set("json"), "digest-mutation-census"),
    compiledOutput("COUT-SettingsPlans", 5,
      "issue-schema|repository-properties|projects|repository-profile|workflow-pins|releases|permissions|security-supply-chain|inspect|plan|apply|verify",
      Set("json"), "digest-settings-plans"),
    compiledOutput("COUT-ProjectionViews", 6,
      "catalogue|relationships|actions|verification|bounds|compatibility",
      Set("markdown", "json"), "digest-projection-views"),
    compiledOutput("COUT-SemanticDiff", 7, "family|identity|content|support|completeness|freshness|order",
      Set("json"), "digest-semantic-diff"),
    compiledOutput("COUT-Diagrams", 8, "authority-map|phase-flow|mutation-flow|stream-flow",
      Set("mermaid"), "digest-diagrams"),
    compiledOutput("COUT-ModelTestInventory", 9, "invariant|witness|negative-control|bounded-verification",
      Set("json"), "digest-model-test-inventory")
  )

  pure def compiledOutputShapeIsQualified(output: CompiledOutput): bool = and {
    output.sourceIdentity == "literate-quint-authority",
    output.profileIdentity == "fsgg-quint-profile/2",
    output.contractIdentity == "compiled-output-contract/v1",
    output.contentDigest != "", output.supported, output.complete, output.fresh,
    compiledOutputFamilyCatalogue.exists(family => and {
      family.id == output.familyId, family.ordinal == output.ordinal,
      family.contentContract == output.contentContract, family.formats == output.formats,
    }),
  }

  pure def compiledOutputSetIsQualified(outputs: Set[CompiledOutput]): bool = and {
    closedCompiledOutputFamilyCatalogue,
    outputs.size() == compiledOutputFamilyCatalogue.size(),
    outputs.map(output => output.familyId) == compiledOutputFamilyCatalogue.map(family => family.id),
    outputs.map(output => output.ordinal) == Set(1, 2, 3, 4, 5, 6, 7, 8, 9),
    outputs.forall(compiledOutputShapeIsQualified),
  }

  pure val desiredStateFamilyCatalogue = Set(
    { id: "DSTATE-IssueSchema", targetKind: "organization-issue-schema", requiredPermission: "organization-administration",
      surfaceIds: Set("issue-type", "issue-field", "field-type", "allowed-value") },
    { id: "DSTATE-RepositoryProperties", targetKind: "repository-properties", requiredPermission: "repository-administration",
      surfaceIds: Set("property-schema", "property-value") },
    { id: "DSTATE-Projects", targetKind: "organization-project", requiredPermission: "project-administration",
      surfaceIds: Set("project-field", "project-view", "project-workflow", "project-visibility", "project-membership-policy") },
    { id: "DSTATE-RepositoryProfile", targetKind: "repository-profile", requiredPermission: "repository-administration",
      surfaceIds: Set("ruleset", "merge-queue", "merge-policy", "actions-policy", "branch-deletion-policy") },
    { id: "DSTATE-WorkflowPins", targetKind: "workflow-policy", requiredPermission: "actions-administration",
      surfaceIds: Set("reusable-workflow-pin", "action-pin") },
    { id: "DSTATE-Releases", targetKind: "release-policy", requiredPermission: "release-administration",
      surfaceIds: Set("release-environment", "immutable-release", "tag-protection", "trusted-publisher") },
    { id: "DSTATE-Permissions", targetKind: "permission-policy", requiredPermission: "organization-administration",
      surfaceIds: Set("repository-visibility", "team-access", "workflow-permission", "environment-protection") },
    { id: "DSTATE-SecuritySupplyChain", targetKind: "security-supply-chain", requiredPermission: "security-administration",
      surfaceIds: Set("vulnerability-policy", "secret-policy", "dependency-policy", "sbom-policy", "attestation-policy") }
  )
  pure val desiredStatePhaseCatalogue = Set(
    { id: "DSPH-Inspect" }, { id: "DSPH-Plan" }, { id: "DSPH-Apply" }, { id: "DSPH-Verify" }
  )
  pure val desiredStatePlanOutcomeCatalogue = Set(
    { id: "DSPLAN-Ready" }, { id: "DSPLAN-NoChange" }, { id: "DSPLAN-Unsupported" },
    { id: "DSPLAN-Unauthorized" }, { id: "DSPLAN-Incomplete" }, { id: "DSPLAN-Stale" },
    { id: "DSPLAN-IdentityMismatch" }
  )

  pure def desiredStateFactShapeIsValid(fact: DesiredStateFact): bool = and {
    fact.subjectId != "", fact.profileId != "", fact.contentDigest != "",
    authorityCatalogue.exists(authority => and {
      authority.id == fact.authorityId, authority.revisionValue == fact.authorityRevision,
    }),
    observationOutcomeCatalogue.exists(outcome => outcome.id == fact.outcomeId),
    desiredStateFamilyCatalogue.exists(family => and {
      family.id == fact.familyId, family.targetKind == fact.targetKind,
      family.requiredPermission == fact.requiredPermission, family.surfaceIds == fact.surfaceIds,
    }),
  }

  pure def desiredStateIdentityMatches(desired: DesiredStateFact, observed: DesiredStateFact): bool = and {
    desired.authorityId == observed.authorityId,
    desired.authorityRevision == observed.authorityRevision,
    desired.subjectId == observed.subjectId, desired.profileId == observed.profileId,
    desired.familyId == observed.familyId, desired.targetKind == observed.targetKind,
    desired.surfaceIds == observed.surfaceIds, desired.requiredPermission == observed.requiredPermission,
  }

  pure def desiredStatePlanOutcomeFor(desired: DesiredStateFact, observed: DesiredStateFact): str =
    if (not(desiredStateFactShapeIsValid(desired)) or not(desiredStateFactShapeIsValid(observed)))
      "DSPLAN-IdentityMismatch"
    else if (not(desiredStateIdentityMatches(desired, observed))) "DSPLAN-IdentityMismatch"
    else if (not(observed.supported) or observed.outcomeId == "OBS-Unsupported") "DSPLAN-Unsupported"
    else if (not(observed.permissionGranted) or observed.outcomeId == "OBS-Unauthorized") "DSPLAN-Unauthorized"
    else if (not(observed.complete) or observed.outcomeId == "OBS-Incomplete") "DSPLAN-Incomplete"
    else if (observed.outcomeId == "OBS-Stale") "DSPLAN-Stale"
    else if (observed.outcomeId == "OBS-ProvenAbsent") "DSPLAN-Ready"
    else if (observed.outcomeId == "OBS-Observed")
      if (desired.contentDigest == observed.contentDigest) "DSPLAN-NoChange" else "DSPLAN-Ready"
    else "DSPLAN-Incomplete"

  pure def desiredStateMayApply(desired: DesiredStateFact, observed: DesiredStateFact): bool = and {
    desired.complete, desired.supported, desired.permissionGranted,
    desired.outcomeId == "OBS-Observed",
    desiredStatePlanOutcomeFor(desired, observed) == "DSPLAN-Ready",
  }

  pure def desiredStatePhaseAuthorityMatches(
    authority: DesiredStatePhaseAuthority, desired: DesiredStateFact, observed: DesiredStateFact
  ): bool = and {
    authority.subjectId == desired.subjectId, authority.subjectId == observed.subjectId,
    authority.profileId == desired.profileId, authority.profileId == observed.profileId,
    authority.familyId == desired.familyId, authority.familyId == observed.familyId,
    authority.desiredContentDigest == desired.contentDigest,
    authority.authorityRevision == desired.authorityRevision,
    authority.authorityRevision == observed.authorityRevision,
  }

  pure def desiredStatePhaseMayAdvance(
    fromPhaseId: str, toPhaseId: str, authority: DesiredStatePhaseAuthority,
    desired: DesiredStateFact, observed: DesiredStateFact
  ): bool = or {
    and {
      fromPhaseId == "DSPH-Inspect", toPhaseId == "DSPH-Plan",
      desiredStatePhaseAuthorityMatches(authority, desired, observed),
      desiredStateFactShapeIsValid(desired), desiredStateFactShapeIsValid(observed),
      desiredStateIdentityMatches(desired, observed), observed.complete, observed.supported,
      observed.permissionGranted, Set("OBS-Observed", "OBS-ProvenAbsent").contains(observed.outcomeId),
    },
    and {
      fromPhaseId == "DSPH-Plan", toPhaseId == "DSPH-Apply",
      desiredStatePhaseAuthorityMatches(authority, desired, observed),
      authority.planOutcomeId == desiredStatePlanOutcomeFor(desired, observed),
      desiredStateMayApply(desired, observed),
    },
    and {
      fromPhaseId == "DSPH-Plan", toPhaseId == "DSPH-Verify",
      desiredStatePhaseAuthorityMatches(authority, desired, observed),
      authority.planOutcomeId == "DSPLAN-NoChange",
      authority.planOutcomeId == desiredStatePlanOutcomeFor(desired, observed),
      desiredStateIsVerified(desired, observed),
    },
    and {
      fromPhaseId == "DSPH-Apply", toPhaseId == "DSPH-Verify",
      desiredStatePhaseAuthorityMatches(authority, desired, observed),
      authority.planOutcomeId == "DSPLAN-Ready",
      Set("MOUT-Applied", "MOUT-Idempotent").contains(authority.applyReceiptOutcomeId),
      desiredStateIsVerified(desired, observed),
    },
  }

  pure def desiredStateIsVerified(desired: DesiredStateFact, observed: DesiredStateFact): bool = and {
    desiredStateFactShapeIsValid(desired), desiredStateFactShapeIsValid(observed),
    desired.complete, desired.supported, desired.permissionGranted, desired.outcomeId == "OBS-Observed",
    desiredStateIdentityMatches(desired, observed), observed.complete, observed.supported,
    observed.permissionGranted, observed.outcomeId == "OBS-Observed",
    desired.contentDigest == observed.contentDigest,
  }

  pure def desiredStateSpecificationIsComplete(facts: Set[DesiredStateFact]): bool = and {
    facts.map(fact => fact.familyId) == desiredStateFamilyCatalogue.map(family => family.id),
    facts.forall(fact => and {
      desiredStateFactShapeIsValid(fact), fact.complete, fact.supported, fact.permissionGranted,
      fact.outcomeId == "OBS-Observed",
      facts.forall(peer => and {
        peer.authorityId == fact.authorityId, peer.authorityRevision == fact.authorityRevision,
        peer.subjectId == fact.subjectId, peer.profileId == fact.profileId,
        if (peer.familyId == fact.familyId) peer == fact else true,
      }),
    }),
  }

  pure val closedDesiredStateCatalogues = and {
    desiredStateFamilyCatalogue.map(family => family.id) == Set(
      "DSTATE-IssueSchema", "DSTATE-RepositoryProperties", "DSTATE-Projects", "DSTATE-RepositoryProfile",
      "DSTATE-WorkflowPins", "DSTATE-Releases", "DSTATE-Permissions", "DSTATE-SecuritySupplyChain"
    ),
    desiredStateFamilyCatalogue.exists(family => and {
      family.id == "DSTATE-IssueSchema",
      family.surfaceIds == Set("issue-type", "issue-field", "field-type", "allowed-value"),
    }),
    desiredStateFamilyCatalogue.exists(family => and {
      family.id == "DSTATE-Projects",
      family.surfaceIds == Set("project-field", "project-view", "project-workflow", "project-visibility", "project-membership-policy"),
    }),
    desiredStateFamilyCatalogue.exists(family => and {
      family.id == "DSTATE-RepositoryProfile",
      family.surfaceIds == Set("ruleset", "merge-queue", "merge-policy", "actions-policy", "branch-deletion-policy"),
    }),
    desiredStateFamilyCatalogue.exists(family => and {
      family.id == "DSTATE-SecuritySupplyChain",
      family.surfaceIds == Set("vulnerability-policy", "secret-policy", "dependency-policy", "sbom-policy", "attestation-policy"),
    }),
    desiredStatePhaseCatalogue.map(phase => phase.id) == Set(
      "DSPH-Inspect", "DSPH-Plan", "DSPH-Apply", "DSPH-Verify"
    ),
    desiredStatePlanOutcomeCatalogue.map(outcome => outcome.id) == Set(
      "DSPLAN-Ready", "DSPLAN-NoChange", "DSPLAN-Unsupported", "DSPLAN-Unauthorized",
      "DSPLAN-Incomplete", "DSPLAN-Stale", "DSPLAN-IdentityMismatch"
    ),
  }

  type DurablePlanCheckpoint = {
    step: DurablePlanStep,
    receipt: MutationResult,
    receiptReadId: str,
    dispositionId: str,
  }

  type DurableAppliedStep = {
    step: DurablePlanStep,
    receipt: MutationResult,
  }

  pure def durableAppliedBoundaryHistoryIsValid(
    current: DurablePlanStep, appliedHistory: Set[DurableAppliedStep],
  ): bool = appliedHistory.forall(applied => and {
    durablePlanStepShapeIsValid(applied.step), mutationResultOutcomeIsValid(applied.receipt),
    applied.receipt.intent == applied.step.intent,
    applied.receipt.outcomeId == "MOUT-Applied" or applied.receipt.outcomeId == "MOUT-Idempotent",
    applied.step.planId == current.planId, applied.step.correlationId == current.correlationId,
    applied.step.compensationBoundaryId == current.compensationBoundaryId,
    applied.step.sequence < current.sequence,
  })

  pure def durablePlanDispositionFor(
    step: DurablePlanStep, receipt: MutationResult, appliedHistory: Set[DurableAppliedStep],
  ): str =
    if (receipt.outcomeId == "MOUT-Applied" or receipt.outcomeId == "MOUT-Idempotent") "PDISP-Advance"
    else if (mutationOutcomeIsUncertain(receipt.outcomeId)) "PDISP-ReceiptReread"
    else if (receipt.outcomeId == "MOUT-Rejected" or receipt.outcomeId == "MOUT-RevisionConflict")
      if (durableAppliedBoundaryHistoryIsValid(step, appliedHistory) and appliedHistory.size() > 0)
        "PDISP-Compensate"
      else "PDISP-Replan"
    else ""

  pure def durablePlanStepShapeIsValid(step: DurablePlanStep): bool = and {
    step.planId != "", step.stepId != "", step.sequence > 0, step.causationId != "",
    step.correlationId != "", step.compensationBoundaryId != "", mutationIntentShapeIsValid(step.intent),
    if (step.sequence == 1) step.predecessorStepId == "" else step.predecessorStepId != "",
  }

  pure def durablePlanStepMayFollow(previous: DurablePlanStep, current: DurablePlanStep): bool = and {
    durablePlanStepShapeIsValid(previous), durablePlanStepShapeIsValid(current),
    previous.planId == current.planId, previous.correlationId == current.correlationId,
    current.sequence == previous.sequence + 1, current.predecessorStepId == previous.stepId,
    current.causationId == previous.intent.operationId, current.stepId != previous.stepId,
    current.intent.operationId != previous.intent.operationId,
  }

  pure def durablePlanCheckpointIsBound(
    checkpoint: DurablePlanCheckpoint, appliedHistory: Set[DurableAppliedStep],
  ): bool = and {
    durablePlanStepShapeIsValid(checkpoint.step), checkpoint.receiptReadId != "",
    checkpoint.receipt.intent == checkpoint.step.intent, mutationResultOutcomeIsValid(checkpoint.receipt),
    durableAppliedBoundaryHistoryIsValid(checkpoint.step, appliedHistory),
    checkpoint.dispositionId == durablePlanDispositionFor(checkpoint.step, checkpoint.receipt, appliedHistory),
    durablePlanDispositionCatalogue.exists(disposition => disposition.id == checkpoint.dispositionId),
  }

  pure def durablePlanMayAdvance(checkpoint: DurablePlanCheckpoint): bool = and {
    durablePlanCheckpointIsBound(checkpoint, Set()), checkpoint.dispositionId == "PDISP-Advance",
  }

  pure def durablePlanCompensationIsValid(
    compensation: DurablePlanStep, original: DurablePlanStep, originalReceipt: MutationResult,
    appliedInBoundary: Set[DurablePlanStep], existingCompensations: Set[MutationIntent],
  ): bool = and {
    durablePlanStepMayFollow(original, compensation),
    compensation.compensationBoundaryId == original.compensationBoundaryId,
    originalReceipt.intent == original.intent,
    originalReceipt.outcomeId == "MOUT-Applied",
    compensationIntentIsValid(compensation.intent, originalReceipt, existingCompensations),
    appliedInBoundary.contains(original),
    appliedInBoundary.forall(applied => and {
      applied.planId == original.planId, applied.compensationBoundaryId == original.compensationBoundaryId,
      applied.sequence <= original.sequence,
    }),
  }

  pure val appendIntent = {
    operationId: "operation-append-2", subjectId: "subject-stream", mutationKindId: "MUT-Append",
    targetKind: "stream", payloadKind: "append", expectedRevision: 1,
    idempotencyKey: "key-append-2", payloadDigest: "digest-append-2", compensatesOperationId: "",
  }
  pure val planCreateStep = {
    planId: "plan-1", stepId: "step-create-1", predecessorStepId: "", sequence: 1,
    causationId: "decision-1", correlationId: "correlation-1", compensationBoundaryId: "boundary-1",
    intent: createIntent,
  }
  pure val planAppendStep = {
    planId: planCreateStep.planId, stepId: "step-append-2", predecessorStepId: planCreateStep.stepId, sequence: 2,
    causationId: createIntent.operationId, correlationId: planCreateStep.correlationId,
    compensationBoundaryId: planCreateStep.compensationBoundaryId, intent: appendIntent,
  }
  pure val planCompensationStep = {
    planId: planCreateStep.planId, stepId: "step-compensate-2", predecessorStepId: planCreateStep.stepId, sequence: 2,
    causationId: createIntent.operationId, correlationId: planCreateStep.correlationId,
    compensationBoundaryId: planCreateStep.compensationBoundaryId, intent: compensateCreateIntent,
  }
  pure val planCreateCheckpoint = {
    step: planCreateStep, receipt: createAppliedResult,
    receiptReadId: "receipt-read-create-1", dispositionId: "PDISP-Advance",
  }
  pure val planUncertainCheckpoint = {
    step: planCreateStep,
    receipt: { intent: createIntent, outcomeId: "MOUT-RateLimited", resultingRevision: 0 },
    receiptReadId: "receipt-read-create-uncertain", dispositionId: "PDISP-ReceiptReread",
  }
  pure val planCreateAppliedHistory = Set({ step: planCreateStep, receipt: createAppliedResult })

  pure def desiredFact(
    familyId: str, targetKind: str, surfaces: Set[str], permission: str, digest: str
  ): DesiredStateFact = {
    authorityId: "AUTH-NativeGitHub", authorityRevision: "node-id-and-updated-at",
    subjectId: "FS-GG/example", profileId: "repository-profile-v2", familyId: familyId,
    targetKind: targetKind, surfaceIds: surfaces, contentDigest: digest, requiredPermission: permission,
    outcomeId: "OBS-Observed", complete: true, supported: true, permissionGranted: true,
  }

  pure def desiredPhaseAuthority(
    desired: DesiredStateFact, planOutcomeId: str, applyReceiptOutcomeId: str
  ): DesiredStatePhaseAuthority = {
    subjectId: desired.subjectId, profileId: desired.profileId, familyId: desired.familyId,
    desiredContentDigest: desired.contentDigest, authorityRevision: desired.authorityRevision,
    planOutcomeId: planOutcomeId, applyReceiptOutcomeId: applyReceiptOutcomeId,
  }

  pure val desiredIssueSchema = desiredFact(
    "DSTATE-IssueSchema", "organization-issue-schema",
    Set("issue-type", "issue-field", "field-type", "allowed-value"),
    "organization-administration", "digest-issue-schema"
  )
  pure val desiredRepositoryProperties = desiredFact(
    "DSTATE-RepositoryProperties", "repository-properties", Set("property-schema", "property-value"),
    "repository-administration", "digest-properties"
  )
  pure val desiredProjects = desiredFact(
    "DSTATE-Projects", "organization-project",
    Set("project-field", "project-view", "project-workflow", "project-visibility", "project-membership-policy"),
    "project-administration", "digest-projects"
  )
  pure val desiredRepositoryProfile = desiredFact(
    "DSTATE-RepositoryProfile", "repository-profile",
    Set("ruleset", "merge-queue", "merge-policy", "actions-policy", "branch-deletion-policy"),
    "repository-administration", "digest-repository-profile"
  )
  pure val desiredWorkflowPins = desiredFact(
    "DSTATE-WorkflowPins", "workflow-policy", Set("reusable-workflow-pin", "action-pin"),
    "actions-administration", "digest-workflow-pins"
  )
  pure val desiredReleases = desiredFact(
    "DSTATE-Releases", "release-policy",
    Set("release-environment", "immutable-release", "tag-protection", "trusted-publisher"),
    "release-administration", "digest-releases"
  )
  pure val desiredPermissions = desiredFact(
    "DSTATE-Permissions", "permission-policy",
    Set("repository-visibility", "team-access", "workflow-permission", "environment-protection"),
    "organization-administration", "digest-permissions"
  )
  pure val desiredSecuritySupplyChain = desiredFact(
    "DSTATE-SecuritySupplyChain", "security-supply-chain",
    Set("vulnerability-policy", "secret-policy", "dependency-policy", "sbom-policy", "attestation-policy"),
    "security-administration", "digest-security"
  )
  pure val completeDesiredState = Set(
    desiredIssueSchema, desiredRepositoryProperties, desiredProjects, desiredRepositoryProfile,
    desiredWorkflowPins, desiredReleases, desiredPermissions, desiredSecuritySupplyChain
  )

  pure val closedDurablePlanDispositionCatalogue = and {
    durablePlanDispositionCatalogue.map(disposition => disposition.id) == Set(
      "PDISP-Advance", "PDISP-ReceiptReread", "PDISP-Replan", "PDISP-Compensate"
    ),
    durablePlanDispositionCatalogue.map(disposition => disposition.nextAction) == Set(
      "next-step", "reread-receipt", "compile-new-plan", "compensate-reverse"
    ),
  }

  run testUnrelatedCheckpointCannotCompactEphemeralHistory =
    not(ephemeralEnvelopeMayBeCompacted(claimEnvelope, Set(claimEnvelope, reviewCheckpointEnvelope)))

  run testUnrelatedCheckpointCannotExcuseMissingPredecessor =
    not(retainedProtocolEnvelopeHasPredecessor(leaseEnvelope, Set(leaseEnvelope, reviewCheckpointEnvelope)))

  run testMutationExactReplayIsIdempotent = and {
    mutationResultMayFollow(createAppliedResult, { ...createAppliedResult, outcomeId: "MOUT-Idempotent" }),
  }

  run testMutationKindsBindPayloadAndTarget = and {
    not(mutationIntentShapeIsValid({ ...createIntent, targetKind: "stream" })),
    not(mutationIntentShapeIsValid({ ...createIntent, payloadKind: "append" })),
    mutationKindCatalogue.exists(kind => and {
      kind.id == "MUT-RemoveEdge", kind.payloadKind == "edge",
    }),
  }

  run testMutationUncertainOutcomesRemainUnknown =
    mutationOutcomeCatalogue.exists(outcome => and {
      outcome.id == "MOUT-RateLimited", outcome.finality == "uncertain", outcome.effectClass == "unknown",
    })

  run testMutationIdempotencyBindingsRejectSubstitution = and {
    mutationIntentsConflict(createIntent, {
      ...createIntent, operationId: "operation-other", mutationKindId: "MUT-Append",
      targetKind: "stream", payloadKind: "append", expectedRevision: 1,
    }),
    mutationIntentsConflict(createIntent, { ...createIntent, idempotencyKey: "key-other" }),
  }

  run testMutationStaleRevisionIsConflict =
    mutationOutcomeForRevision(4, 5) == "MOUT-RevisionConflict"

  run testMutationCompensationBoundary = and {
    compensationIntentIsValid(compensateCreateIntent, createAppliedResult, Set()),
    not(compensationIntentIsValid(
      compensateCreateIntent,
      { ...createAppliedResult, outcomeId: "MOUT-Idempotent" },
      Set(),
    )),
    not(compensationIntentIsValid(
      compensateCreateIntent,
      { ...createAppliedResult, intent: { ...createIntent, mutationKindId: "MUT-Unknown" } },
      Set(),
    )),
    not(compensationIntentIsValid(
      { ...compensateCreateIntent, operationId: "operation-compensate-2", idempotencyKey: "key-compensate-2" },
      createAppliedResult,
      Set(compensateCreateIntent),
    )),
  }

  run testDurablePlanOrderingBindsPredecessorAndIdentity = and {
    closedDurablePlanDispositionCatalogue,
    durablePlanStepMayFollow(planCreateStep, planAppendStep),
    not(durablePlanStepMayFollow(planCreateStep, { ...planAppendStep, predecessorStepId: "step-other" })),
    not(durablePlanStepMayFollow(planCreateStep, { ...planAppendStep, causationId: "operation-other" })),
    not(durablePlanStepMayFollow(planCreateStep, { ...planAppendStep, correlationId: "correlation-other" })),
  }

  run testDurablePlanExactReceiptIsRequiredToAdvance = and {
    durablePlanCheckpointIsBound(planCreateCheckpoint, Set()),
    durablePlanMayAdvance(planCreateCheckpoint),
    not(durablePlanCheckpointIsBound(
      { ...planCreateCheckpoint,
        receipt: { intent: appendIntent, outcomeId: "MOUT-Applied", resultingRevision: 2 } },
      Set(),
    )),
  }

  run testDurablePlanUncertaintyRequiresReceiptReread = and {
    durablePlanCheckpointIsBound(planUncertainCheckpoint, Set()),
    not(durablePlanMayAdvance(planUncertainCheckpoint)),
    not(durablePlanCheckpointIsBound(
      { ...planUncertainCheckpoint, dispositionId: "PDISP-Advance" },
      Set(),
    )),
  }

  run testDurablePlanCompensationIsBoundaryBoundAndReverseOrdered = and {
    durablePlanCompensationIsValid(
      planCompensationStep, planCreateStep, createAppliedResult, Set(planCreateStep), Set()
    ),
    not(durablePlanCompensationIsValid(
      { ...planCompensationStep, compensationBoundaryId: "boundary-other" },
      planCreateStep, createAppliedResult, Set(planCreateStep), Set(),
    )),
    not(durablePlanCompensationIsValid(
      planCompensationStep, planCreateStep, createAppliedResult,
      Set(planCreateStep, planAppendStep), Set(),
    )),
    not(durablePlanCompensationIsValid(
      { ...planCompensationStep, predecessorStepId: "step-forged" },
      planCreateStep, createAppliedResult, Set(planCreateStep), Set(),
    )),
    not(durablePlanCompensationIsValid(
      { ...planCompensationStep, causationId: "operation-forged" },
      planCreateStep, createAppliedResult, Set(planCreateStep), Set(),
    )),
    not(durablePlanCompensationIsValid(
      { ...planCompensationStep, sequence: 99 },
      planCreateStep, createAppliedResult, Set(planCreateStep), Set(),
    )),
    not(durablePlanCompensationIsValid(
      { ...planCompensationStep, stepId: planCreateStep.stepId },
      planCreateStep, createAppliedResult, Set(planCreateStep), Set(),
    )),
  }

  run testDurablePlanDispositionIsDerived = and {
    durablePlanDispositionFor(planCreateStep, createAppliedResult, Set()) == "PDISP-Advance",
    durablePlanDispositionFor(planCreateStep, planUncertainCheckpoint.receipt, Set()) == "PDISP-ReceiptReread",
    durablePlanDispositionFor(
      planAppendStep,
      { intent: appendIntent, outcomeId: "MOUT-Rejected", resultingRevision: 1 }, Set()
    ) == "PDISP-Replan",
    durablePlanDispositionFor(
      planAppendStep,
      { intent: appendIntent, outcomeId: "MOUT-Rejected", resultingRevision: 1 }, planCreateAppliedHistory
    ) == "PDISP-Compensate",
    not(durablePlanCheckpointIsBound(
      { step: planAppendStep,
        receipt: { intent: appendIntent, outcomeId: "MOUT-Rejected", resultingRevision: 1 },
        receiptReadId: "receipt-read-append-rejected", dispositionId: "PDISP-Compensate" },
      Set({ step: { ...planCreateStep, compensationBoundaryId: "boundary-forged" },
            receipt: createAppliedResult }),
    )),
  }

  run testDesiredStateCataloguesAndSpecificationAreClosed = and {
    closedDesiredStateCatalogues,
    desiredStateSpecificationIsComplete(completeDesiredState),
    not(desiredStateSpecificationIsComplete(completeDesiredState.filter(
      fact => fact.familyId != "DSTATE-Projects"
    ))),
    not(desiredStateSpecificationIsComplete(completeDesiredState.union(Set({
      ...desiredWorkflowPins, contentDigest: "digest-conflicting-workflow-pins"
    })))),
    not(desiredStateFactShapeIsValid({
      ...desiredWorkflowPins, surfaceIds: Set("reusable-workflow-pin")
    })),
  }

  run testDesiredStatePlanBindsSubjectProfileAndContent = and {
    desiredStatePlanOutcomeFor(
      desiredWorkflowPins, { ...desiredWorkflowPins, contentDigest: "digest-observed-old-pin" }
    ) == "DSPLAN-Ready",
    desiredStateMayApply(
      desiredWorkflowPins, { ...desiredWorkflowPins, contentDigest: "digest-observed-old-pin" }
    ),
    desiredStatePlanOutcomeFor(desiredWorkflowPins, desiredWorkflowPins) == "DSPLAN-NoChange",
    not(desiredStateMayApply(desiredWorkflowPins, desiredWorkflowPins)),
    desiredStatePlanOutcomeFor(
      desiredWorkflowPins, { ...desiredWorkflowPins, subjectId: "FS-GG/other" }
    ) == "DSPLAN-IdentityMismatch",
    desiredStatePlanOutcomeFor(
      desiredWorkflowPins, { ...desiredWorkflowPins, profileId: "repository-profile-other" }
    ) == "DSPLAN-IdentityMismatch",
    desiredStatePlanOutcomeFor(
      desiredWorkflowPins, { ...desiredWorkflowPins, surfaceIds: Set("reusable-workflow-pin") }
    ) == "DSPLAN-IdentityMismatch",
  }

  run testDesiredStateUnsupportedAndPermissionOutcomesFailClosed = and {
    desiredStatePlanOutcomeFor(
      desiredRepositoryProfile,
      { ...desiredRepositoryProfile, outcomeId: "OBS-Unsupported", supported: false }
    ) == "DSPLAN-Unsupported",
    desiredStatePlanOutcomeFor(
      desiredRepositoryProfile,
      { ...desiredRepositoryProfile, outcomeId: "OBS-Unauthorized", permissionGranted: false }
    ) == "DSPLAN-Unauthorized",
    desiredStatePlanOutcomeFor(
      desiredRepositoryProfile,
      { ...desiredRepositoryProfile, outcomeId: "OBS-Incomplete", complete: false }
    ) == "DSPLAN-Incomplete",
    desiredStatePlanOutcomeFor(
      desiredRepositoryProfile, { ...desiredRepositoryProfile, outcomeId: "OBS-Stale" }
    ) == "DSPLAN-Stale",
  }

  run testDesiredStateVerificationRejectsPolicySubstitution = and {
    desiredStateIsVerified(desiredWorkflowPins, desiredWorkflowPins),
    desiredStateIsVerified(desiredReleases, desiredReleases),
    desiredStateIsVerified(desiredSecuritySupplyChain, desiredSecuritySupplyChain),
    not(desiredStateIsVerified(
      desiredWorkflowPins, { ...desiredWorkflowPins, contentDigest: "digest-floating-workflow-pin" }
    )),
    not(desiredStateIsVerified(
      desiredReleases, { ...desiredReleases, contentDigest: "digest-mutable-release" }
    )),
    not(desiredStateIsVerified(
      desiredSecuritySupplyChain, { ...desiredSecuritySupplyChain, contentDigest: "digest-missing-attestation" }
    )),
  }

  run testDesiredStatePhaseTransitionsAreClosed = and {
    desiredStatePhaseMayAdvance(
      "DSPH-Inspect", "DSPH-Plan", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-NoChange", "MOUT-Incomplete"),
      desiredWorkflowPins, desiredWorkflowPins
    ),
    desiredStatePhaseMayAdvance(
      "DSPH-Plan", "DSPH-Apply", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Ready", "MOUT-Incomplete"),
      desiredWorkflowPins,
      { ...desiredWorkflowPins, contentDigest: "digest-observed-old-pin" }
    ),
    desiredStatePhaseMayAdvance(
      "DSPH-Plan", "DSPH-Verify", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-NoChange", "MOUT-Incomplete"),
      desiredWorkflowPins, desiredWorkflowPins
    ),
    desiredStatePhaseMayAdvance(
      "DSPH-Apply", "DSPH-Verify", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Ready", "MOUT-Applied"),
      desiredWorkflowPins, desiredWorkflowPins
    ),
    not(desiredStatePhaseMayAdvance(
      "DSPH-Inspect", "DSPH-Apply", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Ready", "MOUT-Incomplete"),
      desiredWorkflowPins, desiredWorkflowPins
    )),
    not(desiredStatePhaseMayAdvance(
      "DSPH-Plan", "DSPH-Apply", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Unauthorized", "MOUT-Incomplete"),
      desiredWorkflowPins, desiredWorkflowPins
    )),
    not(desiredStatePhaseMayAdvance(
      "DSPH-Plan", "DSPH-Apply", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Ready", "MOUT-Incomplete"),
      desiredWorkflowPins,
      { ...desiredWorkflowPins, outcomeId: "OBS-Unauthorized", permissionGranted: false }
    )),
    not(desiredStatePhaseMayAdvance(
      "DSPH-Plan", "DSPH-Apply", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Ready", "MOUT-Incomplete"),
      { ...desiredWorkflowPins, outcomeId: "OBS-Unauthorized", permissionGranted: false },
      { ...desiredWorkflowPins, contentDigest: "digest-observed-old-pin" }
    )),
    not(desiredStatePhaseMayAdvance(
      "DSPH-Inspect", "DSPH-Plan", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-NoChange", "MOUT-Incomplete"),
      desiredWorkflowPins,
      { ...desiredWorkflowPins, complete: false }
    )),
    not(desiredStatePhaseMayAdvance(
      "DSPH-Apply", "DSPH-Verify", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Ready", "MOUT-Applied"),
      { ...desiredWorkflowPins, outcomeId: "OBS-Unauthorized", permissionGranted: false }, desiredWorkflowPins
    )),
    not(desiredStatePhaseMayAdvance(
      "DSPH-Apply", "DSPH-Verify", desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Ready", "MOUT-TimedOut"),
      desiredWorkflowPins, desiredWorkflowPins
    )),
    not(desiredStatePhaseMayAdvance(
      "DSPH-Apply", "DSPH-Verify",
      { ...desiredPhaseAuthority(desiredWorkflowPins, "DSPLAN-Ready", "MOUT-Applied"),
        desiredContentDigest: "digest-forged-workflow-pin" },
      desiredWorkflowPins, desiredWorkflowPins
    )),
  }

  run testCompiledOutputsAreCompleteAndDeterministic = and {
    compiledOutputSetIsQualified(completeCompiledOutputs),
    completeCompiledOutputs.map(output => output.familyId) == compiledOutputFamilyCatalogue.map(family => family.id),
    completeCompiledOutputs.map(output => output.ordinal) == Set(1, 2, 3, 4, 5, 6, 7, 8, 9),
    completeCompiledOutputs.filter(output => output.familyId == "COUT-ProjectionViews")
      .forall(output => output.formats == Set("markdown", "json")),
  }

  run testCompiledOutputsRejectMissingDuplicateAndReorderedFamilies = and {
    not(compiledOutputSetIsQualified(completeCompiledOutputs.filter(
      output => output.familyId != "COUT-Diagrams"
    ))),
    not(compiledOutputSetIsQualified(completeCompiledOutputs.union(Set(
      compiledOutput("COUT-Diagrams", 8, "authority-map|phase-flow|mutation-flow|stream-flow",
        Set("mermaid"), "digest-conflicting-diagrams")
    )))),
    not(compiledOutputSetIsQualified(
      completeCompiledOutputs.filter(output => and {
        output.familyId != "COUT-SemanticDiff", output.familyId != "COUT-Diagrams"
      }).union(Set(
        compiledOutput("COUT-SemanticDiff", 8, "family|identity|content|support|completeness|freshness|order",
          Set("json"), "digest-semantic-diff"),
        compiledOutput("COUT-Diagrams", 7, "authority-map|phase-flow|mutation-flow|stream-flow",
          Set("mermaid"), "digest-diagrams")
      ))
    )),
  }

  run testCompiledOutputsRejectSubstitutionAndUnqualifiedFacts = and {
    not(compiledOutputSetIsQualified(
      completeCompiledOutputs.filter(output => output.familyId != "COUT-Schemas").union(Set({
        ...compiledOutput("COUT-Schemas", 1,
          "authority|observation|lifecycle|relation|stream|mutation|durable-plan|desired-state|compiled-output",
          Set("json-schema"), "digest-schemas"), sourceIdentity: "substituted-authority"
      }))
    )),
    not(compiledOutputSetIsQualified(
      completeCompiledOutputs.filter(output => output.familyId != "COUT-PermissionCensus").union(Set({
        ...compiledOutput("COUT-PermissionCensus", 3,
          "organization-administration|repository-administration|project-administration|actions-administration|release-administration|security-administration",
          Set("json"), "digest-permission-census"), supported: false
      }))
    )),
    not(compiledOutputSetIsQualified(
      completeCompiledOutputs.filter(output => output.familyId != "COUT-SettingsPlans").union(Set({
        ...compiledOutput("COUT-SettingsPlans", 5,
          "issue-schema|repository-properties|projects|repository-profile|workflow-pins|releases|permissions|security-supply-chain|inspect|plan|apply|verify",
          Set("json"), "digest-settings-plans"), complete: false
      }))
    )),
    not(compiledOutputSetIsQualified(
      completeCompiledOutputs.filter(output => output.familyId != "COUT-ModelTestInventory").union(Set({
        ...compiledOutput("COUT-ModelTestInventory", 9, "invariant|witness|negative-control|bounded-verification",
          Set("json"), "digest-model-test-inventory"), fresh: false
      }))
    )),
    not(compiledOutputSetIsQualified(
      completeCompiledOutputs.filter(output => output.familyId != "COUT-ProjectionViews").union(Set(
        compiledOutput("COUT-ProjectionViews", 6, "catalogue|relationships|actions|verification|bounds|compatibility",
          Set("json"), "digest-projection-views")
      ))
    )),
  }

  run testDeterministicIdentityEquivalentAuthoringFormsConverge = and {
    behavioralIdentityIsEquivalent(
      canonicalDeterministicIdentity,
      { ...canonicalDeterministicIdentity, sourceSha256: "source-equivalent-authoring" }
    ),
    behavioralIdentityIsEquivalent(
      canonicalDeterministicIdentity,
      { ...canonicalDeterministicIdentity, sourceSha256: "source-prose-only" }
    ),
  }

  run testDeterministicIdentitySemanticChangesRemainReviewable = and {
    not(behavioralIdentityIsEquivalent(
      canonicalDeterministicIdentity,
      { ...canonicalDeterministicIdentity, behavioralSha256: "behavior-changed",
        semanticDiffRows: List("1:/catalogue/0:row-changed", "2:/properties/0:row-b") }
    )),
    canonicalDeterministicIdentity.semanticDiffRows ==
      List("1:/catalogue/0:row-a", "2:/properties/0:row-b"),
  }

  run testDeterministicIdentityUnsupportedVersionsFailClosed = and {
    deterministicIdentityIsQualified(canonicalDeterministicIdentity),
    not(deterministicIdentityIsQualified({ ...canonicalDeterministicIdentity,
      versions: { ...supportedDeterministicVersions, sourceVersion: "unsupported-source" } })),
    not(deterministicIdentityIsQualified({ ...canonicalDeterministicIdentity,
      versions: { ...supportedDeterministicVersions, extractorVersion: "unsupported-extractor" } })),
    not(deterministicIdentityIsQualified({ ...canonicalDeterministicIdentity,
      versions: { ...supportedDeterministicVersions, quintVersion: "unsupported-quint" } })),
    not(deterministicIdentityIsQualified({ ...canonicalDeterministicIdentity,
      versions: { ...supportedDeterministicVersions, profileVersion: "unsupported-profile" } })),
    not(deterministicIdentityIsQualified({ ...canonicalDeterministicIdentity,
      versions: { ...supportedDeterministicVersions, schemaVersion: "unsupported-schema" } })),
  }

  run testQualificationManifestBindsCandidateInputsResultsAndReview = and {
    qualificationManifestIsBound(canonicalQualificationManifest),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      resultCandidateSha: "candidate-substituted" })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      reviewInputSetSha256: "inputs-substituted" })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      inputEntries: canonicalQualificationManifest.inputEntries
        .filter(entry => entry.id != "source")
        .union(Set({ id: "source", candidateSha: "candidate-substituted", producer: "candidate-builder",
          digest: "source-digest", fresh: true })) })),
  }

  run testQualificationManifestRequiresIndependentCasesAndReviewers = and {
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      independentProducers: Set() })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      reviewerPrincipals: Set() })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      independentProducers: Set("candidate-builder") })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      independentProducers: Set("case-generator") })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      reviewerPrincipals: Set("candidate-builder") })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      reviewerPrincipals: Set("gate-runner") })),
  }

  run testQualificationManifestOmissionsAndStaleInputsFailClosed = and {
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest, inputEntries: Set() })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest, environmentClosed: false })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest, resultsComplete: false })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest, reviewsComplete: false })),
    not(qualificationManifestIsBound({ ...canonicalQualificationManifest,
      inputEntries: canonicalQualificationManifest.inputEntries
        .filter(entry => entry.id != "generated")
        .union(Set({ id: "generated", candidateSha: "candidate-a", producer: "case-generator",
          digest: "generated-digest", fresh: false })) })),
  }

  // GS2-03.5 native formal scenarios share this canonical test module so Quint's bounded
  // verifier observes the exact protocol definitions and fixtures without a parallel model.
  var formalClaimStage: int
  var formalClaimEvents: Set[ProtocolEnvelope]
  var formalClaimValid: bool
  action formalClaimInit = all {
    formalClaimStage' = 0, formalClaimEvents' = Set(), formalClaimValid' = true,
  }
  action formalClaimObserve = all {
    formalClaimStage == 0, protocolAppendIsValid(operationLockEnvelope, formalClaimEvents),
    formalClaimStage' = 1,
    formalClaimEvents' = formalClaimEvents.union(Set(operationLockEnvelope)),
    formalClaimValid' = true,
  }
  action formalClaimElect = all {
    formalClaimStage == 1, protocolAppendIsValid(electionCheckpointEnvelope, formalClaimEvents),
    formalClaimStage' = 2,
    formalClaimEvents' = formalClaimEvents.union(Set(electionCheckpointEnvelope)),
    formalClaimValid' = true,
  }
  action formalClaimHold = all {
    formalClaimStage == 2, formalClaimStage' = formalClaimStage,
    formalClaimEvents' = formalClaimEvents, formalClaimValid' = formalClaimValid,
  }
  action formalClaimStep = any { formalClaimObserve, formalClaimElect, formalClaimHold }
  action formalClaimInvalid = all {
    formalClaimStage' = 2, formalClaimEvents' = Set(electionCheckpointEnvelope),
    formalClaimValid' = protocolAppendIsValid(electionCheckpointEnvelope, Set()),
  }
  val formalClaimSafety = formalClaimValid
    and formalClaimEvents.forall(event => protocolEnvelopeIsOrdered(event, formalClaimEvents))
  val formalClaimReached = formalClaimStage == 2
    and formalClaimEvents.contains(electionCheckpointEnvelope)
  temporal formalClaimProgress: bool = and {
    formalClaimObserve.weakFair(Set(formalClaimStage)),
    formalClaimElect.weakFair(Set(formalClaimStage)),
  }.implies(eventually(formalClaimReached))

  var formalRelationStage: int
  var formalRelationEdges: Set[NativeRelationEdge]
  action formalRelationInit = all { formalRelationStage' = 0, formalRelationEdges' = Set() }
  action formalRelationAdd = all {
    formalRelationStage == 0, nativeRelationEdgeIsValid(parentChildEdge),
    formalRelationStage' = 1, formalRelationEdges' = formalRelationEdges.union(Set(parentChildEdge)),
  }
  action formalRelationRemove = all {
    formalRelationStage == 1, formalRelationStage' = 2,
    formalRelationEdges' = formalRelationEdges.exclude(Set(parentChildEdge)),
  }
  action formalRelationHold = all {
    formalRelationStage == 2, formalRelationStage' = formalRelationStage,
    formalRelationEdges' = formalRelationEdges,
  }
  action formalRelationStep = any { formalRelationAdd, formalRelationRemove, formalRelationHold }
  action formalRelationInvalid = all {
    formalRelationStage' = 1,
    formalRelationEdges' = Set({ ...parentChildEdge, targetId: parentChildEdge.sourceId }),
  }
  val formalRelationSafety = formalRelationEdges.forall(nativeRelationEdgeIsValid)
  val formalRelationReached = formalRelationStage >= 1
  temporal formalRelationProgress: bool = and {
    formalRelationAdd.weakFair(Set(formalRelationStage)),
    formalRelationRemove.weakFair(Set(formalRelationStage)),
  }.implies(eventually(formalRelationStage == 2))

  pure val formalDeliveredLifecycleFacts: LifecycleFacts = {
    ...claimedLifecycleFacts, deliveryOutcomeId: "OBS-Observed", delivered: true,
  }
  var formalLifecycleStage: int
  var formalLifecycleStatus: str
  action formalLifecycleInit = all {
    formalLifecycleStage' = 0,
    formalLifecycleStatus' = deriveLifecycleStatus("INTENT-Ready", emptyLifecycleFacts),
  }
  action formalLifecycleClaim = all {
    formalLifecycleStage == 0, formalLifecycleStage' = 1,
    formalLifecycleStatus' = deriveLifecycleStatus("INTENT-Ready", claimedLifecycleFacts),
  }
  action formalLifecycleDeliver = all {
    formalLifecycleStage == 1, formalLifecycleStage' = 2,
    formalLifecycleStatus' = deriveLifecycleStatus("INTENT-Ready", formalDeliveredLifecycleFacts),
  }
  action formalLifecycleHold = all {
    formalLifecycleStage == 2, formalLifecycleStage' = formalLifecycleStage,
    formalLifecycleStatus' = formalLifecycleStatus,
  }
  action formalLifecycleStep = any { formalLifecycleClaim, formalLifecycleDeliver, formalLifecycleHold }
  action formalLifecycleInvalid = all { formalLifecycleStage' = 2, formalLifecycleStatus' = "ready" }
  val formalLifecycleSafety = formalLifecycleStatus ==
    if (formalLifecycleStage == 0) "ready"
    else if (formalLifecycleStage == 1) "claimed" else "delivered"
  val formalLifecycleReached = formalLifecycleStage == 2 and formalLifecycleStatus == "delivered"
  temporal formalLifecycleProgress: bool = and {
    formalLifecycleClaim.weakFair(Set(formalLifecycleStage)),
    formalLifecycleDeliver.weakFair(Set(formalLifecycleStage)),
  }.implies(eventually(formalLifecycleReached))

  var formalSagaStage: int
  var formalSagaValid: bool
  action formalSagaInit = all { formalSagaStage' = 0, formalSagaValid' = true }
  action formalSagaBegin = all {
    formalSagaStage == 0, formalSagaStage' = 1,
    formalSagaValid' = durablePlanStepShapeIsValid(planCreateStep),
  }
  action formalSagaAdvance = all {
    formalSagaStage == 1, formalSagaStage' = 2,
    formalSagaValid' = formalSagaValid and durablePlanStepMayFollow(planCreateStep, planAppendStep),
  }
  action formalSagaHold = all {
    formalSagaStage == 2, formalSagaStage' = formalSagaStage,
    formalSagaValid' = formalSagaValid,
  }
  action formalSagaStep = any { formalSagaBegin, formalSagaAdvance, formalSagaHold }
  action formalSagaInvalid = all { formalSagaStage' = 2, formalSagaValid' = false }
  val formalSagaSafety = formalSagaValid
  val formalSagaReached = formalSagaStage == 2 and formalSagaValid
  temporal formalSagaProgress: bool = and {
    formalSagaBegin.weakFair(Set(formalSagaStage)),
    formalSagaAdvance.weakFair(Set(formalSagaStage)),
  }.implies(eventually(formalSagaReached))

  pure val formalNextEpochEnvelope: ProtocolEnvelope = {
    ...operationLockEnvelope, generation: 2, eventId: "operation-lock-epoch-2",
  }
  var formalEpochStage: int
  var formalEpochEvents: Set[ProtocolEnvelope]
  var formalEpochValid: bool
  action formalEpochInit = all {
    formalEpochStage' = 0, formalEpochEvents' = Set(), formalEpochValid' = true,
  }
  action formalEpochBegin = all {
    formalEpochStage == 0, formalEpochStage' = 1,
    formalEpochValid' = protocolAppendIsValid(operationLockEnvelope, formalEpochEvents),
    formalEpochEvents' = formalEpochEvents.union(Set(operationLockEnvelope)),
  }
  action formalEpochElect = all {
    formalEpochStage == 1, formalEpochStage' = 2,
    formalEpochValid' = formalEpochValid
      and protocolAppendIsValid(electionCheckpointEnvelope, formalEpochEvents),
    formalEpochEvents' = formalEpochEvents.union(Set(electionCheckpointEnvelope)),
  }
  action formalEpochAdvance = all {
    formalEpochStage == 2, formalEpochStage' = 3,
    formalEpochValid' = formalEpochValid
      and protocolAppendIsValid(formalNextEpochEnvelope, formalEpochEvents),
    formalEpochEvents' = formalEpochEvents.union(Set(formalNextEpochEnvelope)),
  }
  action formalEpochHold = all {
    formalEpochStage == 3, formalEpochStage' = formalEpochStage,
    formalEpochEvents' = formalEpochEvents, formalEpochValid' = formalEpochValid,
  }
  action formalEpochStep = any { formalEpochBegin, formalEpochElect, formalEpochAdvance, formalEpochHold }
  action formalEpochInvalid = all {
    formalEpochStage' = 4, formalEpochEvents' = Set(formalNextEpochEnvelope),
    formalEpochValid' = protocolAppendIsValid(formalNextEpochEnvelope, Set()),
  }
  val formalEpochSafety = formalEpochValid
    and (formalEpochStage >= 3 implies formalEpochEvents.contains(operationLockEnvelope))
  val formalEpochReached = formalEpochStage == 3
    and formalEpochEvents.contains(formalNextEpochEnvelope)
  temporal formalEpochProgress: bool = and {
    formalEpochBegin.weakFair(Set(formalEpochStage)),
    formalEpochElect.weakFair(Set(formalEpochStage)),
    formalEpochAdvance.weakFair(Set(formalEpochStage)),
  }.implies(eventually(formalEpochReached))

  var formalRollbackStage: int
  var formalRollbackValid: bool
  action formalRollbackInit = all { formalRollbackStage' = 0, formalRollbackValid' = true }
  action formalRollbackApply = all {
    formalRollbackStage == 0, formalRollbackStage' = 1,
    formalRollbackValid' = durablePlanStepShapeIsValid(planCreateStep),
  }
  action formalRollbackCompensate = all {
    formalRollbackStage == 1, formalRollbackStage' = 2,
    formalRollbackValid' = formalRollbackValid and durablePlanCompensationIsValid(
      planCompensationStep, planCreateStep, createAppliedResult, Set(planCreateStep), Set()),
  }
  action formalRollbackHold = all {
    formalRollbackStage == 2, formalRollbackStage' = formalRollbackStage,
    formalRollbackValid' = formalRollbackValid,
  }
  action formalRollbackStep = any {
    formalRollbackApply, formalRollbackCompensate, formalRollbackHold,
  }
  action formalRollbackInvalid = all { formalRollbackStage' = 2, formalRollbackValid' = false }
  val formalRollbackSafety = formalRollbackValid
  val formalRollbackReached = formalRollbackStage == 2 and formalRollbackValid
  temporal formalRollbackProgress: bool = and {
    formalRollbackApply.weakFair(Set(formalRollbackStage)),
    formalRollbackCompensate.weakFair(Set(formalRollbackStage)),
  }.implies(eventually(formalRollbackReached))

  // TLC requires every variable in the selected module to have a legal initial value. All six
  // independently selected scenarios therefore share this complete initialization action.
  action formalInit = all {
    formalClaimStage' = 0, formalClaimEvents' = Set(), formalClaimValid' = true,
    formalRelationStage' = 0, formalRelationEdges' = Set(),
    formalLifecycleStage' = 0,
    formalLifecycleStatus' = deriveLifecycleStatus("INTENT-Ready", emptyLifecycleFacts),
    formalSagaStage' = 0, formalSagaValid' = true,
    formalEpochStage' = 0, formalEpochEvents' = Set(), formalEpochValid' = true,
    formalRollbackStage' = 0, formalRollbackValid' = true,
  }

  action formalClaimStutter = all {
    formalClaimStage' = formalClaimStage, formalClaimEvents' = formalClaimEvents,
    formalClaimValid' = formalClaimValid,
  }
  action formalRelationStutter = all {
    formalRelationStage' = formalRelationStage, formalRelationEdges' = formalRelationEdges,
  }
  action formalLifecycleStutter = all {
    formalLifecycleStage' = formalLifecycleStage, formalLifecycleStatus' = formalLifecycleStatus,
  }
  action formalSagaStutter = all {
    formalSagaStage' = formalSagaStage, formalSagaValid' = formalSagaValid,
  }
  action formalEpochStutter = all {
    formalEpochStage' = formalEpochStage, formalEpochEvents' = formalEpochEvents,
    formalEpochValid' = formalEpochValid,
  }
  action formalRollbackStutter = all {
    formalRollbackStage' = formalRollbackStage, formalRollbackValid' = formalRollbackValid,
  }

  action formalClaimTlcStep = all {
    formalClaimStep, formalRelationStutter, formalLifecycleStutter,
    formalSagaStutter, formalEpochStutter, formalRollbackStutter,
  }
  action formalClaimTlcInvalid = all {
    formalClaimInvalid, formalRelationStutter, formalLifecycleStutter,
    formalSagaStutter, formalEpochStutter, formalRollbackStutter,
  }
  action formalRelationTlcStep = all {
    formalRelationStep, formalClaimStutter, formalLifecycleStutter,
    formalSagaStutter, formalEpochStutter, formalRollbackStutter,
  }
  action formalRelationTlcInvalid = all {
    formalRelationInvalid, formalClaimStutter, formalLifecycleStutter,
    formalSagaStutter, formalEpochStutter, formalRollbackStutter,
  }
  action formalLifecycleTlcStep = all {
    formalLifecycleStep, formalClaimStutter, formalRelationStutter,
    formalSagaStutter, formalEpochStutter, formalRollbackStutter,
  }
  action formalLifecycleTlcInvalid = all {
    formalLifecycleInvalid, formalClaimStutter, formalRelationStutter,
    formalSagaStutter, formalEpochStutter, formalRollbackStutter,
  }
  action formalSagaTlcStep = all {
    formalSagaStep, formalClaimStutter, formalRelationStutter,
    formalLifecycleStutter, formalEpochStutter, formalRollbackStutter,
  }
  action formalSagaTlcInvalid = all {
    formalSagaInvalid, formalClaimStutter, formalRelationStutter,
    formalLifecycleStutter, formalEpochStutter, formalRollbackStutter,
  }
  action formalEpochTlcStep = all {
    formalEpochStep, formalClaimStutter, formalRelationStutter,
    formalLifecycleStutter, formalSagaStutter, formalRollbackStutter,
  }
  action formalEpochTlcInvalid = all {
    formalEpochInvalid, formalClaimStutter, formalRelationStutter,
    formalLifecycleStutter, formalSagaStutter, formalRollbackStutter,
  }
  action formalRollbackTlcStep = all {
    formalRollbackStep, formalClaimStutter, formalRelationStutter,
    formalLifecycleStutter, formalSagaStutter, formalEpochStutter,
  }
  action formalRollbackTlcInvalid = all {
    formalRollbackInvalid, formalClaimStutter, formalRelationStutter,
    formalLifecycleStutter, formalSagaStutter, formalEpochStutter,
  }
}

// GS2-03.4 bounded executable roots. Each root imports the canonical authority but exposes only
// the actions and properties needed for one independently qualified closure. Quint flattening
// therefore retains the used transitive closure instead of the all-actions integration root.
module QualificationAuthorityRoot {
  import CoordinationProtocol.mutationIntentsConflict
  import CoordinationProtocol.createIntent

  var attemptObserved: bool
  var conflictDetected: bool
  action init = all { attemptObserved' = false, conflictDetected' = false }
  action observeRival = all {
    attemptObserved' = true,
    conflictDetected' = mutationIntentsConflict(createIntent,
      { ...createIntent, operationId: "operation-rival" }),
  }
  action idle = all { attemptObserved' = attemptObserved, conflictDetected' = conflictDetected }
  action rootStep = any { observeRival, idle }
  // An exact replay is an invalid parameterization for the rival-claim transition.
  action invalidStep = all {
    attemptObserved' = true,
    conflictDetected' = mutationIntentsConflict(createIntent, createIntent),
  }
  val rootSafety = not(attemptObserved) or conflictDetected
  val positiveWitness = attemptObserved and conflictDetected
  val adversarialWitness = not(attemptObserved) and not(conflictDetected)
  val invalidParameterWitness = mutationIntentsConflict(createIntent, createIntent)
  val qualificationInvariant = rootSafety
}

module QualificationLifecycleRoot {
  import CoordinationProtocol.deriveLifecycleStatus
  import CoordinationProtocol.emptyLifecycleFacts
  import CoordinationProtocol.claimedLifecycleFacts

  var claimPresent: bool
  var lifecycleStatus: str
  action init = all {
    claimPresent' = false,
    lifecycleStatus' = deriveLifecycleStatus("INTENT-Ready", emptyLifecycleFacts),
  }
  action observeClaim = all {
    claimPresent' = true,
    lifecycleStatus' = deriveLifecycleStatus("INTENT-Ready", claimedLifecycleFacts),
  }
  action idle = all { claimPresent' = claimPresent, lifecycleStatus' = lifecycleStatus }
  action rootStep = any { observeClaim, idle }
  // A claimed fact paired with the empty-facts projection must be rejected.
  action invalidStep = all {
    claimPresent' = true,
    lifecycleStatus' = deriveLifecycleStatus("INTENT-Ready", emptyLifecycleFacts),
  }
  val expectedLifecycleStatus = deriveLifecycleStatus("INTENT-Ready",
    if (claimPresent) claimedLifecycleFacts else emptyLifecycleFacts)
  val rootSafety = lifecycleStatus == expectedLifecycleStatus
  val positiveWitness = claimPresent and lifecycleStatus == "claimed"
  val adversarialWitness = not(claimPresent) and lifecycleStatus == "ready"
  val invalidParameterWitness = deriveLifecycleStatus("INTENT-Ready", emptyLifecycleFacts) == "claimed"
  val qualificationInvariant = rootSafety
}

module QualificationRelationsRoot {
  import CoordinationProtocol.NativeRelationEdge
  import CoordinationProtocol.nativeRelationEdgeIsValid
  import CoordinationProtocol.parentChildEdge
  import CoordinationProtocol.blockingEdge

  var nativeRelationEdges: Set[NativeRelationEdge]
  action init = nativeRelationEdges' = Set()
  action addParentChild = nativeRelationEdges' = nativeRelationEdges.union(Set(parentChildEdge))
  action addBlocking = nativeRelationEdges' = nativeRelationEdges.union(Set(blockingEdge))
  action removeParentChild = nativeRelationEdges' = nativeRelationEdges.exclude(Set(parentChildEdge))
  action rootStep = any { addParentChild, addBlocking, removeParentChild }
  // Self-relations are outside the canonical native-relation contract.
  action invalidStep = all {
    nativeRelationEdges' = nativeRelationEdges.union(Set({ ...parentChildEdge, targetId: "subject-parent" })),
  }
  val rootSafety = nativeRelationEdges.forall(nativeRelationEdgeIsValid)
  val positiveWitness = nativeRelationEdges.contains(parentChildEdge)
  val adversarialWitness = nativeRelationEdges.contains(blockingEdge)
  val invalidParameterWitness = nativeRelationEdgeIsValid({ ...parentChildEdge, targetId: "subject-parent" })
  val qualificationInvariant = rootSafety
}

module QualificationProtocolStreamsRoot {
  import CoordinationProtocol.ProtocolEnvelope
  import CoordinationProtocol.protocolEnvelopeShapeIsValid
  import CoordinationProtocol.protocolEnvelopeIsOrdered
  import CoordinationProtocol.claimEnvelope
  import CoordinationProtocol.leaseEnvelope
  import CoordinationProtocol.reviewCheckpointEnvelope

  var protocolStreamEvents: Set[ProtocolEnvelope]
  var durableProtocolCheckpoints: Set[ProtocolEnvelope]
  action init = all { protocolStreamEvents' = Set(), durableProtocolCheckpoints' = Set() }
  action appendClaim = all {
    protocolStreamEvents' = protocolStreamEvents.union(Set(claimEnvelope)),
    durableProtocolCheckpoints' = durableProtocolCheckpoints,
  }
  action appendLease = all {
    protocolStreamEvents.contains(claimEnvelope),
    protocolStreamEvents' = protocolStreamEvents.union(Set(leaseEnvelope)),
    durableProtocolCheckpoints' = durableProtocolCheckpoints,
  }
  action appendReview = all {
    protocolStreamEvents.contains(claimEnvelope),
    protocolStreamEvents.contains(leaseEnvelope),
    protocolStreamEvents' = protocolStreamEvents.union(Set(reviewCheckpointEnvelope)),
    durableProtocolCheckpoints' = durableProtocolCheckpoints.union(Set(reviewCheckpointEnvelope)),
  }
  action rootStep = any { appendClaim, appendLease, appendReview }
  // A lease without its retained claim predecessor is an invalid stream parameterization.
  action invalidStep = all {
    protocolStreamEvents' = protocolStreamEvents.union(Set(leaseEnvelope)),
    durableProtocolCheckpoints' = durableProtocolCheckpoints,
  }
  val rootSafety = and {
    durableProtocolCheckpoints.subseteq(protocolStreamEvents),
    protocolStreamEvents.forall(event => and {
      protocolEnvelopeShapeIsValid(event),
      protocolEnvelopeIsOrdered(event, protocolStreamEvents),
    }),
  }
  val positiveWitness = protocolStreamEvents.contains(reviewCheckpointEnvelope)
  val adversarialWitness = protocolStreamEvents.contains(leaseEnvelope)
  val invalidParameterWitness = protocolEnvelopeIsOrdered(leaseEnvelope, Set(leaseEnvelope))
  val qualificationInvariant = rootSafety
}

```
