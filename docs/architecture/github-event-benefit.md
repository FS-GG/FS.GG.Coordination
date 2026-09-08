# GitHub event-benefit measurement

GS2-07.7 adds a pure qualification boundary for deciding whether a bounded event-hint path demonstrates benefit while scheduled complete audits remain the correctness authority. It does not install an event worker, expose a production command, or change polling.

## Evidence boundary

Every run declares its finite population and UTC observation window before collection. Each retained source is exactly one of:

- `current-provider-observation`;
- `historical-provider-evidence`;
- `executable-replay`; or
- `injected-negative-control`.

The input binds source bytes by SHA-256 and records complete pages, complete run attempts, available event/ingestion/queue/start/end timestamps, API call attempts and rate outcomes. Every present timestamp must fall inside the inclusive declared window. Current-provider, replay, and injected-control sources bind the declared observation head; historical aggregate evidence may omit a singular head, but any head it supplies must be a valid immutable SHA. Missing provider counters, prices, timestamps, revisions, or native outcomes are explicit unknowns. Collector calls are separate from workload calls.

The current provider observation covers two predeclared successful registration-push runs at protected head `a8b10e073eb7098014ea38ce6edadc35b784ff5c`. It establishes read-only run identity, attempt, start/end, HTTP and rate facts. GitHub's run response does not expose native event delivery, ingestion, or a distinct queue time, so it establishes neither provider dispatch latency nor installed event benefit.

## Replay semantics

The executable replay compares the same three-subject workload under a full-scan baseline and a narrow path. Duplicate and reordered reconciliation hints for subject A reduce to its newest relevant revision. Subject B stays independently processable. Approval, grant, semantic, and non-idempotent operation identities are retained, and a newer hint does not cancel the effect already applying.

Subject C is an injected negative control whose known hint is withheld. The next scheduled complete audit discovers and converges C; repair delay is derived from the audit's scheduled and convergence timestamps and is labeled injected. The audit admission is not added as a universal ordinary merge dependency.

## Result and limitations

The retained bounded replay records eight full-scan API attempts and three baseline schedules versus two narrow workload attempts and one complete-audit admission. It derives a 2,000 ms replay event-to-ingestion interval and a 120,000 ms injected repair delay. This demonstrates benefit only for the declared replay population. It is not production savings, an installed-path result, a provider dispatch-latency measurement, or authority to remove polling.

The report retains a native refusal and unknown native outcomes rather than turning them into successes. Provider request prices are unavailable. Its declared limitation covers the incomplete attribution in the roadmap economics section; no missing counter is invented, useful tests are not classified as bureaucracy, and this strict migration unit is not a routine sample. No overhead intervention is established by this result.

The decision is `retain` polling. A later operational change would require separately authorized installed-path coverage and complete-audit fallback evidence.

## Tamper and hosted claims

Compilation rejects changed prerequisite or roadmap identity, an unbounded or reordered population, an invalid window, unknown categories, incomplete pages or attempts, missing or substituted applicable heads, malformed or out-of-window timestamps, missing call/rate facts, contradictory revisions, unsupported hints, an unrepaired withheld event, or altered source digests. Canonical serialization seals the derived report; changes to head, timestamps, population, metrics, policy, or result invalidate it. Generated and independent control paths execute separate predicates for every advertised case and bind each result to its control ID and retained case description.

If a future input claims hosted measurement, it must bind a retained typed identity to the exact repository, workflow, run and attempt, tested head, observation window, population digest, and every source digest. Local emulation and replay cannot satisfy that field.
