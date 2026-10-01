# Native collector installation v3 manager contract

Installation v3 is prospective. The manager consumes an owner-private `source-reference.json` adjacent to the Host configuration and never promotes or rewrites a v1/v2 installation or source reference.

The v3 source reference has exactly the v2 fields plus `verifierRuntimeManifestSha256`, keeps `captureQualified` false, binds `collectorReadOnlyTarget` to the configured Codex home, and binds `readerProfileSha256` to `EvidenceRoot/fixed-native-capability-profile.json`.

The runtime manifest is closed schema `fsgg.telemetry.native-verifier-runtime/1`. It is limited to 1 MiB, 4096 sorted unique files, and 512 MiB of declared closure bytes. The manager rereads every declared regular file, checks its size and SHA-256, requires the runtime and canonical verifier module in the inventory, and derives the receipt hashes from the bytes it consumed. The canonical module SHA-256 is `8d6a33beae9a4de84fa7a703809e9b1a1656359a085f92091cf56de3b77fd3ba`.

Run the F# behavioral harness against the canonical module and a new disposable path:

```console
tests/telemetry-native-collector-installation/run-v3.sh \
  /absolute/path/to/.github/tools/learn_01_native_source.py \
  /absolute/new/disposable-fixture-root
```

The fixture identifies itself as `disposable-fixture-not-qualified-production`. Its runtime image identity and executable exist only to exercise manager custody. They are not production runtime qualification, publication, installation, or activation evidence.
