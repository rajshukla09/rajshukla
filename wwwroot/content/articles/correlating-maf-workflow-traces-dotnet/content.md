# Correlating HTTP Requests with Native MAF Workflow Traces in .NET

Workflow telemetry is most useful when an operator can connect a business request to the framework activity that executed it. A dashboard full of executor spans is not enough if there is no stable way to answer: “Which trace belongs to this release review, and which branch stopped it?”

The Chapter 7 release-review sample uses a simple correlation contract:

```text
ASP.NET Core request Activity
    -> Activity.Current.TraceId
    -> MAF workflow session/run ID
    -> native workflow, executor, edge, and message Activities
    -> API response run ID and dashboard trace
```

The workflow is deterministic—there is no model call—so trace shape can be asserted without confusing model quality with observability behavior. The same pattern applies when a workflow later contains Agents, provided application and framework spans share the same trace context.

## Subscribe to the native MAF activity source

The application configures OpenTelemetry once at startup:

```csharp
private const string MafWorkflowActivitySource =
    "Microsoft.Agents.AI.Workflows";

services.AddOpenTelemetry()
    .ConfigureResource(resource =>
        resource.AddService("ObservableReleaseReview"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(MafWorkflowActivitySource)
        .AddOtlpExporter(options =>
            options.Endpoint = otlpEndpoint));
```

`AddAspNetCoreInstrumentation` creates the incoming HTTP Activity. `AddSource` subscribes the OpenTelemetry pipeline to the native MAF workflow source. `AddOtlpExporter` sends spans to the configured collector, which the local sample exposes through an Aspire Dashboard.

The source name is a contract worth checking against the installed package version. Subscribing only to application-created sources would miss the framework’s workflow activities; subscribing to a guessed name would produce an apparently healthy exporter with no MAF spans.

The endpoint is validated as an absolute URI at startup. The sample supports either `OpenTelemetry:OtlpEndpoint` or `OTEL_EXPORTER_OTLP_ENDPOINT` and fails configuration when neither is available.

## Use the request trace as the workflow identity

At the workflow boundary, the implementation reads the current HTTP trace ID:

```csharp
var runId = Activity.Current?.TraceId.ToString();
if (string.IsNullOrEmpty(runId))
{
    runId = ActivityTraceId.CreateRandom().ToString();
}

await using var run =
    await InProcessExecution.Concurrent.RunAsync(
        _workflow,
        new StartReview(
            runId,
            request.Release,
            request.SimulateFailure),
        runId,
        cancellationToken);
```

Under ASP.NET Core, the result’s `RunId` is the incoming request’s trace ID. The same value is supplied as the MAF session identifier, so an API response, application log, and dashboard search can use one correlation value.

The fallback matters for non-HTTP callers and tests. If no current Activity exists, the application creates a new trace ID rather than emitting an empty identifier. A production background worker should establish its own parent Activity before entering the workflow if it needs a broader trace relationship.

This is correlation, not persistence. The run ID does not make execution durable, and the API does not query the collector to reconstruct a response.

## Build the graph so spans explain business routing

The sample’s graph has a deterministic conditional path followed by fan-out/fan-in:

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
    .WithOpenTelemetry(options =>
        options.EnableSensitiveData = SensitiveDataEnabled)
    .Build();
```

The native source emits workflow, executor, edge, and message activities. Those span types describe the topology without the application inventing parallel telemetry. A high-risk request includes `ExtraReview`; a normal request takes `ContinueReview`. The fan-in barrier explains why `ReleaseDecision` waits for the three review branches.

The API response separately reports business execution records. In the sample those records are a simplified expected projection used for the UI; they are not read back from the exported spans. This separation prevents the business response from depending on collector availability.

## Assert trace shape with an ActivityListener

The native telemetry test listens directly to the MAF source:

```csharp
var activities = new ConcurrentBag<Activity>();
using var listener = new ActivityListener
{
    ShouldListenTo = source =>
        source.Name == "Microsoft.Agents.AI.Workflows",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activities.Add
};

ActivitySource.AddActivityListener(listener);
```

It starts a parent Activity to represent the HTTP request, runs the workflow, and filters captured MAF activities by the returned run ID:

```csharp
using var parent =
    new Activity("test-http-request").Start();

var result = await Create().ReviewAsync(request);

var mafActivities = activities
    .Where(activity =>
        activity.Source.Name ==
        "Microsoft.Agents.AI.Workflows")
    .Where(activity =>
        activity.TraceId.ToString() == result.RunId)
    .ToArray();
