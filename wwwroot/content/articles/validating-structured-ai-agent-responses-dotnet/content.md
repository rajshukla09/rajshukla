# Validating Structured AI Agent Responses in .NET

Free-form model output is useful when a person only needs to read the answer. It becomes a weak application boundary when code must render individual values, compare a result with the request, or decide whether the result is safe to pass downstream.

Asking for JSON does not close that gap. JSON can be syntactically valid while containing the wrong duration, missing days, duplicate sequence numbers, or empty collections. A production-oriented boundary needs two separate controls:

```text
model generation
  → typed response materialization
  → deterministic application validation
  → API response
```

The Chapter 2 Smart Travel Planner demonstrates this boundary with Microsoft Agent Framework, Azure OpenAI, and ASP.NET Core. The agent produces a typed `TripPlan`. Ordinary C# code then checks the invariants the application can know before the plan reaches the HTTP response.

## Model the data the application actually needs

The first version of the travel agent returned one generated string. That was readable, but a client could not reliably identify days or activities without parsing prose whose headings and formatting could vary.

Chapter 2 replaces the string with a small object hierarchy:

```csharp
public sealed record TripPlan
{
    public required string Destination { get; init; }
    public required int DurationDays { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<TripDay> Days { get; init; } = [];
}
```

Each `TripDay` has an explicit number, title, and activity collection. Each `TripActivity` separates time, name, description, category, and notes.

That shape is deliberately modest. It contains values the application can render and inspect. It does not add current prices, availability, weather, or opening hours because a property cannot make model-generated information current or verified.

The C# type provides an application contract. It does not prove that every generated value is correct.

## Request a typed result from Microsoft Agent Framework

The agent boundary changes from an untyped invocation to the generic `RunAsync<T>` operation:

```csharp
AgentResponse<TripPlan> result = await _agent.RunAsync<TripPlan>(
    prompt,
    cancellationToken: cancellationToken);

TripPlan plan = result.Result;
```

Microsoft Agent Framework requests and materializes the response as `TripPlan`. The application does not scrape Markdown, repair a JSON string, or call a JSON deserializer on raw model text.

The application-facing interface remains independent of the framework response type:

```csharp
public interface ITravelAgent
{
    Task<TripPlan> CreateItineraryAsync(
        TravelPlanRequest request,
        CancellationToken cancellationToken = default);
}
```

`AgentResponse<TripPlan>` stays inside `TravelAgent`; controllers receive the application's `TripPlan`. This keeps provider and framework details from spreading through the HTTP layer.

The prompt supplies explicit request values rather than one opaque sentence:

```csharp
string prompt = $"""
    Create a travel plan for the following request:
    Destination: {request.Destination}
    Duration in days: {request.DurationDays}
    Traveller preferences: {request.Preferences ?? "No additional preferences supplied."}
    """;
```

The agent instructions describe the planning task and expected population of the structured response. The C# model describes the result shape. Neither replaces deterministic validation.

## Validate invariants after materialization

A result can materialize as `TripPlan` and still contradict the request. The implementation validates immediately after reading `result.Result`:

```csharp
if (!IsValid(plan, request.DurationDays, out string validationIssue))
{
    _logger.LogError(
        "Travel agent returned an invalid structured response: {ValidationIssue}",
        validationIssue);

    throw new InvalidOperationException(
        "The travel agent returned an invalid travel plan.");
}
```

The important placement is before `TravelAgent` returns. No controller, UI, or storage component can accidentally treat the candidate plan as accepted application data.

The validator checks only invariants that normal code can determine:

- `TripPlan` is not null.
- `Destination` is not blank.
- `DurationDays` is between 1 and 14.
- The returned duration equals the requested duration.
- `Days` is not null and its count equals the duration.
- Day numbers are sequential, beginning with 1.
- Every day has a non-null activity collection containing at least one non-null activity.

The duration and sequence checks illustrate why typed output is only the first layer:

```csharp
_ when plan.DurationDays != requestedDuration
    => "DurationDays did not match the request.",
_ when plan.Days.Count != plan.DurationDays
    => "The day count did not match DurationDays.",
_ when plan.Days
    .Select((day, index) => day is null || day.DayNumber != index + 1)
    .Any(invalid => invalid)
    => "Day numbers were not sequential.",
```

These are semantic constraints expressed with deterministic code. A schema can describe an integer and an array; the application must still decide that a two-day request requires a duration of two, exactly two day objects, and day numbers `1` and `2`.

