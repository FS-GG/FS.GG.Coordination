# GS2-06.5 permission compilation evidence

`corpus.json` is the complete candidate-local registered-interpreter inventory. Each row names one
interpreter operation, its single principal/environment class, and the exact App and workflow
permissions compiled for it. `independent-expectations.json` is a separately shaped oracle over
identity, operation, class cardinality, environment separation, control inventory, and exact seal.

Run `dotnet fsi eng/validate-github-permission-compilation.fsx -- .` for the exact Q3 contract. The
validator binds the accepted GS2-06.4 receipt and canonical roadmap bytes, rejects incomplete or
unknown inventory input before compilation, exercises generated and independent negative controls,
and exposes no production settings/permission mutation, deployment, publication, or release path.
