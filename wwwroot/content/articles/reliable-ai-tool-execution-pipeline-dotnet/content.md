# Building a Reliable AI Tool Execution Pipeline in .NET

Adding a C# function to an AI agent answers one question: can the model call it?

It does not answer the operational questions that appear as soon as the function depends on real infrastructure:

- Which failures are safe to retry?
- How long may an invocation run?
- Does caller cancellation differ from a timeout?
- How is a terminal failure represented?
- Can operators see why the tool ran and how many attempts it needed?

Those policies should not be reimplemented inside every tool. They also should not differ depending on whether an application rule or the model selected the operation.

Chapter 9 of the Smart Travel Planner introduces one shared execution boundary:

```text
application-mandated step ---\
                              -> reliable tool pipeline -> tool
model-selected function -----/
```

Selection still has two distinct owners. Execution gets one contract.

## Separate selection from execution policy

Some tool calls are optional model decisions. A travel-planning model may decide weather would improve an itinerary and invoke `GetWeather` through Microsoft Agent Framework function calling.

Other operations are mandatory application work. If a classified request explicitly requires a currency conversion, the application can build a validated `ExecutionPlan`, route its currency step, and execute it before asking the travel agent to reason over the result.

These paths answer different questions:

```text
selection -> why should this operation run?
execution -> how should the selected operation run?
```

The implementation records that distinction with an enum:

```csharp
public enum ToolInvocationMode
{
    Deterministic,
    ModelSelected
}
```

Invocation mode belongs in logs and traces. It does not require separate retry or timeout implementations.

## Keep tools focused on domain behavior

Chapter 9 removes tracing from the individual tool classes. `DistanceTool`, for example, validates its arguments, performs an order-independent lookup, and returns a `DistanceResult`. It does not know whether a classifier or a model selected it.

The pipeline owns the cross-cutting policy:

```csharp
public interface IToolExecutionPipeline
{
    Task<object?> ExecuteAsync(
        ToolRouteDecision decision,
        CancellationToken cancellationToken = default);

    T ExecuteModelSelected<T>(
        string toolName,
        object input,
        Func<T> operation);
}
```

The two public methods adapt the two invocation paths to the same internal policy method. Tool code remains ordinary application code, which keeps deterministic behavior testable without an agent or resilience infrastructure.

## Centralize and validate policy configuration

The execution policy is represented as options rather than scattered constants:

```csharp
public sealed class ToolExecutionOptions
{
    public const string SectionName = "ToolExecution";

    [Range(0, 3)]
    public int MaximumRetries { get; init; } = 3;

    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 5;
}
```

The application binds and validates these values at startup:

```csharp
builder.Services.AddOptions<ToolExecutionOptions>()
    .Bind(builder.Configuration.GetSection(
        ToolExecutionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

With the sample defaults, one logical invocation can make one initial attempt plus at most three retries. Its timeout is five seconds.

That distinction matters when reporting attempts. `RetryCount = 2` means three attempts occurred: the initial call and two retries.

## Retry only failures classified as transient

The implementation uses an explicit exception type to identify retryable conditions:

```csharp
public sealed class TransientToolException(
    string message,
    Exception? innerException = null)
    : Exception(message, innerException);
```

The retry loop increments only for that exception and only while the configured limit has not been reached:

```csharp
catch (TransientToolException exception)
    when (retries < _options.MaximumRetries)
{
    retries++;
    logger.LogWarning(
        exception,
        "Transient failure for {Tool}; retry {RetryCount} " +
        "of {MaximumRetries}",
        toolName,
        retries,
        _options.MaximumRetries);
}
```

An `ArgumentException`, unsupported input, or other permanent failure goes directly to terminal failure handling. Repeating the same invalid request would not improve it.

When transient retries are exhausted, the final `TransientToolException` also reaches terminal handling. The trace preserves the number of retries and the final exception message.

This is a deliberately narrow policy. The pipeline does not guess retryability from message text or retry every exception indiscriminately.

## Distinguish timeout from caller cancellation

Each attempt creates a token linked to the caller and schedules the configured timeout:

```csharp
using CancellationTokenSource timeout =
    CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken);

timeout.CancelAfter(
    TimeSpan.FromSeconds(_options.TimeoutSeconds));