## Validate the request before paying for model execution

The input boundary is explicit as well:

```csharp
public sealed record TravelPlanRequest(
    [Required, StringLength(100)] string Destination,
    [Range(1, 14)] int DurationDays,
    [StringLength(500)] string? Preferences = null);
```

ASP.NET Core validates these annotations at the API boundary. The controller adds a separate whitespace check because a required string can still contain only spaces:

```csharp
if (string.IsNullOrWhiteSpace(request.Destination))
{
    ModelState.AddModelError(
        nameof(request.Destination),
        "Destination is required and cannot contain only whitespace.");

    return ValidationProblem(ModelState);
}
```

Invalid destination, duration, or preference length is rejected before `ITravelAgent` is called. This is both a contract decision and a cost boundary: input the application already knows is invalid never reaches Azure OpenAI.

After the agent returns an accepted plan, the controller has almost nothing left to do:

```csharp
TripPlan response = await _travelAgent.CreateItineraryAsync(
    request,
    cancellationToken);

return Ok(response);
```

Because the action returns `ActionResult<TripPlan>`, Swagger can describe the nested response hierarchy rather than an opaque string wrapper.

## Keep internal failure detail out of the API

The validator logs its specific diagnostic, such as a mismatched day count, then throws a generic exception. The application's global exception handler logs the exception server-side and returns controlled Problem Details with the title `Unable to create a travel plan`.

That separation gives operators useful diagnostics without returning provider or internal validation details to callers. The sample maps all unhandled failures to the same `500` response; a larger service would usually classify cancellation, provider availability, throttling, and invalid generated output more precisely.

## Test the deterministic seams honestly

The endpoint tests replace `ITravelAgent` with a fake. They verify that invalid requests return `400 Bad Request` without calling the agent, and that a valid request returns nested `TripPlan` JSON rather than the earlier response-string wrapper.

The success test asserts concrete response properties:

```csharp
Assert.Equal("Jaipur", result.Destination);
Assert.Equal(2, result.DurationDays);
Assert.Equal(2, result.Days.Count);
Assert.Equal(new[] { 1, 2 }, result.Days.Select(day => day.DayNumber));
Assert.All(result.Days, day => Assert.NotEmpty(day.Activities));
Assert.DoesNotContain("\"response\"", json, StringComparison.OrdinalIgnoreCase);
```

Another endpoint test makes the fake agent throw an exception. It verifies the controlled `500` response and confirms that the injected provider detail is absent from the body.

The instruction tests check that important planning and safety clauses remain in `TravelAgentInstructions.SystemPrompt`. They protect prompt content from accidental deletion; they do not prove that a model follows those instructions.

The test suite does not call Azure OpenAI, judge itinerary quality, assert live model compliance, or directly exercise `TravelAgent.IsValid` with invalid generated plans. The endpoint fake returns a completed `TripPlan`, so model integration and generated-result validation remain separate testing concerns.

## Know what the validator does not guarantee

The implemented validation is intentionally narrow. It does not compare the returned destination with the requested destination. It also does not reject blank summaries, day titles, or activity fields, validate the activity time format, restrict category values, detect duplicate activities, or assess geographical feasibility.

More importantly, it cannot verify whether a place is open, a recommendation is safe, a price is current, or travel time is accurate. The agent has no tools or live-data integration in this chapter. Its instructions explicitly prohibit claims of real-time verification and bookings.

There is also no retry or repair loop for an invalid structured result. Validation failure becomes a controlled server error. Each request is independent: there are no sessions, persistence, memory, workflow orchestration, or conversation continuity.

These boundaries are not defects hidden by the type system. They define what this layer can honestly guarantee:

- The framework returns a result materialized as the expected application type.
- ASP.NET Core rejects deterministically invalid input.
- `TravelAgent` rejects the specific generated-data invariant violations it checks.
- Everything else still requires additional validation, tools, evaluation, or operational policy.

## Make acceptance an application decision

Typed responses remove brittle prose parsing and give downstream .NET code a useful contract. Deterministic validation decides whether a particular generated object is acceptable for the request that produced it.

Keeping those responsibilities separate creates a clean production boundary. The model proposes structured data; the application accepts or rejects it according to rules it can enforce.

## Continue Exploring

This article is derived from Chapter 2, “Structured Agent Responses,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 2 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-02)
