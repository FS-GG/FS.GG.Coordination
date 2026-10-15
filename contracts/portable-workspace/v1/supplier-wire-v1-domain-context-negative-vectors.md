# Current wire domain and context negative controls

[The six-case corpus](supplier-wire-v1-domain-context-negative-vectors.json) derives
from `context-response-genesis` in the [existing corpus](supplier-wire-v1-vectors.json)
at Coordination `9ab0b8c652ac3ad745991a4c3acb64b593b6d0c0`.
Source file SHA256 values are recorded in `sourceSha256`.

A keeps the original response and context. B changes only the response challenge's
ID and nonce, then recomputes the response's `challenge_digest`. The creation,
reservation, genesis, operation, purpose and retained history remain identical.
The opaque signature bytes are synthetic and untrusted throughout. Each response
has valid standalone structure; the two successful context checks establish only
consistency with caller-supplied original bytes.

| Response | Supplied context | Challenge digest | Context result |
| --- | --- | --- | --- |
| A | A | Existing challenge domain | Structural/context control |
| B | B | Existing challenge domain | Structural/context control |
| A | B | A's unchanged digest | Refused |
| B | A | B's unchanged digest | Refused |
| A | A | Raw SHA256 of A's challenge bytes | Refused |
| B | B | Existing response domain over B's challenge bytes | Refused |

All four refusals must be `original-response-head-challenge`. Crossing contexts
preserves the response bytes exactly. Each domain mutation preserves the original
context and every response payload field except `challenge_digest`; its outer
payload encoding changes accordingly. Every row includes exact canonical envelope
bytes, SHA256, byte length and the complete existing wire domain table. Payload
and supplied response-challenge byte measurements make the digest target explicit.
`structuralAccept` follows the existing qualifier convention: for rows with
`context`, it records the combined structural/context outcome.

The current wire rule remains
`H(challenge, bytes) = SHA256(ASCII("fsgg.v3.challenge/1") || NUL || bytes)`.
The raw SHA case exercises a refusal under this rule. A companion raw-SHA proof
challenge, shared pending typed ledger and persisted-reader compatibility remain
**UNDECIDED**. These fixtures select no new wire or native-proof version, signed
role, authenticated semantic positive, durable replay behavior, or native
acceptance. All production capabilities remain false. Native campaign and original
attempt identities are unknown (`null`); the corpus creates no operational identity.

## Reproduce the source checks

Run the existing pure Python qualifier explicitly; its default command does not
discover this additional corpus:

```sh
python3 eng/qualify-portable-supplier-contract.py \
  --wire-vectors contracts/portable-workspace/v1/supplier-wire-v1-domain-context-negative-vectors.json \
  --wire-schema contracts/portable-workspace/v1/supplier-wire-v1.schema.json
```

Expected: six wire vectors, six context cases, zero framing-only cases,
`authentication: false`, `nativeAcceptance: false`.

The additional check below verifies standalone structure, exact refusal causes,
all supplied source/challenge/payload measurements, and the controlled mutations.
It uses only the existing qualifier and Python standard library; it runs no native
or CLR tools.

```sh
PYTHONDONTWRITEBYTECODE=1 python3 - <<'PY'
import copy, hashlib, importlib.util, json
from pathlib import Path
p = Path('contracts/portable-workspace/v1')
spec = importlib.util.spec_from_file_location('q', 'eng/qualify-portable-supplier-contract.py')
q = importlib.util.module_from_spec(spec)
spec.loader.exec_module(q)
c = json.loads((p / 'supplier-wire-v1-domain-context-negative-vectors.json').read_text())
s = json.loads((p / 'supplier-wire-v1.schema.json').read_text())
h = lambda k, b: hashlib.sha256(('fsgg.v3.' + k + '/1').encode() + b'\0' + b).hexdigest()
for path, digest in c['sourceSha256'].items():
    assert hashlib.sha256(Path(path).read_bytes()).hexdigest() == digest
rows = c['cases']
assert len(rows) == 6 and [r['structuralAccept'] for r in rows] == [True, True, False, False, False, False]
assert q.qualify_wire(c, s) == 6
payloads = []
for r in rows:
    raw = q.strict_b64(r['bytesBase64url'])
    result = q.wire_envelope(raw, r['family'], s)
    assert result['authority'] == r['authority'] == 'untrusted'
    assert not any(result['capabilities'].values()) and not any(r['capabilities'].values())
    pb = q.strict_b64(q.wire_decode(raw)['payload'])
    payloads.append(q.wire_decode(pb))
    assert int(r['payloadByteCount']) == len(pb)
    assert r['payloadSha256'] == hashlib.sha256(pb).hexdigest()
    cb = q.strict_b64(r['context']['responseChallenge'])
    m = r['challengeBytes']
    assert int(m['byteCount']) == len(cb) and m['sha256'] == hashlib.sha256(cb).hexdigest()
    assert m['domainDigests'] == {k: h(k, cb) for k in s['x-domains']}
    refusal = None
    try:
        context_result = q.wire_context(raw, r['family'], s, r['context'])
        assert context_result['authority'] == 'untrusted'
        assert not any(context_result['capabilities'].values())
    except q.WireRefused as error:
        refusal = str(error)
    assert refusal == r['expectedRefusal'], (r['name'], refusal)
source = json.loads((p / 'supplier-wire-v1-vectors.json').read_text())
original = next(r for r in source['cases'] if r['name'] == 'context-response-genesis')
assert rows[0]['bytesBase64url'] == original['bytesBase64url']
assert rows[0]['context'] == original['context']
ca, cb = [copy.deepcopy(r['context']) for r in rows[:2]]
qa, qb = [q.wire_decode(q.strict_b64(x.pop('responseChallenge'))) for x in (ca, cb)]
assert ca == cb
assert qa['challenge_id'] != qb['challenge_id'] and qa['challenge_nonce'] != qb['challenge_nonce']
for key in ('challenge_id', 'challenge_nonce'):
    qa.pop(key); qb.pop(key)
assert qa == qb
for i, response, context in ((2, 0, 1), (3, 1, 0)):
    assert rows[i]['bytesBase64url'] == rows[response]['bytesBase64url']
    assert rows[i]['context'] == rows[context]['context']
for i, control in ((1, 0), (4, 0), (5, 1)):
    left, right = copy.deepcopy(payloads[i]), copy.deepcopy(payloads[control])
    assert left.pop('challenge_digest') != right.pop('challenge_digest')
    assert left == right
for i, control in ((4, 0), (5, 1)):
    assert rows[i]['context'] == rows[control]['context']
for i in (0, 1):
    assert payloads[i]['challenge_digest'] == rows[i]['challengeBytes']['domainDigests']['challenge']
assert payloads[4]['challenge_digest'] == rows[0]['challengeBytes']['sha256']
assert payloads[5]['challenge_digest'] == rows[1]['challengeBytes']['domainDigests']['response']
print('passed: 2 untrusted controls; 4 exact context/domain refusals; all six standalone structures valid')
PY
```
