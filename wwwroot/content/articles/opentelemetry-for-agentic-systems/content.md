# OpenTelemetry for Agentic Systems

A conventional HTTP request often follows a short, predictable path. An agentic request can expand into a workflow of model invocations, routing decisions, specialist work, tool or retrieval calls, retries, approval waits, and remote services. A final response time or error log tells you that something happened; it rarely tells you which branch ran, where time was spent, or why downstream work never started.

Distributed tracing provides that missing execution narrative. This article uses the release-review sample from *Building AI Agents with .NET — Part 2*. It enables native Microsoft Agent Framework (MAF) workflow tracing, connects those activities to ASP.NET Core request telemetry, and exports traces through OTLP to a standalone Aspire Dashboard.

The sample is deliberately deterministic. Its Security, Quality, and Architecture components are MAF executors, not `AIAgent` instances, and it contains no model, retrieval, approval, persistence, or tool call. That reproducible graph makes the tracing behavior easy to verify. The same observability principles apply to a larger agentic system, but telemetry for operations not present here must be instrumented by the component that performs them.

## Why agentic execution is difficult to observe

Agentic systems add both breadth and time to a request. One user action may create several agent calls, and an agent may invoke tools whose own network requests cross process boundaries. A workflow can fan out, wait for several results, retry a dependency, pause for review, or remain active long after the initiating request ends.

That creates questions a single log line cannot answer:

- Which workflow and route handled this request?
- Which agents, executors, or tools actually ran?
- Did a fan-in wait for an unfinished branch, or did a branch fail?
- Was latency in orchestration, model inference, retrieval, or an external dependency?
- Was an operation attempted again, and did the retry repeat an effect?
- How much of the workflow completed before it stopped?

OpenTelemetry gives applications a vendor-neutral way to produce telemetry. For tracing, an end-to-end operation is a trace; each timed unit of work is a span; attributes describe that span; and parent-child context connects the units into an execution path. It does not infer business meaning. Useful correlation depends on instrumenting the right boundaries and propagating trace context between them.

## The trace demonstrated by the sample

The running example accepts a release description, classifies it deterministically as normal or high risk, and chooses a conditional route. Both routes fan out to three reviews. A fan-in barrier releases the final decision only after Security, Quality, and Architecture have all emitted a finding.

```text
ASP.NET Core request span
  → MAF workflow/session/run spans
    → ValidateRelease executor span
      → conditional edge-group and message spans
        → ExtraReview or ContinueReview executor span
          → fan-out edge-group and message spans
            → SecurityReview executor span
            → QualityReview executor span
            → ArchitectureReview executor span
              → fan-in edge-group and message spans
                → ReleaseDecision executor span
```

This hierarchy exposes orchestration behavior rather than only method timings. Native activities identify workflow build, session and run activity; `executor.process` spans identify executor boundaries; `edge_group.process` spans expose routing and fan-in behavior; and `message.send` spans describe message delivery. In the fan-in path, an `edge_group.delivery_status` attribute can show that an input was buffered while the barrier waited for the other sources.

There are no agent-invocation, tool, retrieval, approval, retry, or checkpoint spans in this implementation because those operations do not exist in the workflow. In a production agentic system, a useful trace should preserve this nesting and add spans at the real operation boundaries:

```text
workflow
  → step
    → agent, model, tool, retrieval, approval, or persistence operation
```

Framework-provided spans should be reused when they already describe those boundaries. Explicit application spans are appropriate for business operations the framework cannot name—for example, publishing a release, applying an approval policy, or committing a checkpoint—but only the code that performs an operation can instrument it truthfully.

## Enabling native MAF workflow telemetry

The workflow topology opts into the framework's native observability with `WithOpenTelemetry`:

```csharp
return new WorkflowBuilder(validate)
    .AddEdge<ValidatedRelease>(
        validate,
        extraReview,
        release => release!.HighRisk,
        "HighRisk: yes")
    .AddEdge<ValidatedRelease>(
        validate,
        continueReview,
        release => !release!.HighRisk,
        "HighRisk: no")
    .AddFanOutEdge(
        extraReview,
        [securityReview, qualityReview, architectureReview],
        "extra review complete")
    .AddFanOutEdge(
        continueReview,
        [securityReview, qualityReview, architectureReview],
        "standard reviews")
    .AddFanInBarrierEdge(
        [securityReview, qualityReview, architectureReview],
        decision,
        "all reviews complete")
    .WithOutputFrom(decision)
    .WithName("ObservableReleaseReview")
    .WithOpenTelemetry(options =>
        options.EnableSensitiveData = SensitiveDataEnabled)
    .Build();
```

