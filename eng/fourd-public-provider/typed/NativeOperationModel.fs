namespace FS.GG.FourD.Typed

/// Authored single-owner native protocol. Generate the .qnt into a temporary directory.
/// Opaque generations abstract the acquired-byte constructors; cryptographic and OS facts
/// remain constructor/adapter obligations covered by the production CLI controls.
module NativeOperationModel =
    [<Literal>]
    let Source = """module FourDNativeOperation {
  type State = {
    rootRefused: bool, remoteObservationElapsed: int, remoteConclusion: str, remoteCredentialRetirement: bool,
    ownedRoles: Set[str], closedRoles: Set[str], issued: Set[str], acknowledged: Set[str],
    preflightDeadline: int, imageDeadline: int, p2Deadline: int, cleanupDeadline: int, collectionDeadline: int,
    collectionAllowance: int, cleanupAllowance: int, p2Command: int, p2Operation: int, wrapper: int, termination: int, drain: int, handoff: int,
    mode: str, phase: str, elapsed: int, producer: bool, root: bool,
    archive: int, currentArchive: int, source: int, current: int, resources: bool, revalidated: bool,
    image: bool, p2: bool, p2Ready: bool, latestAck: bool, cleanupIssued: bool, intents: int, acks: int, owned: int, closed: int,
    cancelled: bool, refused: bool, outcome: str,
    cleanupStarted: int, collectionStarted: int
  }
  pure def initial(mode) = {
    rootRefused: false, remoteObservationElapsed: -1, remoteConclusion: "unknown", remoteCredentialRetirement: false,
    ownedRoles: Set(), closedRoles: Set(), issued: Set(), acknowledged: Set(),
    preflightDeadline: if (mode == "full") 600 else 540, imageDeadline: if (mode == "full") 3325 else 0,
    p2Deadline: if (mode == "full") 4740 else 0, cleanupDeadline: if (mode == "full") 5220 else 1020, collectionDeadline: if (mode == "full") 5400 else 1200,
    collectionAllowance: 180, cleanupAllowance: 480, p2Command: 1200, p2Operation: 900, wrapper: 120, termination: 20, drain: 15, handoff: 60,
    mode: mode, phase: "bootstrap", elapsed: 0, producer: false, root: false,
    archive: 0, currentArchive: 0, source: 0, current: 0, resources: false, revalidated: false,
    image: false, p2: false, p2Ready: false, latestAck: false, cleanupIssued: false, intents: 0, acks: 0, owned: 0, closed: 0,
    cancelled: false, refused: false, outcome: "none", cleanupStarted: -1, collectionStarted: -1
  }
  pure def alive(s) = not(s.cancelled) and not(s.refused)
  pure def limit(s) = if (s.mode == "full") 5400 else 1200
  pure def phaseLimit(s, op) = if (s.mode == "full") {
    if (op == "source" or op == "preflight") 600 else if (op == "image") 3325 else if (op == "p2") 4740 else if (op == "cleanup") 5220 else 5400
  } else {
    if (op == "source" or op == "preflight") 540 else if (op == "cleanup") 1020 else 1200
  }
  pure def intentNext(s, op) = { ...s, phase: if (op == "cleanup") "cleanup" else if (op == "collection") "collecting" else if (op == "source") "source-running" else if (op == "preflight") "preflight-running" else if (op == "image") "image-running" else "p2-running",
    issued: s.issued.union(Set(op)), ownedRoles: s.ownedRoles.union(Set(op)), intents: s.intents + 1, owned: s.owned + 1, revalidated: false, latestAck: false, cleanupIssued: s.cleanupIssued or op == "cleanup",
    outcome: if (op == "image" or op == "p2") "unknown" else s.outcome,
    collectionStarted: if (op == "collection") s.elapsed else s.collectionStarted }
  pure def ackNext(s, op) = { ...s, acknowledged: s.acknowledged.union(Set(op)), acks: s.acks + 1, latestAck: true }
  pure def refuseNext(s, cancelled) = { ...s, refused: true, cancelled: s.cancelled or cancelled,
    outcome: if (s.outcome == "none" or s.outcome == "refused") "refused" else "unknown" }
  pure def cleanupNext(s) = { ...s, phase: "cleanup", cleanupStarted: s.elapsed,
    outcome: if (s.outcome == "none") "unknown" else s.outcome }
  pure def closeNext(s, role) = { ...s, closedRoles: s.closedRoles.union(Set(role)), closed: s.closed + 1 }
  pure def nextCloseRole(s) =
    if (s.ownedRoles.contains("cleanup") and not(s.closedRoles.contains("cleanup"))) "cleanup"
    else if (s.ownedRoles.contains("image") and not(s.closedRoles.contains("image"))) "image"
    else if (s.ownedRoles.contains("p2") and not(s.closedRoles.contains("p2"))) "p2"
    else if (s.ownedRoles.contains("preflight") and not(s.closedRoles.contains("preflight"))) "preflight"
    else if (s.ownedRoles.contains("key-intent") and not(s.closedRoles.contains("key-intent"))) "key-intent"
    else if (s.ownedRoles.contains("key") and not(s.closedRoles.contains("key"))) "key"
    else "source"
  var state: State
  action init = state' = initial("full")
  action initCapability = state' = initial("capability")
  action producer = all { alive(state), state.phase == "bootstrap", state.elapsed < phaseLimit(state,"source"),
    state' = { ...state, producer: true, phase: "packet-wait" } }
  action packet = all { alive(state), state.phase == "packet-wait", state.elapsed < phaseLimit(state,"source"),
    state' = { ...state, root: true, phase: "bound" } }
  action capsule = all { alive(state), state.phase == "bound", state' = { ...state, phase: "capsule-ready", ownedRoles: state.ownedRoles.union(Set("key-intent")), owned: state.owned + 1 } }
  action key = all { alive(state), state.phase == "capsule-ready", state.root, state.producer,
    state' = { ...state, phase: "key-ready", ownedRoles: state.ownedRoles.union(Set("key")), owned: state.owned + 1 } }
  action intentSource = all { alive(state), state.phase == "key-ready", state.elapsed < phaseLimit(state,"source"), state' = intentNext(state,"source") }
  action ackSource = all { state.phase == "source-running", not(state.latestAck), state.elapsed < phaseLimit(state,"source"), state' = ackNext(state,"source") }
  action source = all { alive(state), state.phase == "source-running", state.latestAck, state.elapsed < phaseLimit(state,"source"),
    state' = { ...state, source: 1, current: 1, phase: "source-ready" } }
  action resources = all { alive(state), state.phase == "source-ready", state' = { ...state, resources: true } }
  action intentPreflight = all { alive(state), state.phase == "source-ready", state.resources, state.elapsed < phaseLimit(state,"preflight"), state' = intentNext(state,"preflight") }
  action ackPreflight = all { state.phase == "preflight-running", not(state.latestAck), state.elapsed < phaseLimit(state,"preflight"), state' = ackNext(state,"preflight") }
  action preflight = all { alive(state), state.phase == "preflight-running", state.latestAck, state.source == state.current, state.source != 0, state.elapsed < phaseLimit(state,"preflight"),
    state' = { ...state, phase: "preflight-ready", outcome: if (state.mode == "capability") "capability-complete" else state.outcome } }
  action revalidate = all { alive(state), state.phase == "preflight-ready" or state.phase == "image-ready", state.source == state.current, state.source != 0,
    state' = { ...state, revalidated: true } }
  action intentImage = all { alive(state), state.mode == "full", state.phase == "preflight-ready", state.revalidated, state.source == state.current, state.elapsed < phaseLimit(state,"image"), state' = intentNext(state,"image") }
  action ackImage = all { state.phase == "image-running", not(state.latestAck), state.elapsed < phaseLimit(state,"image"), state' = ackNext(state,"image") }
  action revalidateBuild = all { alive(state), state.phase == "image-running", state.latestAck, state.source == state.current, state.elapsed < phaseLimit(state,"image"), state' = { ...state, revalidated: true } }
  action archive = all { alive(state), state.mode == "full", state.phase == "image-running", state.latestAck, state.revalidated, state.source != 0, state.source == state.current, state.elapsed < phaseLimit(state,"image"), state.currentArchive != 2,
    state' = { ...state, archive: 1, currentArchive: 1 } }
  action mutateArchive = all { alive(state), state.phase == "image-running", state.archive == 1, state.currentArchive == 1, state.revalidated, state.elapsed < phaseLimit(state,"image"),
    state' = refuseNext({ ...state, currentArchive: 2 },false) }
  action image = all { alive(state), state.archive == 1, state.currentArchive == state.archive, state.revalidated, state.mode == "full", state.phase == "image-running", state.latestAck, state.source == state.current, state.elapsed < phaseLimit(state,"image"),
    state' = { ...state, image: true, phase: "image-ready", revalidated: false } }
  action revalidateP2 = all { alive(state), state.phase == "image-ready", state.mode == "full", state' = { ...state, p2Ready: true } }
  action intentP2 = all { alive(state), state.p2Ready, state.mode == "full", state.phase == "image-ready", state.revalidated, state.source == state.current, phaseLimit(state,"p2") - state.elapsed >= 1415, state' = intentNext(state,"p2") }
  action ackP2 = all { state.phase == "p2-running", not(state.latestAck), state.elapsed < phaseLimit(state,"p2"), state' = ackNext(state,"p2") }
  action p2 = all { alive(state), state.mode == "full", state.phase == "p2-running", state.latestAck, state.image, state.source == state.current, state.elapsed < phaseLimit(state,"p2"),
    state' = { ...state, p2: true, phase: "p2-ready", outcome: "success" } }
  action cancel = all { not(state.cancelled), state' = refuseNext(state,true) }
  action mutate = all { state.source == 1, state.current == 1, state' = refuseNext({ ...state, current: 2 },false) }
  action refuse = all { not(state.refused), state' = refuseNext(state,false) }
  action waitExpiry = all { state.phase == "packet-wait", state' = refuseNext({ ...state, elapsed: phaseLimit(state,"source") },false) }
  action beginCleanup = all { state.cleanupStarted == -1, state.phase != "finished", state.phase != "incomplete", state' = cleanupNext(state) }
  action intentCleanup = all { state.phase == "cleanup", not(state.cleanupIssued), state.elapsed < phaseLimit(state,"cleanup"), state' = intentNext(state,"cleanup") }
  action ackCleanup = all { state.phase == "cleanup", state.issued.contains("cleanup"), not(state.latestAck), state.elapsed < phaseLimit(state,"cleanup"), state' = ackNext(state,"cleanup") }
  action close = {
    nondet role = state.ownedRoles.exclude(state.closedRoles).oneOf()
    all { state.phase == "cleanup", state.closed < state.owned, state' = closeNext(state,role) }
  }
  action closeOrdered = all { state.phase == "cleanup", state.closed < state.owned,
    state' = closeNext(state,nextCloseRole(state)) }
  action retired = all { state.phase == "cleanup", state' = { ...state,
    phase: if (state.closed == state.owned and state.elapsed <= phaseLimit(state,"cleanup") and state.elapsed-state.cleanupStarted <= 480) "retired" else "incomplete" } }
  action intentCollection = all { state.phase == "retired", state.elapsed < phaseLimit(state,"collection"), state' = intentNext(state,"collection") }
  action ackCollection = all { state.phase == "collecting", not(state.latestAck), state.elapsed < phaseLimit(state,"collection"), state' = ackNext(state,"collection") }
  action closeCollection = all { state.phase == "collecting", state.closed < state.owned, state' = closeNext(state,"collection") }
  action finish = all { state.phase == "collecting", state' = { ...state,
    phase: if (state.elapsed-state.collectionStarted <= 180 and state.elapsed <= limit(state) and state.closed == state.owned and state.latestAck) "finished" else "incomplete" } }
  action tick = {
    nondet elapsed = Set(1, 539, 540, 600, 3325, 4740, 5220, 5400).oneOf()
    all { elapsed > state.elapsed, elapsed <= limit(state), state' = { ...state, elapsed: elapsed } }
  }
  action remoteFault = all { state.root, state.phase == "finished" or state.phase == "incomplete", state' = { ...state, rootRefused: true, remoteObservationElapsed: state.elapsed } }
  action remoteSuccess = all { state.root, state.phase == "finished" or state.phase == "incomplete", state' = { ...state, remoteObservationElapsed: state.elapsed, remoteConclusion: "success" } }
  action remoteFailure = all { state.root, state.phase == "finished" or state.phase == "incomplete", state' = { ...state, remoteObservationElapsed: state.elapsed, remoteConclusion: "failure" } }
  action remoteRetirement = all { state.root, state.phase == "finished" or state.phase == "incomplete", state' = { ...state, remoteObservationElapsed: state.elapsed, remoteCredentialRetirement: true } }
  action step = any { producer,packet,capsule,key,intentSource,ackSource,source,resources,intentPreflight,ackPreflight,preflight,revalidate,intentImage,ackImage,revalidateBuild,archive,mutateArchive,image,revalidateP2,intentP2,ackP2,p2,cancel,mutate,refuse,waitExpiry,beginCleanup,intentCleanup,ackCleanup,close,retired,intentCollection,ackCollection,closeCollection,finish,tick,remoteFault,remoteSuccess,remoteFailure,remoteRetirement }
  val identitySafe = state.source == state.current or state.outcome != "success"
  val keyBinding = not(state.ownedRoles.contains("key")) or (state.root and state.producer)
  val archiveBeforeImage = not(state.image) or state.archive == 1
  val changedArchiveRefuses = state.currentArchive != 2 or state.refused
  val nativeEvidence = state.outcome != "success" or (state.mode == "full" and state.p2 and state.image and state.source != 0 and not(state.cancelled) and not(state.refused))
  val capabilityCannotP2 = state.mode != "capability" or (not(state.image) and not(state.p2))
  val ownership = state.closed >= 0 and state.closed <= state.owned and state.closedRoles.subseteq(state.ownedRoles) and state.closedRoles.size() == state.closed and state.ownedRoles.size() == state.owned and state.acknowledged.subseteq(state.issued)
  val retirementTruth = state.phase != "finished" or state.closed == state.owned
  val aggregateClock = state.elapsed >= 0 and state.elapsed <= limit(state)
  val invariant = identitySafe and keyBinding and nativeEvidence and capabilityCannotP2 and ownership and retirementTruth and aggregateClock and wholeRequiresSeparateFacts and archiveBeforeImage and changedArchiveRefuses
  val localQualified = state.mode == "full" and state.phase == "finished" and state.outcome == "success" and state.p2 and not(state.cancelled) and not(state.refused)
  val wholeAccepted = localQualified and state.remoteConclusion == "success" and state.remoteCredentialRetirement and not(state.rootRefused)
  val wholeRequiresSeparateFacts = not(wholeAccepted) or (state.remoteConclusion == "success" and state.remoteCredentialRetirement and localQualified)
  val witnessArchiveAdmitted = state.archive == 1 and state.currentArchive == 1 and state.phase == "image-running"
  val witnessArchiveMutationRefused = state.archive == 1 and state.currentArchive == 2 and state.refused and not(state.image)
  val witnessWholeAccepted = wholeAccepted
  val witnessCapability = state.mode == "capability" and state.outcome == "capability-complete" and state.phase == "finished" and not(state.p2)
  val witnessNativeSuccess = state.outcome == "success" and state.phase == "finished"
  val witnessUnacknowledged = state.phase == "image-running" and not(state.latestAck) and state.outcome == "unknown"
  val witnessCancelledPlaintext = state.cancelled and state.source != 0
  val witnessSettledUnknown = state.phase == "finished" and state.outcome == "unknown"
  val witnessChangedGeneration = state.current == 2 and state.refused
  val witnessPacketWaitExpiry = state.phase == "packet-wait" and state.refused and state.elapsed == phaseLimit(state,"source")
  val witnessCleanupIncomplete = state.phase == "incomplete"
}
module FourDNativeJourney {
  import FourDNativeOperation.*
  action journeyStep = any {
    producer, packet, capsule, key, intentSource, ackSource, source,
    all { not(state.resources), resources },
    intentPreflight, ackPreflight, preflight,
    all { not(state.revalidated), revalidate },
    intentImage, ackImage,
    all { not(state.revalidated), revalidateBuild },
    all { state.archive == 0, archive }, image,
    all { state.revalidated, not(state.p2Ready), revalidateP2 },
    intentP2, ackP2, p2,
    all { state.phase == "p2-ready", beginCleanup },
    intentCleanup, ackCleanup,
    all { state.cleanupIssued, state.latestAck, closeOrdered },
    all { state.closed == state.owned, retired },
    intentCollection, ackCollection,
    all { state.latestAck, closeCollection },
    all { state.closed == state.owned, finish }
  }
}
module FourDNativeCapabilityJourney {
  import FourDNativeOperation.*
  action capabilityStep = any {
    producer, packet, capsule, key, intentSource, ackSource, source,
    all { not(state.resources), resources }, intentPreflight, ackPreflight, preflight,
    all { state.phase == "preflight-ready", beginCleanup }, intentCleanup, ackCleanup,
    all { state.cleanupIssued, state.latestAck, closeOrdered },
    all { state.closed == state.owned, retired }, intentCollection, ackCollection,
    all { state.latestAck, closeCollection }, all { state.closed == state.owned, finish }
  }
}
module FourDNativeWholeJourney {
  import FourDNativeOperation.*
  import FourDNativeJourney.journeyStep
  action wholeStep = any {
    all { state.phase != "finished", journeyStep },
    all { state.phase == "finished", state.remoteConclusion == "unknown", remoteSuccess },
    all { state.phase == "finished", state.remoteConclusion == "success", not(state.remoteCredentialRetirement), remoteRetirement }
  }
}
module FourDNativeCancelledJourney {
  import FourDNativeOperation.*
  import FourDNativeJourney.journeyStep
  action faultStep = any {
    all { not(state.cancelled), state.phase != "source-ready", journeyStep },
    all { state.phase == "source-ready", not(state.cancelled), cancel },
    all { state.cancelled, state.cleanupStarted == -1, beginCleanup },
    all { state.cancelled, intentCleanup },
    all { state.cancelled, ackCleanup },
    all { state.cancelled, state.cleanupIssued, state.latestAck, closeOrdered },
    all { state.cancelled, state.closed == state.owned, retired },
    all { state.cancelled, intentCollection },
    all { state.cancelled, ackCollection },
    all { state.cancelled, state.latestAck, closeCollection },
    all { state.cancelled, state.closed == state.owned, finish }
  }
}
module FourDNativeMutationJourney {
  import FourDNativeOperation.*
  import FourDNativeJourney.journeyStep
  action faultStep = any {
    all { not(state.refused), state.phase != "source-ready", journeyStep },
    all { state.phase == "source-ready", not(state.refused), mutate },
    all { state.refused, state.cleanupStarted == -1, beginCleanup },
    all { state.refused, intentCleanup },
    all { state.refused, ackCleanup },
    all { state.refused, state.cleanupIssued, state.latestAck, closeOrdered },
    all { state.refused, state.closed == state.owned, retired },
    all { state.refused, intentCollection },
    all { state.refused, ackCollection },
    all { state.refused, state.latestAck, closeCollection },
    all { state.refused, state.closed == state.owned, finish }
  }
}
module FourDNativeLostAckJourney {
  import FourDNativeOperation.*
  import FourDNativeJourney.journeyStep
  action faultStep = any {
    all { state.phase != "image-running", state.cleanupStarted == -1, journeyStep },
    all { state.phase == "image-running", not(state.latestAck), beginCleanup },
    all { state.cleanupStarted >= 0, intentCleanup },
    all { state.cleanupStarted >= 0, ackCleanup },
    all { state.cleanupStarted >= 0, state.cleanupIssued, state.latestAck, closeOrdered },
    all { state.cleanupStarted >= 0, state.closed == state.owned, retired },
    all { state.cleanupStarted >= 0, intentCollection },
    all { state.cleanupStarted >= 0, ackCollection },
    all { state.cleanupStarted >= 0, state.latestAck, closeCollection },
    all { state.cleanupStarted >= 0, state.closed == state.owned, finish }
  }
}
module FourDNativeArchiveMutationJourney {
  import FourDNativeOperation.*
  import FourDNativeJourney.journeyStep
  action faultStep = any {
    all { not(state.refused), state.archive == 0, journeyStep },
    all { not(state.refused), state.archive == 1, state.currentArchive == 1, mutateArchive },
    all { state.refused, state.cleanupStarted == -1, beginCleanup },
    all { state.refused, intentCleanup },
    all { state.refused, ackCleanup },
    all { state.refused, state.cleanupIssued, state.latestAck, closeOrdered },
    all { state.refused, state.closed == state.owned, retired },
    all { state.refused, intentCollection },
    all { state.refused, ackCollection },
    all { state.refused, state.latestAck, closeCollection },
    all { state.refused, state.closed == state.owned, finish }
  }
}
"""
