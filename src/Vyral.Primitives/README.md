# Vyral.Primitives

Shared low-level primitives used across Vyral packages.

## Ordered IDs

`OrderedId` creates fixed-width decimal identifiers that sort by creation time and can be parsed or decomposed for inspection. It supports:

- monotonic local creation through `OrderedId.CreateString()`
- jittered creation through `OrderedId.CreateJitteredString()` for cases where predictable adjacent IDs are undesirable
- non-throwing creation through `TryCreate` and `TryCreateString`
- fallback string creation through `CreateStringOrFallback`
- timestamp reference IDs through `OrderedId.Reference`
- parsing and inspection through `Parse`, `TryParse`, and `Decompose`

These primitives are intentionally independent of storage providers, orchestration runtimes, retrieval records, or cloud-specific services.
