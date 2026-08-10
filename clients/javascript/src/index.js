export class VyralClientError extends Error {
  constructor(status, body, options = {}) {
    const problem = options.problem ?? parseProblemBody(body);
    const failureClass = options.failureClass;
    super(buildErrorMessage(status, body, problem));
    this.name = "VyralClientError";
    this.status = status;
    this.body = body;
    this.problem = problem;
    this.admission = problem?.admission && typeof problem.admission === "object" && !Array.isArray(problem.admission)
      ? problem.admission
      : null;
    this.failureClass = failureClass ?? stringProblemValue(this.admission, "failureClass");
    this.type = stringProblemValue(problem, "type");
    this.title = stringProblemValue(problem, "title");
    this.detail = stringProblemValue(problem, "detail");
    this.instance = stringProblemValue(problem, "instance");
    this.problemStatus = problemStatus(problem);
    this.retryAfter = options.retryAfter ?? null;
    this.correlationId = options.correlationId ?? null;
  }

  static timeout(detail) {
    return new VyralClientError(0, "", {
      problem: { title: "Request timeout", detail, status: 0 },
      failureClass: "timeout"
    });
  }

  static cancelled(detail = "Request was cancelled") {
    return new VyralClientError(0, "", {
      problem: { title: "Request cancelled", detail, status: 0 },
      failureClass: "cancelled"
    });
  }

  isMissingCollection() {
    const text = `${this.title ?? ""} ${this.detail ?? this.body}`.toLowerCase();
    return this.status === 404 &&
      text.includes("collection") &&
      (text.includes("not found") || text.includes("does not exist"));
  }

  isAuthError() {
    return this.status === 401 || this.status === 403;
  }

  isValidationError() {
    return this.status === 400 || this.status === 422;
  }

  isTimeout() {
    return this.failureClass === "timeout" || this.status === 408 || this.status === 504;
  }

  isCancelled() {
    return this.failureClass === "cancelled";
  }

  isTransient() {
    return this.failureClass === "timeout" || this.failureClass === "transport" ||
      [408, 429, 502, 503, 504].includes(this.status);
  }
}

const TERMINAL_PROVIDER_JOB_STATUSES = new Set([
  "Succeeded",
  "Failed",
  "TimedOut",
  "Rejected",
  "Unsupported",
  "NotConfigured",
  "Cancelled",
  "succeeded",
  "failed",
  "timedOut",
  "rejected",
  "unsupported",
  "notConfigured",
  "cancelled"
]);

export const TERMINAL_EXECUTION_RUN_STATUSES = Object.freeze([
  "succeeded",
  "failed",
  "cancelled",
  "rejected",
  "timed_out"
]);

const TERMINAL_EXECUTION_RUN_STATUS_SET = new Set(TERMINAL_EXECUTION_RUN_STATUSES);

export function isProviderRunSucceeded(result) {
  return !!result && String(result.status ?? "").toLowerCase() === "succeeded";
}

export function getProviderRunRejection(result) {
  if (!result || typeof result !== "object" || Array.isArray(result)) return null;
  if (result.rejection && typeof result.rejection === "object" && !Array.isArray(result.rejection)) {
    return result.rejection;
  }
  const output = result.output;
  if (output && typeof output === "object" && !Array.isArray(output) &&
      output.rejection && typeof output.rejection === "object" && !Array.isArray(output.rejection)) {
    return output.rejection;
  }
  return null;
}

export function isProviderRunOutputUsable(result) {
  if (!isProviderRunSucceeded(result)) return false;
  const rejection = getProviderRunRejection(result);
  return !rejection || !!rejection.contentUsable;
}

export function isExecutionRunTerminal(runOrStatus) {
  const status = typeof runOrStatus === "string" ? runOrStatus : runOrStatus?.status;
  return typeof status === "string" && TERMINAL_EXECUTION_RUN_STATUS_SET.has(status);
}

const TERMINAL_RETRIEVAL_EVALUATION_JOB_STATUSES = new Set([
  "succeeded",
  "failed",
  "cancelled",
  "rejected"
]);

const TERMINAL_EMBEDDING_JOB_STATUSES = new Set([
  "succeeded",
  "failed",
  "cancelled",
  "rejected"
]);

const TERMINAL_RECORD_IMPORT_JOB_STATUSES = new Set([
  "succeeded",
  "failed",
  "cancelled",
  "rejected"
]);

const TERMINAL_RAG_INGESTION_JOB_STATUSES = new Set([
  "succeeded",
  "failed",
  "cancelled",
  "rejected"
]);

const TERMINAL_GRAPH_JOB_STATUSES = new Set([
  "succeeded",
  "failed",
  "cancelled",
  "rejected"
]);

const DEFAULT_RAG_INDEXED_METADATA = [
  "/metadata/documentId",
  "/metadata/topic",
  "/metadata/status",
  "/type"
];

const DEFAULT_VERIFIED_RETRIEVAL_FIELDS = [
  "/content/text",
  "/metadata/referenceId",
  "/metadata/page",
  "/metadata/title",
  "/metadata/source",
  "/id"
];

const DEFAULT_VERIFIED_RETRIEVAL_FIELD_BOOSTS = {
  "/metadata/referenceId": 3.0,
  "/id": 1.5,
  "/metadata/page": 1.25,
  "/metadata/title": 1.15
};

const DEFAULT_RAG_RETRIEVAL_FIELDS = [
  "/content/text",
  "/metadata/title",
  "/metadata/source",
  "/id"
];

export const RETRIEVAL_PROFILES = Object.freeze({
  evidence: "evidence",
  ragBaseline: "ragBaseline",
  rerankPolish: "rerankPolish",
  deepQuality: "deepQuality",
  discovery: "discovery",
  productOptimization: "productOptimization"
});

export const EVIDENCE_BRIEF_SCHEMA = "vyral.evidence-brief.v1";
export const EVIDENCE_BRIEF_DOCUMENT_TYPE = "vyral.evidence-brief";
export const EVIDENCE_BRIEF_CHANGED_EVENT_TOPIC = "vyral.evidence-brief.changed";

export function buildRagCollectionPolicy(collection, options = {}) {
  const embeddingField = options.embeddingField ?? "contentEmbedding";
  const dimensions = normalizePositiveInteger(options.dimensions, "options.dimensions");
  if (!collection) throw new Error("collection is required");
  if (!embeddingField) throw new Error("options.embeddingField is required");

  return {
    name: collection,
    partitionKeyPath: options.partitionKeyPath ?? "/partitionKey",
    indexedMetadata: [...(options.indexedMetadata ?? DEFAULT_RAG_INDEXED_METADATA)],
    vectorPolicies: [
      {
        name: embeddingField,
        path: `/vectors/${embeddingField}/values`,
        dimensions,
        datatype: options.datatype ?? "float32",
        distanceFunction: options.distanceFunction ?? "cosine",
        indexType: options.indexType ?? "flat"
      }
    ]
  };
}

export function buildEvidenceBriefTransaction(tenantId, idempotencyKey, brief, options = {}) {
  if (!tenantId || !String(tenantId).trim()) throw new Error("tenantId is required");
  if (!idempotencyKey || !String(idempotencyKey).trim()) throw new Error("idempotencyKey is required");
  if (!brief || typeof brief !== "object" || Array.isArray(brief)) throw new Error("brief is required");

  const payload = { ...brief, schema: brief.schema ?? EVIDENCE_BRIEF_SCHEMA };
  if (payload.schema !== EVIDENCE_BRIEF_SCHEMA) {
    throw new Error(`brief.schema must be ${EVIDENCE_BRIEF_SCHEMA}`);
  }
  for (const field of ["id", "question", "asOfUtc", "factAnchors", "sourceSnapshots", "citations", "counterEvidence", "uncertainties", "retrievalTraces"]) {
    if (!(field in payload)) throw new Error(`brief.${field} is required`);
  }
  if (!payload.id || !String(payload.id).trim()) throw new Error("brief.id is required");
  if (options.expectedRevision !== undefined && (!Number.isInteger(options.expectedRevision) || options.expectedRevision < 0)) {
    throw new Error("options.expectedRevision must be a non-negative integer");
  }
  const emitChangeEvent = options.emitChangeEvent ?? true;
  const changeEventTopic = options.changeEventTopic ?? EVIDENCE_BRIEF_CHANGED_EVENT_TOPIC;
  if (emitChangeEvent && !String(changeEventTopic).trim()) throw new Error("options.changeEventTopic is required when emitting an event");

  const mutation = {
    operation: "upsert",
    document: {
      tenantId,
      documentType: EVIDENCE_BRIEF_DOCUMENT_TYPE,
      id: payload.id,
      schemaVersion: EVIDENCE_BRIEF_SCHEMA,
      data: payload,
      indexes: { schema: EVIDENCE_BRIEF_SCHEMA, asOfUtc: payload.asOfUtc }
    }
  };
  if (options.expectedRevision !== undefined) mutation.precondition = { expectedRevision: options.expectedRevision };

  const transaction = { tenantId, idempotencyKey, mutations: [mutation] };
  assignDefined(transaction, { correlationId: options.correlationId, actor: options.actor });
  if (emitChangeEvent) {
    transaction.outbox = [{
      topic: changeEventTopic,
      key: payload.id,
      payload: { briefId: payload.id, schema: EVIDENCE_BRIEF_SCHEMA, asOfUtc: payload.asOfUtc }
    }];
  }
  return transaction;
}

export function buildVerifiedRetrievalRequest(query, collections, options = {}) {
  if (!query || !String(query).trim()) throw new Error("query is required");
  if (!Array.isArray(collections) || collections.length === 0) {
    throw new Error("collections is required");
  }

  const request = {
    query,
    collections: [...collections],
    searchMode: "lexical",
    lexical: {
      fields: [...(options.fields ?? DEFAULT_VERIFIED_RETRIEVAL_FIELDS)],
      fieldBoosts: {
        ...DEFAULT_VERIFIED_RETRIEVAL_FIELD_BOOSTS,
        ...(options.fieldBoosts ?? {})
      },
      scanLimit: options.scanLimit ?? 5000,
      scoring: options.scoring ?? "bm25",
      prefixMatching: options.prefixMatching ?? true,
      prefixMinChars: options.prefixMinChars ?? 3
    },
    limit: normalizePositiveInteger(options.limit ?? 8, "options.limit"),
    includeTrace: options.includeTrace ?? true
  };

  if (options.partitionKeys !== undefined) request.partitionKeys = [...options.partitionKeys];
  if (options.filter !== undefined) request.filter = options.filter;
  if (options.minScore !== undefined) request.minScore = options.minScore;
  if (options.rerank !== undefined) request.rerank = options.rerank;

  return request;
}

