# Outbound MCP and interrupted jobs

This change targets `dev`. `main` remains the production release/deployment branch. Consumers keep their own immutable released-kernel pins; this feature does not advance them.

## Acceptance contract

- A generic MCP integration can discover and call remote tools over stdio or Streamable HTTP.
- Discovery reports availability without expanding declared authority. Calls require resource authorization, an enabled integration, a declared tool and policy permission; unknown tools require approval.
- Names are `mcp__<serverName>__<remoteTool>`. A server name is explicit and cannot contain the namespace separator.
- Transport errors, deadlines, cancellation and output bounds have observable outcomes. Authentication values are resolved from secret references rather than stored in resource documents.
- A persisted queued/running job resumes after restart with stable run identity. Single-worker and plan-fan-out plans preserve transcripts, completed workers and pending approvals.
- A completed tool effect is reused from its durable receipt. An effect whose execution began but has no durable result is blocked for review and is never repeated automatically.
- Job orchestration owns a renewable lease. Lost execution ownership cannot overwrite a newer owner's checkpoints.
- Terminal state, operation settlement and result events can be reconciled after interruption. Paused, cancelled and settled jobs must not be restarted as new work.
- Recorded model responses survive interruption. An interrupted request without a recorded response has unknown usage; recovery retries it at most once, marks cost partial, and reports that uncertainty in the console.
- Generic units, independent Workbench consumption, public HTTP, owned-process restart and Chromium journeys run in CI. Skipped or empty E2E lanes fail.
- Product compatibility runs separately from production adoption. No live host, production state or released consumer pin is changed by these tests.

## Declare a server

Create a `Burbuja.Integrations/integrations` resource:

```json
{
  "properties": {
    "kind": "mcp",
    "serverName": "reports",
    "enabled": true,
    "tools": ["mcp__reports__create_report"],
    "transport": {
      "mode": "stdio",
      "command": "/absolute/path/to/server",
      "args": ["--stdio"],
      "environmentSecretRefs": {"REPORT_API_TOKEN": "report-token"}
    }
  }
}
```

For HTTP use `{"mode":"http","url":"https://example.test/mcp","secretRef":"report-token"}`. `secretRef` supplies bearer authentication through `ISecretResolver`; stdio resolves its environment-variable mappings through the same interface. Missing references refuse the connection. Ordinary parent secrets are not inherited by a generic stdio server.

`POST <resource>/probe` records server-qualified names. `POST <resource>/call` takes `{"tool":"mcp__reports__create_report","input":{...}}`. When policy requires a human, it returns an approval ID; retry the same call with that ID after approval. An approval for different executable arguments does not authorize the retry. MCP content and structured content are preserved.

Each action owns and disposes its connection/process. This is bounded outbound tool calling, not automatic import into the agent tool catalogue or a persistent browser/session manager. Sampling, elicitation and arbitrary server-to-client requests are not delegated to a model.

Configuration lives under `Integrations:Mcp`: `Timeout`, `MaxLineChars`, `MaxStderrChars`, `MaxTools`, and `MaxListPages`. HTTP redirects are refused and response bodies are bounded before decoding. Cancellation reaps the stdio child.

## Durable continuation

`JobProperties.Recovery` holds the selected plan, stable worker/merger IDs and per-run checkpoints. `JobStateStore` presents the executing run's state while preserving the aggregate job; optimistic-concurrency conflicts reread and reapply mutations.

`JobOrchestrator` owns the job lease and renews it during model/tool waits. The recovery sweep republishes eligible persisted jobs only when execution ownership has expired, and an approval wait only after an applicable decision exists. SQLite (or an equivalent durable adapter for resources, leases, outbox and approvals) is required to survive process loss; default in-memory storage cannot provide restart durability.

The tool journal writes an intent before the external call and a result before returning to the runner. The journal's identity includes the run, tool-use ID, executable arguments and workspace. A recorded result avoids a repeated side effect. An incomplete intent is uncertainty, including the interval between an external effect and recording its result: the job becomes `blocked` with `InterruptedToolExecution`. Review/read back the external destination before deliberately starting new work.

A model response is checkpointed before charging and applying its transcript/tool batch. Durable budget bookings accept a stable ID and original UTC timestamp for recovery. Usage from an interrupted unrecorded response remains explicitly unknown; `costIsPartial` is not a claim of exact billing or a distributed reservation-based spending cap.

The task board and stateful agents are separate lifecycles. This job recovery contract covers the shipped `single-worker` and `plan-fan-out` strategies; custom strategies must provide their own durable continuation.

## Verification

```sh
make test                 # generic units and independent consumer
make test-http            # public ARM, agents, compute, inbound/outbound MCP
make test-own-host        # SQLite, ledger and interrupted-job hard restarts
make browser-install
make test-browser         # sign-in, permissions, forms, routes and appearances
make verify              # unit and all E2E lanes
```

`scripts/test-e2e.py` creates isolated hosts and records the source commit, dirty status, lane filters, TRX counters and failures under `artifacts/e2e-runs/`. It never attaches to a deployed endpoint. Browser screenshots and child logs are retained separately. Fixture passwords/API keys are public synthetic test values.

The independent AKOS repository keeps its normal full gates against its unchanged release pin. Its candidate-compatibility harness additionally tests a clean committed kernel candidate using a disposable product source snapshot that refuses production-host publication. That evidence is distinct from released-product validation and manual acceptance.

Proof receipts and final counts are recorded after running the complete gates; no live-model, real third-party delivery, production rollback or human acceptance is implied by scripted fixtures.