object? output = await operation(timeout.Token)
    .WaitAsync(timeout.Token);
```

Both timeout and caller cancellation can surface as `OperationCanceledException`, so catch order encodes the semantic distinction:

```csharp
catch (OperationCanceledException exception)
    when (!cancellationToken.IsCancellationRequested)
{
    traceRecorder.RecordToolExecution(
        toolName,
        mode,
        planStepOrder,
        startedAt,
        timeProvider.GetUtcNow(),
        retries,
        "Timeout",
        true,
        input,
        null,
        $"Timed out after {_options.TimeoutSeconds} seconds.");

    throw new ToolExecutionFailedException(
        toolName,
        "the execution timed out",
        exception);
}
catch (OperationCanceledException)
    when (cancellationToken.IsCancellationRequested)
{
    throw;
}
```

An internal timeout becomes a recorded tool failure that downstream deterministic execution can handle. Caller cancellation propagates because the request owner has asked the entire operation to stop.

The sample does not retry timeouts. With a five-second limit and three retries, retrying timeouts could turn one slow operation into roughly twenty seconds of waiting.

## Translate terminal failures without fabricating results

Every completed terminal failure is traced, logged, and surfaced as `ToolExecutionFailedException`:

```csharp
catch (Exception exception)
{
    traceRecorder.RecordToolExecution(
        toolName,
        mode,
        planStepOrder,
        startedAt,
        timeProvider.GetUtcNow(),
        retries,
        "Failure",
        false,
        input,
        null,
        exception.Message);

    logger.LogError(
        exception,
        "Tool {Tool} failed after {RetryCount} retries",
        toolName,
        retries);

    throw exception is ToolExecutionFailedException
        ? exception
        : new ToolExecutionFailedException(
            toolName,
            exception.Message,
            exception);
}
```

The pipeline never returns `default(T)` and labels it success. A caller sees either the real output or an explicit failure.

The current `FailureReason` property is an alias of the recorded error string. It is useful for the sample, but production systems often need stable error codes and redacted public messages instead of retaining arbitrary exception text.

## Send both invocation paths through the same boundary

The deterministic router maps a validated `ExecutionStep` to a `ToolRouteDecision`. It switches only on `ToolType`; it never parses the user’s sentence.

The pipeline then dispatches the named tool:

```csharp
private object Invoke(
    string toolName,
    IReadOnlyDictionary<string, object?> args) =>
    toolName switch
    {
        "DistanceTool" => distance.GetDistance(
            (string)args["origin"]!,
            (string)args["destination"]!),
        "CurrencyTool" => currency.ConvertCurrency(
            (string)args["from"]!,
            (string)args["to"]!,
            (decimal)args["amount"]!),
        "TimeZoneTool" => timeZone.GetLocalTime(
            (string)args["city"]!),
        "WeatherTool" => weather.GetWeather(
            (string)args["destination"]!),
        _ => throw new InvalidOperationException(
            $"Unknown mandatory tool '{toolName}'.")
    };
```

Model-selected functions do not manufacture fake execution-plan steps. `TravelAgent` wraps each bound function directly with the pipeline:

```csharp
AIFunctionFactory.Create(
    (string destination) =>
        toolPipeline.ExecuteModelSelected(
            nameof(WeatherTool),
            new { destination },
            () => weatherTool.GetWeather(destination)),
    "GetWeather")