export function buildRetrievalProfileRequest(profile, query, collections, options = {}) {
  if (!profile || !String(profile).trim()) throw new Error("profile is required");
  if (!query || !String(query).trim()) throw new Error("query is required");
  if (!Array.isArray(collections) || collections.length === 0) {
    throw new Error("collections is required");
  }

  const request = {
    profile,
    query,
    collections: [...collections]
  };

  assignDefined(request, {
    searchMode: options.searchMode,
    embedding: options.embedding,
    vectorFields: options.vectorFields === undefined ? undefined : [...options.vectorFields],
    lexical: options.lexical,
    hybrid: options.hybrid,
    rerank: options.rerank,
    limit: options.limit === undefined ? undefined : normalizePositiveInteger(options.limit, "options.limit"),
    minScore: options.minScore,
    includeTrace: options.includeTrace
  });
  if (options.partitionKeys !== undefined) request.partitionKeys = [...options.partitionKeys];
  if (options.filter !== undefined) request.filter = options.filter;

  return request;
}

export function buildRetrievalEvaluationExpectedMatch(idOrMatch, options = {}) {
  const match = normalizeEvaluationMatch(idOrMatch, "expected match");
  assignDefined(match, {
    partitionKey: options.partitionKey,
    collection: options.collection,
    aliases: options.aliases === undefined ? undefined : [...options.aliases],
    sourceIds: options.sourceIds === undefined ? undefined : [...options.sourceIds],
    sources: options.sources === undefined ? undefined : [...options.sources],
    relevance: options.relevance
  });
  return match;
}

export function buildRetrievalEvaluationHardNegative(idOrMatch, options = {}) {
  const match = normalizeEvaluationMatch(idOrMatch, "hard negative");
  assignDefined(match, {
    partitionKey: options.partitionKey,
    collection: options.collection,
    aliases: options.aliases === undefined ? undefined : [...options.aliases],
    sourceIds: options.sourceIds === undefined ? undefined : [...options.sourceIds],
    sources: options.sources === undefined ? undefined : [...options.sources],
    reason: options.reason
  });
  return match;
}

export function buildRetrievalEvaluationCase(name, request, expected, options = {}) {
  if (!request || typeof request !== "object") throw new Error("request is required");
  const expectedMatches = normalizeEvaluationMatches(expected, buildRetrievalEvaluationExpectedMatch);
  if (expectedMatches.length === 0) throw new Error("expected must contain at least one match");
  const evaluationCase = {
    request,
    expected: expectedMatches,
    hardNegatives: normalizeEvaluationMatches(options.hardNegatives ?? [], buildRetrievalEvaluationHardNegative)
  };
  assignDefined(evaluationCase, {
    name,
    k: options.k === undefined ? undefined : normalizePositiveInteger(options.k, "options.k"),
    metadata: options.metadata
  });
  return evaluationCase;
}

export function buildRetrievalEvaluationRequest(cases, options = {}) {
  if (!Array.isArray(cases) || cases.length === 0) throw new Error("cases is required");
  const request = {
    cases: [...cases],
    continueOnError: options.continueOnError ?? true,
    includeTopResults: options.includeTopResults ?? true
  };
  if (options.defaultK !== undefined) request.defaultK = normalizePositiveInteger(options.defaultK, "options.defaultK");
  return request;
}

export function buildRetrievalEvaluationVariant(id, options = {}) {
  if (!id || !String(id).trim()) throw new Error("id is required");
  const variant = { id };
  assignDefined(variant, {
    label: options.label,
    profile: options.profile,
    collections: options.collections === undefined ? undefined : [...options.collections],
    partitionKeys: options.partitionKeys === undefined ? undefined : [...options.partitionKeys],
    filter: options.filter,
    embedding: options.embedding,
    vectorFields: options.vectorFields === undefined ? undefined : [...options.vectorFields],
    searchMode: options.searchMode,
    lexical: options.lexical,
    hybrid: options.hybrid,
    rerank: options.rerank,
    limit: options.limit === undefined ? undefined : normalizePositiveInteger(options.limit, "options.limit"),
    minScore: options.minScore,
    includeTrace: options.includeTrace
  });
  return variant;
}

export function buildRetrievalEvaluationComparisonRequest(cases, variants, options = {}) {
  if (!Array.isArray(cases) || cases.length === 0) throw new Error("cases is required");
  if (!Array.isArray(variants) || variants.length === 0) throw new Error("variants is required");
  const request = {
    cases: [...cases],
    variants: [...variants],
    continueOnError: options.continueOnError ?? true,
    includeTopResults: options.includeTopResults ?? false,
    includeCaseResults: options.includeCaseResults ?? false
  };
  if (options.defaultK !== undefined) request.defaultK = normalizePositiveInteger(options.defaultK, "options.defaultK");
  return request;
}

export function buildRagTextIngestionRequest(text, partitionKey, options = {}) {
  if (!text || !String(text).trim()) throw new Error("text is required");
  if (!partitionKey || !String(partitionKey).trim()) throw new Error("partitionKey is required");

  const request = {
    partitionKey,
    text
  };
  assignDefined(request, {
    documentId: options.documentId,
    idPrefix: options.idPrefix,
    type: options.type,
    schemaVersion: options.schemaVersion,
    contentField: options.contentField,
    embedding: options.embedding,
    metadata: options.metadata,
    sourceUri: options.sourceUri,
    sourceKind: options.sourceKind,
    sourceId: options.sourceId,
    sourceLabel: options.sourceLabel,
    sources: options.sources === undefined ? undefined : [...options.sources]
  });

  const ingestionOptions = {
    ...(options.options ?? {})
  };
  assignDefined(ingestionOptions, {
    chunkChars: options.chunkChars,
    chunkOverlapChars: options.chunkOverlapChars,
    dryRun: options.dryRun,
    replaceDocumentChunks: options.replaceDocumentChunks,
    skipUnchangedChunks: options.skipUnchangedChunks,
    reuseExistingChunkVectors: options.reuseExistingChunkVectors,
    deduplicateExistingChunks: options.deduplicateExistingChunks,
    persistManifest: options.persistManifest,
    includeTrace: options.includeTrace
  });
  if (Object.keys(ingestionOptions).length > 0) request.options = ingestionOptions;

  return request;
}

export function buildRagContextRequest(query, collections, options = {}) {
  if (!query || !String(query).trim()) throw new Error("query is required");
  if (!Array.isArray(collections) || collections.length === 0) throw new Error("collections is required");

  const searchMode = options.searchMode ?? (options.profile === undefined ? "lexical" : undefined);
  const lexical = {
    ...(options.lexical ?? {})
  };
  if (lexical.fields === undefined) lexical.fields = [...(options.fields ?? DEFAULT_RAG_RETRIEVAL_FIELDS)];
  if (options.fieldBoosts !== undefined) lexical.fieldBoosts = { ...options.fieldBoosts };

  const retrieval = {
    query,
    collections: [...collections],
    limit: normalizePositiveInteger(options.limit ?? 8, "options.limit"),
    includeTrace: options.includeTrace ?? true
  };
  if (options.profile !== undefined) retrieval.profile = options.profile;
  if (searchMode !== undefined) retrieval.searchMode = searchMode;
  if (searchMode === "lexical" || options.lexical !== undefined || options.fields !== undefined || options.fieldBoosts !== undefined) {
    retrieval.lexical = lexical;
  }
  assignDefined(retrieval, {
    partitionKeys: options.partitionKeys === undefined ? undefined : [...options.partitionKeys],
    filter: options.filter,
    embedding: options.embedding,
    vectorFields: options.vectorFields === undefined ? undefined : [...options.vectorFields],
    hybrid: options.hybrid,
    minScore: options.minScore,
    rerank: options.rerank
  });

  const request = {
    retrieval,
    contentField: options.contentField ?? "text",
    maxChars: normalizePositiveInteger(options.maxChars ?? 4000, "options.maxChars"),
    maxCharsPerChunk: normalizePositiveInteger(options.maxCharsPerChunk ?? 1200, "options.maxCharsPerChunk"),
    includeRecords: options.includeRecords ?? false,
    includeCitations: options.includeCitations ?? true,
    includeContextText: options.includeContextText ?? true,
    includeTrace: options.includeTrace ?? true
  };
  if (options.maxCitationsPerChunk !== undefined) {
    request.maxCitationsPerChunk = normalizePositiveInteger(options.maxCitationsPerChunk, "options.maxCitationsPerChunk");
  }
  if (options.contextAssembly !== undefined) request.contextAssembly = options.contextAssembly;
  if (options.graphExpansion !== undefined) request.graphExpansion = options.graphExpansion;

  return request;
}

export function buildGraphCollectionImportRequest(envelope, options = {}) {
  if (!envelope || typeof envelope !== "object" || Array.isArray(envelope)) {
    throw new Error("envelope is required");
  }

  return {
    envelope,
    createCollectionIfMissing: options.createCollectionIfMissing ?? true,
    replaceExisting: options.replaceExisting ?? false,
    continueOnError: options.continueOnError ?? false,
    allowNonGraphPolicy: options.allowNonGraphPolicy ?? false
  };
}

export function buildGraphCollectionExportRequest(options = {}) {
  const request = {
    includeProjections: options.includeProjections ?? true,
    failOnLimitExceeded: options.failOnLimitExceeded ?? true
  };
  assignDefined(request, {
    graphId: options.graphId,
    namespace: options.namespace,
    tenantId: options.tenantId,
    partitionKey: options.partitionKey,
    maxRecords: options.maxRecords === undefined ? undefined : normalizePositiveInteger(options.maxRecords, "options.maxRecords")
  });
  return request;
}

export function buildGraphTraversalRequest(startNodeIds, options = {}) {
  if (!Array.isArray(startNodeIds) || startNodeIds.length === 0) {
    throw new Error("startNodeIds is required");
  }

  const request = {
    startNodeIds: [...startNodeIds],
    profile: { ...(options.profile ?? {}) },
    allowPartialGraph: options.allowPartialGraph ?? false
  };
  assignDefined(request, {
    graphId: options.graphId,
    namespace: options.namespace,
    tenantId: options.tenantId,
    partitionKey: options.partitionKey,
    maxRecords: options.maxRecords === undefined ? undefined : normalizePositiveInteger(options.maxRecords, "options.maxRecords")
  });
  return request;
}

export function buildGraphInspectionRequest(options = {}) {
  const anomalyLimit = options.anomalyLimit ?? 50;
  if (!Number.isInteger(anomalyLimit) || anomalyLimit < 0) {
    throw new Error("options.anomalyLimit must be a non-negative integer");
  }

  const request = {
    allowPartialGraph: options.allowPartialGraph ?? false,
    includeAnomalies: options.includeAnomalies ?? true,
    anomalyLimit
  };
  assignDefined(request, {
    graphId: options.graphId,
    namespace: options.namespace,
    tenantId: options.tenantId,
    partitionKey: options.partitionKey,
    maxRecords: options.maxRecords === undefined ? undefined : normalizePositiveInteger(options.maxRecords, "options.maxRecords")
  });
  return request;
}

