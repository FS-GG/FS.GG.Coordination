
  .schema == "fsgg.coordination.canonical-quint-qualification/1" and
  .q1Outcome == "passed" and .q2Outcome == "passed" and
  .positiveInvariantCount == 8 and .negativeControlCount == 79 and
  (.processCounts.external | type == "number") and (.processCounts.external >= 0) and
  (.processCounts.quintCli | type == "number") and (.processCounts.quintCli >= 0) and
  (.processCounts.apalacheVerify | type == "number") and (.processCounts.apalacheVerify >= 0) and
  .processAccounting == "logical-invocations-plus-explicit-startup-retries/v1" and
  (.startupRetries | keys) == ["earlyLifecycleExit","reflectionDeadline","total","verify"] and
  (.startupRetries.total | type == "number") and (.startupRetries.total >= 0) and
  (.startupRetries.verify | type == "number") and (.startupRetries.verify >= 0) and (.startupRetries.verify <= .startupRetries.total) and
  .startupRetries.total == (.startupRetries.reflectionDeadline + .startupRetries.earlyLifecycleExit) and
  .physicalProcessCounts.external == (.processCounts.external + .startupRetries.total) and
  .physicalProcessCounts.quintCli == (.processCounts.quintCli + .startupRetries.total) and
  .physicalProcessCounts.apalacheVerify == (.processCounts.apalacheVerify + .startupRetries.verify) and
  (.formalCounterexamples | length) == 0 and .failure == null

