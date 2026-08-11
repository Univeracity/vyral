# Source-native and indexed retrieval

Retrieval should follow the corpus rather than forcing every source through an
embedding pipeline. For local, current, structured text that an application is
already authorized to read, direct source search is often the strongest first
path. It preserves exact identifiers, reflects edits immediately, and avoids an
index lifecycle.

Choose the least complex path that meets a measured need:

| Corpus need | First path to test |
| --- | --- |
| Code, Markdown, configuration, or other greppable text | Source-native fixed-string search |
| Governed records, filters, tenant boundaries, or stable snapshots | Vyral lexical retrieval |
| Paraphrases, cross-lingual queries, or weak keyword overlap | Semantic vector retrieval |
| Exact precision plus demonstrated semantic recall | Evaluated hybrid retrieval |
| Top-rank quality improves enough to justify another model call | Evaluated reranking |
| PDFs, images, audio, or other non-greppable sources | Extract or index through an appropriate adapter |

Vector retrieval remains valuable for large unstructured or remote corpora,
semantic discovery, aggregation, access-controlled retrieval services, and
modalities that cannot be searched as text. Hybrid retrieval is a policy to
earn with corpus evidence, not an automatic upgrade over a simpler method.

## Experimental ripgrep integration

The Python runtime includes an experimental, read-only ripgrep integration. It
is not part of the stable Vyral wire contract and is not automatically exposed
through REST or MCP. An application owns the authorized root and may place the
result behind its own Vyral handler after applying its authorization policy.

```python
from pathlib import Path

from vyral_runtime.integrations.ripgrep import (
    RipgrepAdapterOptions,
    RipgrepSearchAdapter,
    RipgrepSearchRequest,
)

search = RipgrepSearchAdapter(
    Path("./knowledge"),
    RipgrepAdapterOptions(
        include_globs=("*.py", "*.md"),
        exclude_globs=("generated/**",),
        max_results=40,
        timeout_seconds=3,
    ),
)

result = search.search(
    RipgrepSearchRequest(
        "durable receipt",
        limit=10,
        case_sensitive=False,
    )
)

for match in result.matches:
    print(match.source_uri, match.source_revision, match.line_text)
```

The adapter:

- accepts one fixed root and static include/exclude globs at construction;
- passes the query over standard input, never through a shell or process
  argument;
- disables ripgrep configuration, regular expressions, hidden-file search,
  and symbolic-link following;
- applies non-overridable credential-file exclusions and filters sensitive
  filenames from parsed results;
- bounds query length, result count, source size, line size, process output,
  and execution time; and
- emits root-relative line citations plus a SHA-256 source revision without
  disclosing the absolute root.

Filename filtering is not data-loss prevention. Point the adapter only at a
root whose contents the caller is authorized to retrieve, keep secrets outside
that root, and authorize any handler or MCP tool before search begins. Treat
returned lines as untrusted source content when they enter an agent context.

The executable is resolved once when the adapter starts and must identify
itself as ripgrep. Install `rg` separately and keep its version in evaluation
receipts when results need to be reproduced.

### Retained comparison evidence

A clean-tree local comparison over 2,016 documents and 18 labeled queries
identifies one bounded role for this integration. Ripgrep achieved 1.00 recall,
1.00 returned precision, and 1.00 MRR across eight exact-literal cases, returned
a committed edit without an index refresh, and produced revision-bound line
citations. Its warm p50/p95 was 16.734/33.398 ms.

Vyral lexical `all` matched the exact-literal quality at a much lower indexed
p50/p95 of 1.172/1.508 ms and beat fixed-string ripgrep on reordered terms.
Vyral prefix retrieval achieved 1.00 recall on the two prefix cases where
ripgrep returned none. Building the local record index took 29.158 seconds;
ripgrep used the authorized source tree directly and initialized in 2.668 ms.

