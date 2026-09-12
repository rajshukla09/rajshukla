# Trace the decision path

An agentic request can cross model calls, tool executions, retrieval operations, and delegated agents. A single duration metric cannot explain where the system spent time or why it made a decision.

## Useful span boundaries

- One span for each model invocation.
- One span for each tool or external dependency call.
- A parent span for each agent or workflow step.
- Events for approvals, retries, and routing decisions.

Record model, token usage, latency, and outcome using semantic conventions where available. Avoid recording sensitive prompt or tool data by default.

```csharp
using var activity = source.StartActivity("agent.plan");
activity?.SetTag("agent.name", "planner");
activity?.SetTag("workflow.run_id", runId);
```

Good telemetry connects a user-visible outcome to the exact execution path that produced it.
