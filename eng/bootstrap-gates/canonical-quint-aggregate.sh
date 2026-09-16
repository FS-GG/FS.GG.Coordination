#!/usr/bin/env bash
set -euo pipefail
: "${FSGG_QUINT_SHARD_ROOT:?FSGG_QUINT_SHARD_ROOT is required}"
: "${FSGG_QUINT_PERFORMANCE_RECEIPT:?FSGG_QUINT_PERFORMANCE_RECEIPT is required}"
: "${FSGG_QUINT_RECEIPT:?FSGG_QUINT_RECEIPT is required}"
: "${FSGG_QUINT_ACCOUNTING_RECEIPT:?FSGG_QUINT_ACCOUNTING_RECEIPT is required}"
base="$FSGG_QUINT_SHARD_ROOT/base.json"
test -f "$base"
jq -e '
  .schema == "fsgg.coordination.canonical-quint-qualification/1" and
  .q1Outcome == "passed" and .q2Outcome == "passed" and
  .positiveInvariantCount == 8 and .negativeControlCount == 71 and
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
' "$base" >/dev/null
jq -e '.schema == "fsgg.coordination.canonical-quint-performance/1" and .outcome == "passed" and .shardCount == 19 and .epochBudgetMs == 150000' \
  "$FSGG_QUINT_PERFORMANCE_RECEIPT" >/dev/null

mapfile -t expected < <(jq -r '.formalTests[].id' eng/quint-qualification.json | sort)
test "${#expected[@]}" -eq 19
rows='[]'
q2_ms="$(jq -r '.q2DurationMs' "$base")"
negative_count="$(jq -r '.negativeControlCount' "$base")"
external_count="$(jq -r '.processCounts.external' "$base")"
quint_count="$(jq -r '.processCounts.quintCli' "$base")"
apalache_count="$(jq -r '.processCounts.apalacheVerify' "$base")"
startup_retry_count="$(jq -r '.startupRetries.total' "$base")"
verify_retry_count="$(jq -r '.startupRetries.verify' "$base")"
reflection_retry_count="$(jq -r '.startupRetries.reflectionDeadline' "$base")"
early_lifecycle_retry_count="$(jq -r '.startupRetries.earlyLifecycleExit' "$base")"
executed_external_count="$(jq -r '.physicalProcessCounts.external' "$base")"
executed_quint_count="$(jq -r '.physicalProcessCounts.quintCli' "$base")"
executed_apalache_count="$(jq -r '.physicalProcessCounts.apalacheVerify' "$base")"
for id in "${expected[@]}"; do
  if [[ "$id" == "epoch" ]]; then
    receipt="$(dirname "$FSGG_QUINT_PERFORMANCE_RECEIPT")/epoch.json"
  else
    receipt="$FSGG_QUINT_SHARD_ROOT/$id.json"
  fi
  test -f "$receipt"
  jq -e --arg id "$id" '
    .schema == "fsgg.coordination.canonical-quint-formal-shard/1" and .id == $id and .outcome == "passed" and
    .accountingMethod == "logical-formal-contribution-and-observed-execution/v1" and
    .negativeControlCount == 5 and .processCounts.external == 7 and
    .processCounts.quintCli == 7 and .processCounts.apalacheVerify == 3 and
    (.startupRetries | keys) == ["earlyLifecycleExit","reflectionDeadline","total","verify"] and
    (.startupRetries.total | type == "number") and (.startupRetries.total >= 0) and
    (.startupRetries.verify | type == "number") and (.startupRetries.verify >= 0) and (.startupRetries.verify <= .startupRetries.total) and
    .startupRetries.total == (.startupRetries.reflectionDeadline + .startupRetries.earlyLifecycleExit) and
    .executedProcessCounts.external == (10 + .startupRetries.total) and
    .executedProcessCounts.quintCli == (10 + .startupRetries.total) and
    .executedProcessCounts.apalacheVerify == (3 + .startupRetries.verify) and
    (.q2DurationMs | type == "number") and (.q2DurationMs > 0)
  ' "$receipt" >/dev/null
  for field in toolchainSha256 quintSha256 apalacheJarSha256 sourceSha256 contractSha256 preparationSha256; do
    test "$(jq -r --arg field "$field" '.[$field]' "$receipt")" = "$(case "$field" in
      toolchainSha256) jq -r '.tools.toolchainSha256' "$base";;
      quintSha256) jq -r '.tools.quintSha256' "$base";;
      apalacheJarSha256) jq -r '.tools.apalacheJarSha256' "$base";;
      sourceSha256) jq -r '.inputs.sourceSha256' "$base";;
      contractSha256) jq -r '.inputs.contractSha256' "$base";;
      preparationSha256) jq -r '.preparationSha256' "$base";;
    esac)"
  done
  shard_q2_ms="$(jq -r '.q2DurationMs' "$receipt")"
  if (( shard_q2_ms > q2_ms )); then q2_ms="$shard_q2_ms"; fi
  negative_count=$((negative_count + $(jq -r '.negativeControlCount' "$receipt")))
  external_count=$((external_count + $(jq -r '.processCounts.external' "$receipt")))
  quint_count=$((quint_count + $(jq -r '.processCounts.quintCli' "$receipt")))
  apalache_count=$((apalache_count + $(jq -r '.processCounts.apalacheVerify' "$receipt")))
  executed_external_count=$((executed_external_count + $(jq -r '.executedProcessCounts.external' "$receipt")))
  executed_quint_count=$((executed_quint_count + $(jq -r '.executedProcessCounts.quintCli' "$receipt")))
  executed_apalache_count=$((executed_apalache_count + $(jq -r '.executedProcessCounts.apalacheVerify' "$receipt")))
  startup_retry_count=$((startup_retry_count + $(jq -r '.startupRetries.total' "$receipt")))
  verify_retry_count=$((verify_retry_count + $(jq -r '.startupRetries.verify' "$receipt")))
  reflection_retry_count=$((reflection_retry_count + $(jq -r '.startupRetries.reflectionDeadline' "$receipt")))
  early_lifecycle_retry_count=$((early_lifecycle_retry_count + $(jq -r '.startupRetries.earlyLifecycleExit' "$receipt")))
  row="$(jq -c '{id,manifestSha256,traceSha256,itfSha256}' "$receipt")"
  rows="$(jq -cn --argjson rows "$rows" --argjson row "$row" '$rows + [$row]')"
