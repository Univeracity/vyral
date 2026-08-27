const DESCRIPTOR_SCHEMA = "vyral.record-search-projection-generation.v1";
const REQUEST_SCHEMA = "vyral.record-search-projection-request.v1";
const RESULT_SCHEMA = "vyral.record-search-projection-result.v1";
const INSPECTION_SCHEMA = "vyral.record-search-projection-inspection.v1";
const CATALOG_SCHEMA = "vyral.private.worker-r2-catalog.v1";
const ACTIVE_SCHEMA = "vyral.private.worker-r2-active.v1";
const MANIFEST_SCHEMA = "vyral.private.worker-r2-manifest.v1";
const SHARD_SCHEMA = "vyral.private.worker-r2-shard.v1";
const CONTINUATION_SCHEMA = "vyral.private.worker-r2-continuation.v1";
const MAX_BODY_BYTES = 131_072;
const MAX_QUERY_BYTES = 2_048;
const MAX_FILTER_NODES = 64;
const MAX_FILTER_DEPTH = 8;
const MAX_CACHE_ENTRIES = 32;
const MAX_REQUEST_MILLISECONDS = 5_000;
const MAX_MANIFEST_BYTES = 1_048_576;
const MAX_CONTROL_OBJECT_BYTES = 1_048_576;
const MAX_IMMUTABLE_OBJECT_BYTES = 33_554_432;
const MAX_SELECTED_SHARD_BYTES = 33_554_432;
const CONTINUATION_LIFETIME_MILLISECONDS = 15 * 60 * 1_000;
const MAX_PAGE_COUNT = 100;
const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder("utf-8", { fatal: true });
const contentCache = new Map();

class ProjectionError extends Error {
  constructor(code, message, retryable = false) {
    super(message);
    this.code = code;
    this.retryable = retryable;
  }
}

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function requireExactKeys(value, keys, label) {
  if (!isObject(value)) throw new ProjectionError("invalidArtifact", `${label} must be an object.`);
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    throw new ProjectionError("invalidArtifact", `${label} has unsupported fields.`);
  }
}

function requireIdentifier(value, label) {
  if (typeof value !== "string" || value.length < 1 || value.length > 200 || value.trim() !== value || /[\0\r\n]/u.test(value)) {
    throw new ProjectionError("invalidArtifact", `${label} is not a bounded identifier.`);
  }
  return value;
}

function requireDigest(value, label) {
  if (typeof value !== "string" || !/^sha256:[0-9a-f]{64}$/.test(value)) {
    throw new ProjectionError("invalidArtifact", `${label} is not a SHA-256 digest.`);
  }
  return value;
}

