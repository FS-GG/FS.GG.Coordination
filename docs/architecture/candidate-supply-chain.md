# Candidate supply chain

GS2-03.7 adds one deliberately pre-production Q7 route for
`FS.GG.Coordination.Protocol`. The manual `Candidate supply chain` workflow accepts an
exact protected-main commit, checks out that immutable revision, and derives the unique
version `0.0.0-gs2-03-7.<first12sha>`. It is the only accepted version form.

The workflow performs exactly one `dotnet pack`, then canonicalizes the archive entry order
and timestamps without rebuilding or repacking the project. That canonical package is the sole byte source for
the SPDX 2.3 SBOM, SLSA-shaped in-toto provenance, GitHub Packages upload, independent
download, verification attestation, and both clean consumers. It never rebuilds a package
for comparison. Reproducibility is therefore measured at the publication boundary: the
authenticated package served by GitHub Packages must have the same length, SHA-256, and
byte sequence as the one locally prepared package.

The allowed channel is the FS-GG GitHub Packages NuGet endpoint under the typed identity
`github-packages-candidate`. Stable versions, nuget.org, tags, releases, deployments, and
production writes are outside this unit and fail the executable workflow validator before any
publication step. The workflow is manual so an accepted PR can declare publication as a
post-merge obligation and run it once against the exact protected merge. Before preparation,
the workflow fetches protected `main` and proves the selected commit is in that history; an
arbitrary feature commit is refused.

`eng/supply-chain-candidate.fsx` owns preparation and verification. It produces canonical
compact JSON for a candidate manifest, SPDX SBOM, provenance statement, verification
statement, and terminal receipt. Every document binds the package SHA-256 and source
revision. The self-test proves package and SBOM tamper, channel substitution, stable-version
substitution, any pack count other than one, additional or bypassed publication commands,
unreadable workflow input, and missing protected-history binding are red. The validator reads
the production workflow itself, and its positive and negative controls execute in the architecture suite.

After publication, the workflow downloads the package through GitHub Packages' authenticated
flat-container surface into a separate directory. Two isolated fixture consumers restore
only that downloaded package for `FS.GG.Coordination.*`, use fresh .NET and NuGet homes,
build with warnings as errors, and execute distinct API observations. The terminal receipt
is retained with the exact candidate and attestations as an immutable Actions artifact.