done

preparation_ms="$(jq -r '.preparationDurationMs' "$base")"
# The canonical receipt retains the closed /1 duration fields. Under parallel
# execution they are a conservative composed upper bound, not workflow wall time.
total_ms=$((preparation_ms + q2_ms))
preparation_sha="$(jq -r '.preparationSha256' "$base")"
identity="$(jq -r 'sort_by(.id)[] | [.id,.manifestSha256,.traceSha256,.itfSha256] | join("|")' <<<"$rows" | paste -sd';' -)"
result_sha="$(printf 'passed|passed|8|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|none|none' "$negative_count" "$external_count" "$quint_count" "$apalache_count" "$startup_retry_count" "$verify_retry_count" "$reflection_retry_count" "$early_lifecycle_retry_count" "$preparation_sha" "$identity" | sha256sum | cut -d' ' -f1)"
mkdir -p "$(dirname "$FSGG_QUINT_RECEIPT")"
jq -n \
  --argjson preparationMs "$preparation_ms" --argjson q2Ms "$q2_ms" --argjson totalMs "$total_ms" \
  --argjson negativeCount "$negative_count" --argjson externalCount "$external_count" \
  --argjson quintCount "$quint_count" --argjson apalacheCount "$apalache_count" \
  --argjson startupRetryCount "$startup_retry_count" --argjson reflectionRetryCount "$reflection_retry_count" \
  --argjson verifyRetryCount "$verify_retry_count" \
  --argjson earlyLifecycleRetryCount "$early_lifecycle_retry_count" \
  --argjson rows "$rows" --arg resultSha256 "$result_sha" --slurpfile base "$base" '
  {schema:"fsgg.coordination.canonical-quint-qualification/1",q1Outcome:"passed",q2Outcome:"passed",
   positiveInvariantCount:8,negativeControlCount:$negativeCount,preparationDurationMs:$preparationMs,q2DurationMs:$q2Ms,totalDurationMs:$totalMs,
   processCounts:{external:$externalCount,quintCli:$quintCount,apalacheVerify:$apalacheCount},
   processAccounting:"logical-invocations-plus-explicit-startup-retries/v1",
   physicalProcessCounts:{external:($externalCount+$startupRetryCount),quintCli:($quintCount+$startupRetryCount),apalacheVerify:($apalacheCount+$verifyRetryCount)},
   startupRetries:{total:$startupRetryCount,verify:$verifyRetryCount,reflectionDeadline:$reflectionRetryCount,earlyLifecycleExit:$earlyLifecycleRetryCount},
   formalCounterexamples:($rows|sort_by(.id)),
   tools:$base[0].tools,inputs:$base[0].inputs,preparationSha256:$base[0].preparationSha256,failure:null,resultSha256:$resultSha256}' \
  > "$FSGG_QUINT_RECEIPT"
mkdir -p "$(dirname "$FSGG_QUINT_ACCOUNTING_RECEIPT")"
jq -n \
  --argjson shards "${#expected[@]}" --argjson logicalExternal "$external_count" \
  --argjson logicalQuint "$quint_count" --argjson logicalApalache "$apalache_count" \
  --argjson executedExternal "$executed_external_count" --argjson executedQuint "$executed_quint_count" \
  --argjson executedApalache "$executed_apalache_count" --argjson preparationMs "$preparation_ms" \
  --argjson startupRetryCount "$startup_retry_count" --argjson reflectionRetryCount "$reflection_retry_count" \
  --argjson verifyRetryCount "$verify_retry_count" \
  --argjson earlyLifecycleRetryCount "$early_lifecycle_retry_count" \
  --argjson q2Ms "$q2_ms" --argjson totalMs "$total_ms" '
  {schema:"fsgg.coordination.canonical-quint-parallel-accounting/1",outcome:"passed",shardCount:$shards,
   accountingMethod:"logical-formal-contribution-and-observed-execution/v1",
   durationSemantics:"parallel-composed-upper-bound-not-workflow-wall-time",
   logicalProcessCounts:{external:$logicalExternal,quintCli:$logicalQuint,apalacheVerify:$logicalApalache},
   executedProcessCounts:{external:$executedExternal,quintCli:$executedQuint,apalacheVerify:$executedApalache},
   startupRetries:{total:$startupRetryCount,verify:$verifyRetryCount,reflectionDeadline:$reflectionRetryCount,earlyLifecycleExit:$earlyLifecycleRetryCount},
   preparationDurationMs:$preparationMs,q2DurationMs:$q2Ms,totalDurationMs:$totalMs}' \
  > "$FSGG_QUINT_ACCOUNTING_RECEIPT"
printf 'CANONICAL_QUINT_AGGREGATE_OK shards=%d durationSemantics=parallel-composed-upper-bound accountingMethod=logical-formal-contribution-and-observed-execution/v1 receipt=%s accounting=%s\n' "${#expected[@]}" "$FSGG_QUINT_RECEIPT" "$FSGG_QUINT_ACCOUNTING_RECEIPT"
