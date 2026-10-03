# Python local retrieval qualification

The Python runtime remains prototype. The preparation optimizations described
here preserve its current local retrieval policy; they do not establish a
relevance advantage over native FTS5 or promote the full runtime.

## Lexical analyzers and ranking

SQLite FTS5 selects candidates with `unicode61` and its default
`remove_diacritics=1`. Python then scores selected fields with analyzer
`vyral.python.lexical.lower-alnum.v1`: lowercase text, split on non-alphanumeric
characters, preserve accented letters, and perform no Unicode normalization.
It uses lowercase rather than full Unicode case folding. Combining marks are
separators, so composed and decomposed spellings can produce different tokens.

For example, FTS5 can select `Café naïve résumé` for query `cafe`, but Python
rejects that candidate with zero lexical score; `Café` and `CAFÉ` match. This is
the current strict scorer behavior. Candidate analysis is intentionally broader
than scoring. Report candidate misses separately from scorer rejections.
Returned lexical diagnostics identify both analyzers as `lexicalAnalyzer` and
`lexicalCandidateAnalyzer`.

Custom BM25 computes document count, document frequency, average length and score
normalization from the selected candidate pool after required-phrase filtering.
Changing component filters or `scanLimit` can change scores and ranking even
when the smaller pool already includes all known positives. The final score
combines normalized BM25 with configured phrase, exact and metadata bonuses and
clamps to 1. Distinct raw scores can consequently tie; ordering then uses
partition key and record ID. These scores are not calibrated probabilities or
comparable confidence across queries. Inspect `lexicalRaw`, `lexicalBase`,
`termIdf`, `corpusDocumentCount`, and the other existing diagnostics.

## Prepared-field cache

Each `SQLiteRecordStore` owns a thread-safe LRU of query-independent prepared
fields. Defaults are 1,024 entries and 8 MiB of conservatively estimated retained
allocations. Both bounds must permit an entry; oversized preparations bypass
the cache. The byte estimate includes retained keys, field objects, normalized
strings and term counters, but is not a process-RSS limit.

The key binds the full actual record payload, selected fields and scorer analyzer
identity. It does not rely on caller-supplied revision alone. Cached values retain
fields and lengths, never a record object, corpus statistics or query scores;
each hit binds the current record and recomputes query/candidate-dependent work.
Mutations through that store clear its cache. Writes by another store are detected
through changed actual inputs on subsequent reads. Cache memory is process-local;
deletion through another writer does not actively purge an existing process's
retained fields. Entries remain bounded and explicit clearing is available.

To disable the cache for an experiment:

```python
store = SQLiteRecordStore("evidence.sqlite", lexical_cache_max_entries=0)
```

Use `lexical_cache_max_entries` and `lexical_cache_max_bytes` for custom bounds,
or `store.clear_lexical_cache()` to discard retained preparations. Closing an
embedded `VyralRuntime` drains its executor and clears its record cache.

## Flat vector search

The dependency-free Python baseline uses the standard-library C float scalar
conversion and retains individual float32 rounding steps
and their explicit overflow/nonfinite errors. Cosine query-norm preparation is
reused within one search, and diagnostics are constructed for retained top
results. Every eligible record and vector row is still hydrated and validated,
including nonselected records: malformed JSON, dimensions or vector byte lengths
fail the search. A bounded top-result heap replaces retaining and sorting every
matching record; it still exhausts and validates all eligible rows. Namespace/partition filtering, thresholds, stable ties and
continuation retain their existing behavior.

This remains exhaustive flat search. High-dimensional Python arithmetic and
full-record hydration have material cost. Native array or compiled kernels need
separate numeric, error, platform and packaging qualification; different
accumulation order must not silently replace the current baseline. Grouping
several passages by investigation item is an application/context-assembly policy
and should be evaluated separately from vector arithmetic or candidate limits.

## Reproduce the bounded preparation probe

```sh
python3 scripts/benchmark-python-retrieval.py --output /tmp/retrieval-preparation.json
```

Defaults: 200 synthetic records, a 100-record lexical candidate limit, eight
queries, 384 vector dimensions and three repeats. The receipt binds source-file
and input hashes, Python/SQLite/platform versions, analyzers and cache bounds.
It checks exact cold/warm lexical records, scores, diagnostics and continuation,
and exact float32 vector score bits. Lexical timings include SQLite retrieval;
vector timings isolate per-candidate versus per-query preparation and exclude
persistence/hydration. Timings characterize one host and fixture, with no SLA or
model-quality claim. Run supported-platform qualification separately before
changing profile maturity.

For a frozen evidence comparison, preserve the cache manifest, source revisions,
eligibility filters, fields, prefix/phrase rules and candidate budgets. Use one
final output renderer and the consuming tokenizer for equal output budgets;
RAG `maxChars` bounds excerpt text before citation formatting and other output.

## High-dimensional qualification and grouping decision

From a Git source checkout, run:

    python3 scripts/benchmark-python-vector-search.py --output /tmp/vector-search.json

The bounded default uses 1,000 deterministic synthetic 384-dimensional records,
three metrics and three repeats. It compares complete records, scores, diagnostics
and continuation against the pinned previous source, plus 2,075 scalar/vector bit
and error controls. It binds source/input hashes and platform versions. Use the
existing Python platform workflow to qualify supported Python 3.10–3.12 on Linux,
macOS and Windows; a single-host timing receipt does not replace that matrix.
No compiled extension, NumPy dependency, relaxed score tolerance, fast-math or
model download is introduced.

The current fail-closed policy deliberately prevents ranking before complete record
validation: a malformed nonselected record must still fail the query. Splitting
storage validation from hydration would require a separately versioned contract
and corruption controls. Keep full validation in this profile.

Budget passages and distinct investigation items as separate experiments. Preserve
one larger candidate pool, group by an explicit caller-owned item identifier after
retrieval, and retain the first ranked passage per item (or an explicitly bounded
number). Compare final rendered output under the same tokenizer budget. Report
pool size, eligible items, selected passages and selected items; grouping after a
small top pool can hide additional items and cannot establish recall equivalence.
The probe reports passage/distinct-item counts without changing Vyral scores or
introducing domain-specific identifiers into the engine. This closes the mechanics
investigation, not application relevance qualification or full runtime promotion.

The [dated synthetic receipt](../../qualification/python-vector-mechanics-2026-10-03.json)
retains all 2,075 numeric/error controls and nine complete responses from a
1,000-record, 384-dimension fixture. Median complete retrieval/serialization was
1,026.9 ms before and 957.1 ms after on this host. Treat this as bounded mechanics
evidence. The supported-platform workflow repeats the same parity controls on
Python 3.10–3.12 across Linux, macOS and Windows with a smaller record count.
