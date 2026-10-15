#!/usr/bin/env python3
"""Pure size-vector qualification; never authenticates, provisions or invokes native tools."""
import argparse
import base64
import hashlib
import json
from pathlib import Path

ENVELOPE = 65536
PAYLOAD = 45000
SIGNATURE = 2048
GENESIS = 18000
EVENT = 12000


def encoded(n):
    if not 0 <= n <= ENVELOPE:
        raise ValueError('supplier-size-refused')
    return n // 3 * 4 + (0, 2, 3)[n % 3]


def envelope(payload, signature):
    if not 0 < payload <= PAYLOAD or not 0 < signature <= SIGNATURE:
        raise ValueError('supplier-size-refused')
    total = 30 + encoded(payload) + encoded(signature)
    if total > ENVELOPE:
        raise ValueError('supplier-size-refused')
    return total


def genesis(creation, reservation, overhead):
    if not (0 < creation <= 6000 and 0 < reservation <= 6000 and 0 < overhead <= 6000):
        raise ValueError('supplier-size-refused')
    total = creation + reservation + overhead
    if total > GENESIS:
        raise ValueError('supplier-size-refused')
    return total


def response(is_genesis, metadata, events):
    if not 9 <= metadata <= 6000 or not 0 < len(events) <= 16 or (is_genesis and len(events) != 1):
        raise ValueError('supplier-size-refused')
    maximum = envelope(GENESIS if is_genesis else EVENT, SIGNATURE)
    if any(not 0 < length <= maximum for length in events):
        raise ValueError('supplier-size-refused')
    total = metadata + 12 + len(events) - 1 + sum(encoded(n) + 2 for n in events)
    if total > PAYLOAD:
        raise ValueError('supplier-size-refused')
    return total


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True) + '\n').encode('ascii')


def b64(value):
    return base64.urlsafe_b64encode(value).rstrip(b'=').decode('ascii')


def qualify(vectors):
    functions = {'base64': lambda case: encoded(case['raw']),
                 'envelope': lambda case: envelope(case['payload'], case['signature']),
                 'genesis': lambda case: genesis(case['creation'], case['reservation'], case['overhead']),
                 'response': lambda case: response(case['genesis'], case['metadata'], case['events'])}
    for case in vectors['cases']:
        try:
            actual = functions[case['kind']](case)
        except ValueError:
            actual = None
        assert actual == case['expected'], case['name']
        if actual is not None and case['kind'] == 'envelope':
            # Build the exact outer C1 syntax independently of the arithmetic.
            wire = canonical({'payload': b64(b'x' * case['payload']), 'signature': b64(b's' * case['signature'])})
            assert len(wire) == actual, case['name']
            assert hashlib.sha256(wire).hexdigest() == case['wireSha256'], case['name']
        if actual is not None and case['kind'] == 'response':
            metadata = {'m': 'x' * (case['metadata'] - 9)}
            assert len(canonical(metadata)) == case['metadata']
            wire = canonical({**metadata, 'events': [b64(b'e' * n) for n in case['events']]})
            assert len(wire) == actual, case['name']
            assert envelope(actual, SIGNATURE) <= ENVELOPE
    return len(vectors['cases'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--vectors', type=Path, default=Path(__file__).resolve().parents[1] / 'contracts/portable-workspace/v1/supplier-evidence-sizing-vectors.json')
    parser.add_argument('--native', action='store_true')
    args = parser.parse_args()
    if args.native:
        print(json.dumps({'outcome': 'held', 'reason': 'native-unavailable: supplier verifier, durable replay admission and isolated host profile are not implemented'}))
        return 2
    count = qualify(json.loads(args.vectors.read_text()))
    print(json.dumps({'outcome': 'passed', 'sizingVectors': count, 'nativeAcceptance': False, 'authentication': False}))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
