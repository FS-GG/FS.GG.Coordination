module workTaxonomy_test {
  import workTaxonomy.* from "./workTaxonomy"

  run mappingAssumptionsTest =
    init
      .then(planAction(LegacyRegister))
      .expect(all {
        targetFor(LegacyEpic) == Epic,
        targetFor(LegacyFeature) == Feature,
        targetFor(LegacyTask) == Task,
        targetFor(LegacyBug) == Bug,
        targetFor(LegacyDecision) == Decision,
        targetFor(LegacyRegister) == Register,
        targetFor(LegacyDirective) == Directive,
        state.target == Register,
        state.lifecycle == StandingExempt,
      })

  run refusalAssumptionsTest =
    init
      .then(refuseAction(Contradictory))
      .expect(all {
        not(acceptedSignals.contains(Missing)),
        not(acceptedSignals.contains(Ambiguous)),
        not(acceptedSignals.contains(Contradictory)),
        not(acceptedSignals.contains(Unsupported)),
        not(acceptedSignals.contains(Unreadable)),
        not(acceptedSignals.contains(Incomplete)),
        not(acceptedSignals.contains(Stale)),
        not(acceptedSignals.contains(Lossy)),
        state.outcome == Refused,
        state.dispositionCount == 0,
      })
}
