# Portable workspace contract v1

This directory is the Coordination-owned first contract slice for V2-LANG-01.2. It defines closed JSON
schemas for reviewed toolchain profiles, fenced commands and structured results. Product source does not
import this repository's CLR types, Akka types or an agent SDK.

Canonical writers emit properties in schema order, UTF-8 without a BOM or insignificant whitespace.
Identifiers use their displayed ASCII form. Command UUIDs are lowercase `D` form. Counters are unsigned
base-10 strings without leading zeroes so JavaScript and other runtimes do not lose integer precision.
Timestamps are UTC with exactly six fractional digits. Optional properties are omitted; JSON `null` is
never an alternate spelling for absence. Results represent known, missing and unknown evidence as tagged
objects and use a structured error object when an error was observed.

Profiles contain named entry points, not uploaded shell text. A host maps those names to reviewed fixed
operations. `PortableWorkspaceAdapter.prepare` checks workspace scope, exact profile and source revision,
workflow revision, fence generation, deadline, component and operation before it returns an entry point.
Contract examples deliberately use recognizable placeholder revisions and image digests. They remain valid
serialization fixtures, while operation preparation requires a 40- or 64-character lowercase hexadecimal
source revision and a non-placeholder SHA-256 image digest. Preparing an operation remains a read-only plan;
it grants no execution authority.

The packaged `fsgg-coordination workspace-contract` surface exports the exact schemas and canonical examples,
validates and digests canonical documents, and prepares a fixed named operation after all fences match. Its
artifacts are compiled into the tool, so schema export does not depend on a repository checkout. Unsupported
schema identifiers, JSON nulls, duplicate or unknown fields and noncanonical encodings are refused.

The Python and TypeScript/Python examples demonstrate non-.NET and mixed component profiles. They are
contract fixtures only: publication through SDD/Templates, clean creation, retained adoption, local-only
operation and native product qualification remain receiver-owned V2-LANG-01.2 work.

The finite 0.2.0 release bundle is prepared separately from these frozen contract bytes. It contains this
directory, the reviewed Linux amd64 image recipe and pinned toolchain inputs, a sanitized qualification summary,
and checksums. Its release manifest binds the exact CLI package, bundle and tested OCI archive. The helper accepts
only explicit package, image-manifest, OCI archive and executor-evidence inputs; it has no runtime image fallback.
Publication, receiver activation and generated-workspace changes require their own authority and evidence.
