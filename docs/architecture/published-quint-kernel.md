# Published Quint kernel

GS2-01.4 consumes the Q1-accepted semantic kernel without copying its producer
machinery into this repository.

## Immutable package boundary

`Directory.Packages.local.props` pins `FS.GG.SDD.Artifacts` to the exact NuGet
range `[1.4.0]`. Only `FS.GG.Coordination.Qualification.Contracts` declares a
direct package reference. Locked restore records the canonical exact range
`[1.4.0, 1.4.0]`, resolved version `1.4.0`, and NuGet SHA-512 content hash
`4moNCZKpvO+UsGHGR3IPLAq4xvZm4qvKe9l0T2FJojwitQ4rffNeWyzWcJHRU3p3djsi2KGjak6quRTL2LZ/Fg==`.

The published package identifies repository commit
`7fec4dd4549789bca67aae004b3dad8ee0b7a4fd`. Its signed NuGet.org container has
SHA-256 `aa3ffa4d3e3ae7a8a6c7b25da2b1d4d6eca93f2bb1310ca90cbf4f2241673302`.
The unsigned pre-push container receipt has a different SHA-256 because the
feed signs the package; those transport hashes are evidence, not semantic
identity.

## Semantic identity

The stable semantic binding is the packaged
`quint/q1-identity-manifest.json` payload. Its SHA-256 is
`abd9c18e8146ac3855be58ce88f1efbf5e74a4b1e42c8bc35927478cc74393b2`.
`PublishedQuintKernel.validateManifest` checks that digest before accepting the
required identities:

- schema `fsgg.quint.q2-toolchain-identity/1`;
- profile `fsgg-quint-profile/1`;
- producer merge `FS-GG/FS.GG.SDD@60351fd0614a5c8e4bdf286c21f185196116fd69`;
- independent consumer merge `EHotwagner/S.I.R.@77e56d11867a5e2e7ad99f4d61b0f0c9fff61a5f`;
- Quint `0.32.0` binary SHA-256 `939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f`;
- optional, non-authoritative `quint-llm-kit` guidance at commit
  `cc75369f741af7d490936f82002c2d28e3b3d78d`, tracked-tree SHA-256
  `68a11d403846de3af26759eef97f4a35eff5e71d561d41ea17d96e535c171556`.

The validator is pure: callers supply manifest bytes, and it returns either the
accepted identity or deterministic path-addressed findings. Package discovery,
filesystem reads, extraction, and tool execution stay outside the production
contract.

## Negative boundary

The dependency verifier rejects:

- a non-exact or missing central pin;
- consumption from any production project other than `Qualification.Contracts`;
- a local version override or a missing package-path property;
- an FS.GG.SDD source-project reference or checkout-relative restore source in
  MSBuild properties or case-insensitive repository `NuGet.Config`
  `<packageSources>` entries;
- local `.qnt`, Quint compiler/profile/source/replay files, the Q1 identity
  manifest, or the packaged LMT source layout.

Architecture tests create isolated mutations for the exact pin, source-project
reference, checkout-relative MSBuild feed, checkout-relative `NuGet.Config`
feed, imported effective package metadata, and copied producer machinery. The
dependency gate evaluates the final `PackageReference` items so imported
`Version`, `VersionOverride`, and `GeneratePathProperty` changes are covered.
Unit tests mutate the manifest digest and profile identity. The positive test
reads the manifest from the actual restored package rather than a repository
fixture.

This unit does not add a Quint extractor, profile definition, compiled-contract
generator, ITF implementation, protocol semantics, GitHub mutation authority,
or runtime host. Those remain with FS.GG.SDD or later GS2 units.
