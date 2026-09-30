#!/usr/bin/env node
// Independent standard-library conformance check for portable workspace v1.

import { createHash } from "node:crypto";

const SCHEMA = "fsgg.workspace.command/1";
const MAX = (2n ** 64n) - 1n;
const REQUIRED = ["schema", "commandId", "idempotencyId", "workspaceScope", "profileId", "profileRevision", "sourceRevision", "expectedWorkflowRevision", "fenceGeneration", "deadline", "operation"];
const OPTIONAL = ["causationId", "componentId"];
const ORDER = [...REQUIRED.slice(0, 9), "causationId", ...REQUIRED.slice(9), "componentId"];

function parseFlatObject(text) {
  const keys = [...text.matchAll(/(?:^|[,{}])\s*"((?:\\.|[^"\\])*)"\s*:/g)].map(match => JSON.parse(`"${match[1]}"`));
  if (new Set(keys).size !== keys.length) throw new Error("duplicate");
  return JSON.parse(text);
}

function validate(text) {
  const value = parseFlatObject(text);
  if (value.schema !== SCHEMA) throw new Error("unsupported-schema");
  const keys = Object.keys(value);
  if (keys.some(key => ![...REQUIRED, ...OPTIONAL].includes(key)) || REQUIRED.some(key => !(key in value))) throw new Error("shape");
  if (OPTIONAL.some(key => key in value && value[key] === null)) throw new Error("null");
  for (const key of ["profileRevision", "expectedWorkflowRevision", "fenceGeneration"]) {
    const counter = value[key];
    if (typeof counter !== "string" || !/^(0|[1-9][0-9]{0,19})$/.test(counter) || BigInt(counter) > MAX) throw new Error("counter");
  }
  if (typeof value.deadline !== "string" || !/^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z$/.test(value.deadline)) throw new Error("timestamp");
  if (Number.isNaN(Date.parse(value.deadline))) throw new Error("timestamp");
  return value;
}

function canonical(value) {
  return Buffer.from(JSON.stringify(Object.fromEntries(ORDER.filter(key => key in value).map(key => [key, value[key]]))));
}

function expectRefused(text) {
  try { validate(text); } catch { return; }
  throw new Error("document unexpectedly accepted");
}

const command = {
  schema: SCHEMA,
  commandId: "11111111-2222-3333-4444-555555555555",
  idempotencyId: "max-counter",
  workspaceScope: "fs-gg/conformance",
  profileId: "portable-v1",
  profileRevision: MAX.toString(),
  sourceRevision: "0123456789abcdef0123456789abcdef01234567",
  expectedWorkflowRevision: MAX.toString(),
  fenceGeneration: MAX.toString(),
  deadline: "2026-09-30T12:34:56.789123Z",
  operation: "build",
};
const encoded = canonical(validate(JSON.stringify(command)));
if (encoded.includes(":null")) throw new Error("null emitted");
expectRefused(encoded.toString().slice(0, -1) + ',"extra":"x"}');
expectRefused(encoded.toString().replace('"schema":', '"schema":"fsgg.workspace.command/1","schema":'));
expectRefused(encoded.toString().replace('"operation":"build"', '"causationId":null,"operation":"build"'));
expectRefused(encoded.toString().replace(SCHEMA, "fsgg.workspace.command/2"));

for (const evidence of [{ state: "known", value: 0 }, { state: "missing", reason: "not-produced" }, { state: "unknown", reason: "readback-lost" }]) {
  const expected = evidence.state === "known" ? ["state", "value"] : ["state", "reason"];
  if (JSON.stringify(Object.keys(evidence)) !== JSON.stringify(expected)) throw new Error("evidence shape");
}

console.log(createHash("sha256").update(encoded).digest("hex"));
