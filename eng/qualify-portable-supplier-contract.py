#!/usr/bin/env python3
"""Pure supplier size/wire fixture qualification; never authenticates or invokes native tools."""
import argparse
import base64
import hashlib
import json
import re
from datetime import datetime
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



class WireRefused(ValueError):
    pass


def wire_c1(value):
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True) + '\n').encode('ascii')


def wire_decode(raw):
    if not raw or len(raw) > ENVELOPE:
        raise WireRefused('document-size')
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise WireRefused('duplicate-field')
            result[key] = value
        return result
    def numeric(_):
        raise WireRefused('numeric-token')
    try:
        raw.decode('ascii')
        value = json.loads(raw, object_pairs_hook=pairs, parse_int=numeric, parse_float=numeric, parse_constant=numeric)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise WireRefused('invalid-c1') from error
    def walk(item, path=(), depth=0):
        if depth > 16:
            raise WireRefused('depth')
        if isinstance(item, dict):
            for key, child in item.items():
                walk(key, (), depth + 1)
                walk(child, path + (key,), depth + 1)
        elif isinstance(item, list):
            if len(item) > 1024:
                raise WireRefused('array-size')
            for child in item:
                walk(child, path + ('*',), depth + 1)
        elif isinstance(item, str):
            limit = 60000 if path == ('payload',) else 2731 if path == ('signature',) else 35682 if path == ('events', '*') else 4096
            if len(item) > limit or any(not 32 <= ord(char) <= 126 for char in item):
                raise WireRefused('string-limit')
        elif item is not None and not isinstance(item, bool):
            raise WireRefused('scalar')
    walk(value)
    if not isinstance(value, dict) or wire_c1(value) != raw:
        raise WireRefused('noncanonical')
    return value


def strict_b64(value):
    if not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9_-]+', value):
        raise WireRefused('base64')
    try:
        raw = base64.b64decode(value + '=' * ((-len(value)) % 4), altchars=b'-_', validate=True)
    except ValueError as error:
        raise WireRefused('base64') from error
    if b64(raw) != value:
        raise WireRefused('base64-canonical')
    return raw


def wire_schema(value, spec, schema):
    if '$ref' in spec:
        return wire_schema(value, schema['$defs'][spec['$ref'].split('/')[-1]], schema)
    if 'oneOf' in spec:
        matches = 0
        for branch in spec['oneOf']:
            try:
                wire_schema(value, branch, schema)
                matches += 1
            except WireRefused:
                pass
        if matches != 1:
            raise WireRefused('schema-oneof')
        return
    if 'not' in spec:
        try:
            wire_schema(value, spec['not'], schema)
        except WireRefused:
            pass
        else:
            raise WireRefused('schema-not')
    if 'const' in spec and (type(value) is not type(spec['const']) or value != spec['const']):
        raise WireRefused('schema-const')
    if 'enum' in spec and value not in spec['enum']:
        raise WireRefused('schema-enum')
    kind = spec.get('type')
    if kind == 'null' and value is not None:
        raise WireRefused('schema-null')
    if kind == 'object':
        if not isinstance(value, dict) or set(value) != set(spec['required']):
            raise WireRefused('schema-fields')
        for key, child in value.items():
            wire_schema(child, spec['properties'][key], schema)
    elif kind == 'array':
        if not isinstance(value, list) or not spec.get('minItems', 0) <= len(value) <= spec.get('maxItems', 1024):
            raise WireRefused('schema-array')
        for child in value:
            wire_schema(child, spec['items'], schema)
    elif kind == 'string':
        if not isinstance(value, str) or not spec.get('minLength', 0) <= len(value) <= spec.get('maxLength', 4096):
            raise WireRefused('schema-string')
        if 'pattern' in spec and re.fullmatch(spec['pattern'], value) is None:
            raise WireRefused('schema-pattern')
        if spec.get('format') == 'utc-microseconds':
            try:
                parsed = datetime.strptime(value, '%Y-%m-%dT%H:%M:%S.%fZ')
                if parsed.strftime('%Y-%m-%dT%H:%M:%S.%fZ') != value:
                    raise ValueError()
            except ValueError as error:
                raise WireRefused('calendar') from error
        if spec.get('format') in {'nonce256', 'canonical-base64url'}:
            decoded = strict_b64(value)
            if spec['format'] == 'nonce256' and len(decoded) != 32:
                raise WireRefused('nonce-size')


