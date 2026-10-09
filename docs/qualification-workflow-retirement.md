# Retired one-off qualification dispatch

The four-D public-provider workflow was disabled and removed from GitHub Actions
on 2026-10-09 during branch cleanup. Its former qualification branch is not an
active dispatch target. No admission was broadened to `main`.

The [exact former workflow](https://github.com/FS-GG/FS.GG.Coordination/blob/b1dbf434ffafbdfc145eafc054606e145fa13020/.github/workflows/fourd-public-provider-qualification.yml)
is preserved unchanged as a [regression fixture](../tests/fixtures/retired-workflows/fourd-public-provider-qualification.yml).
Existing source tests still check its historical safeguards. This archive neither
changes original qualification outcomes nor authorizes execution. Any future
native qualification needs a separately selected current route.
