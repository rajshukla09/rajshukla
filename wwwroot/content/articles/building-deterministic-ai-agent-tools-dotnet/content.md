# Building Deterministic AI Agent Tools with Microsoft Agent Framework and .NET

A language model can understand a request and compose a useful response. It should not automatically become the authority for calculations or facts that your application can determine more reliably.

Consider a travel agent asked to convert 100 USD to INR. The model can recognize that a conversion is needed, but asking it to recall a rate and calculate the answer mixes two responsibilities:

```text
model: interpret the user's intent
application: validate inputs and calculate an application-owned result
model: incorporate that result into the response
```

Microsoft Agent Framework tools create this boundary. The model chooses an appropriate function and supplies arguments. Ordinary C# code validates those arguments, performs deterministic work, and returns a typed result. The framework then gives that result back to the model as intermediate context.

The important production lesson is not merely how to register a function. A useful tool boundary needs truthful metadata, explicit validation, observable execution, and tests that distinguish deterministic application behavior from probabilistic model behavior.

## Keep tool responsibilities narrow

The Chapter 6 travel agent exposes four independent capabilities:

- sample weather for a destination;
- fixed-rate currency conversion;
- local time from an injected clock and fixed offset;
- fixed road distance for supported city pairs.

Each tool returns a small domain result. None creates an itinerary, accesses an `AgentSession`, or mutates the final `TripPlan`.

For example, the currency tool returns the facts needed for a conversion:

```csharp
public sealed record CurrencyConversionResult(
    string From,
    string To,
    decimal Amount,
    decimal ExchangeRate,
    decimal ConvertedAmount);
```

This result is intentionally smaller than the agent's structured response. The application owns the calculation; the model owns how the relevant result is expressed within the requested travel plan.

Keeping those contracts separate prevents tool infrastructure from leaking into the business model. It also makes a deterministic tool callable and testable without running the agent or contacting Azure OpenAI.

## Describe the capability the model can actually use

Agent Framework can turn a bound C# method into a model-facing function. The method name, CLR parameter types, parameter names, and `Description` attributes form the contract visible to the model.

The distance tool makes that contract specific:

```csharp
[Description("Gets a fixed sample road distance between two cities. Use for distance, route-length, or how-far questions.")]
public DistanceResult GetDistance(
    [Description("Starting city.")] string origin,
    [Description("Destination city.")] string destination)
```

The description states both the capability and when it applies. Names such as `origin` and `destination` reduce argument ambiguity. Just as importantly, the text says the distances are fixed samples; it does not imply global routing or live data.

Descriptions guide selection and argument generation. They do not enforce domain rules. A generated schema can require a string while still allowing an empty or unsupported city. Validation remains an application responsibility.

## Validate inside the tool boundary

The distance implementation normalizes harmless differences but rejects values it cannot support:

```csharp
ArgumentException.ThrowIfNullOrWhiteSpace(origin);
ArgumentException.ThrowIfNullOrWhiteSpace(destination);

string normalizedOrigin = origin.Trim();
string normalizedDestination = destination.Trim();

if (!Distances.TryGetValue(
        Key(normalizedOrigin, normalizedDestination),
        out int distance))
{
    throw new ArgumentException(
        "No sample distance is available for that route.");
}

return new DistanceResult(
    normalizedOrigin,
    normalizedDestination,
    distance);
```

An unsupported route fails instead of producing a plausible-looking estimate. Currency conversion similarly rejects missing codes, negative amounts, and unsupported currencies. The time-zone tool rejects cities outside its fixed table.

Failure policy can differ when the domain supports it. The weather tool returns cautious generic sample conditions for an unknown destination, including advice to check local conditions. That is a deliberate contract decision, not a fallback rule that every tool should copy.

The key is to define the boundary explicitly: normalize what is harmless, reject what would create false precision, and never depend on prompt wording as the only validation layer.

## Attach bound functions to the agent

The tool classes are registered through dependency injection along with their dependencies:

```csharp
builder.Services.AddSingleton<IExecutionTraceRecorder,
    ToolExecutionTraceRecorder>();
builder.Services.AddSingleton<WeatherTool>();
builder.Services.AddSingleton<CurrencyTool>();
builder.Services.AddSingleton<TimeZoneTool>();
builder.Services.AddSingleton<DistanceTool>();
builder.Services.AddSingleton(TimeProvider.System);
```

`TravelAgent` receives those instances and exposes only the selected methods:

```csharp
_agent = new AzureOpenAIClient(
        new Uri(settings.Endpoint),
        new AzureKeyCredential(settings.ApiKey))
    .GetChatClient(settings.DeploymentName)
    .AsAIAgent(
        name: nameof(TravelAgent),
        instructions: TravelAgentInstructions.SystemPrompt,
        tools:
        [
            AIFunctionFactory.Create(weatherTool.GetWeather),
            AIFunctionFactory.Create(currencyTool.ConvertCurrency),
            AIFunctionFactory.Create(timeZoneTool.GetLocalTime),
            AIFunctionFactory.Create(distanceTool.GetDistance)
        ]);
```