def wire_payload(raw, family, schema):
    allowed = {item['$ref'].split('/')[-1] for item in schema['oneOf']} | {'command', 'selection'}
    if family not in allowed:
        raise WireRefused('unsupported-family')
    value = wire_decode(raw)
    wire_schema(value, {'$ref': '#/$defs/' + family}, schema)
    limit = 6000 if family in {'creation-core', 'reservation'} else GENESIS if family == 'genesis' else EVENT if family == 'supplier-event' else PAYLOAD
    if len(raw) > limit:
        raise WireRefused('family-size')
    if family == 'genesis':
        c, r = value['creation_core'], value['reservation']
        cb, rb = wire_c1(c), wire_c1(r)
        if len(cb) > 6000 or len(rb) > 6000 or len(raw) - len(cb) - len(rb) + 2 > 6000:
            raise WireRefused('genesis-size')
        h = lambda kind, data: hashlib.sha256(('fsgg.v3.' + kind + '/1').encode() + b'\0' + data).hexdigest()
        if value['creation_core_digest'] != h('creation', cb) or value['reservation_digest'] != h('reservation', rb) or value['creation_receipt_digest'] != r['creation_receipt_digest']:
            raise WireRefused('genesis-domain-join')
    if family == 'supplier-response':
        mode = value['mode']
        for encoded_event in value['events']:
            nested = strict_b64(encoded_event)
            # Inspect only within bounded bytes; family claims never establish retained authority.
            if len(nested) > 26761:
                raise WireRefused('nested-envelope-size')
            framing = wire_decode(nested)
            wire_schema(framing, schema['$defs']['signed-envelope'], schema)
            nested_payload = strict_b64(framing['payload'])
            claim = wire_decode(nested_payload).get('schema')
            member = 'genesis' if claim == 'fsgg.v3.genesis/1' else 'supplier-event' if claim == 'fsgg.v3.supplier-event/1' else None
            if member is None or (mode == 'genesis' and member != 'genesis') or (mode == 'append' and member != 'supplier-event'):
                raise WireRefused('mode-family')
            maximum = 26761 if member == 'genesis' else 18761
            encoded_maximum = 35682 if member == 'genesis' else 25015
            if len(nested) > maximum or len(encoded_event) > encoded_maximum:
                raise WireRefused('nested-family-size')
            nested_facts = wire_envelope(nested, member, schema)['facts']
            nested_operation = nested_facts['creation_core']['operation_id'] if member == 'genesis' else nested_facts['operation_id']
            if nested_operation != value['operation_id'] or nested_facts['reservation_digest'] != value['reservation_digest']:
                raise WireRefused('response-member-identity')
    timestamp = lambda text: datetime.strptime(text, '%Y-%m-%dT%H:%M:%S.%fZ')
    if family == 'creation-core':
        if not timestamp(value['host_lease_origin_at']) <= timestamp(value['created_at']) <= timestamp(value['stopped_at']) < timestamp(value['host_lease_expires_at']):
            raise WireRefused('host-lease-order')
    if family == 'reservation':
        if value['original_deadline'] != value['command']['deadline']:
            raise WireRefused('original-deadline')
        available = min(timestamp(value['host_lease_expires_at']), timestamp(value['original_deadline'])) - timestamp(value['admitted_at'])
        ticks = (available.days * 86400 + available.seconds) * 10000000 + available.microseconds * 10
        if ticks <= int(value['export_reserve_ticks']) + int(value['cleanup_reserve_ticks']):
            raise WireRefused('zero-work-window')
    if family == 'genesis':
        c, r = value['creation_core'], value['reservation']
        wire_payload(wire_c1(c), 'creation-core', schema)
        wire_payload(wire_c1(r), 'reservation', schema)
        if any(c[k] != r[k] for k in ['correlation_id', 'intent_digest', 'operation_id', 'host_lease_expires_at']):
            raise WireRefused('creation-reservation-identity')
        if timestamp(r['admitted_at']) < timestamp(c['stopped_at']) or timestamp(value['observed_at']) < timestamp(r['admitted_at']):
            raise WireRefused('admission-order')
        if int(r['maximum_runtime_ticks']) > int(c['host_maximum_runtime_cap_ticks']) or int(r['export_reserve_ticks']) < int(c['host_min_export_reserve_ticks']) or int(r['cleanup_reserve_ticks']) < int(c['host_min_cleanup_reserve_ticks']):
            raise WireRefused('host-budget')
    if family == 'supplier-response':
        digest = lambda raw: hashlib.sha256(b'fsgg.v3.signed-envelope/1\0' + raw).hexdigest()
        envelopes = [strict_b64(item) for item in value['events']]
        if value['to_head'] != digest(envelopes[-1]):
            raise WireRefused('response-head-domain')
        if value['mode'] == 'unchanged' and value['from_head'] != value['to_head']:
            raise WireRefused('unchanged-head')
        if value['mode'] == 'append':
            previous, sequence = value['from_head'], None
            for raw in envelopes:
                event = wire_decode(strict_b64(wire_decode(raw)['payload']))
                if event['predecessor_head'] != previous or (sequence is not None and int(event['sequence']) != sequence + 1):
                    raise WireRefused('event-gap-or-fork')
                if event['operation_id'] != value['operation_id'] or event['reservation_digest'] != value['reservation_digest']:
                    raise WireRefused('response-event-identity')
                previous, sequence = digest(raw), int(event['sequence'])
    return value


