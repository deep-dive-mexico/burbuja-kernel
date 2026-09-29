# Generic Burbuja knowledge map

The **generic runtime** is the source of truth for reusable mechanisms. `src/Burbuja.Core.Abstractions` and `src/Burbuja.Core.Sdk` define shared contracts; `Core`, `Agents`, `Compute`, `Deployments`, `Integrations` and `Mcp` implement reusable capabilities; `Hosting` composes a server, while generic Host/Portal are independent entry points. `Burbuja.TestKit` and `examples/Example.Workbench` exercise consumer use without AKOS source. Check actual project references and tests before inferring an allowed dependency.

| Change concerns | Open |
|---|---|
| Build, test selection, packaging/export boundary | [README](../../README.md), `export-manifest.json`, `Makefile` |
| Locating an existing runtime mechanism (jobs, stateful agents, tools, integrations, MCP, trace) or an expansion gap | [Runtime capabilities](../runtime-capabilities.md) |
| Independent consuming host, plugin and auth | [Developer workbench](../developer-workbench.md), [example README](../../examples/Example.Workbench/README.md) |
| Plugin manifest, dependency closure and failure diagnostics | [Plugin portability review](../plugin-review-2026-09-27.md), affected `plugins/*/plugin.json` |
| Product behavior requested by the AKOS client | The separate private `akos-server/docs/wiki/OVERVIEW.md` and `akos-docs/docs/wiki/source/requirements.md` when working from the shared workspace |

**Ownership test:** could an independent consumer use this concept without an AKOS assembly, persona, brand, fixture or private data? If yes, it is a candidate kernel mechanism. Product policy and presentation sit above it. The product pins the kernel's **full clean commit** in its own `kernel.lock.json`; see its `docs/developing-akos.md` for the upgrade procedure. The kernel's own tests and Workbench are its local proof, not the product's scripted client acceptance.

**Policy corpus portability, 2026-09-28:** at kernel HEAD `d59a14c`, the uppercase credentials-path case in `tests/Burbuja.Core.UnitTests/Policy/corpus/commands.jsonl` now uses a synthetic account name. `dotnet test tests/Burbuja.Core.UnitTests/Burbuja.Core.UnitTests.csproj --filter FullyQualifiedName~PolicyCorpusTests --no-restore` passed 313/313 against the current working tree. This checks the policy corpus, not the full kernel or AKOS host.
