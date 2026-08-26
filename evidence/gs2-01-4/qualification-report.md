# GS2-01.4 qualification report

Observed at `2026-08-26T22:55:38Z` from base
`e970c56a895226b203ccbf6f922c1c3f1adbe3d6` plus the work-item tree.

## Published package

- Package: `FS.GG.SDD.Artifacts` exact range `[1.4.0]`, resolved `1.4.0`.
- Supported read used by clean restore: NuGet.org.
- Lock-file SHA-512 content hash:
  `4moNCZKpvO+UsGHGR3IPLAq4xvZm4qvKe9l0T2FJojwitQ4rffNeWyzWcJHRU3p3djsi2KGjak6quRTL2LZ/Fg==`.
- Signed downloaded nupkg SHA-256:
  `aa3ffa4d3e3ae7a8a6c7b25da2b1d4d6eca93f2bb1310ca90cbf4f2241673302`.
- Embedded identity-manifest SHA-256:
  `abd9c18e8146ac3855be58ce88f1efbf5e74a4b1e42c8bc35927478cc74393b2`.
- Package repository commit:
  `7fec4dd4549789bca67aae004b3dad8ee0b7a4fd`.

## Clean consumer run

With `NUGET_PACKAGES` set to a newly created empty directory:

```text
dotnet restore FS.GG.Coordination.sln --locked-mode
dotnet build FS.GG.Coordination.sln --no-restore -c Release
dotnet test FS.GG.Coordination.sln --no-build --no-restore -c Release
```

Outcome: restore succeeded from the supported read feed; build succeeded with
zero warnings and zero errors. The retained follow-up suite has 29 passing test
executions after adding the explicit unauthorized-consumer mutation.

## Retained test evidence

- `test-results/unit.trx`: 7 passed, 0 failed, 0 skipped; SHA-256
  `169687fc2d1660b23065dd717316c4ff667260a7d43896a3e495db947b45c086`.
- `test-results/architecture.trx`: 22 passed, 0 failed, 0 skipped; SHA-256
  `440ff8d9676d57b6d9d9cffdaebb92026f29e0d8826110474e4c83ebac368967`.
- `dotnet fsi eng/verify-dependencies.fsx -- --root .` returned
  `DEPENDENCY_POLICY_OK projects=6`.

The positive manifest test reads the actual restored package. Independent
mutations prove rejection of an altered manifest digest/profile, malformed
manifest, non-exact pin, unauthorized package consumer, source-project
reference, checkout-relative package source, and copied producer machinery.
Earlier GS2-01.3 negative controls remain
green as part of the same architecture run.

## Boundary disposition

No extractor, profile definition, compiled-contract generator, generic ITF
machinery, `.qnt` source, FS.GG.SDD checkout reference, protocol behavior,
GitHub mutation, listener, or deployment authority was added.