This is not a custom `ActivitySource` wrapped around each executor. MAF creates its own workflow, executor, edge-group, and message activities based on the graph and its runtime behavior. The topology itself supplies useful names: the trace can distinguish the high-risk route, the three concurrent reviews, the fan-in barrier, and the final decision.

The application still has to subscribe to those activities. Creating an `Activity` is separate from recording and exporting it; without a listener for the source, the MAF spans will not enter the configured OpenTelemetry pipeline.

## Automatic instrumentation and explicit business meaning

The service registration combines two different sources of spans:

```csharp
services.AddOpenTelemetry()
    .ConfigureResource(resource =>
        resource.AddService("ObservableReleaseReview"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("Microsoft.Agents.AI.Workflows")
        .AddOtlpExporter(options => options.Endpoint = otlpEndpoint));
```

`AddAspNetCoreInstrumentation` automatically creates request spans for ASP.NET Core. It provides the inbound HTTP boundary, timing, and request context without controller code manually starting a span.

`AddSource("Microsoft.Agents.AI.Workflows")` subscribes the provider to native MAF activities. Those activities exist because the workflow explicitly called `WithOpenTelemetry`; source registration does not create them by itself.

The combination is valuable: automatic HTTP instrumentation supplies the outer technical boundary, while native workflow instrumentation supplies domain-relevant executor and routing boundaries. Neither can automatically describe application work outside its scope. If a real executor calls an uninstrumented proprietary API, performs a business transaction, or deliberately retries an operation, that code needs supported client instrumentation or a carefully designed explicit span.

Avoid producing a second set of spans that merely duplicates native MAF spans. Duplicate telemetry increases cost and can make the trace ambiguous without adding business meaning.

## Correlating the response and workflow

During an instrumented HTTP request, `Activity.Current` is the ASP.NET Core request activity. The sample reuses its W3C trace ID as the API's `runId` and as the MAF session ID:

```csharp
var runId = Activity.Current?.TraceId.ToString();
if (string.IsNullOrEmpty(runId))
{
    runId = ActivityTraceId.CreateRandom().ToString();
}

await using var run = await InProcessExecution.Concurrent.RunAsync(
    _workflow,
    new StartReview(runId, request.Release, request.SimulateFailure),
    runId,
    cancellationToken);
```

Under HTTP execution, this creates one correlation value rather than an unrelated application run ID. A developer can copy the returned `runId` and find the corresponding trace in the backend. The API response contains business status, decision, duration, route, and the trace ID; it does not contain or query exported spans. The Blazor application only links to the separately configured dashboard.

The random-ID fallback supports invocation without a current HTTP activity, such as a unit test or direct service call. It provides an identifier, but it does not automatically create a parent span. For messaging or cross-service execution, trace-context propagation must be implemented across the actual transport. This sample is in-process and does not demonstrate cross-service propagation.

## OTLP in practical terms

OTLP is the protocol used to send OpenTelemetry data to a collector or compatible backend. The application produces ASP.NET Core and MAF activities, the OpenTelemetry SDK processes the recorded spans, and `AddOtlpExporter` transmits them to the configured endpoint.

The sample resolves the endpoint from the standard environment variable first, with an application-setting fallback, and rejects a missing or invalid value:

```csharp
var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
    ?? configuration["OpenTelemetry:OtlpEndpoint"]
    ?? throw new InvalidOperationException(
        "Configure OpenTelemetry:OtlpEndpoint or OTEL_EXPORTER_OTLP_ENDPOINT.");

if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var otlpEndpoint))
{
    throw new InvalidOperationException(
        $"The configured OTLP endpoint '{endpoint}' is not an absolute URI.");
}
```

The development configuration points to `http://localhost:4317`, the OTLP/gRPC endpoint exposed by the included standalone Aspire Dashboard container. The compose file also maps port 4318 for OTLP/HTTP and port 18888 for the dashboard UI. The application explicitly assigns the configured URI to the exporter; it does not send telemetry to the dashboard's browser URL.

This wording matters: OpenTelemetry does not “collect everything automatically.” Only activities recorded by subscribed instrumentation enter this trace pipeline, and only sampled/exportable telemetry is sent through the configured exporter. The sample configures traces, not an integrated logs-and-metrics pipeline.

Exporter failure is also distinct from workflow failure. An unavailable collector can prevent telemetry delivery while the deterministic business workflow still runs. The sample does not test OTLP network export, collector ingestion, retention, or dashboard rendering.

## Sensitive data is a tracing design decision

MAF's `WorkflowTelemetryOptions.EnableSensitiveData` controls whether raw workflow message inputs and executor outputs are included in native telemetry. The application binds it from configuration and defaults it to `false`:

