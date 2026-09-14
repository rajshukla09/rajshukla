# Injecting Application Context into AI Agents Without Rewriting User Messages

An AI agent often needs information the user did not type into the current message.

The application may already know the user’s approved preferences, the conversation owner, the current session status, or request metadata such as destination and duration. Giving that information to the model is useful. Pretending that all of it came from the user is not.

A common shortcut builds one composite string:

```csharp
string enrichedMessage = $"""
    Traveler preferences: {preferences}
    Destination: {destination}
    Duration: {duration}

    Current request:
    {message}
    """;
```

The model receives the information, but the normal user-message path now contains application-authored data. That blurs provenance, complicates debugging, and can allow transient context to accumulate as conversation history.

Chapter 8 of the Smart Travel Planner replaces that pattern with Microsoft Agent Framework context providers:

```text
user message -> what the user said
AgentSession -> what happened in this conversation
context providers -> what the application supplies for this invocation
```

The resulting design keeps the user message unchanged while projecting durable memory and runtime state into one agent execution.

## Separate source data from invocation context

Long-term memory and model context are related, but they are not the same resource.

`TravelerMemoryStore` owns explicit preferences across conversations. An `AgentSession` owns continuity within one conversation. Context providers temporarily project selected application data into the current invocation.

```text
TravelerMemoryStore -> durable preferences
TravelInvocationContext -> current server-resolved facts
AIContextProvider -> model-facing projection for one run
```

A provider does not become the owner of its source data. It does not write memory, mutate the session, or produce the final `TripPlan`. It reads an application-owned source and contributes only the context needed by the model.

That distinction makes replacement straightforward. If a traveler changes a preference from relaxed to active, the memory provider reads the current record on the next run. There is no provider cache or copied preference block to migrate inside every conversation.

## Carry only the state providers need

Providers execute inside the agent pipeline, but they still need trustworthy facts about the request they are serving. The sample defines a small immutable carrier:

```csharp
public sealed record TravelInvocationContext(
    Guid? TravelerId = null,
    Guid? ConversationId = null,
    SessionStatus? SessionStatus = null,
    string? Destination = null,
    int? DurationDays = null);
```

Each value has an application source. The conversation path resolves the traveler and conversation identifiers from stored state. The one-shot itinerary path supplies destination and duration from the request.

The object does not contain the user message, `AgentSession`, memory record, service provider, database context, tool registry, or trace. Those already have owners. Keeping the carrier narrow prevents it from becoming a request-scoped service locator.

Properties are nullable because the two invocation paths know different facts. A standalone plan has a destination and duration but no conversation ID. A conversational follow-up may not contain destination or duration that the application can safely extract.

## Scope invocation state across asynchronous execution

The providers need to see the right context during asynchronous callbacks without sharing it across concurrent requests. `TravelInvocationContextAccessor` uses `AsyncLocal<T>`:

```csharp
public sealed class TravelInvocationContextAccessor
{
    private readonly AsyncLocal<TravelInvocationContext?> _current = new();

    public TravelInvocationContext? Current => _current.Value;

    public IDisposable Push(TravelInvocationContext context)
    {
        TravelInvocationContext? previous = _current.Value;
        _current.Value = context;
        return new Scope(() => _current.Value = previous);
    }
}
```

`Push` returns a disposable scope instead of exposing unrelated `Set` and `Clear` operations. The scope restores the previous value, which makes nesting possible and cleanup reliable when used with `using`.

`TravelAgent` owns the scope around the actual framework call:

```csharp
using IDisposable invocation = _contextAccessor.Push(
    invocationContext ?? new TravelInvocationContext());

AgentResponse<TripPlan> result = await _agent.RunAsync<TripPlan>(
    message,
    session,
    options: CreateStructuredOutputOptions(),
    cancellationToken: cancellationToken);
```

Because disposal is lexical, the context is restored after success, exception, or cancellation. The accessor is registered as a singleton; logical async-flow isolation comes from `AsyncLocal`, not from storing one mutable `Current` value directly on that singleton.

This is in-process invocation state. It is not persisted, and it is not a distributed context propagation protocol.

## Project durable memory through a context provider

`TravelerMemoryContextProvider` derives from Agent Framework’s `AIContextProvider`. Its constructor supplies a message-transform callback:

