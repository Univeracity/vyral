# Cloudflare R2 object qualification

The R2 object adapter remains preview. On 2026-10-03 a bounded live run passed
8 live checks against a newly created isolated bucket, using short-lived credentials
scoped to that bucket. No existing objects or buckets were used. Retained raw
test receipts and account details are private operator evidence.

The supported subset covers content/metadata/ETag round trips, no-replace and
matching/nonmatching replacement preconditions, portable names and metadata,
pagination (including empty listings), idempotent unconditional deletion, and a
pre-cancelled write that leaves no object. Nine local transport checks additionally
cover a 503 failure without automatic retry and cancellation before dispatch.

The live run found that R2 accepted a delete with a mismatched If-Match value and
deleted the object. Vyral now refuses every conditional R2 delete before sending
it, including a matching value; a HEAD followed by DELETE would introduce a race.
The live check confirms both refusals retain the object, followed by successful
unconditional deletion. This does not qualify R2 for the complete portable
IObjectStore contract, which includes conditional deletion.

The AWS SDK v4 can return a null S3Objects collection for an empty listing.
The shared S3 implementation now treats it as empty and disposes read responses.
R2 sets MaxErrorRetry=0 by default (explicitly configurable from 0 through 3),
and accepts optional session credentials. A caller must reconcile effects after
a dispatched request fails or is cancelled; pre-cancellation is the only live
cancellation case qualified here.

Reproduction requires VYRAL_CLOUDFLARE_ACCOUNT_ID, VYRAL_R2_ACCESS_KEY_ID,
VYRAL_R2_SECRET_ACCESS_KEY, optional VYRAL_R2_SESSION_TOKEN and VYRAL_R2_BUCKET,
then dotnet test tests/Vyral.Tests.Cloudflare/Vyral.Tests.Cloudflare.csproj.
Use an isolated bucket and temporary credentials. The tests use random prefixes
and delete their own objects. Account identifiers, bucket names and secrets must
stay outside public receipts.

Cloudflare documents supported conditions for object reads/writes, without
conditional DeleteObject support, in its [S3 API matrix](https://developers.cloudflare.com/r2/api/s3/api/).
Its [temporary credentials guide](https://developers.cloudflare.com/r2/api/s3/temporary-credentials/)
describes the bucket/object permissions used by this qualification.
