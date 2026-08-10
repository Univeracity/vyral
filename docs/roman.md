# ROMAN graph envelopes

ROMAN—Relational Object Manager—is the portable graph-envelope convention used
by Vyral. It represents domain objects and the relationships among them while
keeping evidence, review state, and task-specific views explicit. The current
wire schema is `roman.graph.v1`.

ROMAN is a contract, not a requirement to run a separate graph service. Vyral
can map an envelope into ordinary records and use its existing storage,
retrieval, execution, and portability boundaries.

## Model

A ROMAN envelope contains six principal shapes:

| Shape | Purpose |
| --- | --- |
| Scope | Names the tenant, corpus, project, or other boundary represented by the envelope |
| Node | Identifies an object such as a document, claim, component, person, task, or product |
| Edge | Relates two nodes with a typed predicate such as `supports`, `dependsOn`, or `cites` |
| Assertion | Records the provenance-bearing claim that a node or edge should exist |
| Review event | Appends a human or system judgment without rewriting prior review history |
| Projection | Captures a bounded, task-specific view over selected nodes and edges |

Nodes, edges, and assertions can carry source spans. A span points back to the
record, object, URI, page, or character range that grounds the graph element.
An edge is therefore a navigable relationship, not evidence by itself.

The normative JSON shapes live in
[`contracts/schemas/vyral-public.schema.json`](../contracts/schemas/vyral-public.schema.json).
The .NET, Python, and JavaScript surfaces use the same camel-case wire model and
cross-runtime golden fixtures.

## How Vyral uses ROMAN

1. A producer builds a `roman.graph.v1` envelope with stable identifiers,
   typed relationships, provenance, and any existing review state.
2. Graph-import preflight validates references and collection policy and
   reports what would change without mutating storage.
3. Durable import maps the envelope and each graph element to ordinary Vyral
   records. This preserves portability and lets normal export, snapshots, and
   provider adapters carry the graph.
4. Content records can declare a graph seed in metadata, conventionally
   `metadata.graphNodeId`.
5. A GraphRAG request retrieves source records first, resolves their seeds, and
   performs a bounded traversal over the graph collection.
6. Vyral returns the resulting projection with source spans, path explanations,
   limits, truncation reasons, and contribution diagnostics.

The retrieval result remains the evidence-bearing center of the response.
Graph expansion supplies bounded connective context and review state; it does
not turn proximity into proof.

## Operational surface

The REST contract includes:

- graph import preflight and durable import;
- envelope export;
- bounded traversal;
- collection inspection and graph doctor;
- RAG context and evaluation with graph expansion.

The Python and JavaScript clients include builders and typed operations for the
same surface. The Python peer runtime implements the same envelope mapping and
conformance fixtures in-process.

Start with narrow traversal depth and edge budgets. Require source grounding
for evidence-sensitive uses, inspect seed coverage before enabling GraphRAG,
and treat generated assertions as proposed until the application records an
appropriate review event. Working recipe shapes are in
[`examples/README.md`](../examples/README.md).