```

The function remains named `GetWeather` for the model. Behind that surface, it shares the same execution and tracing policy as a routed mandatory weather step.

## Record one trace per logical invocation

The pipeline records a final `ToolExecution` after success, timeout, or terminal failure:

```csharp
public sealed record ToolExecution
{
    public required int Order { get; init; }
    public int? PlanStepOrder { get; init; }
    public required string ToolName { get; init; }
    public required string InvocationMode { get; init; }
    public required long DurationMs { get; init; }
    public required string Status { get; init; }
    public required int RetryCount { get; init; }
    public required bool Timeout { get; init; }
    public object? Input { get; init; }
    public object? Output { get; init; }
    public string? Error { get; init; }
}
```

Retries are not emitted as separate entries. One record describes the complete logical invocation, including total retries and elapsed time from before the first attempt until the final outcome.

`PlanStepOrder` correlates deterministic executions with their originating plan. It is null for model-selected calls. `InvocationMode` explains why the call began, while `Order` records the sequence in which completed logical calls were added to the request trace.

Inputs and outputs are included in this educational sample. Production systems need an explicit policy for secrets, personal data, payload size, and retention before storing or returning those fields.

## Preserve partial results for multi-step plans

The reliable pipeline throws on a terminal tool failure. `ExecutionPlanExecutor` catches only `ToolExecutionFailedException` for an individual mandatory step, records a structured failed result, and continues with later steps:

```csharp
foreach (ExecutionStep step in
    plan.Steps.OrderBy(item => item.Order))
{
    ToolRouteDecision route = router.Route(step);

    try
    {
        object? output =
            await pipeline.ExecuteAsync(
                route,
                cancellationToken);

        results.Add(new StepResult(
            step.Order,
            step.Tool,
            "Success",
            output,
            null));
    }
    catch (ToolExecutionFailedException exception)
    {
        results.Add(new StepResult(
            step.Order,
            step.Tool,
            "Failure",
            null,
            exception.Message));
    }
}
```

Caller cancellation is not caught as a step failure, so it stops the plan. A tool timeout is translated to `ToolExecutionFailedException`, so it becomes one failed step and independent later steps continue.

The executor then serializes every ordered result into one enrichment block and instructs the travel agent to use successful results, acknowledge failures, avoid inventing missing data, and not call those mandatory tools again.

This preserves useful partial work, although the enrichment is currently appended to the request string rather than supplied through a dedicated context provider.

## Test policy behavior through a controlled seam

`ToolExecutionPipelineTests` calls the internal `ExecuteWithPolicyAsync` method with controlled delegates. The suite verifies:

- two transient failures followed by success produce three attempts and `RetryCount = 2`;
- exhausting the configured retry count records terminal failure;
- a cancellable five-second delay with a one-second policy records timeout without retry;
- completed calls retain execution order, invocation mode, and plan-step correlation;
- classification telemetry and plan-execution duration reach the request trace.

`ExecutionPlanExecutorTests` uses a fake pipeline to confirm four routed steps execute in order, a failed second step does not prevent the third, and an empty plan returns the original request unchanged.

`ToolRouterTests` confirms enum-based dispatch and case-insensitive argument lookup. Separate classifier tests validate no-tool, single-tool, and four-tool plans plus required arguments and one-shot repair behavior.

The tests do not directly cover caller cancellation, permanent failures receiving zero retries, model-selected wrapper failure behavior, logging, concurrent requests, or a real remote tool. They also do not run the full classifier-to-Azure-OpenAI-to-tool path as an end-to-end integration test.

## Be precise about the sample’s timeout guarantee

The current travel tools are synchronous, deterministic, in-memory operations. Deterministic routing wraps them in `Task.FromResult`, and `ExecuteModelSelected` blocks synchronously with `GetAwaiter().GetResult()` while passing `CancellationToken.None` into the policy method.

That creates important limits:

- the current local tool methods do not accept or observe the timeout token;
- the model-selected wrapper has no caller cancellation token;
- `WaitAsync` can stop waiting for a genuinely asynchronous operation, but non-cooperative underlying work may continue;
- the timeout test proves the internal async policy seam using `Task.Delay`, not one of the four production tool adapters;
- immediate retry has no delay, exponential backoff, jitter, or `Retry-After` support;
- there is no circuit breaker, bulkhead, or rate limiter;
- retries assume the operation is safe to repeat, but idempotency is not modeled explicitly.

Before adapting this pipeline to HTTP or database tools, make the tool contract asynchronous, pass the provided cancellation token into the real dependency, decide which operations are idempotent, and test actual cancellation behavior.

## Put reliability below every selection mechanism

The reusable architecture is not tied to travel planning or one classifier:

```text
structured application route --\
                               -> shared execution policy
model function wrapper --------/       |
                                       -> domain tool
                                       -> one outcome trace
```

Retries, timeouts, cancellation semantics, logging, and failure translation belong below selection. The classifier should not execute tools, the router should not own resilience, and the tool should not need to know why it was selected.

That boundary gives every invocation a consistent operational contract—and makes the remaining limitations visible enough to improve deliberately.

## Continue Exploring

This article is derived from Chapter 9, “Reliable Tool Execution,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 9 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-09)