def wire_context(raw, family, schema, context):
    """Check synthetic original-byte claims; context is never authenticated state."""
    allowed = {'originalIntent', 'creationChallenge', 'creationReceipt', 'originalReservation', 'retainedEvents', 'responseChallenge', 'simulatedNativeImages'}
    if not isinstance(context, dict) or set(context) - allowed:
        raise WireRefused('context-shape')
    digest = lambda kind, data: hashlib.sha256(('fsgg.v3.' + kind + '/1').encode() + b'\0' + data).hexdigest()
    def document(key, member, envelope=False):
        try:
            data = strict_b64(context[key])
        except KeyError as error:
            raise WireRefused('missing-original-context') from error
        facts = wire_envelope(data, member, schema)['facts'] if envelope else wire_payload(data, member, schema)
        return data, facts
    ib, intent = document('originalIntent', 'receiver-intent')
    cb, challenge = document('creationChallenge', 'creation-challenge')
    ec, creation = document('creationReceipt', 'creation-core', True)
    rb, reservation = document('originalReservation', 'reservation')
    intent_digest, receipt_digest = digest('intent', ib), digest('creation-receipt', ec)
    if any(value != intent_digest for value in [challenge['intent_digest'], creation['intent_digest'], reservation['intent_digest']]):
        raise WireRefused('original-intent-domain')
    if creation['challenge_digest'] != digest('challenge', cb) or reservation['creation_receipt_digest'] != receipt_digest:
        raise WireRefused('original-receipt-challenge-domain')
    if any(value['correlation_id'] != intent['correlation_id'] for value in [challenge, creation, reservation]):
        raise WireRefused('original-correlation')
    if intent['command_digest'] != digest('command', wire_c1(reservation['command'])) or intent['command_deadline'] != reservation['original_deadline']:
        raise WireRefused('original-command-domain')
    if any(intent[a] != reservation[b] for a, b in [('qualified_inner_image', 'qualified_inner_image'), ('selection', 'selection'), ('requested_maximum_runtime_ticks', 'maximum_runtime_ticks'), ('requested_export_reserve_ticks', 'export_reserve_ticks'), ('requested_cleanup_reserve_ticks', 'cleanup_reserve_ticks')]):
        raise WireRefused('original-request-terms')
    if creation['profile'] != intent['requested_profile'] or creation['supplier_source_revision'] != intent['requested_source_revision']:
        raise WireRefused('original-source-profile')
    if not challenge['issued_at'] <= creation['created_at'] <= challenge['not_after'] or creation['host_lease_expires_at'] > intent['requested_host_lease_not_after']:
        raise WireRefused('original-challenge-window')
    reservation_digest = digest('reservation', rb)
    head, sequence, previous_lease, lease_expiry = None, None, None, creation['host_lease_expires_at']
    seen_start = False
    retained = context.get('retainedEvents')
    if not isinstance(retained, list) or len(retained) > 1024:
        raise WireRefused('missing-retained-history')
    def advance(envelope):
        nonlocal head, sequence, previous_lease, lease_expiry, seen_start
        framing = wire_decode(envelope)
        wire_schema(framing, schema['$defs']['signed-envelope'], schema)
        payload = strict_b64(framing['payload'])
        member = 'genesis' if head is None else 'supplier-event'
        event = wire_envelope(envelope, member, schema)['facts']
        if member == 'genesis':
            if wire_c1(event['creation_core']) != wire_c1(creation) or wire_c1(event['reservation']) != rb:
                raise WireRefused('original-genesis-bytes')
            sequence = 0
        else:
            if event['operation_id'] != creation['operation_id'] or event['reservation_digest'] != reservation_digest or event['predecessor_head'] != head or int(event['sequence']) != sequence + 1:
                raise WireRefused('original-event-chain')
            body = event['body']
            if event['kind'] in {'launch-intent', 'start-invoked'} and any(body[a] != creation[b] for a, b in [('original_cid', 'cid'), ('original_image_id', 'image_id'), ('original_inode', 'allocation_inode')]):
                raise WireRefused('original-launch-identity')
            if event['kind'] in {'termination', 'inner-cleanup-intent', 'inner-cleanup-observed', 'outer-retire-intent', 'outer-retire-observed'} and body['cid'] != creation['cid']:
                raise WireRefused('original-effect-cid')
            if event['kind'] == 'workload-exit' and body['command_id'] != reservation['command']['command_id']:
                raise WireRefused('original-workload-command')
            if event['kind'] == 'unit-activation-observed':
                if not seen_start or any(body[key] != creation[key] for key in ['unit_name', 'boot_id', 'cid']):
                    raise WireRefused('original-activation-identity-or-order')
            if event['kind'] == 'start-invoked':
                seen_start = True
            if event['kind'] == 'zero-selection-census':
                if reservation['selection']['kind'] != 'none' or body['selection_digest'] != digest('selection', wire_c1(reservation['selection'])):
                    raise WireRefused('original-zero-selection')
            if event['kind'] == 'lease-amendment':
                expected_kind = 'creation-receipt' if previous_lease is None else 'lease-amendment'
                expected_digest = receipt_digest if previous_lease is None else digest('lease-amendment', previous_lease)
                if body['previous_lease_kind'] != expected_kind or body['previous_lease_digest'] != expected_digest or body['old_expiry'] != lease_expiry or body['new_expiry'] <= lease_expiry:
                    raise WireRefused('original-lease-history')
                previous_lease, lease_expiry = payload, body['new_expiry']
            sequence = int(event['sequence'])
        head = digest('signed-envelope', envelope)
    retained_bytes = [strict_b64(item) for item in retained]
    for envelope in retained_bytes:
        advance(envelope)
    value = wire_envelope(raw, family, schema)['facts']
    if family == 'supplier-response':
        challenge_bytes, request = document('responseChallenge', 'response-challenge')
        if value['challenge_digest'] != digest('challenge', challenge_bytes) or request['known_head'] != head or value['from_head'] != head or request['purpose'] != value['purpose']:
            raise WireRefused('original-response-head-challenge')
        if request['reservation_digest'] != reservation_digest or value['reservation_digest'] != reservation_digest or request['operation_id'] != creation['operation_id'] or value['operation_id'] != creation['operation_id']:
            raise WireRefused('original-response-identity')
        if not request['issued_at'] <= value['collected_at'] <= request['not_after']:
            raise WireRefused('response-challenge-window')
        nested = [strict_b64(item) for item in value['events']]
        if value['mode'] == 'unchanged':
            if not retained_bytes or nested != retained_bytes[-1:]:
                raise WireRefused('changed-retained-envelope')
        else:
            if (value['mode'] == 'genesis') != (head is None):
                raise WireRefused('original-response-mode')
            for envelope in nested:
                advance(envelope)
    elif family in {'genesis', 'supplier-event'}:
        advance(raw)
    else:
        raise WireRefused('unsupported-context-family')
    images = context.get('simulatedNativeImages')
    if images is not None:
        if not isinstance(images, dict) or set(images) != {'container.Image', 'imageInspect.ID'} or any(value != creation['image_id'] for value in images.values()):
            raise WireRefused('simulated-native-image-identity')
    return {'authority': 'untrusted', 'capabilities': {'authentication': False, 'grantConsumption': False, 'durableReplay': False, 'native': False}}


