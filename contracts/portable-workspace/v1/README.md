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

The Python and TypeScript/Python examples demonstrate non-.NET and mixed component profiles. They are
contract fixtures only: publication through SDD/Templates, clean creation, retained adoption, local-only
operation and native product qualification remain receiver-owned V2-LANG-01.2 work.