```csharp
public TravelerMemoryContextProvider(
    TravelInvocationContextAccessor accessor,
    TravelerMemoryService memoryService,
    IExecutionTraceRecorder traceRecorder,
    ILogger<TravelerMemoryContextProvider> logger)
    : base(messages => AddContext(
        messages,
        accessor,
        memoryService,
        traceRecorder,
        logger))
{
}
```

The provider reads the server-resolved traveler ID from the current invocation context and requests only that traveler’s memory:

```csharp
Guid? travelerId = accessor.Current?.TravelerId;

TravelerMemory? memory = travelerId.HasValue
    ? memoryService.Get(travelerId.Value)
    : null;

if (memory is null)
{
    return messages;
}
```

The model never selects the traveler identity. The application resolves ownership before the agent runs, and the provider never receives all travelers’ records for the model to filter.

Only populated categories are projected. If no usable preference exists, the original message sequence is returned unchanged. Missing memory is therefore a valid no-context outcome, not an exception.

When preferences exist, the provider appends one system message:

```csharp
string context =
    "Relevant durable traveler preferences " +
    "(apply only when relevant):\n" +
    string.Join(
        "\n",
        preferences.Select(value => $"- {value}"));

return messages.Append(
    new ChatMessage(ChatRole.System, context));
```

The label marks the values as advisory. A current explicit request can differ from a stored default without silently rewriting the durable memory record.

## Add runtime facts through a separate provider

Runtime context has a different source and lifetime. `RuntimeTravelContextProvider` begins with an injected clock and conditionally adds fields available for the current run:

```csharp
List<string> values =
[
    $"Current UTC date and time: {timeProvider.GetUtcNow():O}"
];

if (current?.SessionStatus is { } status)
{
    values.Add($"Session status: {status}");
}

if (!string.IsNullOrWhiteSpace(current?.Destination))
{
    values.Add($"Requested destination: {current.Destination}");
}

if (current?.DurationDays is { } duration)
{
    values.Add($"Requested duration: {duration} days");
}
```

The implementation also projects traveler and conversation IDs when present. That demonstrates provenance, but production systems should ask whether a model genuinely needs internal identifiers. An identifier can be authoritative for selecting context without becoming model-visible text.

Unlike durable memory, runtime data has no store. It is reconstructed for each run and disappears when the invocation scope ends.

## Register providers at the agent boundary

The accessor and providers are normal dependency-injection services:

```csharp
builder.Services.AddSingleton<TravelInvocationContextAccessor>();
builder.Services.AddSingleton<TravelerMemoryContextProvider>();
builder.Services.AddSingleton<RuntimeTravelContextProvider>();
```

`TravelAgent` registers both through `ChatClientAgentOptions.AIContextProviders`:

```csharp
_agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = nameof(TravelAgent),
    ChatOptions = new ChatOptions
    {
        Instructions = TravelAgentInstructions.SystemPrompt,
        Tools =
        [
            AIFunctionFactory.Create(weatherTool.GetWeather),
            AIFunctionFactory.Create(currencyTool.ConvertCurrency),
            AIFunctionFactory.Create(timeZoneTool.GetLocalTime),
            AIFunctionFactory.Create(distanceTool.GetDistance)
        ]
    },
    AIContextProviders =
    [
        memoryContextProvider,
        runtimeContextProvider
    ]
});
```

Provider order is explicit. The memory provider sees the original sequence and may append a system message. The runtime provider receives that resulting sequence and appends its own system message.

Tools remain a different extension point. Providers enrich messages before model execution; tools are capabilities the model may choose during the run. A provider may give the model a destination that helps it select `GetWeather`, but the provider neither invokes nor routes the tool.

## Preserve the original message path

In Chapter 7, `ConversationService` built an `enrichedMessage` by concatenating durable preferences with the current request. Chapter 8 removes that construction.

The service now builds a separate invocation context and passes the original message unchanged:

```csharp
var invocationContext = new TravelInvocationContext(
    travelerId,
    conversationId,
    status,
    ParseDestination(message),
    ParseDuration(message));

TripPlanResponse response =
    await travelAgent.SendMessageAsync(
        message,
        conversation.Session,
        cancellationToken,
        invocationContext);
```