function canonicalJson(value) {
  if (value === null || typeof value === "boolean" || typeof value === "string") return JSON.stringify(value);
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new ProjectionError("invalidArtifact", "Non-finite numbers are not canonical JSON.");
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (isObject(value)) {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(",")}}`;
  }
  throw new ProjectionError("invalidArtifact", "Unsupported canonical JSON value.");
}

async function sha256Bytes(bytes) {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return `sha256:${[...digest].map((value) => value.toString(16).padStart(2, "0")).join("")}`;
}

async function sha256Json(value) {
  return sha256Bytes(textEncoder.encode(canonicalJson(value)));
}

function canonicalSet(value, label, allowEmpty = false) {
  if (!Array.isArray(value) || (!allowEmpty && value.length === 0) || value.some((item) => typeof item !== "string" || !item)) {
    throw new ProjectionError("invalidArtifact", `${label} must be a bounded string set.`);
  }
  const sorted = [...new Set(value)].sort();
  if (sorted.length !== value.length || sorted.some((item, index) => item !== value[index])) {
    throw new ProjectionError("invalidArtifact", `${label} must be canonical and unique.`);
  }
  return sorted;
}

function descriptorMaterial(descriptor) {
  return {
    schema: descriptor.schema,
    collection: descriptor.collection,
    generationId: descriptor.generationId,
    providerId: descriptor.providerId,
    profileId: descriptor.profileId,
    strategyVersion: descriptor.strategyVersion,
    sourceManifestDigest: descriptor.sourceManifestDigest,
    recordRevisionSetDigest: descriptor.recordRevisionSetDigest,
    projectionSchemaDigest: descriptor.projectionSchemaDigest,
    analyzerDigest: descriptor.analyzerDigest,
    configurationDigest: descriptor.configurationDigest,
    expectedItemCount: descriptor.expectedItemCount,
    expectedPartitions: descriptor.expectedPartitions,
    capabilities: descriptor.capabilities,
    artifacts: descriptor.artifacts,
    createdAtUtc: descriptor.createdAtUtc,
  };
}

async function validateDescriptor(descriptor) {
  requireExactKeys(descriptor, [
    "schema", "collection", "generationId", "providerId", "profileId", "strategyVersion",
    "sourceManifestDigest", "recordRevisionSetDigest", "projectionSchemaDigest", "analyzerDigest",
    "configurationDigest", "expectedItemCount", "expectedPartitions", "capabilities", "artifacts",
    "createdAtUtc", "descriptorDigest",
  ], "generation descriptor");
  if (descriptor.schema !== DESCRIPTOR_SCHEMA) throw new ProjectionError("invalidArtifact", "Unsupported descriptor schema.");
  for (const field of ["collection", "generationId", "providerId", "profileId", "strategyVersion"]) requireIdentifier(descriptor[field], field);
  for (const field of ["sourceManifestDigest", "recordRevisionSetDigest", "projectionSchemaDigest", "configurationDigest", "descriptorDigest"]) requireDigest(descriptor[field], field);
  if (descriptor.analyzerDigest !== null) requireDigest(descriptor.analyzerDigest, "analyzerDigest");
  if (!Number.isSafeInteger(descriptor.expectedItemCount) || descriptor.expectedItemCount < 0) throw new ProjectionError("invalidArtifact", "expectedItemCount is invalid.");
  canonicalSet(descriptor.expectedPartitions, "expectedPartitions");
  canonicalSet(descriptor.capabilities, "capabilities");
  if (!descriptor.capabilities.includes("completeCoverage") || !descriptor.capabilities.includes("generationPinnedContinuation") || !descriptor.capabilities.includes("lexical")) {
    throw new ProjectionError("invalidArtifact", "Worker/R2 descriptor lacks required capabilities.");
  }
  if (!Array.isArray(descriptor.artifacts)) throw new ProjectionError("invalidArtifact", "artifacts must be an array.");
  if (descriptor.artifacts.length < 1 || descriptor.artifacts.length > 65) throw new ProjectionError("invalidArtifact", "Descriptor artifact count exceeds the adapter bound.");
  const artifactIds = [];
  for (const artifact of descriptor.artifacts) {
    requireExactKeys(artifact, ["id", "kind", "contentHash", "sizeBytes", "mediaType"], "descriptor artifact");
    artifactIds.push(requireIdentifier(artifact.id, "artifact id"));
    requireIdentifier(artifact.kind, "artifact kind");
    requireDigest(artifact.contentHash, "artifact contentHash");
    if (!Number.isSafeInteger(artifact.sizeBytes) || artifact.sizeBytes < 0) throw new ProjectionError("invalidArtifact", "artifact sizeBytes is invalid.");
    if (artifact.mediaType !== null && (typeof artifact.mediaType !== "string" || artifact.mediaType.length > 200)) throw new ProjectionError("invalidArtifact", "artifact mediaType is invalid.");
  }
  canonicalSet(artifactIds, "artifact IDs", true);
  if (typeof descriptor.createdAtUtc !== "string" || !Number.isFinite(Date.parse(descriptor.createdAtUtc))) throw new ProjectionError("invalidArtifact", "createdAtUtc is invalid.");
  if (await sha256Json(descriptorMaterial(descriptor)) !== descriptor.descriptorDigest) throw new ProjectionError("invalidArtifact", "Descriptor digest mismatch.");
  return descriptor;
}

function keyComponent(value) {
  return encodeURIComponent(requireIdentifier(value, "key component"));
}

function activeKey(collection) {
  return `active/${keyComponent(collection)}.json`;
}

function catalogKey(collection, generationId) {
  return `catalog/${keyComponent(collection)}/${keyComponent(generationId)}.json`;
}

function cachePut(key, value) {
  if (contentCache.has(key)) contentCache.delete(key);
  contentCache.set(key, value);
  while (contentCache.size > MAX_CACHE_ENTRIES) contentCache.delete(contentCache.keys().next().value);
}

function immutableReaderMode(env) {
  const direct = env.INDEX !== undefined && typeof env.INDEX?.get === "function";
  const service = env.OBJECT_READER !== undefined && typeof env.OBJECT_READER?.fetch === "function";
  if (direct === service) throw new ProjectionError("providerUnavailable", "Exactly one immutable-object reader binding is required.", true);
  return direct ? "direct-r2" : "service-reader";
}

function objectByteLimit(key) {
  return key.startsWith("objects/sha256/") ? MAX_IMMUTABLE_OBJECT_BYTES : MAX_CONTROL_OBJECT_BYTES;
}

async function readImmutableBytes(env, key) {
  const limit = objectByteLimit(key);
  if (immutableReaderMode(env) === "direct-r2") {
    const object = await env.INDEX.get(key);
    if (object === null) throw new ProjectionError("generationUnavailable", "A required immutable generation object is unavailable.", true);
    if (object.size > limit) throw new ProjectionError("workLimitExceeded", "A required immutable generation object exceeds the adapter byte bound.");
    const bytes = new Uint8Array(await object.arrayBuffer());
    if (bytes.byteLength > limit) throw new ProjectionError("workLimitExceeded", "A required immutable generation object exceeds the adapter byte bound.");
    return bytes;
  }

  if (typeof env.OBJECT_READER_SECRET !== "string" || textEncoder.encode(env.OBJECT_READER_SECRET).byteLength < 32) {
    throw new ProjectionError("providerUnavailable", "Immutable-object reader authorization is unavailable.", true);
  }
  let response;
  try {
    response = await env.OBJECT_READER.fetch("https://objects.internal/read", {
      method: "POST",
      headers: {
        "authorization": `Bearer ${env.OBJECT_READER_SECRET}`,
        "content-type": "application/json",
      },
      body: canonicalJson({ key }),
    });
  } catch {
    throw new ProjectionError("providerUnavailable", "The immutable-object reader request failed.", true);
  }
  if (response.status === 404) throw new ProjectionError("generationUnavailable", "A required immutable generation object is unavailable.", true);
  if (response.status === 413) throw new ProjectionError("workLimitExceeded", "A required immutable generation object exceeds the adapter byte bound.");
  if (!response.ok) throw new ProjectionError("providerUnavailable", "The immutable-object reader failed.", response.status === 429 || response.status >= 500);
  if (response.headers.get("content-type")?.split(";", 1)[0].trim().toLowerCase() !== "application/json") throw new ProjectionError("invalidArtifact", "The immutable-object reader returned an unsupported media type.");
  const declared = Number(response.headers.get("content-length") ?? "0");
  if (Number.isFinite(declared) && declared > limit) throw new ProjectionError("workLimitExceeded", "A required immutable generation object exceeds the adapter byte bound.");
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength > limit) throw new ProjectionError("workLimitExceeded", "A required immutable generation object exceeds the adapter byte bound.");
  return bytes;
}

async function readObjectJson(env, key, expectedDigest = null) {
  const cacheKey = expectedDigest === null ? null : `${key}:${expectedDigest}`;
  if (cacheKey !== null && contentCache.has(cacheKey)) return { value: contentCache.get(cacheKey), cacheHit: true };
  const bytes = await readImmutableBytes(env, key);
  if (expectedDigest !== null && await sha256Bytes(bytes) !== expectedDigest) throw new ProjectionError("artifactDigestMismatch", "A required immutable generation object failed digest verification.");
  let value;
  try {
    value = JSON.parse(textDecoder.decode(bytes));
  } catch {
    throw new ProjectionError("invalidArtifact", "A required immutable generation object is invalid JSON.");
  }
  if (cacheKey !== null) cachePut(cacheKey, value);
  return { value, cacheHit: false, sizeBytes: bytes.byteLength };
}

async function resolveCatalog(env, collection, requestedGeneration) {
  let generationId = requestedGeneration;
  let activeDigest = null;
  let fromActive = false;
  if (generationId === null) {
    fromActive = true;
    const activeRead = await readObjectJson(env, activeKey(collection));
    const active = activeRead.value;
    requireExactKeys(active, ["schemaVersion", "collection", "generationId", "descriptorDigest"], "active generation pointer");
    if (active.schemaVersion !== ACTIVE_SCHEMA || active.collection !== collection) throw new ProjectionError("invalidArtifact", "Active generation pointer is invalid.");
    generationId = requireIdentifier(active.generationId, "active generationId");
    activeDigest = requireDigest(active.descriptorDigest, "active descriptorDigest");
  }
  const catalogRead = await readObjectJson(env, catalogKey(collection, generationId));
  const catalog = catalogRead.value;
  requireExactKeys(catalog, ["schemaVersion", "collection", "generationId", "state", "descriptor", "manifestKey", "availablePartitions"], "generation catalog record");
  if (catalog.schemaVersion !== CATALOG_SCHEMA || catalog.collection !== collection || catalog.generationId !== generationId) throw new ProjectionError("invalidArtifact", "Generation catalog identity mismatch.");
  if (!["active", "retained", "retired"].includes(catalog.state)) throw new ProjectionError("invalidArtifact", "Generation catalog state is invalid.");
  const descriptor = await validateDescriptor(catalog.descriptor);
  if (descriptor.collection !== collection || descriptor.generationId !== generationId) throw new ProjectionError("invalidArtifact", "Descriptor and catalog identity differ.");
  if (activeDigest !== null && activeDigest !== descriptor.descriptorDigest) throw new ProjectionError("invalidArtifact", "Active pointer descriptor fence mismatch.");
  if (fromActive && catalog.state !== "active") throw new ProjectionError("invalidArtifact", "Active pointer names a generation that is not active.");
  if (typeof catalog.manifestKey !== "string" || !catalog.manifestKey.startsWith("objects/sha256/")) throw new ProjectionError("invalidArtifact", "Manifest key is not content-addressed.");
  canonicalSet(catalog.availablePartitions, "availablePartitions", true);
  if (catalog.availablePartitions.some((partition) => !descriptor.expectedPartitions.includes(partition))) throw new ProjectionError("invalidArtifact", "Catalog availability exceeds descriptor coverage.");
  return { catalog, descriptor, generationId };
}

function validateManifest(manifest, descriptor, manifestArtifact) {
  requireExactKeys(manifest, [
    "schemaVersion", "generationId", "sourceManifestDigest", "recordRevisionSetDigest",
    "projectionSchemaDigest", "analyzerDigest", "scoringContract", "tieBreak", "k1", "b",
    "tokenPattern", "stopWords", "queryAliases", "expectedItemCount", "expectedPartitions",
    "averageDocumentLength", "candidateCapacity", "maxWorkUnits", "shards",
  ], "Worker/R2 manifest");
  if (manifest.schemaVersion !== MANIFEST_SCHEMA || manifest.generationId !== descriptor.generationId) throw new ProjectionError("invalidArtifact", "Manifest generation identity mismatch.");
  for (const field of ["sourceManifestDigest", "recordRevisionSetDigest", "projectionSchemaDigest", "analyzerDigest"]) requireDigest(manifest[field], `manifest ${field}`);
  for (const field of ["sourceManifestDigest", "recordRevisionSetDigest", "projectionSchemaDigest", "analyzerDigest"]) {
    if (manifest[field] !== descriptor[field]) throw new ProjectionError("invalidArtifact", `Manifest ${field} differs from the descriptor.`);
  }
  if (manifest.scoringContract !== "global-bm25-like-card-v1" || manifest.tieBreak !== "score-desc-partition-id-asc-v1" || manifest.tokenPattern !== "[a-z0-9]+") throw new ProjectionError("invalidArtifact", "Manifest search contract is unsupported.");
  if (manifest.k1 !== 1.2 || manifest.b !== 0.75) throw new ProjectionError("invalidArtifact", "Manifest BM25 constants are unsupported.");
  canonicalSet(manifest.stopWords, "manifest stopWords", true);
  if (!Array.isArray(manifest.queryAliases) || manifest.queryAliases.some((entry) => !Array.isArray(entry) || entry.length !== 2 || entry.some((item) => typeof item !== "string" || !item))) throw new ProjectionError("invalidArtifact", "Manifest query aliases are invalid.");
  if (!Number.isSafeInteger(manifest.expectedItemCount) || manifest.expectedItemCount !== descriptor.expectedItemCount) throw new ProjectionError("invalidArtifact", "Manifest expected item count mismatch.");
  canonicalSet(manifest.expectedPartitions, "manifest expectedPartitions");
  if (canonicalJson(manifest.expectedPartitions) !== canonicalJson(descriptor.expectedPartitions)) throw new ProjectionError("invalidArtifact", "Manifest expected partitions mismatch.");
  if (!Number.isFinite(manifest.averageDocumentLength) || manifest.averageDocumentLength <= 0) throw new ProjectionError("invalidArtifact", "Manifest average document length is invalid.");
  if (!Number.isSafeInteger(manifest.candidateCapacity) || manifest.candidateCapacity < 1 || manifest.candidateCapacity > 10_000) throw new ProjectionError("invalidArtifact", "Manifest candidate capacity is invalid.");
  if (!Number.isSafeInteger(manifest.maxWorkUnits) || manifest.maxWorkUnits < 1 || manifest.maxWorkUnits > 10_000_000) throw new ProjectionError("invalidArtifact", "Manifest work bound is invalid.");
  if (!Array.isArray(manifest.shards) || manifest.shards.length < 1 || manifest.shards.length > 64) throw new ProjectionError("invalidArtifact", "Manifest shard set is invalid.");
  const shardIds = [];
  let itemCount = 0;
  for (const shard of manifest.shards) {
    requireExactKeys(shard, ["id", "key", "contentHash", "sizeBytes", "itemCount", "partitions"], "manifest shard");
    shardIds.push(requireIdentifier(shard.id, "shard id"));
    if (typeof shard.key !== "string" || !shard.key.startsWith("objects/sha256/")) throw new ProjectionError("invalidArtifact", "Shard key is not content-addressed.");
    requireDigest(shard.contentHash, "shard contentHash");
    if (!Number.isSafeInteger(shard.sizeBytes) || shard.sizeBytes < 1 || !Number.isSafeInteger(shard.itemCount) || shard.itemCount < 0) throw new ProjectionError("invalidArtifact", "Shard size/count is invalid.");
    canonicalSet(shard.partitions, "shard partitions");
    if (shard.partitions.some((partition) => !manifest.expectedPartitions.includes(partition))) throw new ProjectionError("invalidArtifact", "Shard covers an unexpected partition.");
    itemCount += shard.itemCount;
  }
  canonicalSet(shardIds, "manifest shard IDs");
  if (itemCount !== descriptor.expectedItemCount) throw new ProjectionError("invalidArtifact", "Manifest shard item counts are incomplete.");
  if (manifestArtifact.kind !== "worker-r2-generation-manifest" || manifestArtifact.sizeBytes > MAX_MANIFEST_BYTES) throw new ProjectionError("invalidArtifact", "Manifest artifact declaration is invalid or exceeds the adapter bound.");
  return manifest;
}

function validateShard(shard, declaration, manifest, descriptor) {
  requireExactKeys(shard, ["schemaVersion", "generationId", "sourceManifestDigest", "shardId", "partitions", "itemCount", "records", "directMap", "terms"], "Worker/R2 shard");
  if (shard.schemaVersion !== SHARD_SCHEMA || shard.generationId !== descriptor.generationId || shard.sourceManifestDigest !== descriptor.sourceManifestDigest || shard.shardId !== declaration.id) throw new ProjectionError("invalidArtifact", "Shard identity mismatch.");
  canonicalSet(shard.partitions, "shard partitions");
  if (canonicalJson(shard.partitions) !== canonicalJson(declaration.partitions)) throw new ProjectionError("invalidArtifact", "Shard partition declaration mismatch.");
  if (!Number.isSafeInteger(shard.itemCount) || shard.itemCount !== declaration.itemCount || !Array.isArray(shard.records) || shard.records.length !== shard.itemCount) throw new ProjectionError("invalidArtifact", "Shard item count mismatch.");
  const identifiers = new Set();
  for (const record of shard.records) {
    requireExactKeys(record, ["partitionKey", "id", "revision", "length", "metadata"], "shard record");
    requireIdentifier(record.partitionKey, "record partitionKey");
    requireIdentifier(record.id, "record id");
    if (!shard.partitions.includes(record.partitionKey) || identifiers.has(record.id)) throw new ProjectionError("invalidArtifact", "Shard record identity or partition is invalid.");
    identifiers.add(record.id);
    if (!Number.isSafeInteger(record.revision) || record.revision < 1 || !Number.isFinite(record.length) || record.length <= 0 || !isObject(record.metadata)) throw new ProjectionError("invalidArtifact", "Shard record evidence is invalid.");
  }
  if (!isObject(shard.directMap) || !isObject(shard.terms)) throw new ProjectionError("invalidArtifact", "Shard directories are invalid.");
  for (const [key, ordinals] of Object.entries(shard.directMap)) {
    if (!key || !Array.isArray(ordinals) || ordinals.some((ordinal) => !Number.isSafeInteger(ordinal) || ordinal < 0 || ordinal >= shard.records.length)) throw new ProjectionError("invalidArtifact", "Shard direct map is invalid.");
  }
  for (const [term, value] of Object.entries(shard.terms)) {
    if (!term || !Array.isArray(value) || value.length !== 2 || !Number.isFinite(value[0]) || value[0] < 0 || !Array.isArray(value[1])) throw new ProjectionError("invalidArtifact", "Shard term directory is invalid.");
    let previous = -1;
    for (const posting of value[1]) {
      if (!Array.isArray(posting) || posting.length !== 2 || !Number.isSafeInteger(posting[0]) || posting[0] <= previous || posting[0] >= shard.records.length || !Number.isFinite(posting[1]) || posting[1] <= 0) throw new ProjectionError("invalidArtifact", "Shard posting is invalid.");
      previous = posting[0];
    }
  }
  return shard;
}

function requestedPartitions(query, descriptor) {
  const value = query.partitionKeys === undefined || query.partitionKeys === null
    ? descriptor.expectedPartitions
    : query.partitionKeys;
  if (!Array.isArray(value) || value.length < 1 || value.some((item) => typeof item !== "string" || !item)) throw new ProjectionError("invalidRequest", "partitionKeys must be a non-empty string set.");
  const canonical = [...new Set(value)].sort();
  if (canonical.length !== value.length) throw new ProjectionError("invalidRequest", "partitionKeys must be unique.");
  if (canonical.some((partition) => !descriptor.expectedPartitions.includes(partition))) throw new ProjectionError("invalidRequest", "The request names a partition outside the generation.");
  return canonical;
}

function valueAtPath(record, path) {
  if (typeof path !== "string" || !path || path.length > 200 || path.split(".").some((part) => !part)) throw new ProjectionError("invalidRequest", "Filter path is invalid.");
  let value = { ...record.metadata, partitionKey: record.partitionKey, id: record.id, revision: record.revision };
  for (const part of path.split(".")) {
    if (!isObject(value) || !Object.hasOwn(value, part)) return undefined;
    value = value[part];
  }
  return value;
}

function compareFilter(actual, op, expected) {
  if (op === "exists") return expected === false ? actual === undefined : actual !== undefined;
  if (op === "eq") return actual === expected;
  if (op === "neq") return actual !== expected;
  if (op === "in") return Array.isArray(expected) && expected.some((item) => actual === item || (Array.isArray(actual) && actual.includes(item)));
  if (op === "contains") return (Array.isArray(actual) && actual.includes(expected)) || (typeof actual === "string" && typeof expected === "string" && actual.includes(expected));
  if (!["gt", "gte", "lt", "lte"].includes(op) || (typeof actual !== "number" && typeof actual !== "string") || typeof expected !== typeof actual) throw new ProjectionError("invalidRequest", "Filter comparison is invalid.");
  if (op === "gt") return actual > expected;
  if (op === "gte") return actual >= expected;
  if (op === "lt") return actual < expected;
  return actual <= expected;
}

function evaluateFilter(record, filter, state = { nodes: 0 }, depth = 0) {
  if (filter === null || filter === undefined) return true;
  if (!isObject(filter) || depth > MAX_FILTER_DEPTH || ++state.nodes > MAX_FILTER_NODES) throw new ProjectionError("invalidRequest", "Filter tree exceeds its structural bound.");
  const hasCompound = Object.hasOwn(filter, "combine") || Object.hasOwn(filter, "children");
  if (hasCompound) {
    if (!Object.keys(filter).every((key) => ["combine", "children"].includes(key)) || !["all", "any"].includes(filter.combine) || !Array.isArray(filter.children) || filter.children.length < 1) throw new ProjectionError("invalidRequest", "Compound filter is invalid.");
    const values = filter.children.map((child) => evaluateFilter(record, child, state, depth + 1));
    return filter.combine === "all" ? values.every(Boolean) : values.some(Boolean);
  }
  if (!Object.keys(filter).every((key) => ["path", "op", "value"].includes(key)) || typeof filter.op !== "string") throw new ProjectionError("invalidRequest", "Leaf filter is invalid.");
  return compareFilter(valueAtPath(record, filter.path), filter.op, filter.value);
}

function queryWithoutContinuation(query) {
  const value = structuredClone(query);
  value.continuationToken = null;
  return value;
}

function validateSearchRequest(request, descriptor, manifest) {
  if (!isObject(request) || request.schema !== REQUEST_SCHEMA || !isObject(request.query)) throw new ProjectionError("invalidRequest", "Search request is invalid.");
  if (!["schema", "generationId", "expectedDescriptorDigest", "query", "deadlineUtc"].every((key) => Object.hasOwn(request, key))) throw new ProjectionError("invalidRequest", "Search request is missing required fields.");
  if (Object.keys(request).some((key) => !["schema", "generationId", "expectedDescriptorDigest", "query", "deadlineUtc"].includes(key))) throw new ProjectionError("invalidRequest", "Search request has unsupported fields.");
  if (request.generationId !== null) requireIdentifier(request.generationId, "request generationId");
  if (request.expectedDescriptorDigest !== null) requireDigest(request.expectedDescriptorDigest, "request expectedDescriptorDigest");
  const query = request.query;
  if (Object.keys(query).some((key) => !["partitionKeys", "filter", "vector", "lexical", "orderBy", "limit", "continuationToken"].includes(key))) throw new ProjectionError("invalidRequest", "Query envelope has unsupported fields.");
  if (query.vector !== undefined && query.vector !== null) throw new ProjectionError("capabilityUnsupported", "Worker/R2 proof supports lexical search only.");
  if (query.orderBy !== undefined && query.orderBy !== null) throw new ProjectionError("capabilityUnsupported", "Worker/R2 proof uses its bound score order.");
  if (!isObject(query.lexical) || typeof query.lexical.query !== "string" || query.lexical.query.trim() === "" || textEncoder.encode(query.lexical.query).byteLength > MAX_QUERY_BYTES) throw new ProjectionError("invalidRequest", "A bounded lexical query is required.");
  const lexical = query.lexical;
  const lexicalFields = ["query", "fields", "top", "scanLimit", "minScore", "scoring", "matchMode", "fieldBoosts", "bm25K1", "bm25B", "phraseBoost", "exactBoost", "metadataBoost", "prefixMatching", "prefixMinChars", "requiredPhraseGroups"];
  if (Object.keys(lexical).some((key) => !lexicalFields.includes(key))) throw new ProjectionError("invalidRequest", "Lexical options have unsupported fields.");
  if (lexical.fields !== undefined && lexical.fields !== null) throw new ProjectionError("capabilityUnsupported", "Custom lexical fields are not supported by this profile.");
  if (lexical.scoring !== undefined && lexical.scoring !== "bm25") throw new ProjectionError("capabilityUnsupported", "The scoring contract is BM25-like.");
  if (lexical.matchMode !== undefined && lexical.matchMode !== "any") throw new ProjectionError("capabilityUnsupported", "Only any-term matching is supported.");
  if (
    lexical.prefixMatching === true ||
    (lexical.fieldBoosts !== undefined && lexical.fieldBoosts !== null) ||
    (lexical.requiredPhraseGroups !== undefined && lexical.requiredPhraseGroups !== null) ||
    (lexical.bm25K1 !== undefined && lexical.bm25K1 !== manifest.k1) ||
    (lexical.bm25B !== undefined && lexical.bm25B !== manifest.b) ||
    (lexical.phraseBoost !== undefined && lexical.phraseBoost !== 0.15) ||
    (lexical.exactBoost !== undefined && lexical.exactBoost !== 0.25) ||
    (lexical.metadataBoost !== undefined && lexical.metadataBoost !== 0.1) ||
    (lexical.prefixMinChars !== undefined && lexical.prefixMinChars !== 3)
  ) throw new ProjectionError("capabilityUnsupported", "Lexical options differ from the immutable profile.");
  const limit = query.limit ?? lexical.top ?? 10;
  if (!Number.isSafeInteger(limit) || limit < 1 || limit > manifest.candidateCapacity) throw new ProjectionError("invalidRequest", "Candidate limit exceeds the generation bound.");
  const scanLimit = lexical.scanLimit ?? manifest.maxWorkUnits;
  if (!Number.isSafeInteger(scanLimit) || scanLimit < 1 || scanLimit > manifest.maxWorkUnits) throw new ProjectionError("invalidRequest", "scanLimit exceeds the generation work bound.");
  if (lexical.minScore !== undefined && lexical.minScore !== null && !Number.isFinite(lexical.minScore)) throw new ProjectionError("invalidRequest", "minScore must be finite.");
  if (query.continuationToken !== undefined && query.continuationToken !== null && (typeof query.continuationToken !== "string" || query.continuationToken.length > 8192)) throw new ProjectionError("invalidRequest", "Continuation token is invalid.");
  const partitions = requestedPartitions(query, descriptor);
  return { query, lexical, limit, scanLimit, partitions };
}

function base64Url(bytes) {
  let binary = "";
  for (const value of bytes) binary += String.fromCharCode(value);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

function fromBase64Url(value) {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]+$/u.test(value)) throw new ProjectionError("invalidContinuation", "Continuation token is invalid.");
  const padded = value.replaceAll("-", "+").replaceAll("_", "/") + "=".repeat((4 - value.length % 4) % 4);
  try {
    return Uint8Array.from(atob(padded), (character) => character.charCodeAt(0));
  } catch {
    throw new ProjectionError("invalidContinuation", "Continuation token is invalid.");
  }
}

async function hmac(secret, bytes) {
  if (typeof secret !== "string" || textEncoder.encode(secret).byteLength < 32) throw new ProjectionError("providerUnavailable", "Continuation signing is unavailable.", true);
  const key = await crypto.subtle.importKey("raw", textEncoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, bytes));
}

function constantTimeEqual(left, right) {
  let difference = left.length ^ right.length;
  const length = Math.max(left.length, right.length);
  for (let index = 0; index < length; index += 1) difference |= (left[index] ?? 0) ^ (right[index] ?? 0);
  return difference === 0;
}

function scoreHex(score) {
  const bytes = new Uint8Array(8);
  new DataView(bytes.buffer).setFloat64(0, score, false);
  return [...bytes].map((value) => value.toString(16).padStart(2, "0")).join("");
}

function scoreFromHex(value) {
  if (typeof value !== "string" || !/^[0-9a-f]{16}$/u.test(value)) throw new ProjectionError("invalidContinuation", "Continuation score boundary is invalid.");
  const bytes = Uint8Array.from(value.match(/../gu), (pair) => Number.parseInt(pair, 16));
  const score = new DataView(bytes.buffer).getFloat64(0, false);
  if (!Number.isFinite(score)) throw new ProjectionError("invalidContinuation", "Continuation score boundary is invalid.");
  return score;
}

async function issueContinuation(env, payload) {
  const bytes = textEncoder.encode(canonicalJson(payload));
  const signature = await hmac(env.CONTINUATION_SECRET, bytes);
  return `${base64Url(bytes)}.${base64Url(signature)}`;
}

async function parseContinuation(env, token) {
  const parts = typeof token === "string" ? token.split(".") : [];
  if (parts.length !== 2) throw new ProjectionError("invalidContinuation", "Continuation token is invalid.");
  const bytes = fromBase64Url(parts[0]);
  const supplied = fromBase64Url(parts[1]);
  const expected = await hmac(env.CONTINUATION_SECRET, bytes);
  if (!constantTimeEqual(supplied, expected)) throw new ProjectionError("invalidContinuation", "Continuation authentication failed.");
  let payload;
  try {
    payload = JSON.parse(textDecoder.decode(bytes));
  } catch {
    throw new ProjectionError("invalidContinuation", "Continuation payload is invalid.");
  }
  try {
    requireExactKeys(payload, ["schemaVersion", "collection", "generationId", "descriptorDigest", "requestFingerprint", "scoreHex", "partitionKey", "identifier", "page", "expiresAtUnixMs"], "continuation payload");
    if (payload.schemaVersion !== CONTINUATION_SCHEMA || !Number.isSafeInteger(payload.page) || payload.page < 1 || payload.page >= MAX_PAGE_COUNT || !Number.isSafeInteger(payload.expiresAtUnixMs) || Date.now() >= payload.expiresAtUnixMs) throw new ProjectionError("invalidContinuation", "Continuation payload is expired or invalid.");
    requireIdentifier(payload.collection, "continuation collection");
    requireIdentifier(payload.generationId, "continuation generationId");
    requireIdentifier(payload.partitionKey, "continuation partitionKey");
    requireIdentifier(payload.identifier, "continuation identifier");
    requireDigest(payload.descriptorDigest, "continuation descriptorDigest");
    requireDigest(payload.requestFingerprint, "continuation requestFingerprint");
    payload.score = scoreFromHex(payload.scoreHex);
  } catch {
    throw new ProjectionError("invalidContinuation", "Continuation payload is expired or invalid.");
  }
  return payload;
}

function failureResult({ generationId = null, descriptorDigest = null, requested = [], covered = [], missing = requested, coverageStatus = "unavailable", code, message, retryable = false, details = null }) {
  return {
    schema: RESULT_SCHEMA,
    status: "failed",
    generationId,
    generationDescriptorDigest: descriptorDigest,
    items: [],
    continuationToken: null,
    consistency: "immutableGeneration",
    coverage: { status: coverageStatus, requestedPartitions: requested, coveredPartitions: covered, missingPartitions: missing },
    diagnostics: { workLimit: null, workUnits: null, candidateBound: null, candidateCount: null, returnedCount: 0, cacheStatus: "notApplicable", details },
    failure: { code, message, retryable },
  };
}

function jsonResponse(status, body) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" } });
}

async function readBody(request) {
  const contentLength = Number(request.headers.get("content-length") ?? "0");
  if (Number.isFinite(contentLength) && contentLength > MAX_BODY_BYTES) throw new ProjectionError("invalidRequest", "Request body exceeds its bound.");
  const text = await request.text();
  if (textEncoder.encode(text).byteLength > MAX_BODY_BYTES) throw new ProjectionError("invalidRequest", "Request body exceeds its bound.");
  try {
    return JSON.parse(text);
  } catch {
    throw new ProjectionError("invalidRequest", "Request body is invalid JSON.");
  }
}

function availableCoverage(catalog, requested) {
  const covered = requested.filter((partition) => catalog.availablePartitions.includes(partition));
  const missing = requested.filter((partition) => !catalog.availablePartitions.includes(partition));
  return { covered, missing, complete: missing.length === 0 };
}

async function loadGeneration(env, catalog, descriptor) {
  const manifestArtifact = descriptor.artifacts.find((artifact) => artifact.id === "worker-r2-manifest");
  if (manifestArtifact === undefined) throw new ProjectionError("invalidArtifact", "Descriptor lacks the Worker/R2 manifest artifact.");
  const manifestRead = await readObjectJson(env, catalog.manifestKey, manifestArtifact.contentHash);
  if (manifestRead.sizeBytes !== undefined && manifestRead.sizeBytes !== manifestArtifact.sizeBytes) throw new ProjectionError("artifactDigestMismatch", "Manifest size differs from its descriptor evidence.");
  const manifest = validateManifest(manifestRead.value, descriptor, manifestArtifact);
  return { manifest, manifestCacheHit: manifestRead.cacheHit };
}

async function loadRequiredShards(env, manifest, descriptor, partitions, deadlineMs) {
  const declarations = manifest.shards.filter((shard) => shard.partitions.some((partition) => partitions.includes(partition)));
  if (declarations.reduce((total, declaration) => total + declaration.sizeBytes, 0) > MAX_SELECTED_SHARD_BYTES) throw new ProjectionError("workLimitExceeded", "Selected immutable shards exceed the adapter memory bound.", true);
  const represented = new Set(declarations.flatMap((shard) => shard.partitions));
  if (partitions.some((partition) => !represented.has(partition))) throw new ProjectionError("coverageIncomplete", "The immutable manifest does not cover every requested partition.", true);
  const reads = await Promise.allSettled(declarations.map(async (declaration) => {
    if (Date.now() >= deadlineMs) throw new ProjectionError("deadlineExceeded", "The shared search deadline elapsed.", true);
    const read = await readObjectJson(env, declaration.key, declaration.contentHash);
    if (read.sizeBytes !== undefined && read.sizeBytes !== declaration.sizeBytes) throw new ProjectionError("artifactDigestMismatch", "Shard size differs from its manifest evidence.");
    if (Date.now() >= deadlineMs) throw new ProjectionError("deadlineExceeded", "The shared search deadline elapsed.", true);
    return { shard: validateShard(read.value, declaration, manifest, descriptor), cacheHit: read.cacheHit };
  }));
  const failed = reads.find((result) => result.status === "rejected");
  if (failed !== undefined) throw failed.reason instanceof ProjectionError ? failed.reason : new ProjectionError("providerUnavailable", "A required shard could not be read.", true);
  return reads.map((result) => result.value);
}

function analyze(query, manifest) {
  let expanded = query.toLowerCase();
  for (const [alias, canonical] of manifest.queryAliases) expanded = expanded.replaceAll(alias, canonical);
  const stopWords = new Set(manifest.stopWords);
  return [...new Set(expanded.match(/[a-z0-9]+/gu) ?? [])].filter((term) => !stopWords.has(term)).sort();
}

function afterBoundary(candidate, boundary) {
  return boundary === null ||
    candidate.score < boundary.score ||
    (candidate.score === boundary.score && candidate.partitionKey > boundary.partitionKey) ||
    (candidate.score === boundary.score && candidate.partitionKey === boundary.partitionKey && candidate.id > boundary.identifier);
}

function searchShards(shards, manifest, lexical, filter, limit, scanLimit, boundary, deadlineMs, requested) {
  let workUnits = 0;
  const directKey = lexical.query.toLowerCase().trim();
  const direct = [];
  const seenIdentities = new Set();
  for (const { shard } of shards) {
    for (const record of shard.records) {
      const identity = `${record.partitionKey}\n${record.id}`;
      if (seenIdentities.has(identity)) throw new ProjectionError("invalidArtifact", "A record appears in multiple immutable shards.");
      seenIdentities.add(identity);
    }
    for (const ordinal of shard.directMap[directKey] ?? []) {
      const record = shard.records[ordinal];
      if (requested.includes(record.partitionKey) && evaluateFilter(record, filter)) {
        direct.push({ partitionKey: record.partitionKey, id: record.id, revision: record.revision, score: record.id === directKey ? 1000 : 900 });
      }
    }
  }
  let candidates;
  if (direct.length > 0) {
    candidates = direct;
  } else {
    const terms = analyze(lexical.query, manifest);
    candidates = [];
    const termIdfs = new Map();
    for (const { shard } of shards) {
      const scores = new Map();
      for (const term of terms) {
        const termValue = shard.terms[term];
        if (termValue === undefined) continue;
        const [idf, postings] = termValue;
        if (termIdfs.has(term) && termIdfs.get(term) !== idf) throw new ProjectionError("invalidArtifact", "Shards disagree on global term statistics.");
        termIdfs.set(term, idf);
        for (const [ordinal, frequency] of postings) {
          workUnits += 1;
          if (workUnits > scanLimit) throw new ProjectionError("workLimitExceeded", "Lexical work exceeded the admitted scan limit.", true);
          if ((workUnits & 1023) === 0 && Date.now() >= deadlineMs) throw new ProjectionError("deadlineExceeded", "The shared search deadline elapsed.", true);
          const record = shard.records[ordinal];
          if (!requested.includes(record.partitionKey) || !evaluateFilter(record, filter)) continue;
          const denominator = frequency + manifest.k1 * (1 - manifest.b + manifest.b * record.length / manifest.averageDocumentLength);
          const contribution = idf * frequency * (manifest.k1 + 1) / denominator;
          scores.set(ordinal, (scores.get(ordinal) ?? 0) + contribution);
        }
      }
      for (const [ordinal, score] of scores) {
        const record = shard.records[ordinal];
        candidates.push({ partitionKey: record.partitionKey, id: record.id, revision: record.revision, score });
      }
    }
  }
  const minimum = lexical.minScore ?? Number.NEGATIVE_INFINITY;
  candidates = candidates.filter((candidate) => candidate.score >= minimum && afterBoundary(candidate, boundary));
  candidates.sort((left, right) => right.score - left.score ||
    (left.partitionKey < right.partitionKey ? -1 : left.partitionKey > right.partitionKey ? 1 : 0) ||
    (left.id < right.id ? -1 : left.id > right.id ? 1 : 0));
  const returned = candidates.slice(0, limit);
  return { returned, hasMore: candidates.length > limit, candidateCount: candidates.length, workUnits };
}

async function inspect(env, body) {
  if (!isObject(body) || Object.keys(body).some((key) => !["collection", "generationId", "expectedDescriptorDigest"].includes(key))) throw new ProjectionError("invalidRequest", "Inspection request is invalid.");
  let collection;
  try {
    collection = requireIdentifier(body.collection, "collection");
  } catch {
    throw new ProjectionError("invalidRequest", "Inspection collection is invalid.");
  }
  const generationId = body.generationId === undefined || body.generationId === null ? null : requireIdentifier(body.generationId, "generationId");
  const resolved = await resolveCatalog(env, collection, generationId);
  if (body.expectedDescriptorDigest !== undefined && body.expectedDescriptorDigest !== null) {
    try {
      requireDigest(body.expectedDescriptorDigest, "expectedDescriptorDigest");
    } catch {
      throw new ProjectionError("invalidRequest", "Inspection expectedDescriptorDigest is invalid.");
    }
    if (body.expectedDescriptorDigest !== resolved.descriptor.descriptorDigest) throw new ProjectionError("generationDescriptorMismatch", "Generation descriptor fence mismatch.");
  }
  let available = resolved.catalog.state === "retired" ? [] : resolved.catalog.availablePartitions;
  let coverageStatus = resolved.catalog.state === "retired" ? "unavailable" : available.length === resolved.descriptor.expectedPartitions.length ? "complete" : "incomplete";
  if (resolved.catalog.state !== "retired") {
    try {
      const loaded = await loadGeneration(env, resolved.catalog, resolved.descriptor);
      await loadRequiredShards(env, loaded.manifest, resolved.descriptor, resolved.descriptor.expectedPartitions, Date.now() + MAX_REQUEST_MILLISECONDS);
    } catch {
      available = [];
      coverageStatus = "unavailable";
    }
  }
  return {
    schema: INSPECTION_SCHEMA,
    descriptor: resolved.descriptor,
    state: resolved.catalog.state,
    availablePartitions: available,
    coverageStatus,
    observedAtUtc: new Date().toISOString(),
  };
}

async function search(env, body) {
  if (!isObject(body) || Object.keys(body).some((key) => !["collection", "request"].includes(key)) || !isObject(body.request)) throw new ProjectionError("invalidRequest", "Search envelope is invalid.");
  let collection;
  try {
    collection = requireIdentifier(body.collection, "collection");
  } catch {
    throw new ProjectionError("invalidRequest", "Search collection is invalid.");
  }
  const request = body.request;
  if (request.generationId !== null && request.generationId !== undefined) {
    try {
      requireIdentifier(request.generationId, "request generationId");
    } catch {
      return failureResult({ code: "invalidRequest", message: "Search generationId is invalid." });
    }
  }
  if (request.expectedDescriptorDigest !== null && request.expectedDescriptorDigest !== undefined) {
    try {
      requireDigest(request.expectedDescriptorDigest, "request expectedDescriptorDigest");
    } catch {
      return failureResult({ code: "invalidRequest", message: "Search expectedDescriptorDigest is invalid." });
    }
  }
  let continuation = null;
  try {
    if (request.query?.continuationToken !== undefined && request.query.continuationToken !== null) continuation = await parseContinuation(env, request.query.continuationToken);
  } catch (error) {
    const wrapped = error instanceof ProjectionError ? error : new ProjectionError("invalidContinuation", "Continuation token is invalid.");
    return failureResult({ code: wrapped.code, message: wrapped.message, retryable: wrapped.retryable });
  }
  if (continuation !== null && continuation.collection !== collection) return failureResult({ code: "invalidContinuation", message: "Continuation collection mismatch." });
  const requestedGeneration = continuation?.generationId ?? (request.generationId ?? null);
  let resolved;
  try {
    resolved = await resolveCatalog(env, collection, requestedGeneration);
  } catch (error) {
    const wrapped = error instanceof ProjectionError ? error : new ProjectionError("providerUnavailable", "Generation resolution failed.", true);
    return failureResult({ generationId: requestedGeneration, code: wrapped.code, message: wrapped.message, retryable: wrapped.retryable });
  }
  const { catalog, descriptor, generationId } = resolved;
  let preliminaryPartitions;
  try {
    preliminaryPartitions = requestedPartitions(request.query, descriptor);
  } catch (error) {
    const wrapped = error instanceof ProjectionError ? error : new ProjectionError("invalidRequest", "Search request is invalid.");
    return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: descriptor.expectedPartitions, code: wrapped.code, message: wrapped.message, retryable: wrapped.retryable });
  }
  if (catalog.state === "retired") return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: preliminaryPartitions, code: "generationRetired", message: "The selected generation is retired." });
  const expectedDescriptor = continuation?.descriptorDigest ?? request.expectedDescriptorDigest;
  if (expectedDescriptor !== undefined && expectedDescriptor !== null && expectedDescriptor !== descriptor.descriptorDigest) return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: preliminaryPartitions, covered: preliminaryPartitions, missing: [], coverageStatus: "complete", code: "generationDescriptorMismatch", message: "Generation descriptor fence mismatch." });
  const deadlineValue = request.deadlineUtc === undefined || request.deadlineUtc === null ? Date.now() + MAX_REQUEST_MILLISECONDS : Date.parse(request.deadlineUtc);
  if (!Number.isFinite(deadlineValue)) return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: preliminaryPartitions, code: "invalidRequest", message: "deadlineUtc is invalid." });
  const deadlineMs = Math.min(deadlineValue, Date.now() + MAX_REQUEST_MILLISECONDS);
  if (Date.now() >= deadlineMs) return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: preliminaryPartitions, code: "deadlineExceeded", message: "The shared search deadline elapsed.", retryable: true });
  let loaded;
  try {
    loaded = await loadGeneration(env, catalog, descriptor);
  } catch (error) {
    const wrapped = error instanceof ProjectionError ? error : new ProjectionError("providerUnavailable", "Manifest loading failed.", true);
    return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: preliminaryPartitions, code: wrapped.code, message: wrapped.message, retryable: wrapped.retryable });
  }
  let validated;
  try {
    validated = validateSearchRequest(request, descriptor, loaded.manifest);
  } catch (error) {
    const wrapped = error instanceof ProjectionError ? error : new ProjectionError("invalidRequest", "Search request is invalid.");
    return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: preliminaryPartitions, code: wrapped.code, message: wrapped.message, retryable: wrapped.retryable });
  }
  const { query, lexical, limit, scanLimit, partitions } = validated;
  const coverage = availableCoverage(catalog, partitions);
  if (!coverage.complete) return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: partitions, covered: coverage.covered, missing: coverage.missing, coverageStatus: "incomplete", code: "coverageIncomplete", message: "The selected generation lacks complete requested coverage.", retryable: true });
  const fingerprint = await sha256Json({ collection, generationId, descriptorDigest: descriptor.descriptorDigest, query: queryWithoutContinuation(query) });
  if (continuation !== null && continuation.requestFingerprint !== fingerprint) return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: partitions, covered: partitions, missing: [], coverageStatus: "complete", code: "invalidContinuation", message: "Continuation request binding mismatch." });
  let shards;
  try {
    shards = await loadRequiredShards(env, loaded.manifest, descriptor, partitions, deadlineMs);
  } catch (error) {
    const wrapped = error instanceof ProjectionError ? error : new ProjectionError("providerUnavailable", "Required shard loading failed.", true);
    return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: partitions, code: wrapped.code, message: wrapped.message, retryable: wrapped.retryable });
  }
  let page;
  try {
    page = searchShards(shards, loaded.manifest, lexical, query.filter, limit, scanLimit, continuation, deadlineMs, partitions);
  } catch (error) {
    const wrapped = error instanceof ProjectionError ? error : new ProjectionError("providerFailure", "Candidate evaluation failed.", true);
    const coverageStatus = wrapped.code === "workLimitExceeded" ? "complete" : "unavailable";
    return failureResult({ generationId, descriptorDigest: descriptor.descriptorDigest, requested: partitions, covered: coverageStatus === "complete" ? partitions : [], missing: coverageStatus === "complete" ? [] : partitions, coverageStatus, code: wrapped.code, message: wrapped.message, retryable: wrapped.retryable });
  }
  let next = null;
  if (page.hasMore && page.returned.length > 0) {
    const last = page.returned.at(-1);
    next = await issueContinuation(env, {
      schemaVersion: CONTINUATION_SCHEMA,
      collection,
      generationId,
      descriptorDigest: descriptor.descriptorDigest,
      requestFingerprint: fingerprint,
      scoreHex: scoreHex(last.score),
      partitionKey: last.partitionKey,
      identifier: last.id,
      page: (continuation?.page ?? 0) + 1,
      expiresAtUnixMs: Date.now() + CONTINUATION_LIFETIME_MILLISECONDS,
    });
  }
  const cacheHit = loaded.manifestCacheHit && shards.every((value) => value.cacheHit);
  return {
    schema: RESULT_SCHEMA,
    status: "succeeded",
    generationId,
    generationDescriptorDigest: descriptor.descriptorDigest,
    items: page.returned,
    continuationToken: next,
    consistency: "immutableGeneration",
    coverage: { status: "complete", requestedPartitions: partitions, coveredPartitions: partitions, missingPartitions: [] },
    diagnostics: {
      workLimit: scanLimit,
      workUnits: page.workUnits,
      candidateBound: limit,
      candidateCount: page.candidateCount,
      returnedCount: page.returned.length,
      cacheStatus: cacheHit ? "hit" : "miss",
      details: { adapter: "private-worker-r2", immutableReaderMode: immutableReaderMode(env), shardCount: shards.length, scoringContract: loaded.manifest.scoringContract },
    },
    failure: null,
  };
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method !== "POST" || !["/inspect", "/search"].includes(url.pathname) || url.search || url.hash) return jsonResponse(404, { status: "not-found" });
    if (typeof env.AUTHORIZATION_SECRET !== "string" || textEncoder.encode(env.AUTHORIZATION_SECRET).byteLength < 32) return jsonResponse(503, { status: "failed", code: "providerUnavailable", message: "Worker authorization is unavailable.", retryable: true });
    const authorization = request.headers.get("authorization") ?? "";
    const expectedAuthorization = `Bearer ${env.AUTHORIZATION_SECRET}`;
    if (authorization.length > 1024 || !constantTimeEqual(textEncoder.encode(authorization), textEncoder.encode(expectedAuthorization))) return jsonResponse(401, { status: "failed", code: "unauthorized", message: "Worker authorization failed.", retryable: false });
    if (request.headers.get("content-type")?.split(";", 1)[0].trim().toLowerCase() !== "application/json") return jsonResponse(415, { status: "failed", code: "invalidRequest", message: "Content-Type must be application/json.", retryable: false });
    try {
      const body = await readBody(request);
      const value = url.pathname === "/inspect" ? await inspect(env, body) : await search(env, body);
      return jsonResponse(200, value);
    } catch (error) {
      const wrapped = error instanceof ProjectionError ? error : new ProjectionError("providerFailure", "The projection request failed.", true);
      return jsonResponse(wrapped.code === "invalidRequest" ? 400 : 503, { status: "failed", code: wrapped.code, message: wrapped.message, retryable: wrapped.retryable });
    }
  },
};
