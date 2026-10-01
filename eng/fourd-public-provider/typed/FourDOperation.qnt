module FourDOperation {
  type State = {
    phase: str,
    acquiredIdentity: int,
    validatedIdentity: int,
    admittedIdentity: int,
    currentIdentity: int,
    effectAcknowledged: bool,
    outcome: str,
    owned: int,
    closed: int,
    cancelled: bool,
    budget: int,
  }

  pure val initialState = {
    phase: "idle", acquiredIdentity: 0, validatedIdentity: 0,
    admittedIdentity: 0, currentIdentity: 0, effectAcknowledged: false,
    outcome: "none", owned: 0, closed: 0, cancelled: false, budget: 12,
  }

  pure def acquireNext(s, id) = { ...s, phase: "acquired", acquiredIdentity: id,
    currentIdentity: id, owned: s.owned + 1, budget: s.budget - 1 }
  pure def validateNext(s) = { ...s, phase: "validated",
    validatedIdentity: s.currentIdentity, budget: s.budget - 1 }
  pure def admitNext(s) = { ...s, phase: "admitted",
    admittedIdentity: s.validatedIdentity, budget: s.budget - 1 }
  pure def mutateNext(s, id) = { ...s, phase: "invalidated", currentIdentity: id,
    budget: s.budget - 1 }
  pure def effectNext(s, acknowledged) = { ...s, phase: "effect",
    effectAcknowledged: acknowledged, outcome: if (acknowledged) "success" else "unknown",
    owned: s.owned + 1, budget: s.budget - 1 }
  pure def cancelNext(s) = { ...s, phase: "cleanup", cancelled: true,
    outcome: if (s.outcome == "success") "success" else "unknown", budget: s.budget - 1 }
  pure def cleanupNext(s, count) = { ...s, phase: "cleanup", closed: s.closed + count,
    budget: s.budget - 1 }
  pure def finishNext(s) = { ...s,
    phase: if (s.closed == s.owned) "finished" else "cleanup-failed",
    budget: s.budget - 1 }

  var state: State
  action init = state' = initialState
  action acquire = all { state.phase == "idle", state.budget > 0, state' = acquireNext(state, 1) }
  action validate = all { state.phase == "acquired", state.budget > 0, state' = validateNext(state) }
  action admit = all { state.phase == "validated", state.budget > 0,
    state.validatedIdentity == state.currentIdentity, state' = admitNext(state) }
  action mutate = all { state.phase == "validated" or state.phase == "admitted",
    state.budget > 0, state' = mutateNext(state, 2) }
  action beginEffect = all { state.phase == "admitted", state.budget > 0,
    state.admittedIdentity == state.currentIdentity, state' = effectNext(state, false) }
  action acknowledgeEffect = all { state.phase == "admitted", state.budget > 0,
    state.admittedIdentity == state.currentIdentity, state' = effectNext(state, true) }
  action cancel = all { state.phase != "finished", state.phase != "cleanup-failed",
    state.budget > 0, state' = cancelNext(state) }
  action closeOne = all { state.phase == "cleanup", state.closed < state.owned,
    state.budget > 0, state' = cleanupNext(state, 1) }
  action finish = all { state.phase == "cleanup", state.budget > 0, state' = finishNext(state) }
  action observeSuccess = all { state.phase == "effect", state.outcome == "unknown", state.budget > 0,
    state' = { ...state, effectAcknowledged: true, outcome: "success", budget: state.budget - 1 } }
  action step = any { acquire, validate, admit, mutate, beginEffect, acknowledgeEffect, observeSuccess, cancel, closeOne, finish }

  val identitySafe = state.phase != "effect" or state.admittedIdentity == state.currentIdentity
  val cleanupTruth = state.phase != "finished" or state.closed == state.owned
  val successHasEvidence = state.outcome != "success" or state.effectAcknowledged
  val ownershipBounded = state.closed >= 0 and state.closed <= state.owned
  val budgetNeverRenews = state.budget >= 0 and state.budget <= initialState.budget
  val invariant = identitySafe and cleanupTruth and successHasEvidence and ownershipBounded and budgetNeverRenews
  val witnessAdmittedSuccess = state.phase == "effect" and state.outcome == "success"
  val witnessStaleRefusal = state.phase == "invalidated"
  val witnessCancelAfterAcquisition = state.phase == "cleanup" and state.cancelled and state.owned > 0
  val witnessLostResponse = state.phase == "effect" and state.outcome == "unknown"
  val witnessSettledUnknown = state.phase == "finished" and state.outcome == "unknown"
  val witnessCleanupFailure = state.phase == "cleanup-failed"
}