The application can now answer two different questions clearly:

```text
What did the user send? -> message
What context did the application add? -> registered providers
```

Provider messages participate in the framework’s invocation pipeline, but the application no longer disguises them as part of the input string supplied by the user.

The sample’s conversational destination parser is intentionally conservative and limited. It looks for `to`, `in`, or `for`, while duration parsing recognizes a one- or two-digit value followed by `day` or `days`. These helpers produce optional runtime hints; they are not a general natural-language parser and do not replace the model’s interpretation.

## Trace provider execution without copying context values

Moving enrichment behind providers should not make it operationally invisible. Chapter 8 adds a separate provider trace record:

```csharp
public sealed record ContextProviderExecution
{
    public required int Order { get; init; }
    public required string ProviderName { get; init; }
    public required string ContextCategory { get; init; }
    public required long DurationMs { get; init; }
    public required bool ContextAdded { get; init; }
    public required string Status { get; init; }
}
```

`ContextAdded` distinguishes success with enrichment from success without enrichment. Provider execution has its own order counter because context preparation and model-selected tool calls are different phases.

The trace deliberately excludes the generated context and preference values. It records that the traveler-memory provider ran, not which accessibility requirement it exposed. This prevents observability from becoming another persistence channel for personal data.

The recorder rethrows provider exceptions after adding a failure entry. Each provider catches at its own optional-enrichment boundary, logs a warning, and returns the incoming messages. In this sample, an unexpected provider failure therefore degrades to no added context rather than failing the trip request.

That is a product policy, not a universal rule. Applications where context is required for authorization or safety should not silently downgrade it to optional enrichment.

## Test what the application controls

The Chapter 8 tests directly exercise the provider projection helpers.

`ContextProviderTests` creates memory for two travelers, pushes one traveler ID, and confirms that the appended message contains only that traveler’s preferences. It verifies that missing memory adds no message and that the runtime provider includes traveler ID, conversation ID, destination, and duration.

An updated `TravelerMemoryTests` composition test uses a fake travel agent and asserts that `ConversationService` passes exactly `Plan a trip to Tokyo.` rather than a concatenated memory block. It also confirms that only the active conversation’s message count changes and that durable memory remains separate.

These are useful deterministic boundaries, but the suite does not currently test several manuscript-level expectations:

- it does not run the context providers through a live configured `AIAgent`;
- it does not inspect persisted framework history to prove provider messages never accumulate there;
- it does not assert provider trace order, status, duration, or value redaction;
- it does not verify nested accessor restoration or concurrent invocation isolation;
- it does not show that provider context changes the behavior of a live Azure OpenAI model.

Those require additional integration and concurrency tests. Claims about provider history semantics should be verified against the exact Agent Framework version used in production rather than inferred only from the application’s unchanged message string.

## Understand the production boundaries

The design creates a clean enrichment seam, not a complete context platform.

- The accessor is process-local and depends on .NET logical execution-context flow.
- Provider failures are treated as optional and logged rather than surfaced to callers.
- Runtime destination and duration extraction is deliberately narrow.
- Provider output is textual, so formatting and selective disclosure still matter.
- The runtime provider currently exposes operational identifiers to the model.
- Context providers do not provide authentication, authorization, consent, encryption, or retention controls.
- Trace metadata shows execution behavior, not whether the context improved the response.
- The provider list is explicit; it does not discover arbitrary context sources dynamically.

Context providers are also not workflow stages. They run inside one `TravelAgent` invocation, before the model and its possible tool calls. Keeping that boundary clear avoids turning every source of contextual information into a new orchestration abstraction.

## Preserve provenance at the invocation boundary

The core pattern is small:

```text
resolve authoritative application state
  -> push a narrow invocation context
  -> run registered context providers
  -> append only relevant model-facing context
  -> send the original user message through the agent path
  -> trace provider metadata without copying values
```

The model receives richer input, but the application retains a clear account of where each part came from. Conversation history remains the responsibility of `AgentSession`, durable preferences remain in the memory store, and runtime facts exist only for the current execution.

That provenance boundary is what turns context injection from string formatting into an architectural capability.

## Continue Exploring

This article is derived from Chapter 8, “Adding Context Providers,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 8 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-08)
