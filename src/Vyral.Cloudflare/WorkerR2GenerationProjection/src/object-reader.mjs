const MAX_BODY_BYTES = 1_024;
const MAX_OBJECT_BYTES = 33_554_432;
const textEncoder = new TextEncoder();

function response(status, body, contentType = "application/json") {
  return new Response(body, {
    status,
    headers: {
      "cache-control": "no-store",
      "content-type": contentType,
      "referrer-policy": "no-referrer",
      "x-content-type-options": "nosniff",
    },
  });
}

function constantTimeEqual(left, right) {
  let difference = left.length ^ right.length;
  const length = Math.max(left.length, right.length);
  for (let index = 0; index < length; index += 1) difference |= (left[index] ?? 0) ^ (right[index] ?? 0);
  return difference === 0;
}

function authorized(request, env) {
  if (typeof env.OBJECT_READER_SECRET !== "string" || textEncoder.encode(env.OBJECT_READER_SECRET).byteLength < 32) return null;
  const supplied = request.headers.get("authorization") ?? "";
  if (supplied.length > 1_024) return false;
  return constantTimeEqual(textEncoder.encode(supplied), textEncoder.encode(`Bearer ${env.OBJECT_READER_SECRET}`));
}

async function boundedBody(request) {
  const declared = Number(request.headers.get("content-length") ?? "0");
  if (Number.isFinite(declared) && declared > MAX_BODY_BYTES) throw new Error("body-too-large");
  const bytes = new Uint8Array(await request.arrayBuffer());
  if (bytes.byteLength > MAX_BODY_BYTES) throw new Error("body-too-large");
  return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
}

function validKey(value) {
  if (typeof value !== "string" || value.length < 1 || value.length > 500 || value.includes("..")) return false;
  return (
    /^active\/[A-Za-z0-9._%~-]+\.json$/u.test(value) ||
    /^catalog\/[A-Za-z0-9._%~-]+\/[A-Za-z0-9._%~-]+\.json$/u.test(value) ||
    /^objects\/sha256\/[0-9a-f]{64}\.json$/u.test(value)
  );
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method !== "POST" || url.pathname !== "/read" || url.search || url.hash) return response(404, '{"status":"not-found"}');
    const authorization = authorized(request, env);
    if (authorization === null) return response(503, '{"status":"unavailable"}');
    if (!authorization) return response(401, '{"status":"unauthorized"}');
    if (env.INDEX === undefined || typeof env.INDEX?.get !== "function") return response(503, '{"status":"unavailable"}');
    if (request.headers.get("content-type")?.split(";", 1)[0].trim().toLowerCase() !== "application/json") return response(415, '{"status":"invalid-request"}');
    let body;
    try {
      body = await boundedBody(request);
    } catch {
      return response(400, '{"status":"invalid-request"}');
    }
    if (body === null || typeof body !== "object" || Array.isArray(body) || Object.keys(body).length !== 1 || !validKey(body.key)) return response(400, '{"status":"invalid-request"}');
    const object = await env.INDEX.get(body.key);
    if (object === null) return response(404, '{"status":"not-found"}');
    if (object.size > MAX_OBJECT_BYTES) return response(413, '{"status":"object-too-large"}');
    return response(200, await object.arrayBuffer(), "application/json");
  },
};
