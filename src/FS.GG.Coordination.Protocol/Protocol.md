# GS2-02.1 canonical coordination protocol

This document is the sole authored source for the coordination protocol baseline. Every behavioral
fact is inside a named Quint block. The generated `.qnt`, compiled contract, and F# bindings are
projections and must never be edited independently.

This unit establishes vocabulary and stable integration identities only. Later GS2-02 units refine
the inert seams for authorities, lifecycle, relations, streams, mutations, plans, desired state, and
compiled outputs. No hosted runtime or production mutation authority is defined here.

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
  )

  pure val boundCatalogue = Set(
    { id: "BOUND-VocabularyCardinality", kind: "bound", minimum: 11, maximum: 11 },
    { id: "BOUND-TraceSteps", kind: "bound", minimum: 0, maximum: 4 }
  )

  pure val verificationCatalogue = Set(
    { id: "VERIFY-VocabularyBaseline", kind: "verification", verificationKind: "bounded-invariant-and-witness", subjectIds: Set(
        "SubjectVocabulary", "AuthorityVocabulary", "CodecVocabulary", "CommandVocabulary",
        "EventVocabulary", "MutationVocabulary", "ProjectionVocabulary", "ObservationPlanVocabulary",
        "SettingsProfileVocabulary", "EvidenceObligationVocabulary", "VersionIdentityVocabulary"
      ), boundIds: Set("BOUND-VocabularyCardinality", "BOUND-TraceSteps") }
  )

  pure val compatibilityCatalogue = Set(
    { id: "COMPAT-Profile2", kind: "compatibility", surface: "fsgg-quint-profile/2", requirement: "exact", detail: "Consumer-defined structural profile; profile 1 remains frozen." }
  )

  pure val propertyCatalogue = Set(
    { id: "AcceptedVocabularyIsQualified", kind: "invariant", subjects: Set("EvidenceObligationVocabulary") },
    { id: "VocabularyCanBeAccepted", kind: "example", subjects: Set("SubjectVocabulary") }
  )

  var evidenceObserved: bool
  var acceptedVocabulary: Set[str]

  action init = all {
    evidenceObserved' = false,
    acceptedVocabulary' = Set(),
  }

  action observeProtocolEvidence: bool = all {
    evidenceObserved' = true,
    acceptedVocabulary' = acceptedVocabulary,
  }

  action acceptVocabularyIdentity(vocabularyId: str): bool = all {
    vocabularyCatalogue.exists(entry => entry.id == vocabularyId),
    evidenceObserved,
    evidenceObserved' = evidenceObserved,
    acceptedVocabulary' = acceptedVocabulary.union(Set(vocabularyId)),
  }

  action step = any {
    observeProtocolEvidence,
    acceptVocabularyIdentity("SubjectVocabulary"),
  }

  val acceptedVocabularyIsQualified = acceptedVocabulary == Set() or evidenceObserved
  val vocabularyCanBeAccepted = acceptedVocabulary.contains("SubjectVocabulary")
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
}
```
