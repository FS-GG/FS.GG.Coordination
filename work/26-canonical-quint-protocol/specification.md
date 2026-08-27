# GS2-02.1 canonical coordination protocol

This document is both the reviewer-facing requirements package and the sole authored source for the
candidate model. Every semantic identity named in the prose appears in the executable catalogue below.
The slice asks one question: can `REQ-AUDIT-001` become accepted before its required `EV-VERIFY-001`
evidence is observed? The answer must remain no.

The catalogue is data, not a compiler-node naming convention. `RequirementEntry.id`,
`EvidenceEntry.id`, and their explicit relationship are the compiled-contract inputs.

```quint requirements.qnt +=
module RequirementsSlice {
  type RequirementEntry = { id: str, evidenceId: str, priority: int }
  type EvidenceEntry = { id: str, kind: str, required: bool }
  type ActionEntry = { id: str, reads: Set[str], writes: Set[str] }
  type PropertyEntry = { id: str, kind: str }

  pure val auditRequirement =
    { id: "REQ-AUDIT-001", evidenceId: "EV-VERIFY-001", priority: 1 }
  pure val requirements = Set(auditRequirement)

  pure val evidenceCatalogue = Set(
    { id: "EV-VERIFY-001", kind: "verification", required: true }
  )
  pure val actionCatalogue = Set(
    { id: "ObserveEvidence", reads: Set("EvidenceCatalogue"), writes: Set("ObservedEvidence") },
    { id: "AcceptRequirement", reads: Set("AuditRequirement", "ObservedEvidence"), writes: Set("AcceptedRequirements") }
  )
  pure val propertyCatalogue = Set(
    { id: "AcceptedOnlyWithEvidence", kind: "invariant" },
    { id: "RequirementCanBeAccepted", kind: "reachability" }
  )

  var observedEvidence: Set[str]
  var acceptedRequirements: Set[str]

  action init = all {
    observedEvidence' = Set(),
    acceptedRequirements' = Set(),
  }

  action observeEvidence(evidenceId: str): bool = all {
    evidenceCatalogue.exists(e => e.id == evidenceId),
    observedEvidence' = observedEvidence.union(Set(evidenceId)),
    acceptedRequirements' = acceptedRequirements,
  }

  action acceptRequirement(requirementId: str): bool =
    all {
      requirementId == auditRequirement.id,
      observedEvidence.contains(auditRequirement.evidenceId),
      observedEvidence' = observedEvidence,
      acceptedRequirements' = acceptedRequirements.union(Set(requirementId)),
    }

  action step = any {
    observeEvidence("EV-VERIFY-001"),
    acceptRequirement("REQ-AUDIT-001"),
  }

  val acceptedOnlyWithEvidence =
    acceptedRequirements.contains("REQ-AUDIT-001") implies
      observedEvidence.contains("EV-VERIFY-001")

  val requirementCanBeAccepted =
    not(acceptedRequirements.contains("REQ-AUDIT-001"))
}
```

The executable example traces the only accepted path: initialization, evidence observation, then
acceptance. The deliberately absent shortcut is also meaningful: calling `acceptRequirement` first is
disabled, so injected removal of its evidence guard must make the invariant red.

```quint requirements.qnt +=
module RequirementsSliceTests {
  import RequirementsSlice.*

  run evidenceBeforeAcceptance =
    init
      .then(observeEvidence("EV-VERIFY-001"))
      .then(acceptRequirement("REQ-AUDIT-001"))
      .expect(and {
        acceptedRequirements.contains("REQ-AUDIT-001"),
        acceptedOnlyWithEvidence,
      })
}
```

No prose-only requirement exists: the requirement, evidence obligation, relationship, invariant, and
example are all explicit in the embedded Quint source.