```

The test asserts that workflow, executor, edge, and message activities share the parent trace. It also checks concrete executor names such as `ExtraReview`, `SecurityReview`, `QualityReview`, `ArchitectureReview`, and `ReleaseDecision`.

This is stronger than asserting only that “some spans exist.” It proves that the business response’s correlation ID points to the framework activities that matter to the operator.

## Keep release content out of telemetry attributes

The test also asserts that sensitive release text does not appear in captured span tags:

```csharp
Assert.DoesNotContain(
    mafActivities.SelectMany(activity => activity.Tags),
    tag => tag.Value?.Contains(
        "authentication and payment",
        StringComparison.OrdinalIgnoreCase) == true);
```

The sample configures `EnableSensitiveData` as an explicit option. The default is false:

```csharp
public ReleaseReviewWorkflow(
    IOptions<WorkflowTelemetryOptions> telemetry)
{
    SensitiveDataEnabled =
        telemetry.Value.EnableSensitiveData;
    _workflow = BuildWorkflow();
}
```

The option is passed to the framework’s `WithOpenTelemetry` configuration. This makes the data decision visible at workflow construction rather than relying on an undocumented exporter behavior.

“No sensitive text in tags” is not the same as “no sensitive data can ever leave the process.” Logs, exception messages, baggage, exporter processors, collector configuration, and custom application instrumentation need their own policy. The test covers the tested native tag surface, not every representation an exporter might produce.

## Inspect failures without inventing failure spans

The simulated failure path throws in `SecurityReview`. The workflow response is failed, and `ReleaseDecision` is not executed:

```csharp
var result = await Create().ReviewAsync(
    new ReleaseReviewRequest(
        "Release 4.8",
        SimulateFailure: true));

Assert.Equal("Failed", result.Status);
Assert.Equal(
    "Failed",
    result.Executors.Single(
        item => item.Name == "SecurityReview").Status);
Assert.Equal(
    "Not Executed",
    result.Executors.Single(
        item => item.Name == "ReleaseDecision").Status);
```

The native failure-trace test verifies that the MAF source reaches `SecurityReview` but not `ReleaseDecision`. A limitation of the installed MAF version is important: a failed executor Activity may retain an `Unset` status without exception events. The absence of an explicit error status in a span does not prove the executor succeeded; correlate the span with the workflow’s business result and error response.

The application currently catches all workflow exceptions and returns a failed business response, including cancellation. That behavior should be revisited if callers need cancellation to remain distinguishable from a fault.

## Keep the UI and trace backend separate

The API returns business-facing fields: status, decision, route, executor summaries, durations, error text, and the trace ID. The web UI renders that response and links the trace ID to the Aspire Dashboard.

The UI does not query the OpenTelemetry collector for spans. The collector and dashboard own telemetry exploration; the API owns workflow response semantics. This separation means the release-review endpoint can still return a useful business result when telemetry delivery is unavailable, subject to exporter configuration and runtime behavior.

The sample has no trace database and no durable workflow continuation. OTLP retention and dashboard history depend on the external collector’s configuration.

## Test correlation, not collector rendering

The Chapter 7 tests establish the native Activity boundary without requiring OTLP network ingestion. They prove:

- normal and high-risk business outcomes;
- failure stops before `ReleaseDecision`;
- sensitive-data configuration defaults and option application;
- MAF workflow, executor, edge, and message activities share the parent trace ID;
- tested sensitive release text does not appear in native tags; and
- the endpoint returns the expected business response.

They do not prove OTLP export, Aspire Dashboard rendering, retention, logs, metrics, or cross-service propagation. A collector integration test and an operational dashboard check are separate concerns.

## Choose a safe correlation contract

A production workflow should make these decisions explicit:

- which trace ID is returned to callers;
- whether background executions create a new root or continue an incoming context;
- which business identifiers are safe span attributes;
- whether release text, prompts, tool results, or exception details may be exported;
- how sampling affects support investigations; and
- how long the collector retains traces.

Do not put customer content, source code, credentials, or complete prompts into tags merely because a dashboard can display them. Prefer stable identifiers and bounded status values, and keep detailed sensitive material in an access-controlled system designed for it.

## Understand the observability boundary

The durable pattern from this chapter is:

```text
HTTP Activity
    -> stable trace/run ID
    -> native MAF Activities
    -> OTLP exporter
    -> external trace viewer
```

The framework owns native workflow spans. The application owns correlation, safe configuration, and business response projection. Tests assert the relationship between those layers without claiming that the collector or dashboard has been exercised.

OpenTelemetry makes workflow behavior inspectable; it does not make the workflow durable, authorize actions, or replace application error handling. Keeping those boundaries clear is what makes the trace useful when a real release review fails in production.

## Continue Exploring

This article is derived from Chapter 7, “OpenTelemetry for Workflows,” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 7 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-07)
