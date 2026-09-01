# GS2-05.1 work taxonomy evidence

This directory freezes the complete declared legacy Class × Kind corpus (including the specified
absent-Kind-as-work interpretation), the seven already-native no-op cases, a separately authored
expectation inventory, and the executable plain Quint model.

It covers pure classification and deterministic migration planning only. It deliberately excludes
GitHub reads/writes, live issue conversion, organization fields, Projects, intake, and cutover.

Update the model, corpus, independent expectations, and validator together before changing the F#
contract. The registered validator rejects an omitted cross-product row, duplicate case identity,
changed canonical bytes, or a divergence between the independent expectation and implementation.
