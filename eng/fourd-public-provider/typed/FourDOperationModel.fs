namespace FS.GG.FourD.Typed

/// Canonical authored source for the FourD operation model. The Quint file is generated only
/// into an owned temporary path so generated protocol artifacts never become repository authority.
module FourDOperationModel =
    [<Literal>]
    let Source = """module FourDOperation {
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
  pure def mutateNext(s, id) = { ...s, phase: "validated", currentIdentity: id,
    admittedIdentity: 0, budget: s.budget - 1 }
  pure def effectNext(s, acknowledged) = { ...s, phase: "effect",
    effectAcknowledged: acknowledged, outcome: if (acknowledged) "success" else "unknown",
    owned: s.owned + 1, budget: s.budget - 1 }
  pure def beginCleanupNext(s, cancelled) = { ...s, phase: "cleanup", cancelled: cancelled,
    outcome: if (s.outcome == "success") "success" else "unknown", budget: s.budget - 1 }
  pure def cancelNext(s) = if (s.phase == "finished") { ...s, cancelled: true, budget: s.budget - 1 }
    else beginCleanupNext(s, true)
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
  action cancel = all { state.phase != "cleanup-failed",
    state.budget > 0, state' = cancelNext(state) }
  action beginCleanup = all { state.phase != "cleanup", state.phase != "finished",
    state.phase != "cleanup-failed", state.budget > 0,
    state' = beginCleanupNext(state, false) }
  action closeOne = all { state.phase == "cleanup", state.closed < state.owned,
    state.budget > 0, state' = cleanupNext(state, 1) }
  action finish = all { state.phase == "cleanup", state.budget > 0, state' = finishNext(state) }
  action observeSuccess = all { state.phase == "effect", state.outcome == "unknown", state.budget > 0,
    state' = { ...state, effectAcknowledged: true, outcome: "success", budget: state.budget - 1 } }
  action step = any { acquire, validate, admit, mutate, beginEffect, acknowledgeEffect, observeSuccess, beginCleanup, cancel, closeOne, finish }

  val identitySafe = state.phase != "effect" or state.admittedIdentity == state.currentIdentity
  val cleanupTruth = state.phase != "finished" or state.closed == state.owned
  val successHasEvidence = state.outcome != "success" or state.effectAcknowledged
  val ownershipBounded = state.closed >= 0 and state.closed <= state.owned
  val budgetNeverRenews = state.budget >= 0 and state.budget <= initialState.budget
  val invariant = identitySafe and cleanupTruth and successHasEvidence and ownershipBounded and budgetNeverRenews
  val witnessAdmittedSuccess = state.phase == "effect" and state.outcome == "success"
  val witnessStaleRefusal = state.phase == "validated" and state.currentIdentity == 2 and state.admittedIdentity == 0
  val witnessCancelAfterAcquisition = state.phase == "cleanup" and state.cancelled and state.owned > 0
  val witnessLostResponse = state.phase == "effect" and state.outcome == "unknown"
  val witnessSettledUnknown = state.phase == "finished" and state.outcome == "unknown"
  val witnessCleanupFailure = state.phase == "cleanup-failed"
}

module FourDOperationCorrespondence {
  import FourDOperation as Model
  var state: Model::State
  action init = state' = Model::initialState
  action step =
    if (state.phase == "idle") state' = Model::acquireNext(state, 1)
    else if (state.phase == "acquired") state' = Model::validateNext(state)
    else if (state.phase == "validated") state' = Model::admitNext(state)
    else if (state.phase == "admitted") state' = Model::effectNext(state, false)
    else if (state.phase == "effect" and state.outcome == "unknown")
      state' = { ...state, effectAcknowledged: true, outcome: "success", budget: state.budget - 1 }
    else if (state.phase == "effect") state' = Model::beginCleanupNext(state, false)
    else if (state.phase == "cleanup" and state.closed < state.owned)
      state' = Model::cleanupNext(state, 1)
    else if (state.phase == "cleanup") state' = Model::finishNext(state)
    else state' = state
}

module FourDOperationStaleCorrespondence {
  import FourDOperation as Model
  var state: Model::State
  action init = state' = Model::initialState
  action step = if (state.phase=="idle") state'=Model::acquireNext(state,1)
    else if (state.phase=="acquired") state'=Model::validateNext(state)
    else if (state.phase=="validated" and state.currentIdentity==1) state'=Model::admitNext(state)
    else state'=Model::mutateNext(state,2)
}

module FourDOperationCancelledCorrespondence {
  import FourDOperation as Model
  var state: Model::State
  action init = state'=Model::initialState
  action step = if (state.phase=="idle") state'=Model::acquireNext(state,1)
    else if (state.phase=="acquired") state'=Model::beginCleanupNext(state,true)
    else if (state.phase=="cleanup" and state.closed<state.owned) state'=Model::cleanupNext(state,1)
    else state'=Model::finishNext(state)
}

module FourDOperationUnknownCorrespondence {
  import FourDOperation as Model
  var state: Model::State
  action init = state'=Model::initialState
  action step = if (state.phase=="idle") state'=Model::acquireNext(state,1)
    else if (state.phase=="acquired") state'=Model::validateNext(state)
    else if (state.phase=="validated") state'=Model::admitNext(state)
    else if (state.phase=="admitted") state'=Model::effectNext(state,false)
    else if (state.phase=="effect") state'=Model::beginCleanupNext(state,false)
    else if (state.phase=="cleanup" and state.closed<state.owned) state'=Model::cleanupNext(state,1)
    else state'=Model::finishNext(state)
}

module FourDOperationCleanupFailureCorrespondence {
  import FourDOperation as Model
  var state: Model::State
  action init = state'=Model::initialState
  action step = if (state.phase=="idle") state'=Model::acquireNext(state,1)
    else if (state.phase=="acquired") state'=Model::validateNext(state)
    else if (state.phase=="validated") state'=Model::admitNext(state)
    else if (state.phase=="admitted") state'=Model::effectNext(state,false)
    else if (state.phase=="effect") state'=Model::beginCleanupNext(state,false)
    else if (state.phase=="cleanup" and state.closed==0) state'=Model::cleanupNext(state,1)
    else state'=Model::finishNext(state)
}

module FourDRunObservation {
  type State = {
    identity: int, rerunSends: int, rerun: str, seenTarget2: bool, sequence: int,
    elapsed: int, nextPoll: int, target: str, cancel: str, settlement: str,
    resourcesRetired: bool, resourceFactsClosed: bool, resultAccepted: bool,
    refused: bool, signalCancelled: bool, cleanupStarted: bool, cleanupFreshCurrent: bool,
    nextAction: str, budget: int, remoteClosed: bool,
  }
  pure val initialState = {
    identity: 1, rerunSends: 0, rerun: "not-issued", seenTarget2: false,
    sequence: 0, elapsed: 0, nextPoll: 0, target: "none", cancel: "none",
    settlement: "none", resourcesRetired: false, resourceFactsClosed: false,
    resultAccepted: false, refused: false, signalCancelled: false,
    cleanupStarted: false, cleanupFreshCurrent: false,
    nextAction: "none", budget: 2700, remoteClosed: false,
  }
  pure def charged(s, elapsed) = { ...s, elapsed: elapsed, nextPoll: elapsed,
    budget: s.budget - (elapsed - s.elapsed) }
  pure def intent(s, elapsed) = if (s.rerun == "not-issued" and elapsed < 2460)
    { ...charged(s, elapsed), rerunSends: 1, rerun: "intent-recorded", nextAction: "issue-rerun" }
    else refuse(s, elapsed)
  pure def acknowledge(s, elapsed) = if (s.rerun == "intent-recorded")
    { ...charged(s, elapsed), rerun: "acknowledged", nextAction: "observe-current" } else refuse(s,elapsed)
  pure def uncertain(s, elapsed) = { ...charged(s, elapsed), rerun: "uncertain", refused: true, nextAction: "retire-resources" }
  pure def stale1(s, elapsed, sequence) = { ...charged(s, elapsed), sequence: sequence,
    nextPoll: elapsed + 20, nextAction: "wait" }
  pure def targetActive(s, elapsed, sequence) = { ...charged(s, elapsed), seenTarget2: true,
    sequence: sequence, nextPoll: elapsed + 20, nextAction: "wait" }
  pure def currentTerminal(s, elapsed, sequence, conclusion) = { ...charged(s, elapsed), seenTarget2: true,
    sequence: sequence, target: conclusion, settlement: "current-terminal", nextAction: "observe-target-and-jobs" }
  pure def targetTerminal(s, elapsed, sequence) = { ...charged(s, elapsed), sequence: sequence,
    settlement: "target-terminal", nextAction: "observe-target-and-jobs" }
  pure def jobsTerminal(s, elapsed, sequence) = { ...charged(s, elapsed), sequence: sequence,
    settlement: "jobs-terminal", nextAction: "observe-current" }
  pure def bracket(s, elapsed, sequence) = { ...charged(s, elapsed), sequence: sequence,
    settlement: "settled", nextAction: if (s.resourcesRetired) (if (s.target == "success" and s.cancel == "none" and not(s.refused) and not(s.signalCancelled)) "continue-result-readback" else "finish-refused") else "retire-resources",
    remoteClosed: s.resourceFactsClosed }
  pure def enterCleanup(s, elapsed, signal) = { ...charged(s, elapsed), cleanupStarted: true,
    signalCancelled: s.signalCancelled or signal,
    nextAction: if (s.settlement == "none" and s.rerun == "acknowledged") "observe-current" else "retire-resources" }
  pure def freshCleanup(s, elapsed, sequence) = { ...charged(s, elapsed), sequence: sequence,
    cleanupFreshCurrent: true, cancel: if (not(s.refused) and elapsed < 2460) "intent" else s.cancel,
    nextAction: if (not(s.refused) and elapsed < 2460) "cancel-once" else "retire-resources" }
  pure def cancelResult(s, elapsed, accepted) = { ...charged(s, elapsed),
    cancel: if (accepted) "accepted" else "refused", nextAction: "retire-resources" }
  pure def retire(s, elapsed) = { ...charged(s, elapsed), resourcesRetired: true,
    resourceFactsClosed: true, remoteClosed: s.settlement == "settled",
    nextPoll: if (s.settlement == "settled") elapsed else elapsed + 20,
    nextAction: if (s.settlement == "settled") (if (s.target == "success" and s.cancel == "none" and not(s.refused) and not(s.signalCancelled)) "continue-result-readback" else "finish-refused") else "observe-current" }
  pure def readback(s, elapsed) = if (not(s.refused) and not(s.signalCancelled) and s.remoteClosed)
    { ...charged(s, elapsed), resultAccepted: true, nextAction: "complete" } else refuse(s,elapsed)
  pure def signal(s, elapsed) = { ...charged(s,elapsed), signalCancelled: true, resultAccepted: false,
    nextPoll: if(s.resourcesRetired and s.settlement != "settled") elapsed + 20 else elapsed,
    nextAction: if(s.resourcesRetired) (if(s.settlement=="settled") "finish-refused" else "wait") else "retire-resources" }
  pure def refuse(s, elapsed) = { ...charged(s, elapsed), refused: true, nextAction: "refuse" }
  pure def refuseObserved(s, elapsed, sequence) = { ...refuse(s, elapsed), sequence: sequence }
  pure def canObserveTerminal(s) = s.rerun == "acknowledged" and s.seenTarget2 and s.settlement == "none"

  var state: State
  action init = state' = initialState
  action recordIntent = all { state.rerun == "not-issued", state.elapsed < 2460, state' = intent(state, state.elapsed + 1) }
  action recordAck = all { state.rerun == "intent-recorded", state' = acknowledge(state, state.elapsed + 1) }
  action recordLost = all { state.rerun == "intent-recorded", state' = uncertain(state, state.elapsed + 1) }
  action observeStale = all { state.rerun == "acknowledged", not(state.seenTarget2), state' = stale1(state,state.elapsed+20,state.sequence+1) }
  action observeActive = all { state.rerun == "acknowledged", state.settlement == "none", not(state.cleanupStarted), state' = targetActive(state,state.elapsed+20,state.sequence+1) }
  action observeCleanupActive = all { state.rerun == "acknowledged", state.settlement == "none", state.cleanupStarted,
    state.cleanupFreshCurrent, state' = targetActive(state,state.elapsed+20,state.sequence+1) }
  action observeSuccess = all { canObserveTerminal(state), state' = currentTerminal(state,state.elapsed+20,state.sequence+1,"success") }
  action observeFailure = all { canObserveTerminal(state), state' = currentTerminal(state,state.elapsed+1,state.sequence+1,"failure") }
  action confirmTarget = all { state.settlement == "current-terminal", state' = targetTerminal(state,state.elapsed+1,state.sequence+1) }
  action confirmJobs = all { state.settlement == "target-terminal", state' = jobsTerminal(state,state.elapsed+1,state.sequence+1) }
  action confirmBracket = all { state.settlement == "jobs-terminal", state' = bracket(state,state.elapsed+1,state.sequence+1) }
  action beginCleanup = all { not(state.cleanupStarted), state' = enterCleanup(state,state.elapsed+1,false) }
  action observeFresh = all { state.cleanupStarted, not(state.cleanupFreshCurrent), state.settlement == "none", state' = freshCleanup(state,state.elapsed+1,state.sequence+1) }
  action acceptCancel = all { state.cancel == "intent", state' = cancelResult(state,state.elapsed+1,true) }
  action refuseCancel = all { state.cancel == "intent", state' = cancelResult(state,state.elapsed+1,false) }
  action retireResources = all { state.cleanupStarted, not(state.resourcesRetired), state' = retire(state,state.elapsed+1) }
  action acceptReadback = all { state.settlement == "settled", state.resourceFactsClosed, state.target == "success", state.cancel == "none", not(state.refused), not(state.signalCancelled), state' = readback(state,state.elapsed+1) }
  action wrongIdentity = all { not(state.refused), state' = refuse(state,state.elapsed+1) }
  action observeSignal = all { not(state.signalCancelled), state' = signal(state,state.elapsed+1) }
  action stutter = state' = state
  action deadline = all { state.elapsed < 2700, state' = refuse(state,2700) }
  action step = if (state.budget <= 0) state'=state else if (state.resultAccepted) any { observeSignal, stutter } else any {
    recordIntent, recordAck, recordLost, observeStale, observeActive, observeCleanupActive, observeSuccess, observeFailure,
    confirmTarget, confirmJobs, confirmBracket, beginCleanup, observeFresh, acceptCancel, refuseCancel,
    retireResources, acceptReadback, wrongIdentity, observeSignal, deadline }

  val invariant = state.rerunSends <= 1 and state.identity == 1
    and (not(state.resultAccepted) or (state.seenTarget2 and state.remoteClosed and state.target=="success" and state.cancel=="none" and not(state.refused) and not(state.signalCancelled)))
    and (not(state.remoteClosed) or (state.settlement=="settled" and state.resourceFactsClosed))
    and (not(state.resourcesRetired) or (state.cleanupStarted and state.resourceFactsClosed))
    and (not(state.cleanupFreshCurrent) or state.cleanupStarted)
    and state.budget >= 0 and state.budget <= 2700 and (state.nextPoll==0 or state.nextPoll>=state.elapsed)
  pure def invariantOf(s) = s.rerunSends <= 1 and s.identity == 1
    and (not(s.resultAccepted) or (s.seenTarget2 and s.remoteClosed and s.target=="success" and s.cancel=="none" and not(s.refused) and not(s.signalCancelled)))
    and (not(s.remoteClosed) or (s.settlement=="settled" and s.resourceFactsClosed))
    and (not(s.resourcesRetired) or (s.cleanupStarted and s.resourceFactsClosed))
    and (not(s.cleanupFreshCurrent) or s.cleanupStarted)
    and s.budget>=0 and s.budget<=2700 and (s.nextPoll==0 or s.nextPoll>=s.elapsed)
  val witnessPostCleanupTerminal = state.cleanupStarted and state.resourcesRetired and state.settlement == "settled"
}

module FourDRunSuccessCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2)
  else if(state.sequence==0)state'=M::stale1(state,3,1) else if(state.sequence==1)state'=M::targetActive(state,23,2)
  else if(state.sequence==2)state'=M::currentTerminal(state,43,3,"success") else if(state.settlement=="current-terminal")state'=M::targetTerminal(state,44,4)
  else if(state.settlement=="target-terminal")state'=M::jobsTerminal(state,45,5) else if(state.settlement=="jobs-terminal")state'=M::bracket(state,46,6)
  else if(not(state.cleanupStarted))state'=M::enterCleanup(state,47,false) else if(not(state.resourcesRetired))state'=M::retire(state,48) else state'=M::readback(state,49)
 val invariant=M::invariantOf(state) val witness=state.resultAccepted and state.remoteClosed }
module FourDRunCancelRaceCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2)
  else if(not(state.seenTarget2))state'=M::targetActive(state,3,1) else if(not(state.cleanupStarted))state'=M::enterCleanup(state,23,false)
  else if(not(state.cleanupFreshCurrent))state'=M::freshCleanup(state,24,2) else if(state.cancel=="intent")state'=M::cancelResult(state,25,false)
  else if(not(state.resourcesRetired))state'=M::retire(state,26) else if(M::canObserveTerminal(state))state'=M::currentTerminal(state,46,3,"failure")
  else if(state.settlement=="current-terminal")state'=M::targetTerminal(state,47,4) else if(state.settlement=="target-terminal")state'=M::jobsTerminal(state,48,5) else state'=M::bracket(state,49,6)
 val invariant=M::invariantOf(state) val witness=state.cancel=="refused" and state.settlement=="settled" and state.remoteClosed }
module FourDRunDeadlineCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2)
  else if(state.sequence==0)state'=M::stale1(state,3,1) else if(state.sequence==1)state'=M::stale1(state,23,2) else state'=M::refuse(state,2700)
 val invariant=M::invariantOf(state) val witness=state.refused and state.budget==0 }
module FourDRunCancelAcceptedCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2)
  else if(not(state.seenTarget2))state'=M::targetActive(state,3,1) else if(not(state.cleanupStarted))state'=M::enterCleanup(state,23,false)
  else if(not(state.cleanupFreshCurrent))state'=M::freshCleanup(state,24,2) else if(state.cancel=="intent")state'=M::cancelResult(state,25,true)
  else if(not(state.resourcesRetired))state'=M::retire(state,26) else if(M::canObserveTerminal(state))state'=M::currentTerminal(state,46,3,"cancelled")
  else if(state.settlement=="current-terminal")state'=M::targetTerminal(state,47,4) else if(state.settlement=="target-terminal")state'=M::jobsTerminal(state,48,5) else state'=M::bracket(state,49,6)
 val invariant=M::invariantOf(state) val witness=state.cancel=="accepted" and state.settlement=="settled" and state.remoteClosed and not(state.resultAccepted) }
module FourDRunWrongIdentityCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2) else state'=M::refuseObserved(state,3,1)
 val invariant=M::invariantOf(state) val witness=state.refused and state.rerunSends==1 }
module FourDRunPartialJobsCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2)
  else if(not(state.seenTarget2))state'=M::targetActive(state,3,1) else if(state.settlement=="none")state'=M::currentTerminal(state,23,2,"failure")
  else if(state.settlement=="current-terminal")state'=M::targetTerminal(state,24,3) else if(not(state.refused))state'=M::refuseObserved(state,25,4)
  else if(not(state.cleanupStarted))state'=M::enterCleanup(state,26,false) else if(not(state.resourcesRetired))state'=M::retire(state,27)
  else if(state.elapsed<47)state'=M::refuseObserved(state,47,5) else state'=M::refuse(state,2700)
 val invariant=M::invariantOf(state) val witness=state.refused and state.budget==0 }

module FourDRunSignalCancelCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2)
  else if(not(state.seenTarget2))state'=M::targetActive(state,3,1) else if(not(state.signalCancelled))state'=M::signal(state,4)
  else if(not(state.cleanupStarted))state'=M::enterCleanup(state,5,true) else if(not(state.cleanupFreshCurrent))state'=M::freshCleanup(state,6,2)
  else if(state.cancel=="intent")state'=M::cancelResult(state,7,true) else if(not(state.resourcesRetired))state'=M::retire(state,8)
  else if(M::canObserveTerminal(state))state'=M::currentTerminal(state,28,3,"cancelled")
  else if(state.settlement=="current-terminal")state'=M::targetTerminal(state,29,4) else if(state.settlement=="target-terminal")state'=M::jobsTerminal(state,30,5) else state'=M::bracket(state,31,6)
 val invariant=M::invariantOf(state) val witness=state.signalCancelled and state.cancel=="accepted" and state.remoteClosed and not(state.resultAccepted) }
module FourDRunRetiredActiveSignalCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2)
  else if(not(state.seenTarget2))state'=M::targetActive(state,3,1) else if(not(state.cleanupStarted))state'=M::enterCleanup(state,23,false)
  else if(not(state.cleanupFreshCurrent))state'=M::freshCleanup(state,24,2) else if(state.cancel=="intent")state'=M::cancelResult(state,25,true)
  else if(not(state.resourcesRetired))state'=M::retire(state,26) else if(state.sequence==2)state'=M::targetActive(state,46,3)
  else if(not(state.signalCancelled))state'=M::signal(state,47) else if(M::canObserveTerminal(state))state'=M::currentTerminal(state,67,4,"cancelled")
  else if(state.settlement=="current-terminal")state'=M::targetTerminal(state,68,5) else if(state.settlement=="target-terminal")state'=M::jobsTerminal(state,69,6) else state'=M::bracket(state,70,7)
 val invariant=M::invariantOf(state) val witness=state.signalCancelled and state.resourcesRetired and state.remoteClosed and not(state.resultAccepted) }
module FourDRunLateSignalCorrespondence {
 import FourDRunObservation as M var state:M::State action init=state'=M::initialState
 action step=if(state.rerun=="not-issued")state'=M::intent(state,1) else if(state.rerun=="intent-recorded")state'=M::acknowledge(state,2)
  else if(state.sequence==0)state'=M::stale1(state,3,1) else if(state.sequence==1)state'=M::targetActive(state,23,2)
  else if(state.sequence==2)state'=M::currentTerminal(state,43,3,"success") else if(state.settlement=="current-terminal")state'=M::targetTerminal(state,44,4)
  else if(state.settlement=="target-terminal")state'=M::jobsTerminal(state,45,5) else if(state.settlement=="jobs-terminal")state'=M::bracket(state,46,6)
  else if(not(state.cleanupStarted))state'=M::enterCleanup(state,47,false) else if(not(state.resourcesRetired))state'=M::retire(state,48)
  else if(not(state.resultAccepted) and not(state.signalCancelled))state'=M::readback(state,49) else state'=M::signal(state,50)
 val invariant=M::invariantOf(state) val witness=state.signalCancelled and not(state.resultAccepted) and state.remoteClosed }

"""
