# GS2-02.4 canonical coordination protocol

This document is the sole authored source for the coordination protocol baseline. Every behavioral
fact is inside a named Quint block. The generated `.qnt`, compiled contract, and F# bindings are
projections and must never be edited independently.

GS2-02.1 established vocabulary and stable integration identities; GS2-02.2 added the closed,
revision-aware authority catalogue; GS2-02.3 added observation outcomes and knowledge semantics.
This unit refines only lifecycle intent and derived status. Human scheduling intent remains distinct
from claims, blockers, pull-request, review, and delivery observations. Later GS2-02 units refine
relation algebra, streams, mutations, plans, desired state, and compiled outputs. No hosted runtime
or production mutation authority is defined here.

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

  pure val nativeGitHubAbsentObservation = {
    ...nativeGitHubObservation,
    outcomeId: "OBS-ProvenAbsent",
  }

  pure val nativeGitHubRateLimitedObservation = {
    ...nativeGitHubObservation,
    outcomeId: "OBS-RateLimited",
    complete: false,
    retryAfterPresent: true,
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

  pure val reviewLifecycleFacts = {
    ...claimedLifecycleFacts,
    pullRequestOutcomeId: "OBS-Observed",
    pullRequestOpen: true,
    reviewOutcomeId: "OBS-Observed",
    reviewAccepted: true,
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

  pure val closedLifecycleIntentCatalogue = and {
    lifecycleIntentCatalogue.map(intent => intent.id) == Set(
      "INTENT-Backlog", "INTENT-Ready", "INTENT-Paused", "INTENT-Cancelled"
    ),
    lifecycleIntentCatalogue.filter(intent => intent.terminal).map(intent => intent.id) == Set("INTENT-Cancelled"),
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
  }

  val acceptedVocabularyIsQualified = acceptedVocabulary == Set() or evidenceObserved
  val vocabularyCanBeAccepted = acceptedVocabulary.contains("SubjectVocabulary")
  val acceptedAuthoritiesAreQualified = and {
    closedAuthorityCatalogue,
    closedObservationOutcomeCatalogue,
    acceptedAuthorityObservations.forall(authorityObservationIsQualified),
    acceptedObservationKnowledge.forall(observationContributesKnowledge),
  }
  val authorityCanBeAccepted = acceptedAuthorityObservations.contains(nativeGitHubObservation)
  val acceptedObservationKnowledgeIsQualified = and {
    closedObservationOutcomeCatalogue,
    acceptedObservationKnowledge.forall(observationContributesKnowledge),
  }
  val provenAbsenceWasAccepted = acceptedObservationKnowledge.contains(nativeGitHubAbsentObservation)
  val failureOutcomesDoNotBecomeAbsence = Set(
    "OBS-Contradictory", "OBS-Unreadable", "OBS-Unsupported", "OBS-Unauthorized", "OBS-Incomplete", "OBS-Stale", "OBS-RateLimited"
  ).forall(outcomeId => not(observationContributesNegativeKnowledge({ ...nativeGitHubObservation, outcomeId: outcomeId })))
  val humanIntentIsObservationIndependent = and {
    closedLifecycleIntentCatalogue,
    lifecycleIntentExists(humanIntentId),
    humanIntentId == authorizedHumanIntentId,
  }
  val lifecycleStatusIsDerived =
    not(lifecycleStatusCurrent) or lifecycleStatus == deriveLifecycleStatus(humanIntentId, lifecycleFacts)
  val unknownLifecycleFactsFailClosed = Set(
    "OBS-Contradictory", "OBS-Unreadable", "OBS-Unsupported", "OBS-Unauthorized", "OBS-Incomplete", "OBS-Stale", "OBS-RateLimited"
  ).forall(outcomeId => deriveLifecycleStatus("INTENT-Ready", { ...emptyLifecycleFacts, claimOutcomeId: outcomeId }) == "indeterminate")
}
```

The executable witness records evidence before accepting the subject vocabulary identity. Removing
the evidence guard must make the invariant red in the bounded negative control.

```quint protocol.qnt +=
module CoordinationProtocolTests {
  import CoordinationProtocol.*

  run testEvidenceBeforeAcceptance =
    init
      .then(observeProtocolEvidence)
      .then(acceptVocabularyIdentity("SubjectVocabulary"))
      .expect(and { acceptedVocabularyIsQualified, vocabularyCanBeAccepted })

  run testAuthorityBindingCanBeAccepted =
    init
      .then(observeAuthority(nativeGitHubObservation))
      .then(acceptObservedAuthority)
      .expect(and { closedAuthorityCatalogue, acceptedAuthoritiesAreQualified, authorityCanBeAccepted })

  run testProvenAbsenceCanBeAccepted =
    init
      .then(observeAuthority(nativeGitHubAbsentObservation))
      .then(acceptObservationKnowledge)
      .expect(and { closedObservationOutcomeCatalogue, acceptedObservationKnowledgeIsQualified, provenAbsenceWasAccepted })

  run testIncompleteAuthorityIsRejected =
    not(authorityObservationIsQualified({ ...nativeGitHubObservation, complete: false }))

  run testStaleRevisionIsRejected =
    not(authorityObservationIsQualified({ ...nativeGitHubObservation, revisionValue: "stale-revision" }))

  run testWrongAuthorityIsRejected =
    not(authorityObservationIsQualified({ ...nativeGitHubObservation, authorityId: "AUTH-PackageFeed" }))

  run testContradictoryAuthorityIsRejected =
    not(authorityObservationIsQualified({ ...nativeGitHubObservation, contradictory: true }))

  run testIncompleteAbsenceIsRejected =
    not(observationContributesNegativeKnowledge({ ...nativeGitHubAbsentObservation, complete: false }))

  run testUnreadableIsNotAbsence =
    not(observationContributesNegativeKnowledge({ ...nativeGitHubObservation, outcomeId: "OBS-Unreadable" }))

  run testUnauthorizedIsNotAbsence =
    not(observationContributesNegativeKnowledge({ ...nativeGitHubObservation, outcomeId: "OBS-Unauthorized" }))

  run testStaleIsNotAbsence =
    not(observationContributesNegativeKnowledge({ ...nativeGitHubObservation, outcomeId: "OBS-Stale", revisionValue: "stale-revision" }))

  run testRateLimitIsRetryableNotAbsent = and {
    observationIsRetryableFailure(nativeGitHubRateLimitedObservation),
    not(observationContributesNegativeKnowledge(nativeGitHubRateLimitedObservation)),
  }

  run testFailureOutcomesStayNonAbsent = failureOutcomesDoNotBecomeAbsence

  run testHumanIntentSurvivesClaimObservation =
    init
      .then(setHumanIntent("INTENT-Paused"))
      .then(observeLifecycleFacts(claimedLifecycleFacts))
      .then(refreshLifecycleStatus)
      .expect(and {
        humanIntentIsObservationIndependent,
        lifecycleStatusIsDerived,
        humanIntentId == "INTENT-Paused",
        lifecycleStatus == "claimed",
      })

  run testReviewAndDeliveryAreDerivedFacts = and {
    deriveLifecycleStatus("INTENT-Ready", reviewLifecycleFacts) == "accepted",
    deriveLifecycleStatus("INTENT-Ready", { ...reviewLifecycleFacts, deliveryOutcomeId: "OBS-Observed", delivered: true }) == "delivered",
  }

  run testBlockerDoesNotRewriteIntent =
    init
      .then(setHumanIntent("INTENT-Ready"))
      .then(observeLifecycleFacts({ ...emptyLifecycleFacts, blockerOutcomeId: "OBS-Observed", blocked: true }))
      .then(refreshLifecycleStatus)
      .expect(and {
        humanIntentIsObservationIndependent,
        humanIntentId == "INTENT-Ready",
        lifecycleStatus == "blocked",
      })

  run testCancelledIntentOverridesObservedProgress =
    deriveLifecycleStatus("INTENT-Cancelled", reviewLifecycleFacts) == "cancelled"

  run testUnknownLifecycleObservationsFailClosed = unknownLifecycleFactsFailClosed

  run testInvalidHumanIntentIsRejected = not(lifecycleIntentExists("INTENT-Claimed"))
}
```