Each delegate is already bound to a dependency-injected tool instance. Agent Framework supplies its model-facing definition and coordinates the function-calling exchange. The application does not need a service locator, a custom function registry, or a keyword switch in the controller.

Attaching a function does not force it to run. During one agent operation, the model may request no tool, one tool, several tools, or the same tool more than once. Function metadata and system instructions guide that choice, but the choice remains model-driven.

That distinction matters: deterministic tools do not make tool selection deterministic. The reliable guarantee begins after a valid function call reaches the C# boundary.

## Make execution observable without changing the domain model

Tool execution is operational metadata, not travel-plan data. Chapter 6 leaves `TripPlan` unchanged and composes it with a separate trace:

```csharp
public sealed record TripPlanResponse(
    TripPlan TripPlan,
    ExecutionTrace Execution);
```

The trace records request timing and ordered tool executions. Each call contains its tool name, input, output, status, duration, and optional error.

The agent creates one scope around each run:

```csharp
using ExecutionTraceScope trace = _traceRecorder.BeginRequest();

AgentResponse<TripPlan> result =
    await _agent.RunAsync<TripPlan>(
        prompt,
        cancellationToken: cancellationToken);

TripPlan plan = Validate(result.Result, request.DurationDays);
return new TripPlanResponse(plan, trace.Complete());
```

`ToolExecutionTraceRecorder` is a singleton, but it does not keep one global list. An `AsyncLocal<RequestTrace?>` associates tool calls with the current asynchronous request scope. The scope owns its collection, and disposing it restores any parent scope.

Inside `RecordToolCall`, the recorder assigns invocation order before executing the operation. It records the successful output or captures the failure and rethrows the original exception:

```csharp
try
{
    T output = operation();
    request.Add(CreateExecution(
        order,
        toolName,
        startedAt,
        timeProvider.GetUtcNow(),
        "Success",
        input,
        output,
        null));
    return output;
}
catch (Exception exception)
{
    request.Add(CreateExecution(
        order,
        toolName,
        startedAt,
        timeProvider.GetUtcNow(),
        "Failure",
        input,
        null,
        exception.Message));
    throw;
}
```

The exception still controls the agent execution path; tracing does not manufacture a successful result. Assigning order before execution also preserves invocation order when completion timing differs.

Validation belongs inside the recorded operation. If validation happens before `RecordToolCall`, invalid model-generated arguments disappear from the trace even though the application attempted a tool call.

## Test deterministic behavior separately from model selection

The Chapter 6 tests run without live Azure OpenAI. They directly verify the parts the application controls:

- weather lookup is case-insensitive;
- fixed currency cross-rates produce expected decimal results;
- an injected clock produces an exact local time;
- distance lookup works in either direction;
- successful calls capture input, output, timing, and status;
- failed calls are recorded and rethrown;
- multiple calls retain invocation order;
- a request without tool calls returns an empty collection.

An advancing `TimeProvider` makes timing assertions deterministic. One test then verifies order across two calls:

```csharp
using ExecutionTraceScope scope = recorder.BeginRequest();

new DistanceTool(recorder)
    .GetDistance("Hyderabad", "Jaipur");
new CurrencyTool(recorder)
    .ConvertCurrency("USD", "INR", 100);

ExecutionTrace trace = scope.Complete();

Assert.Equal(
    new[] { 1, 2 },
    trace.ToolCalls.Select(call => call.Order));
```

Prompt tests confirm that the instructions mention all four functions, allow multiple relevant calls, and require a complete `TripPlan`. They do not prove that a configured model will choose an identical tool sequence for every paraphrase.

That behavior needs a controlled or live integration evaluation. Even then, assertions should focus on the required response structure and relevant tool participation rather than exact generated prose.

## Know the production boundary

The sample establishes a clean tool architecture, but its data providers are deliberately local and limited:

- weather, exchange rates, UTC offsets, and distances are fixed samples rather than live data;
- fixed UTC offsets do not account for daylight-saving transitions;
- unsupported currencies, cities, and routes are limited to the explicit tables;
- there is no production authentication, remote-service retry, timeout policy, or distributed telemetry around the tools;
- recorded inputs, outputs, and error text would need a sensitivity and redaction policy before production retention;
- actual model selection requires configured Azure OpenAI and is not asserted by the deterministic unit tests.

The trace reports application-observed function execution. It does not expose hidden model reasoning, and it should not be presented as doing so.

These limits do not weaken the pattern. They show where later production work belongs: behind the same narrow tool contracts and around the same execution boundary, without moving provider details into prompts or the final domain response.

## Build a boundary, not just a callable method

A useful agent tool combines probabilistic intent interpretation with deterministic application behavior:

```text
truthful function metadata
  -> model supplies typed arguments
  -> C# validates and executes
  -> request scope records the outcome
  -> typed result returns to the model
  -> agent produces the validated business response
```

This division lets the model do what it does well while keeping calculations, supported data, failure semantics, and observability under application control. The result is not a fully deterministic agent. It is a system with a clear, testable deterministic boundary.

## Continue Exploring

This article is derived from Chapter 6, “Adding AI Tools,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 6 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-06)
