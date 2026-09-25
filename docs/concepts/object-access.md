# Object HTTP access policies

`/objects/{container}` and `/objects/{container}/{key}` can enforce a verified
workload identity for `list`, `read`, `write`, and `delete`. Configure
`Server:ObjectAccess:IdentityPolicies` to activate this boundary. Once any
policy is configured, **every object HTTP request** needs a matching identity,
container, key prefix, and operation. The generic server API key is an
additional host-level check, not an object policy. With no object policies,
the routes retain their existing host-level behavior for compatibility;
shared multi-tenant deployments must configure object policies before exposure.

Each `AllowedKeyPrefixes` value must end in `/`. This is a key-segment
boundary: `tenant-a/` permits `tenant-a/song.wav` but never `tenant-aa/song.wav`.
Listing requires a supplied prefix inside a permitted prefix; a container-root
list is denied. The explicit `*` prefix grants the whole container and is
intended only for trusted administrative workloads. It must appear alone.
Returned list entries are checked against the requested container and prefix
before any metadata is sent to the caller, including continuation pages.
Read and write responses are also checked against the requested object key.

For example, one service account can access one tenant's masters:

```text
Server:ObjectAccess:AuthenticationMode=google-oidc
Server:ObjectAccess:AllowedAudiences:0=https://vyral.example.com
Server:ObjectAccess:IdentityPolicies:0:Principal=app@your-gcp-project.iam.gserviceaccount.com
Server:ObjectAccess:IdentityPolicies:0:Container=publisure-masters
Server:ObjectAccess:IdentityPolicies:0:AllowedKeyPrefixes:0=tenant-a/
Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:0=read
Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:1=list
Server:ObjectAccess:IdentityPolicies:0:AllowedOperations:2=write
```

Add separate entries for additional containers or worker identities. A read-only
worker needs only `read`; a producer may need `write`, `list`, and `read` to
verify uploads. Omit `delete` unless required. The principal is the verified
email in a Google OIDC ID token with an allowed audience. If the generic API
key is enabled, send it in `X-Vyral-Api-Key` and the ID token in
`X-Serverless-Authorization`.

The `development-header` mode is accepted only in `Development`. To use another
identity provider, implement `IObjectIdentityAuthenticator` with a public
parameterless constructor and configure `Server:ObjectAccess:AuthenticatorType`.
The authenticator returns a stable principal; the same object policy applies.

Object policies protect the public object HTTP routes. They do not authorize
internal `IObjectStore` calls made by other Vyral services, or establish
CanonicalStore or execution policies. Configure and test those boundaries
separately for a shared service.