export function buildGraphDoctorRequest(options = {}) {
  const anomalyLimit = options.anomalyLimit ?? 50;
  if (!Number.isInteger(anomalyLimit) || anomalyLimit < 0) {
    throw new Error("options.anomalyLimit must be a non-negative integer");
  }

  const request = {
    targetPartitionKeys: [...(options.targetPartitionKeys ?? [])],
    maxTargetRecords: normalizePositiveInteger(options.maxTargetRecords ?? 1000, "options.maxTargetRecords"),
    allowPartialGraph: options.allowPartialGraph ?? false,
    includeAnomalies: options.includeAnomalies ?? true,
    anomalyLimit
  };
  assignDefined(request, {
    graphId: options.graphId,
    namespace: options.namespace,
    tenantId: options.tenantId,
    partitionKey: options.partitionKey,
    targetCollection: options.targetCollection,
    seedJsonPointers: options.seedJsonPointers === undefined ? undefined : [...options.seedJsonPointers],
    maxGraphRecords: options.maxGraphRecords === undefined ? undefined : normalizePositiveInteger(options.maxGraphRecords, "options.maxGraphRecords")
  });
  return request;
}

export function buildGraphExpansionOptions(collection, options = {}) {
  if (!collection || !String(collection).trim()) throw new Error("collection is required");
  const expansion = {
    enabled: options.enabled ?? true,
    collection,
    seedNodeIds: [...(options.seedNodeIds ?? [])],
    maxSeedNodes: normalizePositiveInteger(options.maxSeedNodes ?? 16, "options.maxSeedNodes"),
    profile: { ...(options.profile ?? {}) },
    allowPartialGraph: options.allowPartialGraph ?? false,
    includeGraphContextText: options.includeGraphContextText ?? true,
    maxGraphContextChars: normalizePositiveInteger(options.maxGraphContextChars ?? 1200, "options.maxGraphContextChars"),
    includeGraphProvenance: options.includeGraphProvenance ?? true,
    maxGraphProvenanceItems: normalizeNonNegativeInteger(options.maxGraphProvenanceItems ?? 64, "options.maxGraphProvenanceItems"),
    fallbackOnFailure: options.fallbackOnFailure ?? true
  };
  assignDefined(expansion, {
    graphId: options.graphId,
    namespace: options.namespace,
    tenantId: options.tenantId,
    partitionKey: options.partitionKey,
    seedJsonPointers: options.seedJsonPointers === undefined ? undefined : [...options.seedJsonPointers],
    maxRecords: options.maxRecords === undefined ? undefined : normalizePositiveInteger(options.maxRecords, "options.maxRecords")
  });
  return expansion;
}

export function buildGraphScope(graphId, options = {}) {
  if (!graphId || !String(graphId).trim()) throw new Error("graphId is required");
  return {
    graphId,
    namespace: options.namespace ?? "default",
    collection: options.collection ?? "default",
    tenantId: options.tenantId ?? "",
    partitionKey: options.partitionKey ?? ""
  };
}

export function buildGraphSourceSpan(sourceRef, options = {}) {
  if (!sourceRef || !String(sourceRef).trim()) throw new Error("sourceRef is required");
  const span = {
    sourceRef,
    unit: options.unit ?? "utf16"
  };
  assignDefined(span, {
    charStart: options.charStart,
    charEnd: options.charEnd,
    locator: options.locator,
    textHash: options.textHash,
    metadata: options.metadata
  });
  return span;
}

export function buildGraphNode(id, type, options = {}) {
  if (!id || !String(id).trim()) throw new Error("id is required");
  if (!type || !String(type).trim()) throw new Error("type is required");
  const node = {
    id,
    type,
    sourceSpans: [...(options.sourceSpans ?? [])],
    assertionIds: [...(options.assertionIds ?? [])]
  };
  assignDefined(node, {
    label: options.label,
    properties: options.properties === undefined ? undefined : { ...options.properties }
  });
  return node;
}

export function buildGraphEdge(id, sourceId, targetId, predicate, options = {}) {
  if (!id || !String(id).trim()) throw new Error("id is required");
  if (!sourceId || !String(sourceId).trim()) throw new Error("sourceId is required");
  if (!targetId || !String(targetId).trim()) throw new Error("targetId is required");
  if (!predicate || !String(predicate).trim()) throw new Error("predicate is required");
  const edge = {
    id,
    sourceId,
    targetId,
    predicate,
    sourceSpans: [...(options.sourceSpans ?? [])],
    assertionIds: [...(options.assertionIds ?? [])]
  };
  assignDefined(edge, {
    label: options.label,
    properties: options.properties === undefined ? undefined : { ...options.properties }
  });
  return edge;
}

export function buildGraphAssertion(id, subjectId, options = {}) {
  if (!id || !String(id).trim()) throw new Error("id is required");
  if (!subjectId || !String(subjectId).trim()) throw new Error("subjectId is required");
  const assertion = {
    id,
    subjectId,
    subjectKind: options.subjectKind ?? "node",
    status: options.status ?? "proposed",
    method: options.method ?? "unspecified",
    actor: options.actor ?? "system",
    sourceSpans: [...(options.sourceSpans ?? [])]
  };
  assignDefined(assertion, {
    confidence: options.confidence,
    properties: options.properties === undefined ? undefined : { ...options.properties }
  });
  return assertion;
}

export function buildGraphReview(id, subjectId, status, reviewer, options = {}) {
  if (!id || !String(id).trim()) throw new Error("id is required");
  if (!subjectId || !String(subjectId).trim()) throw new Error("subjectId is required");
  if (!status || !String(status).trim()) throw new Error("status is required");
  if (!reviewer || !String(reviewer).trim()) throw new Error("reviewer is required");
  const review = {
    id,
    subjectId,
    subjectKind: options.subjectKind ?? "assertion",
    status,
    reviewer
  };
  assignDefined(review, {
    notes: options.notes,
    properties: options.properties === undefined ? undefined : { ...options.properties }
  });
  return review;
}

export function buildGraphEnvelope(scope, options = {}) {
  if (!scope || typeof scope !== "object" || Array.isArray(scope)) throw new Error("scope is required");
  const envelope = {
    schema: options.schema ?? "roman.graph.v1",
    scope: { ...scope },
    nodes: [...(options.nodes ?? [])],
    edges: [...(options.edges ?? [])],
    assertions: [...(options.assertions ?? [])],
    reviews: [...(options.reviews ?? [])],
    projections: [...(options.projections ?? [])]
  };
  assignDefined(envelope, {
    metadata: options.metadata === undefined ? undefined : { ...options.metadata }
  });
  return envelope;
}

export function stampGraphNodeMetadata(record, graphNodeId, options = {}) {
  if (!record || typeof record !== "object" || Array.isArray(record)) throw new Error("record is required");
  if (!graphNodeId || !String(graphNodeId).trim()) throw new Error("graphNodeId is required");
  const stamped = {
    ...record,
    metadata: {
      ...(record.metadata ?? {}),
      graphNodeId
    }
  };
  if (options.graphNodeIds !== undefined) {
    stamped.metadata.graphNodeIds = [...options.graphNodeIds];
  }
  return stamped;
}

export function buildRerankOptions(options = {}) {
  const rerank = {
    enabled: options.enabled ?? true,
    mode: options.mode ?? "advisory",
    maxCandidateChars: normalizePositiveInteger(options.maxCandidateChars ?? 1000, "options.maxCandidateChars"),
    contentField: options.contentField ?? "text",
    fallbackOnFailure: options.fallbackOnFailure ?? true
  };
  assignDefined(rerank, {
    provider: options.provider,
    candidateLimit: options.candidateLimit ?? 8,
    rerankScoreWeight: options.rerankScoreWeight,
    originalScoreWeight: options.originalScoreWeight,
    timeoutSeconds: options.timeoutSeconds,
    maxOutputBytes: options.maxOutputBytes
  });
  return rerank;
}

export function buildProviderRunRequest(capability, payload, options = {}) {
  if (!capability || !String(capability).trim()) throw new Error("capability is required");
  if (payload === undefined || payload === null) throw new Error("payload is required");

  const request = {
    capability,
    operation: options.operation ?? "run",
    mode: options.mode ?? "advisory",
    payload: { ...payload }
  };
  assignDefined(request, {
    provider: options.provider,
    modelId: options.modelId,
    correlationId: options.correlationId,
    contextRefs: options.contextRefs === undefined ? undefined : [...options.contextRefs],
    timeoutSeconds: options.timeoutSeconds,
    maxOutputBytes: options.maxOutputBytes,
    artifactDirectory: options.artifactDirectory
  });
  return request;
}

export function buildProviderChatRequest(messages, options = {}) {
  if (!Array.isArray(messages) || messages.length === 0) throw new Error("messages is required");
  const payload = {
    messages: [...messages]
  };
  assignDefined(payload, {
    system: options.system,
    maxOutputChars: options.maxOutputChars
  });
  return buildProviderRunRequest("ai.chat", payload, options);
}

export function buildProviderExtractRequest(text, options = {}) {
  if (!text || !String(text).trim()) throw new Error("text is required");
  const payload = { text };
  assignDefined(payload, {
    schema: options.schema,
    instructions: options.instructions
  });
  return buildProviderRunRequest("ai.extract", payload, options);
}

export function buildProviderRerankRequest(query, candidates, options = {}) {
  if (!query || !String(query).trim()) throw new Error("query is required");
  if (!Array.isArray(candidates) || candidates.length === 0) throw new Error("candidates is required");
  const payload = {
    query,
    candidates: candidates.map((candidate) => ({ ...candidate }))
  };
  assignDefined(payload, {
    limit: options.limit
  });
  return buildProviderRunRequest("ai.rerank", payload, options);
}

export function buildProviderReviewRequest(options = {}) {
  const hasPrompt = options.prompt && String(options.prompt).trim();
  const hasSubject = options.subject && String(options.subject).trim();
  const hasInstructions = options.instructions && String(options.instructions).trim();
  const hasReferences = Array.isArray(options.references) && options.references.length > 0;
  if (!hasPrompt && !hasSubject && !hasInstructions && !hasReferences) {
    throw new Error("prompt, subject, instructions, or references is required");
  }

  const payload = {};
  assignDefined(payload, {
    prompt: options.prompt,
    subject: options.subject,
    instructions: options.instructions,
    references: options.references === undefined ? undefined : options.references.map((reference) => ({ ...reference })),
    maxFindings: options.maxFindings
  });
  return buildProviderRunRequest("ai.review", payload, options);
}

