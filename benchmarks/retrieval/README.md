# Source-native retrieval comparison

Vyral retains a local comparison between the experimental ripgrep adapter and
the Python runtime's SQLite FTS5 lexical retrieval. The purpose is narrow: prove
whether direct source search has a user-facing role that is not already served
better by an indexed Vyral collection.

The retained receipt uses 16 labeled documents, 2,000 deterministic noise
documents, 18 queries, top 5 retrieval, and 30 timed iterations per query. It
compares fixed-string ripgrep with Vyral lexical `any`, `all`, and prefix
policies. The source tree was clean and the report identifies its exact fixture,
fixture digest, source commit, runtime versions, and machine shape.

## Result

The adapter is retained as an experimental integration for one identifiable
case: bounded exact-literal lookup in an authorized, rapidly changing local
code or Markdown tree when copying that tree into a retrieval index is not
justified.

| Query group | ripgrep fixed recall@5 | Vyral lexical policy | Vyral recall@5 |
| --- | ---: | --- | ---: |
| Exact literals, 8 cases | 1.00 | `all` | 1.00 |
| Reordered or separated terms, 4 cases | 0.00 | `all` | 0.75 |
| Prefixes, 2 cases | 0.00 | `all` plus prefix matching | 1.00 |
| Ambiguous single terms, 2 cases | 1.00 | `all` | 1.00 |
| Queries expected to return nothing, 2 cases | 1.00 | `all` | 1.00 |

On this machine, ripgrep's warm p50/p95 was 16.707/33.120 ms. Indexed Vyral
lexical `all` was 1.152/1.421 ms, and the prefix policy was 1.202/1.524 ms.
Vyral is decisively faster after indexing. The default lexical `any` policy had
a 286.457 ms p95 because common fixture terms produced broad candidate sets;
its term-retrieval recall was 1.00, but returned precision was 0.425.

The tradeoff appears before the first query. The ripgrep adapter initialized in
2.632 ms and used the existing source tree directly. Mirroring 2,016 documents
into the local Vyral store took 26,510.524 ms and produced a 13,324,288-byte
database for a 288,995-byte fixture corpus. These ratios are fixture-specific;
the small generated documents emphasize per-record overhead and should not be
generalized to larger records.

After a source edit, ripgrep returned the new canary without an index refresh
in 16.796 ms. Vyral correctly remained stale until the changed record was
upserted, then returned it in 2.052 ms. Sensitive-path canaries were excluded,
the absolute root was not disclosed, and the adapter emitted line citations
bound to source SHA-256 revisions.

The evidence does not support using ripgrep for semantic or paraphrase
retrieval, prefix discovery, governed record filtering, tenant authorization,
remote corpora, or non-text sources. Those remain Vyral indexed retrieval or
adapter concerns.

Results are from one Linux x86-64 machine and establish this bounded local use
case, not a universal performance ranking. See the
[full retained receipt](ripgrep-vs-vyral-local-2026-08-11.json) for every query,
returned path, metric, timing, and admission criterion.

## Reproduce

Run the full comparison from a clean checkout:

```bash
python3 scripts/benchmark-ripgrep-retrieval.py \
  --output /tmp/ripgrep-vs-vyral.json \
  --noise-documents 2000 \
  --iterations 30 \
  --require-admission

python3 scripts/verify-ripgrep-retrieval-report.py \
  /tmp/ripgrep-vs-vyral.json \
  --require-admission
```

CI also runs a smaller comparison and proves that its verifier rejects altered
quality metrics. A future result that fails any admission criterion changes the
decision to `reject`; the experimental adapter must then be revised or removed.