```csharp
builder.Services.Configure<WorkflowTelemetryOptions>(options =>
    options.EnableSensitiveData = builder.Configuration.GetValue(
        "WorkflowTelemetry:EnableSensitiveData",
        false));
```

That value is passed into `WithOpenTelemetry`. With the default, the tested release text does not appear in the native activity tags. For intentional local diagnostics, the sample supports `WorkflowTelemetry__EnableSensitiveData=true`, but its documentation explicitly warns against enabling this for secrets or production payloads.

Sensitive-data control is broader than one switch. Span attributes should favor bounded operational facts such as workflow name, executor name, route, outcome, dependency type, and a safe correlation identifier. Do not attach prompts, retrieved document bodies, model responses, credentials, access tokens, personal data, or arbitrary tool payloads merely because they simplify one debugging session. Redaction must occur before export; access control and retention at the backend are additional safeguards, not substitutes.

The tests verify that a specific sensitive phrase is absent from activity tags when capture is disabled. They do not inspect every possible exporter representation, so this is useful evidence rather than a complete data-loss-prevention guarantee.

## Diagnosing failures and incomplete execution

The sample can force the Security review executor to throw. Its native trace contains `executor.process SecurityReview`, but no downstream `executor.process ReleaseDecision`. That shape establishes the failure boundary and shows that the fan-in never produced a decision.

In MAF 1.17.0, the tested failed executor activity currently has `ActivityStatusCode.Unset` and no exception event. The application returns a safe, deterministic error message separately and does not manufacture a replacement telemetry span. Operators therefore must not assume that every failed business operation will have an error status or exception text in this package version.

Even with that limitation, trace topology answers important questions:

- Missing downstream spans show where execution stopped.
- Span durations identify slow executors and fan-in waiting.
- Edge and message activities reveal conditional routing and delivery outcomes.
- A shared trace ID connects the HTTP request with workflow execution.
- Dependency spans, when an actual client library emits them, can isolate remote latency and failure.
- Retry spans or attempt attributes, when explicitly implemented, can distinguish one slow call from repeated work.

The sample's API executor timeline is not built from trace data. `BuildExecutions` constructs a simplified expected list from the known route and outcome. That UI is a business-oriented projection; the collector's distributed trace is the execution evidence. Production systems should avoid presenting inferred workflow status as if it were read from telemetry.

## Production design considerations

Tracing every workflow, message, edge, agent, and tool call can create substantial volume. A production design should set sampling and retention policies according to diagnostic value, compliance requirements, and cost. Preserve enough head-based or tail-based samples to investigate failures and high latency, while recognizing that aggressive sampling can remove the one trace needed for an intermittent issue.

Keep attributes low-cardinality and queryable. Workflow and executor names are usually bounded; prompts, document text, URLs containing identifiers, raw error messages, and per-user values are not. High-cardinality attributes increase storage and indexing costs and can become a privacy risk.

Correlation identifiers need a defined lifecycle. This sample exposes the W3C trace ID as its run ID for a single in-process request. A long-running or durable workflow may also require a stable business/workflow instance ID because a trace's lifetime and a workflow's lifetime are not necessarily identical. Record both deliberately and propagate trace context across queues, services, and tool transports rather than assuming it crosses those boundaries.

Logs and traces should reinforce each other. Structured logs can carry the trace ID and stable workflow ID, while spans provide timing and parent-child structure. Metrics and alerts then summarize rates, latency, saturation, and failures. Chapter 7 implements traces only; production monitoring decisions, dashboards, service-level objectives, alert thresholds, and incident response remain application and platform responsibilities.

Finally, verify the telemetry path separately from business tests. The source tests attach an `ActivityListener` directly and prove that native MAF activities share the returned trace ID, include workflow/executor/edge/message activity, buffer at fan-in, omit the tested sensitive text, and stop before the decision on failure. They do not prove OTLP transmission or backend ingestion.

## What OpenTelemetry does not solve

OpenTelemetry describes execution; it does not control it. It does not provide:

- workflow durability or restart recovery;
- retry, timeout, or compensation policy;
- an authoritative business audit history;
- agent memory or conversation state;
- idempotency for tools and external effects;
- application-level decisions about alerts, health, or acceptable behavior.

A trace can reveal that a step ran twice, but it cannot make that step idempotent. It can show that a workflow stopped, but it cannot resume it. It can correlate a model or tool call when that operation is instrumented, but it cannot decide what content is safe to record. Those remain architectural responsibilities of the system around the telemetry pipeline.

## Continue Exploring

This implementation belongs to *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2)
