module workTaxonomy {
  type Signal =
    | LegacyEpic
    | LegacyFeature
    | LegacyTask
    | LegacyBug
    | LegacyDecision
    | LegacyRegister
    | LegacyDirective
    | AlreadyNative
    | Missing
    | Ambiguous
    | Contradictory
    | Unsupported
    | Unreadable
    | Incomplete
    | Stale
    | Lossy

  type NativeType =
    | Unclassified
    | Epic
    | Feature
    | Task
    | Bug
    | Decision
    | Register
    | Directive

  type Lifecycle = Work | StandingExempt | NotApplicable
  type Outcome = Pending | Planned | Refused

  type State = {
    signal: Signal,
    outcome: Outcome,
    target: NativeType,
    lifecycle: Lifecycle,
    hierarchyPreserved: bool,
    repositoryScopePreserved: bool,
    dispositionCount: int,
  }

  var state: State

  pure val acceptedSignals: Set[Signal] = Set(
    LegacyEpic,
    LegacyFeature,
    LegacyTask,
    LegacyBug,
    LegacyDecision,
    LegacyRegister,
    LegacyDirective,
    AlreadyNative
  )

  pure val allSignals: Set[Signal] = acceptedSignals.union(Set(
    Missing,
    Ambiguous,
    Contradictory,
    Unsupported,
    Unreadable,
    Incomplete,
    Stale,
    Lossy
  ))

  pure def targetFor(signal: Signal): NativeType =
    match signal {
      | LegacyEpic => Epic
      | LegacyFeature => Feature
      | LegacyTask => Task
      | LegacyBug => Bug
      | LegacyDecision => Decision
      | LegacyRegister => Register
      | LegacyDirective => Directive
      | AlreadyNative => Task
      | _ => Unclassified
    }

  pure def lifecycleFor(target: NativeType): Lifecycle =
    if (target == Register or target == Directive) StandingExempt
    else if (target == Unclassified) NotApplicable
    else Work

  pure def canPlan(s: State, signal: Signal): bool =
    s.outcome == Pending and acceptedSignals.contains(signal)

  pure def applyPlan(s: State, signal: Signal): State = {
    val target = targetFor(signal)
    {
      ...s,
      signal: signal,
      outcome: Planned,
      target: target,
      lifecycle: lifecycleFor(target),
      hierarchyPreserved: true,
      repositoryScopePreserved: true,
      dispositionCount: 1,
    }
  }

  pure def canRefuse(s: State, signal: Signal): bool =
    s.outcome == Pending and not(acceptedSignals.contains(signal))

  pure def applyRefusal(s: State, signal: Signal): State =
    {
      ...s,
      signal: signal,
      outcome: Refused,
      target: Unclassified,
      lifecycle: NotApplicable,
      dispositionCount: 0,
    }

  action init: bool =
    state' = {
      signal: Missing,
      outcome: Pending,
      target: Unclassified,
      lifecycle: NotApplicable,
      hierarchyPreserved: true,
      repositoryScopePreserved: true,
      dispositionCount: 0,
    }

  action planAction(signal: Signal): bool = all {
    canPlan(state, signal),
    state' = applyPlan(state, signal),
  }

  action refuseAction(signal: Signal): bool = all {
    canRefuse(state, signal),
    state' = applyRefusal(state, signal),
  }

  action step: bool = {
    nondet signal = allSignals.oneOf()
    any {
      planAction(signal),
      refuseAction(signal),
    }
  }

  val plannedWitness: bool = state.outcome == Planned
  val refusedWitness: bool = state.outcome == Refused
  val soleNativeAuthority: bool = state.outcome == Planned implies state.target != Unclassified
  val refusedHasNoDisposition: bool = state.outcome == Refused implies state.dispositionCount == 0
  val uniqueDisposition: bool = state.dispositionCount >= 0 and state.dispositionCount <= 1
  val preservation: bool = state.outcome == Planned implies (state.hierarchyPreserved and state.repositoryScopePreserved)
  val standingExemptionExact: bool =
    state.outcome == Planned implies
      ((state.lifecycle == StandingExempt) iff (state.target == Register or state.target == Directive))
  val deterministicOutcome: bool =
    state.outcome == Planned implies
      (state.target == targetFor(state.signal) and state.lifecycle == lifecycleFor(state.target))
}
