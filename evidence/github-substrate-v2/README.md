# GitHub substrate v2 evidence

This root implements `fsgg.coordination.evidence-storage-policy/1`.

Git contains only versioned schemas, compact indexes, digests, manifests,
reviews, receipts, and payloads no larger than 65,536 bytes. Large generated
payloads belong in an immutable GitHub Actions artifact or GitHub release asset;
their tracked manifest records the immutable producer identity, exact byte
length, media type, and lowercase SHA-256.

Run the closed offline contract gate with:

```console
dotnet fsi eng/validate-evidence-storage.fsx -- --self-test evidence/github-substrate-v2
```

Accepted receipts are append-only. The validator reads them and verifies their
indexed bytes; it never creates or rewrites them.
