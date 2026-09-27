# Burbuja — reusable kernel source

The initial history-free tree is a **one-time migration** of the generic source: Core and SDK,
resource-provider libraries, reusable Hosting, generic Host and Portal, independent plugins,
public TestKit, and the standalone Workbench example. After the official repository's first
commit, it is the only source of truth for generic changes and its own committed HEAD supplies
every normal release. The host accepts consumer contributions and does not reference AKOS.

The [plugin portability review](docs/plugin-review-2026-09-27.md) records the installed-manifest,
failure-diagnostic and authenticated-transport regressions exercised in the official kernel.

```sh
dotnet build Burbuja.slnx -c Release -m:1
make test
dotnet run --project src/Burbuja.Host -- --role all
```

`examples/Example.Workbench` is an independent consuming host/plugin example. The
[developer workbench guide](docs/developer-workbench.md) covers its external-copy, pinned-source,
install and HTTP procedures. Hosting.Tests exercises that composition. NuGet restores use
nuget.org, not a developer's local package feed.

## Test coverage boundary

The generic Core, Agents, Compute, Deployments, DeepSeek, Store, HostFacts, Integrations, MCP and
Hosting test projects are selected explicitly, together with their test plugin/tool sources.
`export-manifest.json` lists the exact project/file inventory and exclusions.

The historical mixed Host, Portal and E2E suites belong to the private product export until they are
split by ownership. They are **not** claimed as retained generic regression coverage. The old packaging
test names a private contract and is omitted. Product scenarios are omitted from the public tree.

The HostFacts fixture set is generated from `distribution/kernel/fixtures/hostfacts.json` in the
integration checkout, not copied from captured device output. It uses invented node/account labels,
documentation IPv6 addresses, short battery-gauge examples and synthetic filesystem rows. All parser
and provider tests remain, with expectations adapted to the synthetic values; `fixtures/PROVENANCE.md`
describes the exported examples. Source line-end blanks on reviewed Razor attribute lines are trimmed
during export; other `.cs`/`.razor` trailing whitespace is refused for review rather than silently
changing a raw string or rendered text.

## Publication boundary

After main's final source/license review and publication, the authoritative generic source lives in
`deep-dive-mexico/burbuja-kernel`; new generic changes go there. The private product consumes a
**clean checkout pinned to a full commit**, not an imagined Hosting NuGet package. The original
integration repository and its Git history remain historical/private. Do not change its visibility
as a substitute for exporting. The exporter neither commits nor publishes.

`.github/workflows/ci.yml` checks official committed source, builds the solution and Workbench,
and runs generic units. In the authoritative repo, `python3 scripts/release-source.py --check`
validates its own HEAD; `--destination ../fresh-release-snapshot` creates a fresh release from
tracked source only, with its own SHA. Neither command reads the legacy integration checkout.
The migration command in that old checkout is `python3 scripts/export-products.py kernel
--migration --destination ../burbuja-kernel-fresh --final`; it is not a release command.
