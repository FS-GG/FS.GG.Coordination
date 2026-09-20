# Synthetic orchestration telemetry batches

These five immutable `fsgg.telemetry.ingest/1` batches were generated from
`TelemetryFactBatches` for a fixed synthetic `work-item-v1-...` item and parsed by
the released 0.91.2 `TelemetryStore.parseBatch`. They contain no prompt, output
body, credential or private user data.

For an isolated positive runtime transport check, submit in this order:

1. `prospective-root.json`
2. `process-start.json`
3. `native-turn.json`
4. `runtime-terminal.json`

`runtime-gap.json` is a separate negative control that should make coverage
incomplete. Each batch has a fixed ingest identity and may be retried unchanged.
Use the isolated Host scope, matching workspace config, exact repository binding
digest and the released CLI 0.91.2. Applied transport receipts and private
readback are needed to establish delivery. These synthetic facts do not prove a
genuine orchestration dispatch, the installed runner, or a completed item.