def wire_framing(raw, family, schema):
    # Opaque payload bytes in framing fixtures do not establish schema-valid messages.
    value = wire_decode(raw)
    wire_schema(value, schema['$defs']['signed-envelope'], schema)
    payload, signature = strict_b64(value['payload']), strict_b64(value['signature'])
    limit = 6000 if family in {'creation-core', 'reservation'} else GENESIS if family == 'genesis' else EVENT if family == 'supplier-event' else PAYLOAD
    if not 0 < len(signature) <= SIGNATURE or not 0 < len(payload) <= limit:
        raise WireRefused('envelope-size')
    return payload


def wire_envelope(raw, family, schema):
    payload = wire_framing(raw, family, schema)
    parsed = wire_payload(payload, family, schema)
    return {'facts': parsed, 'authority': 'untrusted', 'capabilities': {'authentication': False, 'grantConsumption': False, 'durableReplay': False, 'native': False}, 'futureHeld': ['original-context-and-domain-joins', 'retained-head-sequence-lease-state', 'issuer-pin-signature-request-grant', 'durable-admission', 'native']}


def qualify_wire(corpus, schema):
    for row in corpus['cases']:
        raw = strict_b64(row['bytesBase64url'])
        assert hashlib.sha256(raw).hexdigest() == row['sha256'], row['name']
        assert int(row['byteCount']) == len(raw), row['name']
        for encoded_member in row.get('individuallyBoundedNestedFrames', []):
            wire_framing(strict_b64(encoded_member), 'supplier-event', schema)
        assert set(row['domainDigests']) == set(schema['x-domains']), row['name']
        for kind, expected in row['domainDigests'].items():
            assert hashlib.sha256(('fsgg.v3.' + kind + '/1').encode() + b'\0' + raw).hexdigest() == expected, row['name']
        try:
            if row['encoding'] == 'framing-only':
                wire_framing(raw, row['family'], schema)
            elif 'context' in row:
                result = wire_context(raw, row['family'], schema, row['context'])
                assert not any(result['capabilities'].values())
            elif row['encoding'] == 'envelope':
                result = wire_envelope(raw, row['family'], schema)
                assert not any(result['capabilities'].values())
            else:
                wire_payload(raw, row['family'], schema)
            accepted = True
        except WireRefused:
            accepted = False
        assert accepted == row['structuralAccept'], row['name']
    return len(corpus['cases'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--vectors', type=Path, default=Path(__file__).resolve().parents[1] / 'contracts/portable-workspace/v1/supplier-evidence-sizing-vectors.json')
    parser.add_argument('--wire-vectors', type=Path)
    parser.add_argument('--wire-schema', type=Path)
    parser.add_argument('--native', action='store_true')
    args = parser.parse_args()
    if args.native:
        print(json.dumps({'outcome': 'held', 'reason': 'native-unavailable: supplier verifier, durable replay admission and isolated host profile are not implemented'}))
        return 2
    if args.wire_vectors:
        if not args.wire_schema:
            parser.error('--wire-vectors requires --wire-schema')
        corpus = json.loads(args.wire_vectors.read_text())
        count = qualify_wire(corpus, json.loads(args.wire_schema.read_text()))
        print(json.dumps({'outcome': 'passed', 'wireVectors': count, 'framingOnlyCases': sum(row['encoding'] == 'framing-only' for row in corpus['cases']), 'contextCases': sum('context' in row for row in corpus['cases']), 'authentication': False, 'nativeAcceptance': False}))
        return 0
    count = qualify(json.loads(args.vectors.read_text()))
    print(json.dumps({'outcome': 'passed', 'sizingVectors': count, 'nativeAcceptance': False, 'authentication': False}))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
