# FS.GG Coordination CLI

`fsgg-coordination` is the callable FS.GG Coordination boundary. Its ordinary delivery command observes and
plans by default; advancement remains guarded by the authoritative epoch, exact sealed plan, protected journal,
and explicit receiver/provider selection. Installing this package does not enable a production writer.

`fsgg-coordination ordinary-settlement execute` is the non-interactive post-merge settlement entry point.
It accepts no plan, token, or arbitrary operation arguments and exits with a typed refusal until the trusted
workflow installs an `IOrdinarySettlementCommandProvider` compiled against the pinned command artifact.