export function buildProviderScaffoldRequest(prompt, options = {}) {
  if (!prompt || !String(prompt).trim()) throw new Error("prompt is required");
  const payload = { prompt };
  assignDefined(payload, {
    instructions: options.instructions,
    target: options.target,
    references: options.references === undefined ? undefined : options.references.map((reference) => ({ ...reference })),
    allowedPaths: options.allowedPaths === undefined ? undefined : [...options.allowedPaths],
    maxArtifacts: options.maxArtifacts
  });
  return buildProviderRunRequest("ai.scaffold", payload, options);
}

export function buildProviderToolPlanRequest(prompt, options = {}) {
  if (!prompt || !String(prompt).trim()) throw new Error("prompt is required");
  const payload = {
    prompt,
    tools: Array.isArray(options.tools) ? options.tools.map((tool) => ({ ...tool })) : []
  };
  return buildProviderRunRequest("ai.toolPlan", payload, options);
}

export function summarizeRagIngestResult(result = {}) {
  const summary = result.actionSummary;
  if (summary && typeof summary === "object" && !Array.isArray(summary)) {
    return {
      actionCounts: { ...(summary.actionCounts ?? {}) },
      embeddingActionCounts: { ...(summary.embeddingActionCounts ?? {}) },
      createdIds: [...(summary.createdIds ?? [])],
      updatedIds: [...(summary.updatedIds ?? [])],
      reusedIds: [...(summary.reusedIds ?? [])],
      deduplicatedIds: [...(summary.deduplicatedIds ?? [])],
      staleDeleteIds: [...(summary.staleDeleteIds ?? [])]
    };
  }

  const chunks = Array.isArray(result.chunks) ? result.chunks : [];
  const staleDeletes = Array.isArray(result.staleDeletes) ? result.staleDeletes : [];
  const actionCounts = { created: 0, updated: 0, reused: 0, deduplicated: 0 };
  const embeddingActionCounts = { generated: 0, reused: 0, unchanged: 0, deduplicated: 0 };
  const createdIds = [];
  const updatedIds = [];
  const reusedIds = [];
  const deduplicatedIds = [];

  for (const chunk of chunks) {
    const action = chunk?.action ?? "";
    const embeddingAction = chunk?.embeddingAction ?? "";
    if (action) actionCounts[action] = (actionCounts[action] ?? 0) + 1;
    if (embeddingAction) embeddingActionCounts[embeddingAction] = (embeddingActionCounts[embeddingAction] ?? 0) + 1;
    if (action === "created") createdIds.push(chunk.id);
    else if (action === "updated") updatedIds.push(chunk.id);
    else if (action === "reused") reusedIds.push(chunk.id);
    else if (action === "deduplicated") deduplicatedIds.push(chunk.id);
  }

  return {
    actionCounts,
    embeddingActionCounts,
    createdIds,
    updatedIds,
    reusedIds,
    deduplicatedIds,
    staleDeleteIds: staleDeletes
      .map((stale) => stale?.id)
      .filter((id) => id !== undefined && id !== null)
      .map(String)
      .sort()
  };
}

export function compareRagIngestResults(planned = {}, committed = {}) {
  return {
    planHash: compareHash("plan", planned.planHash, committed.planHash),
    manifestHash: compareHash("manifest", planned.manifestHash, committed.manifestHash),
    plannedSummary: summarizeRagIngestResult(planned),
    committedSummary: summarizeRagIngestResult(committed)
  };
}

function normalizeBaseUrl(baseUrl) {
  if (typeof baseUrl !== "string") {
    throw new Error("baseUrl must be an absolute HTTP(S) URL without user credentials");
  }
  let parsed;
  try {
    parsed = new URL(baseUrl);
  } catch {
    throw new Error("baseUrl must be an absolute HTTP(S) URL without user credentials");
  }
  if (!["http:", "https:"].includes(parsed.protocol) || parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error("baseUrl must be an absolute HTTP(S) URL without user credentials");
  }
  return baseUrl.replace(/\/+$/, "");
}

function isLoopbackBaseUrl(baseUrl) {
  const hostname = new URL(baseUrl).hostname.toLowerCase();
  return hostname === "localhost" || hostname === "[::1]" || hostname === "::1" || /^127(?:\.[0-9]{1,3}){3}$/u.test(hostname);
}

function requireSecureCredentialTransport(baseUrl, headers) {
  if (
    (headers.has("Authorization") || headers.has("X-Vyral-Api-Key")) &&
    new URL(baseUrl).protocol !== "https:" &&
    !isLoopbackBaseUrl(baseUrl)
  ) {
    throw new Error("Vyral credentials require HTTPS except on loopback");
  }
}

export class VyralClient {
  constructor(baseUrl = "http://localhost:5220", options = {}) {
    this.baseUrl = normalizeBaseUrl(baseUrl);
    this.fetch = options.fetch ?? globalThis.fetch;
    this.apiKey = options.apiKey;
    this.bearerToken = options.bearerToken;
    this.defaultHeaders = new Headers(options.headers);
    this.signal = options.signal;
    this.timeoutMs = options.timeoutMs ?? 30_000;
    this.correlationId = options.correlationId;
    this.maxRetries = options.maxRetries ?? 0;
    this.retryBackoffMs = options.retryBackoffMs ?? 250;
    if (!this.fetch) {
      throw new Error("VyralClient requires fetch. Use Node 18+ or pass options.fetch.");
    }
    if (this.timeoutMs <= 0) throw new Error("options.timeoutMs must be greater than zero");
    if (!Number.isInteger(this.maxRetries) || this.maxRetries < 0) {
      throw new Error("options.maxRetries must be a non-negative integer");
    }
    if (this.retryBackoffMs < 0) throw new Error("options.retryBackoffMs must be non-negative");
    if (this.apiKey && this.bearerToken) throw new Error("options.apiKey and options.bearerToken are mutually exclusive");
    const configuredHeaders = new Headers(this.defaultHeaders);
    if (this.bearerToken && !configuredHeaders.has("Authorization")) {
      configuredHeaders.set("Authorization", `Bearer ${this.bearerToken}`);
    }
    if (this.apiKey && !configuredHeaders.has("Authorization") && !configuredHeaders.has("X-Vyral-Api-Key")) {
      configuredHeaders.set("X-Vyral-Api-Key", this.apiKey);
    }
    requireSecureCredentialTransport(this.baseUrl, configuredHeaders);
  }

  withOptions(options = {}) {
    const headers = new Headers(this.defaultHeaders);
    for (const [name, value] of new Headers(options.headers)) headers.set(name, value);
    return new VyralClient(this.baseUrl, {
      fetch: this.fetch,
      apiKey: this.apiKey,
      bearerToken: this.bearerToken,
      headers,
      signal: options.signal ?? this.signal,
      timeoutMs: options.timeoutMs ?? this.timeoutMs,
      correlationId: options.correlationId ?? this.correlationId,
      maxRetries: options.maxRetries ?? this.maxRetries,
      retryBackoffMs: options.retryBackoffMs ?? this.retryBackoffMs
    });
  }

  health() {
    return this.#json("GET", "/health");
  }

  readiness() {
    return this.#json("GET", "/readiness");
  }

  ingestRecordArtifact(manifest, artifact, options = {}) {
    if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) {
      throw new Error("manifest is required");
    }
    if (typeof FormData === "undefined" || typeof Blob === "undefined") {
      throw new Error("ingestRecordArtifact requires FormData and Blob support");
    }

    const fileName = options.fileName ?? "artifact.bin";
    if (!fileName || /[\r\n]/u.test(fileName)) {
      throw new Error("options.fileName must be non-empty and cannot contain line breaks");
    }
    const requestedContentType = options.contentType;
    if (requestedContentType !== undefined && (!requestedContentType || /[\r\n]/u.test(requestedContentType))) {
      throw new Error("options.contentType must be non-empty and cannot contain line breaks");
    }

    let artifactBlob;
    if (artifact instanceof Blob) {
      artifactBlob = requestedContentType && requestedContentType !== artifact.type
        ? artifact.slice(0, artifact.size, requestedContentType)
        : artifact;
    } else if (artifact instanceof ArrayBuffer || ArrayBuffer.isView(artifact)) {
      artifactBlob = new Blob([artifact], { type: requestedContentType ?? "application/octet-stream" });
    } else {
      throw new Error("artifact must be a Blob, ArrayBuffer, or typed array");
    }

