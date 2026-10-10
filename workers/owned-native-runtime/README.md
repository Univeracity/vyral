# Owned native runtime lifetime

This Linux owner entry point reuses `CliProviderTargets.CreateWorkspaceCodingAgent`
and its existing Bubblewrap/process lifetime engine to serve one pinned native
runtime until an immutable deadline. It does not admit work, call a model, retry
requests, sign evidence or implement a second service controller. The private
IPC coding worker remains a separate network-isolated profile.

Build the entry point, then launch its DLL directly as the owner's child:

```text
dotnet build workers/owned-native-runtime/owned-native-runtime.csproj --output /opt/vyral/owned-native-runtime
dotnet /opt/vyral/owned-native-runtime/owned-native-runtime.dll --profile /private/attempt/owned-native-profile.json
```

The closed profile contains exactly:

```json
{
  "schemaVersion":"vyral.owned-native-runtime.v1",
  "nativeProfileFile":"/private/attempt/native-profile.json",
  "nativeProfileSha256":"sha256:<exact native options file digest>",
  "nativeCommand":"/opt/native/bin/llama-server",
  "nativeCommandSha256":"sha256:<exact executable digest>",
  "nativeBindingId":"sha256:<public LlamaCppRuntimeSessionFactory.BindingId>",
  "runtimeLibraryDirectory":"/opt/native/bin",
  "runtimeFiles":[{"path":"/opt/native/bin/llama-server","sha256":"sha256:<exact digest>"}],
  "runtimeReadOnlyPaths":["/lib","/lib64","/usr/lib/x86_64-linux-gnu"],
  "controlWorkspaceRoot":"/var/lib/vyral/attempt-control",
  "stagingRoot":"/var/lib/vyral/attempt-staging",
  "bubblewrapCommand":"/usr/bin/bwrap",
  "deadlineUtc":"<immutable owner deadline in UTC>",
  "maxExecutionSeconds":60,
  "maxOutputBytes":65536
}
```

Use real hashes and include every file in the runtime library directory,
including library aliases. Aliases must resolve inside that directory. The
entry point verifies the command, every runtime file, model bytes, native
options file and public BindingId before starting. The native options file is
the exact ten-field public `LlamaCppRuntimeOptions` camelCase profile. Its
literal loopback endpoint selects the listening address/port; its context is
at most 4096 tokens. The native adapter independently revalidates runtime
properties, effective template and template probe before each completion.

Prepare a dedicated clean Git control repository containing `control/`, outside
all product workspaces. Keep the staging directory outside that repository.
The native process receives only read-only runtime/model mounts, an isolated
staged control workspace and a bounded temporary directory. Host environment
inheritance is cleared. It starts with one slot, no warmup, no context shift,
no web UI, local model bytes, CPU execution and explicit prediction/context
settings. Its owner profile uses host loopback networking; model workers retain
their separate private IPC transport and network denial. No model generation
occurs merely by starting this entry point.

Pin the entry point, profile and runtime closure in the owner attempt manifest.
Set `deadlineUtc` no later than the immutable admitted task deadline. The
deadline is checked before hashing/loading and its cancellation token includes
preparation. It cannot be renewed by a retry. Pass the same absolute task expiry
to the owner workflow, and bound workspace-job timeout to the remaining whole
seconds before its public admission POST. Keep one serialized owner attempt;
this entry point does not coordinate multiple owner processes or replicas.

The entry point binds itself to its direct parent with Linux `PR_SET_PDEATHSIG`,
including a parent-change race check. Bubblewrap binds its PID namespace to the
entry point with `--die-with-parent`. Killing either owner process terminates
the owned native namespace; deadline or Ctrl-C cancellation uses the existing
process runner's teardown. There is a two-second teardown grace and best-effort
filesystem cleanup. A response-return wall bound is not promised. Abrupt death
can leave an isolated staged directory for owner cleanup. A directly launched
child is required: a detached launcher changes the owning-parent boundary.

The workspace host's `lifetime-v3` process runner keeps the thread that starts
each child alive through execution and cleanup. Linux parent-death signals
track that creating thread; retiring an asynchronous caller thread must not
terminate a child while the owner process is alive. This changes the host
adapter identity and requires consumers to requalify their exact source/build
profile. Historical `lifetime-v2` acceptance is unchanged.

The first JSON line says `starting`, with exact profile/native digests and
deadline. A `ready` line follows only after the public
`LlamaCppRuntimeSessionFactory.OpenAsync` validates model bytes, live properties,
actual template and rendered probe against the exact native profile. This
check generates no tokens. Wait for that line and check the public Vyral
provider descriptor before dispatch. The final line says `stopped`; normal lease expiry is `TimedOut` or
`Cancelled`. An unexpected native exit is failure. Owner death may yield no
final line. Preserve the original gateway invocation and inspect it by the same
key; process termination does not authorize resending or invent unknown usage.

Native completion failures retain a safe diagnostic in the public result's
`rejection.category`, including `native_transport_failed`, `native_io_failed`,
`native_json_invalid` and `native_response_shape_invalid`. Its fixed message
identifies the failing stage without exposing exception, prompt or response
text. `native_effect_unresolved` remains the provider status after an uncertain
dispatch, and the diagnostic says `do_not_retry`. Invalid completion response
shapes do not produce token measurements. A lost completion response does not
prove that generation did or did not occur.

This provides a reusable development lifetime boundary. Production custody,
multi-host containment, independent task evaluation and metering remain separate
owner qualifications. The native runtime's unauthenticated loopback endpoint
assumes a trusted local host; untrusted local processes require additional
operator isolation. It is never mounted into the coding worker.