The adapter therefore remains experimental for exact identifiers, error codes,
headers, and literal phrases in current local code or Markdown when an index
would be duplicate state. Prefer Vyral lexical retrieval once records need
filters, tenant boundaries, stable snapshots, term-order tolerance, prefixes,
or consistently lower query latency. The
[retained report](../../benchmarks/retrieval/README.md) describes the fixture,
limitations, complete metrics, and reproduction command.

## Executable migration walkthrough

Run the bundled walkthrough from a source checkout:

```bash
python3 examples/python/retrieval_migration.py
```

The first stage uses the bounded adapter to find one exact phrase directly in
an authorized Markdown tree. It also shows that a reordered version of that
phrase is not a fixed-string match. The second stage deliberately copies the
three example documents into a local Vyral collection and retrieves the
reordered query with lexical `all` matching and no embeddings.

This is the intended migration boundary: do not build duplicate state while
direct search meets the measured need. Introduce an index when the application
needs governed records, partitions, filters, snapshots, order-tolerant terms,
prefixes, or lower repeated-query latency. Replace the walkthrough's authored
fixture with an application-owned ingestion policy; never turn arbitrary
filesystem traversal into an implicit REST or MCP capability.

## Corpus comparison recipe

Use a labeled fixture that resembles the real corpus. Keep the source revision,
query set, expected records or files, hard negatives, and relevance grades in
version control. Include exact identifiers, paraphrases, recently edited
content, ambiguous terms, and queries that should return nothing.

Run five variants against the same fixture:

1. Source-native `rg` through `RipgrepSearchAdapter`.
2. Vyral lexical retrieval over source-backed records.
3. Vyral vector retrieval with the intended production embedding provider.
4. Vyral hybrid retrieval with declared fusion weights.
5. The best prior candidate set with the intended reranker.

Vyral can compare the four indexed variants in one
`POST /retrieval/evaluate/compare` request. The essential variant shape is:

```json
{
  "variants": [
    {
      "id": "lexical",
      "searchMode": "lexical",
      "lexical": { "fields": ["/content/text", "/metadata/title"] }
    },
    {
      "id": "vector",
      "searchMode": "vector",
      "embedding": { "field": "contentEmbedding", "purpose": "query" }
    },
    {
      "id": "hybrid",
      "searchMode": "hybrid",
      "embedding": { "field": "contentEmbedding", "purpose": "query" },
      "lexical": { "fields": ["/content/text", "/metadata/title"] },
      "hybrid": {
        "fusion": "rrf",
        "lexicalWeight": 0.65,
        "vectorWeight": 0.35
      }
    },
    {
      "id": "reranked-lexical",
      "searchMode": "lexical",
      "lexical": { "fields": ["/content/text", "/metadata/title"] },
      "rerank": { "enabled": true, "candidateLimit": 20 }
    }
  ]
}
```

Each `cases` entry supplies a normal retrieval request, expected matches, hard
negatives, and `k`. Normalize each source-native result to the same labeled
file or source id so it can be scored beside the indexed variants.

Record at least these measures:

| Measure | What to retain |
| --- | --- |
| Accuracy | hit rate, recall@k, precision@k, MRR, nDCG, and hard-negative rate |
| Latency | warm and cold p50/p95, including agent/tool handoff where applicable |
| Context | returned characters and tokens measured with the actual consuming tokenizer |
| Freshness | time from a committed source edit until that edit can be retrieved |
| Cost | embedding, rerank, storage, indexing, request, and agent-token units |
| Reliability | timeouts, fallbacks, empty results, malformed citations, and provider failures |

For a freshness check, modify one labeled source after the initial run. Search
the source immediately, then measure each index's documented update path until
the new text is retrievable. Do not compare direct search's zero index lag with
an index that was never refreshed.

The bundled `local-token-hash` embedding provider is appropriate for vector
mechanics and deterministic tests, but it is not semantic. Label any comparison
that uses it as a mechanics baseline. A semantic-quality conclusion requires
the embedding and rerank providers intended for the target deployment.

Promote a more complex retrieval path only when the gain is repeatable and
worth its freshness, latency, cost, and operational tradeoffs. Retain the
fixture and comparison output so later adapter or model changes can be judged
against the same boundary.