    const form = new FormData();
    form.append("manifest", JSON.stringify(manifest));
    form.append("artifact", artifactBlob, fileName);
    const headers = new Headers(options.headers);
    if (options.idempotencyKey) headers.set("Idempotency-Key", options.idempotencyKey);
    const params = new URLSearchParams();
    if (options.productId !== undefined) params.set("productId", options.productId);
    if (options.tenantId !== undefined) params.set("tenantId", options.tenantId);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("POST", `/ingest/record-artifact${suffix}`, undefined, {
      body: form,
      headers,
      signal: options.signal
    });
  }

  openApiContract() {
    return this.#json("GET", "/openapi/vyral.json");
  }

  getPublicSchemaContract() {
    return this.#json("GET", "/contracts/schemas/vyral-public.schema.json");
  }

  listCanonicalMigrations() {
    return this.#json("GET", "/canonical/migrations");
  }

  getCanonicalPreflight() {
    return this.#json("GET", "/canonical/preflight");
  }

  probeCanonicalDataPlane() {
    return this.#json("POST", "/canonical/preflight/probe");
  }

  applyCanonicalMigrations(migrations) {
    return this.#json("POST", "/canonical/migrations", migrations);
  }

  commitCanonicalTransaction(tenantId, request) {
    return this.#json("POST", `/canonical/tenants/${encodeURIComponent(tenantId)}/transactions`, request);
  }

  storeEvidenceBrief(tenantId, brief, options = {}) {
    if (!options.idempotencyKey) throw new Error("options.idempotencyKey is required");
    return this.commitCanonicalTransaction(
      tenantId,
      buildEvidenceBriefTransaction(tenantId, options.idempotencyKey, brief, options)
    );
  }

  async getEvidenceBrief(tenantId, briefId, options = {}) {
    const document = await this.getCanonicalDocument(tenantId, EVIDENCE_BRIEF_DOCUMENT_TYPE, briefId, options);
    if (!document) return null;
    if (document.documentType !== EVIDENCE_BRIEF_DOCUMENT_TYPE || document.schemaVersion !== EVIDENCE_BRIEF_SCHEMA) {
      throw new Error("canonical document is not a supported EvidenceBrief");
    }
    const brief = document.data;
    if (!brief || typeof brief !== "object" || Array.isArray(brief) || brief.schema !== EVIDENCE_BRIEF_SCHEMA || brief.id !== briefId) {
      throw new Error("canonical EvidenceBrief data is invalid");
    }
    return { document, brief };
  }

  async getCanonicalDocument(tenantId, documentType, id, options = {}) {
    try {
      return await this.#json("POST", `/canonical/tenants/${encodeURIComponent(tenantId)}/documents/read`, {
        tenantId,
        documentType,
        id,
        includeDeleted: options.includeDeleted ?? false
      });
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  queryCanonicalDocuments(tenantId, query) {
    return this.#json("POST", `/canonical/tenants/${encodeURIComponent(tenantId)}/documents/query`, query);
  }

  async *iterateCanonicalDocuments(tenantId, query = {}, options = {}) {
    validatePaginationOptions(options);
    const request = { ...query };
    const seenTokens = new Set();
    let yielded = 0;
    for (let pageNumber = 0; pageNumber < (options.maxPages ?? 1000); pageNumber += 1) {
      const page = await this.queryCanonicalDocuments(tenantId, request);
      for (const item of page.items ?? []) {
        if (options.maxItems !== undefined && yielded >= options.maxItems) return;
        yield item;
        yielded += 1;
      }
      if (!page.continuationToken) return;
      if (seenTokens.has(page.continuationToken)) {
        throw new Error("Canonical document pagination returned a repeated continuation token");
      }
      seenTokens.add(page.continuationToken);
      request.continuationToken = page.continuationToken;
    }
    throw new Error(`Canonical document pagination exceeded maxPages=${options.maxPages ?? 1000}`);
  }

  async queryAllCanonicalDocuments(tenantId, query = {}, options = {}) {
    const items = [];
    for await (const item of this.iterateCanonicalDocuments(tenantId, query, options)) items.push(item);
    return items;
  }

  listCanonicalDocumentRevisions(tenantId, documentType, id, options = {}) {
    const request = { tenantId, documentType, id };
    if (options.limit !== undefined) request.limit = options.limit;
    return this.#json("POST", `/canonical/tenants/${encodeURIComponent(tenantId)}/documents/revisions`, request);
  }

  leaseCanonicalOutbox(tenantId, request) {
    return this.#json("POST", `/canonical/tenants/${encodeURIComponent(tenantId)}/outbox/leases`, request);
  }

  queryCanonicalOutbox(tenantId, query) {
    return this.#json("POST", `/canonical/tenants/${encodeURIComponent(tenantId)}/outbox/query`, query);
  }

  renewCanonicalOutboxLease(tenantId, eventId, request) {
    return this.#json(
      "POST",
      `/canonical/tenants/${encodeURIComponent(tenantId)}/outbox/${encodeURIComponent(eventId)}/renew`,
      request
    );
  }

  acknowledgeCanonicalOutbox(tenantId, eventId, leaseToken) {
    return this.#json(
      "POST",
      `/canonical/tenants/${encodeURIComponent(tenantId)}/outbox/${encodeURIComponent(eventId)}/ack`,
      { leaseToken }
    );
  }

  releaseCanonicalOutbox(tenantId, eventId, request) {
    return this.#json(
      "POST",
      `/canonical/tenants/${encodeURIComponent(tenantId)}/outbox/${encodeURIComponent(eventId)}/nack`,
      request
    );
  }

  replayCanonicalOutbox(tenantId, eventId, request) {
    return this.#json(
      "POST",
      `/canonical/tenants/${encodeURIComponent(tenantId)}/outbox/${encodeURIComponent(eventId)}/replay`,
      request
    );
  }

  exportCanonicalTenant(tenantId) {
    return this.#json("GET", `/canonical/tenants/${encodeURIComponent(tenantId)}/export`);
  }

  restoreCanonicalTenant(tenantId, snapshot, expectedContentHash) {
    const request = { snapshot };
    if (expectedContentHash !== undefined) request.expectedContentHash = expectedContentHash;
    return this.#json("POST", `/canonical/tenants/${encodeURIComponent(tenantId)}/restore`, request);
  }

  listGraphProviderShapes() {
    return this.#json("GET", "/graph/provider-shapes");
  }

  async getGraphProviderShape(providerId) {
    try {
      return await this.#json("GET", `/graph/provider-shapes/${encodeURIComponent(providerId)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  listEmbeddingProviders() {
    return this.#json("GET", "/embedding-providers");
  }

  listEmbeddingProviderGuidance() {
    return this.#json("GET", "/embedding-providers/guidance");
  }

  getEmbeddingProviderDoctor() {
    return this.#json("GET", "/embedding-providers/doctor");
  }

  getExecutionRuntime() {
    return this.#json("GET", "/execution/runtime");
  }

  getEffectiveExecutionRuntime(options = {}) {
    const params = new URLSearchParams();
    if (options.productId !== undefined) params.set("productId", options.productId);
    if (options.tenantId !== undefined) params.set("tenantId", options.tenantId);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/execution/runtime/effective${suffix}`);
  }

  getExecutionRuntimeMaintenance() {
    return this.#json("GET", "/execution/runtime/maintenance");
  }

  pruneExecutionRuntimeMaintenance(options = {}) {
    const request = {
      dryRun: options.dryRun ?? true
    };
    if (options.retainTerminalRuns !== undefined) request.retainTerminalRuns = options.retainTerminalRuns;
    return this.#json("POST", "/execution/runtime/maintenance/prune", request);
  }

  reconcileExecutionRuntimeDispatch(options = {}) {
    const request = {
      dryRun: options.dryRun ?? false
    };
    if (options.limit !== undefined) request.limit = options.limit;
    return this.#json("POST", "/execution/runtime/maintenance/reconcile", request);
  }

  leaseExternalExecutionRun(request) {
    return this.#json("POST", "/execution/workers/leases", request);
  }

  heartbeatExternalExecutionLease(request) {
    return this.#json("POST", "/execution/workers/leases/heartbeat", request);
  }

  reportExternalExecutionLease(request) {
    return this.#json("POST", "/execution/workers/leases/reports", request);
  }

  recordExternalExecutionLeaseEvent(request) {
    return this.#json("POST", "/execution/workers/leases/events", request);
  }

  putExternalExecutionLeaseArtifact(request) {
    return this.#json("POST", "/execution/workers/leases/artifacts", request);
  }

  putExternalExecutionLeaseCheckpoint(request) {
    return this.#json("POST", "/execution/workers/leases/checkpoints", request);
  }

  async getExternalExecutionLeaseCheckpoint(request) {
    try {
      return await this.#json("POST", "/execution/workers/leases/checkpoints/read", request);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  waitExternalExecutionLease(request) {
    return this.#json("POST", "/execution/workers/leases/wait", request);
  }

  completeExternalExecutionLease(request) {
    return this.#json("POST", "/execution/workers/leases/complete", request);
  }

  listExecutionRuns(options = {}) {
    const params = new URLSearchParams();
    if (options.handlerId !== undefined) params.set("handlerId", options.handlerId);
    if (options.pluginId !== undefined) params.set("pluginId", options.pluginId);
    if (options.status !== undefined) params.set("status", options.status);
    if (options.correlationId !== undefined) params.set("correlationId", options.correlationId);
    if (options.idempotencyKey !== undefined) params.set("idempotencyKey", options.idempotencyKey);
    if (options.createdAfterUtc !== undefined) params.set("createdAfterUtc", options.createdAfterUtc);
    if (options.createdBeforeUtc !== undefined) params.set("createdBeforeUtc", options.createdBeforeUtc);
    if (options.updatedAfterUtc !== undefined) params.set("updatedAfterUtc", options.updatedAfterUtc);
    if (options.updatedBeforeUtc !== undefined) params.set("updatedBeforeUtc", options.updatedBeforeUtc);
    for (const [key, value] of Object.entries(options.tags ?? {})) {
      params.set(`tag.${key}`, value);
    }
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    if (options.includeResult !== undefined) params.set("includeResult", String(options.includeResult));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/execution/runs${suffix}`);
  }

  startExecutionRun(request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", "/execution/runs", request, { headers });
  }

  raiseExecutionEvent(runId, request) {
    if (request?.runId !== undefined && request.runId !== "" && request.runId !== runId) {
      throw new Error("request.runId must match runId");
    }
    return this.#json("POST", `/execution/runs/${encodeURIComponent(runId)}/events`, request);
  }

  async getExecutionRun(runId, options = {}) {
    const params = new URLSearchParams();
    if (options.includeResult !== undefined) params.set("includeResult", String(options.includeResult));
    const suffix = params.size > 0 ? `?${params}` : "";
    try {
      return await this.#json("GET", `/execution/runs/${encodeURIComponent(runId)}${suffix}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async cancelExecutionRun(runId) {
    try {
      return await this.#json("DELETE", `/execution/runs/${encodeURIComponent(runId)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async waitExecutionRun(runId, options = {}) {
    const timeoutMs = options.timeoutMs ?? 120_000;
    const pollIntervalMs = options.pollIntervalMs ?? 1_000;
    const includeResult = options.includeResult ?? true;
    if (timeoutMs <= 0) throw new Error("timeoutMs must be greater than zero");
    if (pollIntervalMs < 0) throw new Error("pollIntervalMs must be non-negative");

    const deadline = Date.now() + timeoutMs;
    while (true) {
      const run = await this.getExecutionRun(runId, { includeResult });
      if (run === null || isExecutionRunTerminal(run)) return run;

      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new Error(`Execution run ${runId} did not complete within ${timeoutMs}ms`);
      }

      await delay(Math.min(pollIntervalMs, remaining));
    }
  }

  getExecutionRunHistory(runId, options = {}) {
    const params = new URLSearchParams();
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/execution/runs/${encodeURIComponent(runId)}/history${suffix}`);
  }

  listExecutionRunArtifacts(runId) {
    return this.#json("GET", `/execution/runs/${encodeURIComponent(runId)}/artifacts`);
  }

  async getExecutionRunArtifact(runId, artifactRef) {
    try {
      return await this.#json(
        "GET",
        `/execution/runs/${encodeURIComponent(runId)}/artifacts/${encodeURIComponent(artifactRef)}`
      );
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async getExecutionRunCheckpoint(runId, key) {
    try {
      return await this.#json(
        "GET",
        `/execution/runs/${encodeURIComponent(runId)}/checkpoints/${encodeURIComponent(key)}`
      );
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  listProviders() {
    return this.#json("GET", "/providers");
  }

  getProviderCapabilityMatrix() {
    return this.#json("GET", "/providers/capabilities");
  }

  async getProvider(provider) {
    try {
      return await this.#json("GET", `/providers/${encodeURIComponent(provider)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  listProviderDoctor() {
    return this.#json("GET", "/providers/doctor");
  }

  async getProviderDoctor(provider) {
    try {
      return await this.#json("GET", `/providers/${encodeURIComponent(provider)}/doctor`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  listProviderReadiness() {
    return this.#json("GET", "/providers/readiness");
  }

  async getProviderReadiness(provider) {
    try {
      return await this.#json("GET", `/providers/${encodeURIComponent(provider)}/readiness`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  listProviderQuotas() {
    return this.#json("GET", "/providers/quotas");
  }

  async getProviderQuota(provider) {
    try {
      return await this.#json("GET", `/providers/${encodeURIComponent(provider)}/quota`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  listProviderQualifications(provider) {
    return this.#json("GET", `/providers/${encodeURIComponent(provider)}/qualifications`);
  }

  async listProviderModels(provider) {
    try {
      return await this.#json("GET", `/providers/${encodeURIComponent(provider)}/models`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  qualifyProvider(provider, request = {}) {
    return this.#json("POST", `/providers/${encodeURIComponent(provider)}/qualify`, request);
  }

  runProvider(provider, request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", `/providers/${encodeURIComponent(provider)}/run`, request, { headers });
  }

  runProviderExtract(provider, text, options = {}) {
    return this.runProvider(provider, buildProviderExtractRequest(text, options), options);
  }

  startProviderJob(provider, request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", `/providers/${encodeURIComponent(provider)}/jobs`, request, { headers });
  }

  listProviderJobs(options = {}) {
    const params = new URLSearchParams();
    if (options.provider !== undefined) params.set("provider", options.provider);
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    if (options.includeResult !== undefined) params.set("includeResult", String(options.includeResult));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/provider-jobs${suffix}`);
  }

  async getProviderJob(id) {
    try {
      return await this.#json("GET", `/provider-jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async cancelProviderJob(id) {
    try {
      return await this.#json("DELETE", `/provider-jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async waitProviderJob(id, options = {}) {
    const timeoutMs = options.timeoutMs ?? 120_000;
    const pollIntervalMs = options.pollIntervalMs ?? 1_000;
    if (timeoutMs <= 0) throw new Error("timeoutMs must be greater than zero");
    if (pollIntervalMs < 0) throw new Error("pollIntervalMs must be non-negative");

    const deadline = Date.now() + timeoutMs;
    while (true) {
      const job = await this.getProviderJob(id);
      if (job === null || TERMINAL_PROVIDER_JOB_STATUSES.has(job.status)) return job;

      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new Error(`Provider job ${id} did not complete within ${timeoutMs}ms`);
      }

      await delay(Math.min(pollIntervalMs, remaining));
    }
  }

  async embedText(text, options = {}) {
    const response = await this.embedTexts([text], options);
    return response.items[0].values;
  }

  embedTexts(texts, options = {}) {
    const request = { texts };
    if (options.purpose !== undefined) request.purpose = options.purpose;
    if (options.queryPrefix !== undefined) request.queryPrefix = options.queryPrefix;
    if (options.passagePrefix !== undefined) request.passagePrefix = options.passagePrefix;
    if (options.symmetricPrefix !== undefined) request.symmetricPrefix = options.symmetricPrefix;
    return this.#json("POST", "/embeddings", request);
  }

  startEmbeddingJob(request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", "/embeddings/jobs", request, { headers });
  }

  listEmbeddingJobs(options = {}) {
    const params = new URLSearchParams();
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    if (options.includeResult !== undefined) params.set("includeResult", String(options.includeResult));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/embeddings/jobs${suffix}`);
  }

  async getEmbeddingJob(id) {
    try {
      return await this.#json("GET", `/embeddings/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async cancelEmbeddingJob(id) {
    try {
      return await this.#json("DELETE", `/embeddings/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async waitEmbeddingJob(id, options = {}) {
    const timeoutMs = options.timeoutMs ?? 120_000;
    const pollIntervalMs = options.pollIntervalMs ?? 1_000;
    if (timeoutMs <= 0) throw new Error("timeoutMs must be greater than zero");
    if (pollIntervalMs < 0) throw new Error("pollIntervalMs must be non-negative");

    const deadline = Date.now() + timeoutMs;
    while (true) {
      const job = await this.getEmbeddingJob(id);
      if (job === null || TERMINAL_EMBEDDING_JOB_STATUSES.has(job.status)) return job;

      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new Error(`Embedding job ${id} did not complete within ${timeoutMs}ms`);
      }

      await delay(Math.min(pollIntervalMs, remaining));
    }
  }

  listCollections() {
    return this.#json("GET", "/collections");
  }

  createCollection(policy, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    const params = new URLSearchParams();
    if (options.productId !== undefined) params.set("productId", options.productId);
    if (options.tenantId !== undefined) params.set("tenantId", options.tenantId);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("POST", `/collections${suffix}`, policy, { headers });
  }

  async createRagCollection(collection, options = {}) {
    const health = options.dimensions === undefined ? await this.health() : null;
    const dimensions = options.dimensions ?? health?.embedding?.dimensions;
    return this.createCollection(
      buildRagCollectionPolicy(collection, { ...options, dimensions }),
      options
    );
  }

  async getCollectionPolicy(collection) {
    try {
      return await this.#json("GET", `/collections/${encodeURIComponent(collection)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async exportCollection(collection, options = {}) {
    try {
      if (
        options.query !== undefined ||
        options.maxRecords !== undefined ||
        options.failOnLimitExceeded === false
      ) {
        const request = {
          failOnLimitExceeded: options.failOnLimitExceeded ?? true
        };
        if (options.query !== undefined) request.query = options.query;
        if (options.maxRecords !== undefined) request.maxRecords = options.maxRecords;
        return await this.#json("POST", `/collections/${encodeURIComponent(collection)}/export`, request);
      }
      return await this.#json("GET", `/collections/${encodeURIComponent(collection)}/export`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  importCollection(collection, snapshot, options = {}) {
    const request = {
      snapshot,
      replaceExisting: options.replaceExisting ?? false,
      continueOnError: options.continueOnError ?? false,
      allowCollectionRename: options.allowCollectionRename ?? false,
      allowPartialSnapshot: options.allowPartialSnapshot ?? false
    };
    if (options.expectedContentHash !== undefined) request.expectedContentHash = options.expectedContentHash;
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    const params = new URLSearchParams();
    if (options.productId !== undefined) params.set("productId", options.productId);
    if (options.tenantId !== undefined) params.set("tenantId", options.tenantId);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("POST", `/collections/${encodeURIComponent(collection)}/import${suffix}`, request, { headers });
  }

  startCollectionImportJob(collection, snapshot, options = {}) {
    const request = {
      snapshot,
      replaceExisting: options.replaceExisting ?? false,
      continueOnError: options.continueOnError ?? false,
      allowCollectionRename: options.allowCollectionRename ?? false,
      allowPartialSnapshot: options.allowPartialSnapshot ?? false
    };
    if (options.expectedContentHash !== undefined) request.expectedContentHash = options.expectedContentHash;
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", `/collections/${encodeURIComponent(collection)}/import/jobs`, request, { headers });
  }

  importGraphEnvelope(collection, envelope, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json(
      "POST",
      `/collections/${encodeURIComponent(collection)}/graph/import`,
      buildGraphCollectionImportRequest(envelope, options),
      { headers }
    );
  }

  preflightGraphImport(collection, envelope, options = {}) {
    return this.#json(
      "POST",
      `/collections/${encodeURIComponent(collection)}/graph/import/preflight`,
      buildGraphCollectionImportRequest(envelope, options)
    );
  }

  async exportGraphEnvelope(collection, options = {}) {
    try {
      return await this.#json(
        "POST",
        `/collections/${encodeURIComponent(collection)}/graph/export`,
        buildGraphCollectionExportRequest(options)
      );
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async traverseGraph(collection, startNodeIds, options = {}) {
    try {
      return await this.#json(
        "POST",
        `/collections/${encodeURIComponent(collection)}/graph/traverse`,
        buildGraphTraversalRequest(startNodeIds, options)
      );
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async inspectGraph(collection, options = {}) {
    try {
      return await this.#json(
        "POST",
        `/collections/${encodeURIComponent(collection)}/graph/inspect`,
        buildGraphInspectionRequest(options)
      );
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async doctorGraph(collection, options = {}) {
    try {
      return await this.#json(
        "POST",
        `/collections/${encodeURIComponent(collection)}/graph/doctor`,
        buildGraphDoctorRequest(options)
      );
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  startGraphImportJob(collection, request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json(
      "POST",
      `/collections/${encodeURIComponent(collection)}/graph/import/jobs`,
      request,
      { headers }
    );
  }

  startGraphInspectionJob(collection, request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json(
      "POST",
      `/collections/${encodeURIComponent(collection)}/graph/inspect/jobs`,
      request,
      { headers }
    );
  }

  startGraphDoctorJob(collection, request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json(
      "POST",
      `/collections/${encodeURIComponent(collection)}/graph/doctor/jobs`,
      request,
      { headers }
    );
  }

  listGraphJobs(options = {}) {
    const params = new URLSearchParams();
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    if (options.includeResult !== undefined) params.set("includeResult", String(options.includeResult));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/graph/jobs${suffix}`);
  }

  async getGraphJob(id) {
    try {
      return await this.#json("GET", `/graph/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async cancelGraphJob(id) {
    try {
      return await this.#json("DELETE", `/graph/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async waitGraphJob(id, options = {}) {
    const timeoutMs = options.timeoutMs ?? 120_000;
    const pollIntervalMs = options.pollIntervalMs ?? 1_000;
    if (timeoutMs <= 0) throw new Error("timeoutMs must be greater than zero");
    if (pollIntervalMs < 0) throw new Error("pollIntervalMs must be non-negative");

    const deadline = Date.now() + timeoutMs;
    while (true) {
      const job = await this.getGraphJob(id);
      if (job === null || TERMINAL_GRAPH_JOB_STATUSES.has(job.status)) return job;

      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new Error(`Graph job ${id} did not complete within ${timeoutMs}ms`);
      }

      await delay(Math.min(pollIntervalMs, remaining));
    }
  }

  inspectCollection(collection, options = {}) {
    const params = new URLSearchParams();
    if (options.includeAnomalies !== undefined) params.set("includeAnomalies", String(options.includeAnomalies));
    if (options.anomalyLimit !== undefined) params.set("anomalyLimit", String(options.anomalyLimit));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/collections/${encodeURIComponent(collection)}/inspect${suffix}`);
  }

  deleteCollection(collection, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    const params = new URLSearchParams();
    if (options.productId !== undefined) params.set("productId", options.productId);
    if (options.tenantId !== undefined) params.set("tenantId", options.tenantId);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("DELETE", `/collections/${encodeURIComponent(collection)}${suffix}`, undefined, { headers });
  }

  upsertRecord(collection, record) {
    return this.#json("POST", `/collections/${encodeURIComponent(collection)}/records`, record);
  }

  upsertRecords(collection, records, options = {}) {
    const request = {
      records,
      continueOnError: options.continueOnError ?? false
    };
    if (options.preconditions !== undefined) request.preconditions = options.preconditions;
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    const params = new URLSearchParams();
    if (options.productId !== undefined) params.set("productId", options.productId);
    if (options.tenantId !== undefined) params.set("tenantId", options.tenantId);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("POST", `/collections/${encodeURIComponent(collection)}/records/batch${suffix}`, request, { headers });
  }

  startRecordBatchUpsertJob(collection, records, options = {}) {
    const request = {
      records,
      continueOnError: options.continueOnError ?? false
    };
    if (options.preconditions !== undefined) request.preconditions = options.preconditions;
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    const params = new URLSearchParams();
    if (options.productId !== undefined) params.set("productId", options.productId);
    if (options.tenantId !== undefined) params.set("tenantId", options.tenantId);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json(
      "POST",
      `/collections/${encodeURIComponent(collection)}/records/batch/jobs${suffix}`,
      request,
      { headers }
    );
  }

  listRecordImportJobs(options = {}) {
    const params = new URLSearchParams();
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    if (options.includeResult !== undefined) params.set("includeResult", String(options.includeResult));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/record-import/jobs${suffix}`);
  }

  async getRecordImportJob(id) {
    try {
      return await this.#json("GET", `/record-import/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async cancelRecordImportJob(id) {
    try {
      return await this.#json("DELETE", `/record-import/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async waitRecordImportJob(id, options = {}) {
    const timeoutMs = options.timeoutMs ?? 120_000;
    const pollIntervalMs = options.pollIntervalMs ?? 1_000;
    if (timeoutMs <= 0) throw new Error("timeoutMs must be greater than zero");
    if (pollIntervalMs < 0) throw new Error("pollIntervalMs must be non-negative");

    const deadline = Date.now() + timeoutMs;
    while (true) {
      const job = await this.getRecordImportJob(id);
      if (job === null || TERMINAL_RECORD_IMPORT_JOB_STATUSES.has(job.status)) return job;

      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new Error(`Record import job ${id} did not complete within ${timeoutMs}ms`);
      }

      await delay(Math.min(pollIntervalMs, remaining));
    }
  }

  async getRecord(collection, partitionKey, id) {
    const path = `/collections/${encodeURIComponent(collection)}/records/${encodeURIComponent(partitionKey)}/${encodeURIComponent(id)}`;
    try {
      return await this.#json("GET", path);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async deleteRecord(collection, partitionKey, id) {
    const path = `/collections/${encodeURIComponent(collection)}/records/${encodeURIComponent(partitionKey)}/${encodeURIComponent(id)}`;
    const response = await this.#fetch("DELETE", path);
    await assertOk(response);
  }

  queryRecords(collection, query) {
    return this.#json("POST", `/collections/${encodeURIComponent(collection)}/query`, query);
  }

  async *iterateRecords(collection, query = {}, options = {}) {
    validatePaginationOptions(options);
    const request = { ...query };
    const seenTokens = new Set();
    let yielded = 0;
    for (let pageNumber = 0; pageNumber < (options.maxPages ?? 1000); pageNumber += 1) {
      const page = await this.queryRecords(collection, request);
      for (const item of page.items ?? []) {
        if (options.maxItems !== undefined && yielded >= options.maxItems) return;
        yield item;
        yielded += 1;
      }
      if (!page.continuationToken) return;
      if (seenTokens.has(page.continuationToken)) {
        throw new Error("Record pagination returned a repeated continuation token");
      }
      seenTokens.add(page.continuationToken);
      request.continuationToken = page.continuationToken;
    }
    throw new Error(`Record pagination exceeded maxPages=${options.maxPages ?? 1000}`);
  }

  async queryAllRecords(collection, query = {}, options = {}) {
    const items = [];
    for await (const item of this.iterateRecords(collection, query, options)) items.push(item);
    return items;
  }

  searchRecords(collection, query) {
    return this.#json("POST", `/collections/${encodeURIComponent(collection)}/search`, query);
  }

  async *iterateSearchRecords(collection, query = {}, options = {}) {
    validatePaginationOptions(options);
    const request = { ...query };
    const seenTokens = new Set();
    let yielded = 0;
    for (let pageNumber = 0; pageNumber < (options.maxPages ?? 1000); pageNumber += 1) {
      const page = await this.searchRecords(collection, request);
      for (const item of page.items ?? []) {
        if (options.maxItems !== undefined && yielded >= options.maxItems) return;
        yield item;
        yielded += 1;
      }
      if (!page.continuationToken) return;
      if (seenTokens.has(page.continuationToken)) {
        throw new Error("Search pagination returned a repeated continuation token");
      }
      seenTokens.add(page.continuationToken);
      request.continuationToken = page.continuationToken;
    }
    throw new Error(`Search pagination exceeded maxPages=${options.maxPages ?? 1000}`);
  }

  async searchAllRecords(collection, query = {}, options = {}) {
    const items = [];
    for await (const item of this.iterateSearchRecords(collection, query, options)) items.push(item);
    return items;
  }

  retrieve(request) {
    return this.#json("POST", "/search", request);
  }

  listRetrievalProfiles() {
    return this.#json("GET", "/retrieval/profiles");
  }

  evaluateRetrieval(request) {
    return this.#json("POST", "/retrieval/evaluate", request);
  }

  compareRetrievalEvaluations(request) {
    return this.#json("POST", "/retrieval/evaluate/compare", request);
  }

  startRetrievalEvaluationJob(request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", "/retrieval/evaluate/jobs", request, { headers });
  }

  startRetrievalEvaluationComparisonJob(request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", "/retrieval/evaluate/compare/jobs", request, { headers });
  }

  listRetrievalEvaluationJobs(options = {}) {
    const params = new URLSearchParams();
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    if (options.includeResult !== undefined) params.set("includeResult", String(options.includeResult));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/retrieval/evaluate/jobs${suffix}`);
  }

  async getRetrievalEvaluationJob(id) {
    try {
      return await this.#json("GET", `/retrieval/evaluate/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async cancelRetrievalEvaluationJob(id) {
    try {
      return await this.#json("DELETE", `/retrieval/evaluate/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async waitRetrievalEvaluationJob(id, options = {}) {
    const timeoutMs = options.timeoutMs ?? 120_000;
    const pollIntervalMs = options.pollIntervalMs ?? 1_000;
    if (timeoutMs <= 0) throw new Error("timeoutMs must be greater than zero");
    if (pollIntervalMs < 0) throw new Error("pollIntervalMs must be non-negative");

    const deadline = Date.now() + timeoutMs;
    while (true) {
      const job = await this.getRetrievalEvaluationJob(id);
      if (job === null || TERMINAL_RETRIEVAL_EVALUATION_JOB_STATUSES.has(job.status)) return job;

      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new Error(`Retrieval evaluation job ${id} did not complete within ${timeoutMs}ms`);
      }

      await delay(Math.min(pollIntervalMs, remaining));
    }
  }

  buildRagContext(request) {
    return this.#json("POST", "/rag/context", request);
  }

  evaluateRagContext(request) {
    return this.#json("POST", "/rag/context/evaluate", request);
  }

  buildRagPrompt(request) {
    return this.#json("POST", "/rag/prompt", request);
  }

  ingestRagText(collection, request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", `/collections/${encodeURIComponent(collection)}/rag/ingest-text`, request, { headers });
  }

  planRagTextIngestion(collection, request) {
    return this.ingestRagText(collection, withRagIngestionOptions(request, { dryRun: true }));
  }

  commitRagTextIngestion(collection, request, plannedResult = null, options = {}) {
    const payload = withRagIngestionOptions(request, { dryRun: false });
    const ingestionOptions = { ...(payload.options ?? {}) };
    if (plannedResult?.planHash !== undefined && ingestionOptions.expectedPlanHash === undefined) {
      ingestionOptions.expectedPlanHash = plannedResult.planHash;
    }
    if (plannedResult?.manifestHash !== undefined && ingestionOptions.expectedManifestHash === undefined) {
      ingestionOptions.expectedManifestHash = plannedResult.manifestHash;
    }
    payload.options = ingestionOptions;
    return this.ingestRagText(collection, payload, options);
  }

  ingestRagTexts(collection, items, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json("POST", `/collections/${encodeURIComponent(collection)}/rag/ingest-text/batch`, {
      items,
      continueOnError: options.continueOnError ?? false
    }, { headers });
  }

  startRagTextIngestionJob(collection, request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json(
      "POST",
      `/collections/${encodeURIComponent(collection)}/rag/ingest-text/jobs`,
      request,
      { headers }
    );
  }

  startRagTextBatchIngestionJob(collection, request, options = {}) {
    const headers = options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : undefined;
    return this.#json(
      "POST",
      `/collections/${encodeURIComponent(collection)}/rag/ingest-text/batch/jobs`,
      request,
      { headers }
    );
  }

  listRagIngestionJobs(options = {}) {
    const params = new URLSearchParams();
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    if (options.includeResult !== undefined) params.set("includeResult", String(options.includeResult));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/rag/ingestion/jobs${suffix}`);
  }

  async getRagIngestionJob(id) {
    try {
      return await this.#json("GET", `/rag/ingestion/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async cancelRagIngestionJob(id) {
    try {
      return await this.#json("DELETE", `/rag/ingestion/jobs/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async waitRagIngestionJob(id, options = {}) {
    const timeoutMs = options.timeoutMs ?? 120_000;
    const pollIntervalMs = options.pollIntervalMs ?? 1_000;
    if (timeoutMs <= 0) throw new Error("timeoutMs must be greater than zero");
    if (pollIntervalMs < 0) throw new Error("pollIntervalMs must be non-negative");

    const deadline = Date.now() + timeoutMs;
    while (true) {
      const job = await this.getRagIngestionJob(id);
      if (job === null || TERMINAL_RAG_INGESTION_JOB_STATUSES.has(job.status)) return job;

      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new Error(`RAG ingestion job ${id} did not complete within ${timeoutMs}ms`);
      }

      await delay(Math.min(pollIntervalMs, remaining));
    }
  }

  listObjects(container, options = {}) {
    const params = new URLSearchParams();
    if (options.prefix !== undefined) params.set("prefix", options.prefix);
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    if (options.continuationToken !== undefined) params.set("continuationToken", options.continuationToken);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/objects/${encodeURIComponent(container)}${suffix}`);
  }

  putObject(container, key, content, options = {}) {
    const headers = new Headers();
    if (options.contentType) headers.set("Content-Type", options.contentType);
    if (options.ifMatch) headers.set("If-Match", options.ifMatch);
    if (options.ifNoneMatch) headers.set("If-None-Match", options.ifNoneMatch);
    for (const [metadataKey, metadataValue] of Object.entries(options.metadata ?? {})) {
      headers.set(`X-Vyral-Meta-${metadataKey}`, metadataValue);
    }

    return this.#json("PUT", `/objects/${encodeURIComponent(container)}/${encodePath(key)}`, undefined, {
      body: content,
      headers
    });
  }

  async getObject(container, key) {
    const response = await this.#fetch("GET", `/objects/${encodeURIComponent(container)}/${encodePath(key)}`);
    if (response.status === 404) return null;
    await assertOk(response);
    return response.arrayBuffer();
  }

  async deleteObject(container, key, options = {}) {
    const headers = new Headers();
    if (options.ifMatch) headers.set("If-Match", options.ifMatch);
    const response = await this.#fetch("DELETE", `/objects/${encodeURIComponent(container)}/${encodePath(key)}`, { headers });
    await assertOk(response);
  }

  listTraces(options = {}) {
    const params = new URLSearchParams();
    if (options.operation !== undefined) params.set("operation", options.operation);
    if (options.limit !== undefined) params.set("limit", String(options.limit));
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/traces${suffix}`);
  }

  pruneTraces(request) {
    return this.#json("POST", "/traces/prune", request);
  }

  summarizeTraces(options = {}) {
    const params = new URLSearchParams();
    if (options.operation !== undefined) params.set("operation", options.operation);
    const suffix = params.size > 0 ? `?${params}` : "";
    return this.#json("GET", `/traces/summary${suffix}`);
  }

  exportTraces(request = {}) {
    return this.#json("POST", "/traces/export", request);
  }

  async getTrace(id) {
    try {
      return await this.#json("GET", `/traces/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof VyralClientError && error.status === 404) return null;
      throw error;
    }
  }

  async #json(method, path, payload, init = {}) {
    const headers = new Headers(init.headers);
    let body = init.body;
    if (payload !== undefined) {
      body = JSON.stringify(payload);
      headers.set("Content-Type", "application/json");
    }

    const response = await this.#fetch(method, path, { ...init, headers, body });
    await assertOk(response);
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }

  async #fetch(method, path, init = {}) {
    const headers = new Headers(this.defaultHeaders);
    for (const [name, value] of new Headers(init.headers)) headers.set(name, value);
    if (this.bearerToken && !headers.has("Authorization")) {
      headers.set("Authorization", `Bearer ${this.bearerToken}`);
    }
    if (this.apiKey && !headers.has("X-Vyral-Api-Key") && !headers.has("Authorization")) {
      headers.set("X-Vyral-Api-Key", this.apiKey);
    }
    if (this.correlationId && !headers.has("X-Correlation-ID")) {
      headers.set("X-Correlation-ID", this.correlationId);
    }
    requireSecureCredentialTransport(this.baseUrl, headers);

    const normalizedMethod = method.toUpperCase();
    const carriesCredentials = headers.has("Authorization") || headers.has("X-Vyral-Api-Key");
    const canRetry = ["GET", "HEAD", "OPTIONS"].includes(normalizedMethod) ||
      headers.has("Idempotency-Key") || headers.has("X-Idempotency-Key");
    const callerSignal = init.signal ?? this.signal;

    for (let attempt = 0; attempt <= this.maxRetries; attempt += 1) {
      if (callerSignal?.aborted) throw VyralClientError.cancelled();
      const requestSignal = createRequestSignal(callerSignal, this.timeoutMs);
      try {
        const requestInit = {
          ...init,
          headers,
          method: normalizedMethod,
          signal: requestSignal.signal
        };
        if (carriesCredentials) requestInit.redirect = "manual";
        const response = await this.fetch(`${this.baseUrl}${path}`, requestInit);
        requestSignal.cleanup();
        if (canRetry && attempt < this.maxRetries && [408, 429, 502, 503, 504].includes(response.status)) {
          await response.body?.cancel();
          await delay(retryDelayMs(response.headers.get("Retry-After"), this.retryBackoffMs * (2 ** attempt)));
          continue;
        }
        return response;
      } catch (error) {
        const timedOut = requestSignal.timedOut();
        requestSignal.cleanup();
        if (callerSignal?.aborted) throw VyralClientError.cancelled(error?.message);
        if (canRetry && attempt < this.maxRetries) {
          await delay(this.retryBackoffMs * (2 ** attempt));
          continue;
        }
        if (timedOut || isTimeoutError(error)) {
          throw VyralClientError.timeout(error?.message || `Request timed out after ${this.timeoutMs}ms`);
        }
        throw new VyralClientError(0, String(error?.message ?? error), {
          problem: {
            title: "Transport failure",
            detail: String(error?.message ?? error),
            status: 0
          },
          failureClass: "transport"
        });
      }
    }

    throw new Error("request retry loop exhausted");
  }
}

function encodePath(path) {
  return String(path)
    .split("/")
    .map((segment) => encodeURIComponent(segment))
    .join("/");
}

function normalizePositiveInteger(value, name) {
  if (!Number.isInteger(value) || value <= 0) {
    throw new Error(`${name} must be a positive integer`);
  }
  return value;
}

function normalizeNonNegativeInteger(value, name) {
  if (!Number.isInteger(value) || value < 0) {
    throw new Error(`${name} must be a non-negative integer`);
  }
  return value;
}

function validatePaginationOptions(options) {
  if (options.maxPages !== undefined && (!Number.isInteger(options.maxPages) || options.maxPages <= 0)) {
    throw new Error("options.maxPages must be a positive integer");
  }
  if (options.maxItems !== undefined && (!Number.isInteger(options.maxItems) || options.maxItems <= 0)) {
    throw new Error("options.maxItems must be a positive integer");
  }
}

function assignDefined(target, values) {
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined && value !== null) target[key] = value;
  }
  return target;
}

function normalizeEvaluationMatch(idOrMatch, label) {
  if (typeof idOrMatch === "string") {
    if (!idOrMatch.trim()) throw new Error(`${label} id is required`);
    return { id: idOrMatch };
  }
  if (idOrMatch && typeof idOrMatch === "object") {
    const match = { ...idOrMatch };
    if (idOrMatch.aliases !== undefined) match.aliases = [...idOrMatch.aliases];
    if (idOrMatch.sourceIds !== undefined) match.sourceIds = [...idOrMatch.sourceIds];
    if (idOrMatch.sources !== undefined) match.sources = [...idOrMatch.sources];
    return match;
  }
  throw new Error(`${label} is required`);
}

function normalizeEvaluationMatches(matches, builder) {
  if (matches === undefined || matches === null) return [];
  const list = Array.isArray(matches) ? matches : [matches];
  return list.map((match) => builder(match));
}

function withRagIngestionOptions(request, updates) {
  const payload = { ...request };
  payload.options = {
    ...(request.options ?? {}),
    ...Object.fromEntries(Object.entries(updates).filter(([, value]) => value !== undefined && value !== null))
  };
  return payload;
}

function compareHash(kind, expected, actual) {
  const expectedHash = typeof expected === "string" && expected ? expected : null;
  const actualHash = typeof actual === "string" && actual ? actual : null;
  if (expectedHash === null) {
    return {
      kind,
      expectedHash: null,
      actualHash,
      compared: false,
      matches: false,
      status: "not_provided"
    };
  }

  const matches = expectedHash === actualHash;
  return {
    kind,
    expectedHash,
    actualHash,
    compared: true,
    matches,
    status: actualHash === null ? "actual_missing" : matches ? "matched" : "drifted"
  };
}

async function assertOk(response) {
  if (response.ok) return;
  throw new VyralClientError(response.status, await response.text(), {
    retryAfter: response.headers.get("Retry-After"),
    correlationId: response.headers.get("X-Correlation-ID") ?? response.headers.get("X-Request-ID")
  });
}

function retryDelayMs(retryAfter, fallbackMs) {
  if (retryAfter) {
    const seconds = Number(retryAfter);
    if (Number.isFinite(seconds)) return Math.max(0, seconds * 1000);
    const date = Date.parse(retryAfter);
    if (Number.isFinite(date)) return Math.max(0, date - Date.now());
  }
  return fallbackMs;
}

function createRequestSignal(callerSignal, timeoutMs) {
  const controller = new AbortController();
  let didTimeout = false;
  const onAbort = () => controller.abort(callerSignal?.reason);
  callerSignal?.addEventListener("abort", onAbort, { once: true });
  const timer = setTimeout(() => {
    didTimeout = true;
    controller.abort(new DOMException("Request timed out", "TimeoutError"));
  }, timeoutMs);
  return {
    signal: controller.signal,
    timedOut: () => didTimeout,
    cleanup: () => {
      clearTimeout(timer);
      callerSignal?.removeEventListener("abort", onAbort);
    }
  };
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function parseProblemBody(body) {
  if (!body) return null;
  try {
    const parsed = JSON.parse(body);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null;
    const problemKeys = ["type", "title", "status", "detail", "instance"];
    const hasAdmission = parsed.admission && typeof parsed.admission === "object" && !Array.isArray(parsed.admission);
    return problemKeys.some((key) => Object.prototype.hasOwnProperty.call(parsed, key)) || hasAdmission ? parsed : null;
  } catch {
    return null;
  }
}

function stringProblemValue(problem, key) {
  const value = problem?.[key];
  return typeof value === "string" && value ? value : null;
}

function problemStatus(problem) {
  const value = problem?.status;
  if (Number.isInteger(value)) return value;
  if (typeof value === "string" && /^\d+$/.test(value)) return Number(value);
  return null;
}

function buildErrorMessage(status, body, problem) {
  if (status === 0) {
    const title = stringProblemValue(problem, "title") ?? "Request failed";
    const detail = stringProblemValue(problem, "detail") ?? body ?? "request failed before an HTTP response was received";
    return `VYRAL request failed before receiving HTTP response: ${title}: ${detail}`;
  }

  const title = stringProblemValue(problem, "title");
  const detail = stringProblemValue(problem, "detail");
  if (title && detail) return `VYRAL request failed with HTTP ${status}: ${title}: ${detail}`;
  if (title) return `VYRAL request failed with HTTP ${status}: ${title}`;
  return `VYRAL request failed with HTTP ${status}: ${body}`;
}

function isTimeoutError(error) {
  return (typeof DOMException !== "undefined" && error instanceof DOMException && error.name === "TimeoutError") ||
    error?.name === "TimeoutError" ||
    error?.name === "AbortError" ||
    error?.code === "ETIMEDOUT";
}
