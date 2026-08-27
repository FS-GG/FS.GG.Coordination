# GS2-02.2 canonical coordination protocol

This document is the sole authored source for the coordination protocol baseline. Every behavioral
fact is inside a named Quint block. The generated `.qnt`, compiled contract, and F# bindings are
projections and must never be edited independently.

GS2-02.1 established vocabulary and stable integration identities. This unit refines only the
authority seam with a closed, revision-aware catalogue and fail-closed qualification. Later GS2-02
units refine lifecycle, observation outcomes, streams, mutations, plans, desired state, and compiled
outputs. No hosted runtime or production mutation authority is defined here.

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
  type AuthorityObservation = {
    authorityId: str,
    family: str,
    revisionKind: str,
    revisionValue: str,
    completenessContract: str,
    evidenceRelationship: str,
    complete: bool,
    contradictory: bool,
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
      ), boundIds: Set("BOUND-AuthorityCardinality", "BOUND-TraceSteps") }
  )

  pure val compatibilityCatalogue = Set(
    { id: "COMPAT-Profile2", kind: "compatibility", surface: "fsgg-quint-profile/2", requirement: "exact", detail: "Consumer-defined structural profile; profile 1 remains frozen." }
  )

  pure val propertyCatalogue = Set(
    { id: "AcceptedVocabularyIsQualified", kind: "invariant", subjects: Set("EvidenceObligationVocabulary") },
    { id: "VocabularyCanBeAccepted", kind: "example", subjects: Set("SubjectVocabulary") },
    { id: "AuthorityCatalogueIsClosed", kind: "invariant", subjects: Set("AuthorityVocabulary") },
    { id: "AcceptedAuthoritiesAreQualified", kind: "invariant", subjects: Set("AuthorityVocabulary", "EvidenceObligationVocabulary") },
    { id: "AuthorityCanBeAccepted", kind: "example", subjects: Set("AUTH-NativeGitHub") }
  )

  pure val nativeGitHubObservation = {
    authorityId: "AUTH-NativeGitHub",
    family: "native-github",
    revisionKind: "github-object-version",
    revisionValue: "node-id-and-updated-at",
    completenessContract: "complete-required-fields",
    evidenceRelationship: "REL-AUTH-NativeGitHub-Evidence",
    complete: true,
    contradictory: false,
  }

  pure def authorityObservationIsQualified(observation: AuthorityObservation): bool = and {
    observation.complete,
    not(observation.contradictory),
    authorityCatalogue.exists(binding => and {
      binding.id == observation.authorityId,
      binding.family == observation.family,
      binding.revisionKind == observation.revisionKind,
      binding.revisionValue == observation.revisionValue,
      binding.completenessContract == observation.completenessContract,
      binding.evidenceRelationship == observation.evidenceRelationship,
    }),
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

  var evidenceObserved: bool
  var acceptedVocabulary: Set[str]
  var authorityObservation: AuthorityObservation
  var authorityObservationAvailable: bool
  var acceptedAuthorityObservations: Set[AuthorityObservation]

  action init = all {
    evidenceObserved' = false,
    acceptedVocabulary' = Set(),
    authorityObservation' = { ...nativeGitHubObservation, complete: false },
    authorityObservationAvailable' = false,
    acceptedAuthorityObservations' = Set(),
  }

  action observeProtocolEvidence: bool = all {
    evidenceObserved' = true,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
  }

  action acceptVocabularyIdentity(vocabularyId: str): bool = all {
    vocabularyCatalogue.exists(entry => entry.id == vocabularyId),
    evidenceObserved,
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary.union(Set(vocabularyId)),
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
  }

  action observeAuthority(observation: AuthorityObservation): bool = all {
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = observation,
    authorityObservationAvailable' = true,
    acceptedAuthorityObservations' = acceptedAuthorityObservations,
  }

  action acceptObservedAuthority: bool = all {
    authorityObservationAvailable,
    authorityObservationIsQualified(authorityObservation),
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary,
    authorityObservation' = authorityObservation,
    authorityObservationAvailable' = authorityObservationAvailable,
    acceptedAuthorityObservations' = acceptedAuthorityObservations.union(Set(authorityObservation)),
  }

  action step = any {
    observeProtocolEvidence,
    acceptVocabularyIdentity("SubjectVocabulary"),
    observeAuthority(nativeGitHubObservation),
    acceptObservedAuthority,
  }

  val acceptedVocabularyIsQualified = acceptedVocabulary == Set() or evidenceObserved
  val vocabularyCanBeAccepted = acceptedVocabulary.contains("SubjectVocabulary")
  val acceptedAuthoritiesAreQualified = and {
    closedAuthorityCatalogue,
    acceptedAuthorityObservations.forall(authorityObservationIsQualified),
  }
  val authorityCanBeAccepted = acceptedAuthorityObservations.contains(nativeGitHubObservation)
}
```

The executable witness records evidence before accepting the subject vocabulary identity. Removing
the evidence guard must make the invariant red in the bounded negative control.

```quint protocol.qnt +=
module CoordinationProtocolTests {
  import CoordinationProtocol.*

  run evidenceBeforeAcceptance =
    init
      .then(observeProtocolEvidence)
      .then(acceptVocabularyIdentity("SubjectVocabulary"))
      .expect(and { acceptedVocabularyIsQualified, vocabularyCanBeAccepted })

  run authorityBindingCanBeAccepted =
    init
      .then(observeAuthority(nativeGitHubObservation))
      .then(acceptObservedAuthority)
      .expect(and { closedAuthorityCatalogue, acceptedAuthoritiesAreQualified, authorityCanBeAccepted })

  run incompleteAuthorityIsRejected =
    not(authorityObservationIsQualified({ ...nativeGitHubObservation, complete: false }))

  run staleRevisionIsRejected =
    not(authorityObservationIsQualified({ ...nativeGitHubObservation, revisionValue: "stale-revision" }))

  run wrongAuthorityIsRejected =
    not(authorityObservationIsQualified({ ...nativeGitHubObservation, authorityId: "AUTH-PackageFeed" }))

  run contradictoryAuthorityIsRejected =
    not(authorityObservationIsQualified({ ...nativeGitHubObservation, contradictory: true }))
}
```
