# Runtime capabilities and expansion seams

[Kernel README](../README.md) · [External Workbench](developer-workbench.md)

Source inspection: `19bc47254cebfc633230efff218b56fac5124240`, 2026-09-29.
This guide recovers the useful navigation from the integration tree's runtime,
compute, MCP and integrations guides. The historical deployment commands and
test counts are not current kernel instructions. Paths below are relative to this repo.

## What a consumer can already build on

| Capability | Current implementation | Existing verification surface |
|---|---|---|
| Independent executable and dynamically installed resource providers | `src/Burbuja.Hosting/BurbujaApplication.cs`, `BurbujaHostOptions.cs`; `Core/Plugin/PluginHost.cs` | `Hosting.Tests/ExternalPluginTests`, `ExecutableSmokeTests`; Workbench |
| ARM resource lifecycle, authorization, schemas, ETags, named actions and asynchronous operations | `src/Burbuja.Core/Arm/`, contracts in `Core.Abstractions/Arm/`, SDK handlers in `Core.Sdk` | `tests/Burbuja.Core.UnitTests`; Workbench HTTP CRUD/RBAC/SSE cases |
| One-shot jobs with single-worker, plan-fan-out and team-board strategies | `src/Burbuja.Agents/Runtime/Strategies.cs`, `Orchestration.cs`, `TeamBoard.cs` | `tests/Burbuja.Agents.UnitTests`, `tests/scenarios` |
| Stateful agents with inboxes, fenced activation, forks, interrupt/resume and tool loops | `Agents/Runtime/Activation.cs`, `AgentActivationWorker.cs`, `AgentTurnRunner.cs`, `SessionLog.cs` | Agents units; generic black-box tests currently also retained in the separate consuming product |
| Shared context and peer-aware tools | `plugins/Burbuja.Plugins.Tools.Agents/AgentTools.cs`, `Agents/Runtime/Adjacency.cs`, policy classifiers | Agents and Core units |
| Real token-cost booking and provider selection | `Agents/Runtime/DurableBudgetLedger.cs`, `LlmProviderRouter.cs`, `Support.cs`; `Agents.Abstractions/Llm/` | Agents units; pricebooks/provider-specific units |
| Persistent resources, events and artifacts | `plugins/Burbuja.Plugins.Store.Sqlite/` | `tests/Burbuja.Store.UnitTests`; consuming-host restart tests |
| Compute workspaces and execution environments | `src/Burbuja.Compute/Backends/`, `Runtime/ComputeExecutor.cs`; agent workspace binding in `Agents/Runtime/WorkspaceInitializer.cs` | `tests/Burbuja.Compute.UnitTests` |
| Policy-gated shell/workspace/HTTP tools, approvals and integration calls | `Agents/Runtime/Tooling.cs`, `GateSubjects.cs`, `plugins/Burbuja.Plugins.Policy.Core/`, `src/Burbuja.Integrations/IntegrationHandler.cs` | Core/Agents/Integrations units |
| MCP server for external drivers | `src/Burbuja.Mcp/`, `src/Burbuja.Mcp.Abstractions/` | `tests/Burbuja.Mcp.UnitTests` |
| Mail, browser bridge and Notion callers | `src/Burbuja.Integrations/{MailIntegration,BrowserIntegration,NotionIntegration}.cs` | `MailIntegrationTests`, `BrowserBridgeTests`, `NotionIntegrationTests`, `IntegrationCallTests` |
| Operator portal and replaceable consumer portal | `src/Burbuja.Portal/`; `BurbujaHostOptions.ConfigureBuilder`, portal registrations and Razor assemblies | Workbench `Consumer_portal_uses_its_own_assembly_and_real_cookie_policy_without_kernel_portal_access` |

An agent job and a stateful agent are different lifecycles. A job starts via its
workspace/job resource; creating an agent is inert and `send` starts work. Session
history derives from events. `driver: external` drains messages without making a
model call: an external driver owns the calls through MCP. Keep these distinctions
when building a consumer instead of writing another queue/session engine.

## Boundaries worth reusing

- **Host composition:** use `ConfigureBuilder` and declared plugin contributions.
  `AdditionalStaticPlugins` is not a grant of trust. A staged manifest takes precedence
  over a static fallback, and damaged staged installs must fail visibly.
- **Trace:** resource events and agent session events already exist. A consumer may
  project them into an understandable account of a task; it should not infer execution
  from the existence of a planned step. Domain-specific judgments belong in the consumer.
- **Tools:** route calls through the existing policy/approval gate. Declared integration
  tools are authority; discovered tools are a report of availability. Approval is scoped
  to the particular decision, including arguments, rather than blanket permission.
- **Provider adapters:** implement the model contracts and pricing seam; register them
  explicitly. The shipped generic paid adapter is DeepSeek. Other providers in consumers
  are candidates for later independent plugins, not dependencies to reach across for.

## Verified gaps, not lost finished capabilities

1. **Generic MCP client integration:** `McpStdioClient` implements bounded stdio
   exchanges and is used by the browser caller, but there is no general `mcp` caller.
   `Integrations.cs` declares the kind; the handler still needs a caller plus an explicit
   server/tool namespace contract. Next proof: fake-server discovery does not grant
   authority, a declared tool can be called through the gate, altered arguments cannot
   reuse an approval, and cancellation reaps the process. Long-lived sessions/HTTP
   transport need separate lifecycle work. The legacy code had this same gap.
2. **Interrupted job recovery:** `JobRecoveryHandler.RecoverAsync` checks cancellation
   and otherwise does nothing; strategy recovery returns empty work. This is distinct
   from stateful agent resume and from SQLite durability. Next proof: kill after a worker
   result, restart from durable state, settle once without duplicating a tool side effect.
3. **Completion-to-report producer:** the Notion report sink exists; orchestration does
   not make that a verified report-delivery workflow. Choose a consumer-owned explicit
   action/trigger with intent, approval, delivery and destination-readback receipts.
4. **Standalone regression ownership:** the full historical Host/Portal/E2E suites were
   retained by the consuming product. Extract product-neutral cases using Workbench/TestKit
   before claiming that kernel CI alone protects all operator UI/auth/durability journeys.
5. **Published host package:** source-pinned composition works; the 0.4.0 packable set
   does not include Hosting. Packable metadata does not prove a package was published.

## Recovered packaging gate

The legacy `PackagingTests.cs` was omitted during extraction because it named a
private contract and the old repository URL. Its reusable rule is restored in
`tests/Burbuja.Core.UnitTests/PackagingTests.cs`: exactly eight explicit public
artifacts opt into packing; default is non-packable, with the authoritative kernel
repository and MIT metadata. No consumer contract is pulled into the kernel.

Receipt on base `19bc47254cebfc633230efff218b56fac5124240` plus this uncommitted
test/documentation change, 2026-09-29:

```sh
dotnet test tests/Burbuja.Core.UnitTests/Burbuja.Core.UnitTests.csproj \
  -c Release -m:1 --filter FullyQualifiedName~PackagingTests --nologo
```

**2 passed, 0 failed, 0 skipped.** This verifies source packaging policy; it is not
a NuGet publication, runtime change, or new consumer dependency pin.
