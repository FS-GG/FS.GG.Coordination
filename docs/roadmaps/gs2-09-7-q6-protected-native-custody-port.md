# GS2-09.7 Q6 protected native custody port hold

Status: **source-only fake-port contract; no native reader, canonicalizer,
custody store, signer, Q5/Q6 receipt or admission is installed**.

The [native byte commitment](gs2-09-7-q6-native-byte-custody.md) accepts
caller-supplied byte arrays. This new port boundary looks up retained bytes
through `IProtectedNativeCustodyPort` instead. It requires independently
provided pins for the reader resource, canonicalizer artifact SHA-256 and
custody store resource. Every lookup carries the exact run nonce, plan seal,
sandbox, witness generation, subject and receipt hash. Each returned record
must match its lookup and pinned store, have a unique custody object ID and
contain exactly one step or terminal response. The signed payload binds the
pins, all lookup and custody object identities, and the existing ordered
byte commitments. An absent port or pin, descriptor drift, missing retained
read, cross-run record and duplicate object refuse in controlled fake tests.

The protected Q5/Q6 owner must supply and attest the installation facts:

- The reader, canonicalizer, custody store and signer run outside every
  candidate-writable workspace, with exact principals, ACLs and artifact
  digests independently read from their protected hosts.
- The protected journal durably records each receipt commit and native read
  in one order namespace; lookup records are immutable and keyed by the
  selected run, plan, sandbox, generation, subject and receipt.
- The reader retains raw native responses, exact requests, provider identity,
  status and revision. The installed canonicalizer proves each canonical
  state or epoch projection derives from that response and the selected
  provider endpoint. The store's readback and object IDs are authoritative.
- The protected signer releases one signature only after all six retained
  records and the terminal OperatingV1 epoch have been read back. Revocation,
  crash and unknown-result paths keep the qualification pending.

An injected fake port can satisfy this source contract; its success is not
evidence of those installation properties. There is no installed parser for
all five rollback domains, so source cannot establish native origin or
canonicalization alone. The accepted Q5 nine-authority and Q6 six-cut
qualification and protected receipt remain required. #3690 is unadmitted;
accepted GS2-09.6 command bytes and live pins remain unchanged.
