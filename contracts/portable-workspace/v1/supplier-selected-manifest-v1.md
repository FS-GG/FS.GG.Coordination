# Opt-in selected manifest companion /1

`fsgg.v3.selected-manifest/1` defines exact member bytes only for newly supplied
manifests whose caller explicitly selects this companion. It does not upgrade
legacy digest-only originals. The frozen supplier-wire /1, scalar manifest DTO,
sixteen hash domains and supplier-journal /2 and /3 retain their existing bytes
and behavior.

The root contains exactly `members` and `schema`; each member contains exactly
`bytes`, `path` and `sha256`. C1 is compact sorted ASCII JSON and one final LF.
Numeric tokens, duplicate/unknown fields, alternate escapes, BOM, extra whitespace
and trailing bytes refuse. `bytes` is a canonical decimal u64 string; lowercase
64-hex `sha256` commits ordinary content SHA256. Sizes are supplied commitments,
not proof that file content exists. Zero-byte regular files are valid.

Require 1–16 members, ordinal increasing paths, 1–128 ASCII path bytes and at most
eight slash-separated segments matching `[A-Za-z0-9._-]+`, excluding `.` and
`..`. Empty segments, absolute paths, backslashes, colons, case aliases and
regular-file ancestor conflicts (including ASCII case aliases) refuse. This is
neither a Windows compatibility promise nor native filesystem safety; descriptor,
no-follow and actual regular-file checks remain host obligations.

Canonical bytes M are bounded by 6000 including LF. With all conjunctive limits,
the largest realizable document is 3989 bytes: sixteen 128-byte paths, sixteen
64-byte hashes and sixteen nineteen-digit sizes summing within u64. Sixteen
nineteen-digit sizes are realizable; adding a twentieth digit requires reducing
others and cannot increase total encoded length. The corpus includes this
positive, separate exact-cap invalid bytes and 6001-byte refusal, without padding
fields. Sum overflow refuses.

The manifest digest remains SHA256(`fsgg.v3.manifest/1` + NUL + exact M).
`selected` requires exact bytes, digest, count and checked total equality.
`none` accepts absent bytes only, producing inert no-selection data; empty
members never become `none`. Neither outcome provides a host census, durable
export/readback, authentication, ACK, handoff or cleanup evidence. The selection
commitment remains a separate existing domain. Legacy synthetic digests cannot
be promoted into a validated companion value.

`PortableSupplierManifest` parses, validates, serializes and joins these bytes.
Its immutable validated value returns fresh byte buffers. The maintained Python
qualifier explicitly selects the same companion using:

```sh
python3 -I -S -B eng/qualify-portable-supplier-contract.py --manifest-vectors contracts/portable-workspace/v1/supplier-selected-manifest-v1-vectors.json
```

The immutable corpus was constructed with an independent standard-library JSON
and SHA256 calculation, without importing either codec. Both actual codecs must
match its exact bytes and commitments, and refuse the negative corpus. The schema
is structural; codecs additionally enforce C1, u64/path bounds and context joins.
Default sizing/wire qualification is unchanged. Source qualification leaves
production verification, journal durability, export, host installation and native
acceptance held.
