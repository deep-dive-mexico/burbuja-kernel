# Developer workbench: an external host and plugin

[Kernel README](../README.md) · [Sample source](../examples/Example.Workbench/README.md)

`examples/Example.Workbench` is a buildable external-consumer starting point: its **own executable**
using `Burbuja.Hosting`, a **separately installed dynamic resource provider**, and a small
consumer-owned portal. Neither module nor `tests/Burbuja.Hosting.Tests` references AKOS source,
assemblies, or its executable. The tests inspect the compiled dependency closures too.

## Prerequisites and package status

- .NET SDK **10.0.400**, as pinned in the sample's `global.json`; **10.0.401** was tested.
  Running the framework-dependent publish requires the .NET 10 ASP.NET Core runtime.
- A **clean, pinned checkout of `deep-dive-mexico/burbuja-kernel`** containing
  `src/Burbuja.Hosting`. Commit `c4020370250dbf7989207cec172f9824ffb2db04` is the
  sanitized stable v0.6.1 runtime revision; a later compatible revision may be pinned instead.
  Record the full SHA in the consuming application's dependency documentation. The historical
  integration repository is not the supported source for generic changes.
- NuGet restore access for the source projects' dependencies. `curl` is used below; the external-copy
  recipe uses `rsync` (or copy the same source files with your platform's file manager). `gh` is
  needed only for the optional checkout recipe below.
- No Docker, model credentials, database service, sibling product checkout, or global template install.

**The 0.4.0 packages do not include `Burbuja.Hosting`.** The supported method demonstrated here is
project references against pinned source, with `-p:BurbujaRoot=/absolute/path`. This is not a claim
that Hosting or a complete consumer runtime has been published as NuGet packages. Published
contracts/SDK packages alone cannot replace the host composition dependency. A future package
recipe must identify actual available versions and compatible runtime dependencies before use.

`BurbujaRoot` is an MSBuild property, not a runtime setting. The shell variable `BURBUJA_ROOT` in
these commands is passed explicitly to that property. The sample supplies its own `Directory.Build.props`,
`Directory.Build.targets`, `Directory.Packages.props`, solution and SDK pin. The referenced kernel
projects continue using their own build policy and package pins.

## Pin the authoritative source and create an external consumer

Choose a fresh destination for the authoritative checkout, or start with your own clean checkout
of the same GitHub repository. Pin it to a full commit; this example uses the independently
tested runtime revision. Resolve the **physical** path before copying or building:

```bash
gh repo clone deep-dive-mexico/burbuja-kernel "$HOME/Developer/burbuja-kernel-pinned"
BURBUJA_ROOT="$HOME/Developer/burbuja-kernel-pinned"
git -C "$BURBUJA_ROOT" checkout --detach c4020370250dbf7989207cec172f9824ffb2db04
BURBUJA_ROOT="$(python3 -c 'from pathlib import Path; import sys; print(Path(sys.argv[1]).expanduser().resolve(strict=True))' "$BURBUJA_ROOT")"
git -C "$BURBUJA_ROOT" rev-parse HEAD
```

The full SHA printed must be the one your consumer is intended to use. In particular, do not
substitute a local integration checkout or an unpinned `main`. Copy **source only**, including
the sample's local build files and `global.json`, into a separate directory:

```bash
WORKBENCH="$HOME/Developer/my workbench"
mkdir -p "$WORKBENCH"
rsync -a --exclude bin/ --exclude obj/ --exclude artifacts/ \
  "$BURBUJA_ROOT/examples/Example.Workbench/" "$WORKBENCH/"
cd "$WORKBENCH"
WORKBENCH="$(pwd -P)"
dotnet build Example.Workbench.slnx -p:BurbujaRoot="$BURBUJA_ROOT"
```

The destination can have spaces. On macOS, `/var/...` may alias `/private/var/...` (and `/tmp`
may alias `/private/tmp`). An external build using the logical `/var/...` value for
`BurbujaRoot` failed with missing transitive abstractions references; resolving it with
`Path.resolve(strict=True)` to `/private/var/...` **before the first build** succeeded. Keep the
physical `BURBUJA_ROOT` for every build, test and publish invocation. `pwd -P` normalizes the
copied consumer's current directory; it does not by itself normalize `BurbujaRoot`.

To work in the kernel repository instead, use `examples/Example.Workbench` as the current
directory. There `BurbujaRoot` defaults to the repository root, so the override is optional;
an external copy must pass the pinned, physical source path explicitly.

## Build the executable and install the plugin

From the workbench directory, with `BURBUJA_ROOT` set:

```bash
dotnet publish Example.Workbench.Host/Example.Workbench.Host.csproj \
  -c Release -p:BurbujaRoot="$BURBUJA_ROOT" -o artifacts/host

dotnet publish Example.Workbench.Plugin/Example.Workbench.Plugin.csproj \
  -c Release -p:BurbujaRoot="$BURBUJA_ROOT" \
  -o artifacts/host/plugins/Example.Workbench
```

The install is the **entire plugin publish output**, under one immediate child of the host's
plugin directory:

```text
artifacts/host/
  Example.Workbench.Host.dll
  Example.Workbench.Host.deps.json
  Example.Workbench.Host.runtimeconfig.json
  appsettings.json
  ...host dependencies and static assets...
  plugins/
    Example.Workbench/
      plugin.json
      Example.Workbench.Plugin.dll
      Example.Workbench.Plugin.deps.json
      Example.Workbench.Plugin.runtimeconfig.json
      Example.Workbench.Plugin.pdb
```

Do not copy the kernel's contracts or SDK DLLs into the plugin folder. The two plugin project
references set `Private="false" ExcludeAssets="runtime"`; `EnableDynamicLoading=true` supplies its
runtime dependency metadata. The loader shares contract/SDK type identity with the host and loads
the provider assembly in its own load context. For this plugin the publish output contains no
`Burbuja.*.dll` files. There is no static fallback registration of the workbench provider.

The sample host's `RemoveStaticPluginPublishManifests` target filters only root-level `plugin.json`
publish items. Hosting currently brings several built-in static plugin projects, each with that
filename; without the filter a new consumer's publish fails with `NETSDK1152`. The installed dynamic
plugin manifest remains under `plugins/Example.Workbench/`. Other duplicate publish files still fail.

An installed manifest claims its plugin ID before the host considers a linked static factory.
A missing DLL therefore fails with `PluginBootException` (exit 78 via `RunAsync`), naming the
ID, manifest and missing DLL; it never silently falls back to the built-in. Two installs claiming
one ID fail with both locations named. The loader compares the disk manifest against the plugin's
code declaration before registering services, including requested capabilities, trust, roles,
dependencies, configuration section and assembly identity. Installing code declared `static` with
a `dynamic` disk manifest is supported: it loads in a plugin ALC with shared contracts and SDK.
Changing `plugin.json` alone cannot change what its assembly declares.

## Run and bootstrap over HTTP

```bash
dotnet artifacts/host/Example.Workbench.Host.dll --urls=http://127.0.0.1:18490
```

The executable uses its own assembly identity and `AppContext.BaseDirectory` as content root.
Settings and relative `Kernel:PluginsDirectory` paths are resolved there, even if launched from
another working directory. Default role is `control-plane`; the usual command-line settings are
available. Stop with Ctrl-C. Resources and events use in-memory kernel stores and disappear on restart.

These are **public development credentials** from the sample's `appsettings.json`:

| API key | Principal | Role / authority |
|---|---|---|
| `workbench-owner-dev-key` | `owner` | Built-in Owner; used to create tenancy |
| `workbench-reader-dev-key` | `reader` | Workbench Reader: note GET/list only |
| `workbench-editor-dev-key` | `editor` | Workbench Editor: note GET/list/PUT/DELETE and event reads |

In another terminal, create the subscription and resource group (each returns **201** on a fresh host):

```bash
BASE=http://127.0.0.1:18490
API='api-version=2026-09-01'
OWNER='x-api-key: workbench-owner-dev-key'
READER='x-api-key: workbench-reader-dev-key'
EDITOR='x-api-key: workbench-editor-dev-key'

curl -fsS "$BASE/health/ready"
curl -fsS "$BASE/health/plugins"
curl -fsS -H "$OWNER" "$BASE/providers/Example.Workbench?$API"

curl -fsS -X PUT -H "$OWNER" -H 'content-type: application/json' \
  "$BASE/subscriptions/workbench?$API" \
  -d '{"properties":{"displayName":"Workbench"}}'
curl -fsS -X PUT -H "$OWNER" -H 'content-type: application/json' \
  "$BASE/subscriptions/workbench/resourceGroups/demo?$API" \
  -d '{"location":"local","properties":{}}'

NOTES="$BASE/subscriptions/workbench/resourceGroups/demo/providers/Example.Workbench/notes"
curl -fsS -D - -X PUT -H "$EDITOR" -H 'content-type: application/json' \
  "$NOTES/hello?$API" -d '{"properties":{"text":"Hello from an external plugin"}}'
curl -fsS -H "$READER" "$NOTES/hello?$API"
curl -fsS -H "$READER" "$NOTES?$API"
```

The note PUT returns **201**, an ARM envelope, and an `ETag`; later changed PUTs return **200**.
Use that `ETag` in `If-Match` for conditional writes; stale versions return **412**. A repeated
identical PUT uses the kernel's idempotency response and does not append another trace event.
The provider descriptor declares GET, PUT, DELETE and list, with `text` required (1–1000 characters,
no additional properties). PATCH is deliberately undeclared and returns **405**.

Read the bounded JSON trace and verify its chain:

```bash
curl -fsS -H "$EDITOR" -H 'accept: application/json' "$NOTES/hello/events?$API"
curl -fsS -H "$EDITOR" "$NOTES/hello/events/verify?$API"
```

The first response contains a `workbench.note.saved` event with resource/principal IDs. The second
reports `verified: true`, `complete: true`, and `events: 1` for this fresh example. Each resource has
its own stream. Successful DELETE adds `workbench.note.deleted`; its trace remains readable after
the resource is removed. The trace omits note text. Store mutation and event append are separate
operations, so this minimal example does **not** promise atomic resource/event persistence.

The same declared route speaks SSE with `Accept: text/event-stream`; `Last-Event-ID` resumes after
the supplied sequence. `Declared_event_route_replays_sse_after_the_clients_last_event_id` tests that
transport with a bounded cancellation deadline, rather than waiting indefinitely for a quiet stream.

These two requests return **403** (omit `-f` so curl shows the ARM error):

```bash
curl -sS -D - -X PUT -H "$READER" -H 'content-type: application/json' \
  "$NOTES/hello?$API" -d '{"properties":{"text":"forbidden"}}'
curl -sS -D - -H "$READER" -H 'accept: application/json' "$NOTES/hello/events?$API"
```

## What a plugin developer implements

1. **`WorkbenchPlugin`** implements `IBurbujaPlugin` and registers the provider, typed handler,
   event-stream resolver and role contributor. Its manifest matches `plugin.json`. It requests no
   elevated capabilities, so it needs no addition to `Kernel:TrustedPlugins`.
2. **`NotesProvider`** supplies `IResourceProvider` descriptors using `DescriptorBuilder`. Despite
   the `Burbuja.Core.Arm` namespace, that builder and `EnvelopeHandler<T>` live in **Core.Sdk**, not
   the Core implementation assembly. The plugin references only **Core.Abstractions + Core.Sdk**.
3. **`NotesHandler`** extends the SDK envelope handler and uses `IResourceStore`, `IClock` and
   `IEventLog`. The kernel supplies tenancy, authentication, authorization, schema validation,
   ETags, idempotency, routing, JSON event paging and SSE transport.
4. **`WorkbenchRoles`** contributes explicit actions. The event route uses
   `Example.Workbench/notes/read` in **DataActions**; ordinary GET/list uses the same string in
   **Actions**. A Reader's control-plane permission does not grant event access. Editor cannot
   create subscriptions or administer other kernel resources.
5. **`NoteEventStreams`** maps the resource identity to a namespace-owned stream ID. It strips the
   action/data-plane suffix so `/notes/hello` and `/notes/hello/events` name the same stream.

`roles: ["all"]` in a plugin manifest means **every host role**, not just a host configured as `all`.
The workbench provider can therefore load on workers; Hosting mounts its ARM/portal routes only on
control-plane/all hosts. Disabling it with `--Plugins:Example.Workbench:Enabled=false` removes its
provider and roles. An absent provider is reported as HTTP **400 `NoRegisteredProviderFound`**.
An installed manifest with a missing DLL, incompatible contract version, or wrong entry type fails
boot with the plugin named; `RunAsync` reports plugin boot failures with exit code 78.

## Consumer-owned portal

Open <http://127.0.0.1:18490/workbench>. Sign in as **`owner` / `owner-dev-password`**. The portal is
a small landing/sign-in/sign-out example; use the HTTP recipe to edit notes.

`WorkbenchHost.Options` calls `AddPortal(new PortalScope(...))` through
`BurbujaHostOptions.ConfigureBuilder`, **before core/plugin and portal boot**. It contributes the
executable's Razor assembly, layout, scope and default landing route. The generic registry supplies
route policies and the existing cookie sign-in/sign-out handlers. The example account grants only
the `workbench` portal claim: even its Owner API role cannot open `/portal` through that browser
login. The executable references no product-specific portal. Its own Razor pages cause the Web SDK
to include the ASP.NET web assets; the published Blazor script and portal CSS were fetched successfully.

`/portal/download?path=…` is a shared browser-to-ARM transport for authenticated portals, even
when their users cannot open the kernel console. It validates that `path` is local and sends the
read with the live session's API key; the kernel still denies data-plane routes that key may not
read. Anonymous requests cannot use the proxy. The Hosting HTTP fact covers all three outcomes.

Hosting currently includes the built-in agents/compute/deployments/integrations/MCP composition.
This sample proves consumer independence from AKOS and from the shipped `Burbuja.Host` executable;
it does not claim that Hosting is a bare-Core-only dependency.

## Bounded tests and evidence

Run from the pinned kernel source checkout. The official `make test` already builds the solution
and runs this project alongside the other selected generic unit projects; to run only the Hosting tests:

```bash
dotnet test tests/Burbuja.Hosting.Tests/Burbuja.Hosting.Tests.csproj \
  --logger 'trx;LogFileName=hosting-workbench.trx'
```

The project builds its own fixtures and stages the plugin as a build-only project reference
(`ReferenceOutputAssembly=false`). Tests never instantiate or compile against the plugin's types.
The in-process tests use the public Hosting API, the sample's own executable identity/settings,
the real dynamic loader, and TestServer for HTTP. Another fact launches the actual entry point
under Kestrel on an atomically allocated loopback port, from an unrelated working directory with
spaces in the install path. Each test uses isolated temporary state and disposes its host/child.

**Independent proof on 2026-09-27 from a fresh private GitHub clone at
`c4020370250dbf7989207cec172f9824ffb2db04` (not the integration tree):**

- A separate copy of `examples/Example.Workbench` in a path **with spaces** built with the
  physical, pinned `BurbujaRoot`: **0 warnings, 0 errors**. Release-published the host and
  plugin independently, then installed the plugin beneath the published host.
- Official Hosting test project: **12 passed, 0 failed, 0 skipped** (the targeted
  `dotnet test` command above, run against that clone).
- Verified dynamic ALC and shared contract identity; compiled host/test dependency closures exclude
  AKOS and the plugin; manifests, CRUD/list, ETags, rejected schema/versions/verbs, role boundaries,
  isolated event traces, idempotent replay, chain verification, SSE resume, corrupt installs,
  disabled-plugin boot, worker routing, and actual portal cookie access/sign-out.
- The **installed** executable served **27/27 real-Kestrel HTTP checks**: readiness, plugin
  health/discovery, tenancy bootstrap, CRUD/list, RBAC denials, ETag update and stale rejection,
  isolated event trace/chain, cookie session login/revocation and published CSS/JS assets.
  Editing only the external consumer's Razor page, then rebuilding and re-publishing it,
  appeared in the installed HTTP response without editing kernel source. The pinned clone's
  git status remained clean. Published host/plugin `.deps.json` libraries contained no AKOS
  entries; the dynamic plugin folder contained no `Burbuja.*.dll`.

This bounded evidence does not cover a full browser interactive circuit, a durable store, a restart
migration, or a published Hosting package.
