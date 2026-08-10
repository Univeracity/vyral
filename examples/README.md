# Vyral Examples

These examples exercise the local HTTP boundary intended for Python and JavaScript consumers.

Start the server from the repository root:

```bash
dotnet run --project src/Vyral.Server
```

Run the Python quickstart:

```bash
python3 examples/python/rag_quickstart.py
```

Run the JavaScript quickstart:

```bash
node examples/javascript/rag-quickstart.mjs
```

Both examples create a collection, call `/collections/{collection}/rag/ingest-text` with the server's configured embedding provider, call `/rag/context`, and print returned citation IDs plus the deterministic `contextText` block. Set `VYRAL_URL` to point at a non-default server URL. Set `VYRAL_COLLECTION` to override the sample collection name.

Run the broader consumer workflows when you want to exercise lexical RAG, vector RAG, lexical plus rerank, GraphRAG expansion/evaluation, `ai.extract`, provider model listing, and quota discovery:

```bash
python3 examples/python/consumer_workflows.py
node examples/javascript/consumer-workflows.mjs
```

These scripts create isolated example collections by default. Override `VYRAL_COLLECTION`, `VYRAL_GRAPH_COLLECTION`, `VYRAL_RECIPE_AI_PROVIDER`, and `VYRAL_RECIPE_RERANK_PROVIDER` to point them at different local test surfaces.

## Execution Runtime Sample

The execution runtime sample is a .NET console app rather than an HTTP client script. It runs
entirely on local SQLite and demonstrates a portable plugin, idempotent run start, status polling,
history, and artifacts:

```bash
dotnet run --project samples/Vyral.Execution.LocalSample/Vyral.Execution.LocalSample.csproj -- --once
```

## Prefect composition

`python/prefect_receipt_flow.py` shows how a
Prefect 3 flow can compose a receipt-bound Vyral operation without confusing
the two systems' responsibilities. Prefect owns scheduling, task retries, and
operator visibility. Vyral owns durable admission, lifecycle state, and the
result. The admission task derives one idempotency key per Prefect flow run, so
a task retry observes the accepted Vyral job instead of creating another one.

With a local Vyral server running:

```bash
python3 -m pip install --editable clients/python 'prefect>=3,<4'
python3 examples/python/prefect_receipt_flow.py
```

Set `VYRAL_URL` for a different server and `VYRAL_API_KEY` when that server
requires API-key authentication. The key is resolved inside the task and is
not passed as a Prefect flow parameter. This is an optional composition recipe,
not a Prefect runtime adapter or a provider-qualification claim.

## GraphRAG Recipes

GraphRAG works best when the record collection remains the retrieval source of truth and the graph is used to add bounded relationships, provenance, and review state around retrieved chunks.

Evidence expansion:

- Store pages or chunks as RAG records with `metadata.graphNodeId` pointing to the corresponding graph node.
- Import graph nodes for pages, claims, people, entities, exhibits, events, or issues.
- Import grounded edges such as `supports`, `contradicts`, `mentions`, `cites`, or `sameAs` with `sourceSpans` where possible.
- Use `graphExpansion.seedJsonPointers` such as `/metadata/graphNodeId` and `/id`, conservative `maxDepth`, and `requireSourceGrounding` for evidence-sensitive workflows.
- Use `/rag/context/evaluate` with expected graph nodes, edges, and source-grounded provenance before promoting a traversal profile.

Reference expansion:

- Store passages as normal RAG chunks and model concepts, categories, interpretations, authorities, or cross-references as graph nodes.
- Use graph predicates that explain the relationship instead of overloading a generic link, for example `interprets`, `supports`, `qualifies`, `contrasts`, or `dependsOn`.
- Keep graph context budgets small enough that retrieved text remains dominant; graph expansion should clarify context rather than replace retrieval.

Product and catalog expansion:

- Store product copy, manuals, specifications, return comments, keyword research, and competitor notes as records.
- Model products, attributes, use cases, search terms, constraints, and evidence sources as graph nodes.
- Use grounded edges such as `mentions`, `supports`, `derivedFrom`, `conflictsWith`, or `requiresReview` so provider-backed `ai.extract` runs can produce draft copy with review notes and evidence references.
- Keep review/risk state in assertions and reviews rather than treating generated copy as authoritative.

For all three shapes, start with a narrow traversal profile, inspect the graph with `/collections/{collection}/graph/inspect`, review traversal `diagnostics`, and only then expand depth, edge limits, or context budgets.
