# Maintaining Remote Agent Sessions over A2A in .NET

Calling a remote Agent once is relatively straightforward. Maintaining a useful conversation across calls is the harder boundary: the coordinator must discover the remote capability, create the protocol-backed session, associate it with the caller’s conversation, and handle the fact that the remote service may disappear between requests.

The Chapter 8 implementation in *Building AI Agents with .NET — Part 2* uses the Agent-to-Agent (A2A) protocol and Microsoft Agent Framework to make that boundary explicit:

```text
coordinator request
    -> discover Agent Card over HTTP
    -> adapt card with AgentCard.AsAIAgent
    -> create or reuse AgentSession
    -> send remote message over A2A
    -> pass remote advice to local coordinator
```

The application owns conversation lookup and call ordering. The remote Agent owns its own model execution. A2A carries communication between independently hosted services; it does not provide durable conversation storage or a global orchestration manager.

## Discover the remote Agent before creating a session

The coordinator resolves the configured destination service through its well-known Agent Card endpoint:

```csharp
var remoteBase = new Uri(
    configuration["DestinationExpert:BaseUrl"]
    ?? "http://localhost:5181");

var http = clients.CreateClient("a2a");
var resolver = new A2ACardResolver(
    remoteBase,
    http,
    logger: loggerFactory.CreateLogger<A2ACardResolver>());

var card = await resolver.GetAgentCardAsync(ct);
var endpoint = card.SupportedInterfaces
    .First()
    .Url;
```

The card describes `DestinationExpert`, its version, supported interface, protocol binding, and destination-advice skill. The coordinator returns a safe `CardView` to the browser and records an `AgentCardDiscovered` event before making the remote call.

Discovery is deterministic application behavior. The model does not choose which remote Agent to contact in this sample; the configured `DestinationExpert:BaseUrl` does. The implementation selects the first advertised interface, which is acceptable for the small sample but should become explicit version and binding validation in production.

An Agent Card is not an MCP tool catalog. MCP describes capabilities a host may expose to an Agent; A2A describes an independently hosted Agent endpoint and its skills.

## Adapt the card into a client-side MAF agent

After discovery, the coordinator adapts the card into a client-side `AIAgent`:

```csharp
var agent = card.AsAIAgent(
    http,
    loggerFactory: loggerFactory);
```

The returned object has the same high-level MAF interaction shape as a local Agent. `RunAsync` still accepts a message and an optional session, but the implementation sends the request across the A2A HTTP/JSON boundary to the independently hosted service.

That adaptation keeps protocol concerns at the edge. The coordinator does not construct A2A JSON envelopes itself, parse remote message objects, or call the destination service’s internal classes. The official adapter owns the wire protocol while application code owns the conversation mapping and error policy.

The destination service publishes its own Agent and A2A endpoints:

```csharp
builder.AddA2AServer(agent);
app.MapWellKnownAgentCard(card);
app.MapA2AHttpJson(
    agent,
    "/a2a/destination-expert");
```

These are two independent ASP.NET Core hosts in the integration test. They communicate over HTTP even though both use the same MAF abstractions.

## Map caller conversations to AgentSession

The coordinator accepts an optional conversation ID. If the caller does not provide one, it creates a new identifier:

```csharp
var conversationId =
    string.IsNullOrWhiteSpace(request.ConversationId)
        ? Guid.NewGuid().ToString("N")
        : request.ConversationId;
```

It then looks up a cached remote conversation:

```csharp
private readonly ConcurrentDictionary<
    string,
    RemoteConversation> _conversations = new();

private async Task<RemoteConversation> GetConversationAsync(
    string id,
    AgentCard card,
    HttpClient http,
    CancellationToken ct)
{
    if (_conversations.TryGetValue(id, out var existing))
        return existing;

    var agent = card.AsAIAgent(
        http,
        loggerFactory: loggerFactory);
    var created = new RemoteConversation(
        agent,
        await agent.CreateSessionAsync(ct));

    return _conversations.GetOrAdd(id, created);
}
```

The session is then passed to every remote call for that conversation:

```csharp
var response = await conversation.Agent.RunAsync(
    remoteRequest,
    conversation.Session,
    cancellationToken: ct);
```

The follow-up request therefore reuses the same `ConversationId` and `AgentSession`. In DemoMode, the destination Agent returns different advice when the prompt asks what it recommended for the second day, allowing the integration test to demonstrate that the second call reached the remote session path.

The caller-provided conversation ID is a lookup key, not an authenticated identity. A production service must bind it to an authenticated user and tenant, validate its format, and prevent one caller from reading another caller’s session.

## Keep remote advice separate from local synthesis

The coordinator does not return the remote text as the final answer. It labels the remote advice and sends it to a local coordinator Agent:

```csharp
var final = await coordinator.RunAsync(
    $"User request:\n{request.Message}\n\n" +
    $"DestinationExpert response received over A2A:\n" +
    remoteResponse,
    cancellationToken: ct);
```

The label is an application boundary. It tells the local Agent that the content came from a remote service rather than from the user’s original request or trusted system instructions.

The API response keeps both values:

```csharp
return new TravelResult(
    "Completed",
    conversationId!,
    request.Message,
    remoteRequest,
    remoteResponse,
    final.Text,
    cardView,
    total.ElapsedMilliseconds,
    events);
```

This makes it possible to inspect what the remote Agent said and what the local coordinator synthesized. It also avoids presenting the remote response as if it were the local Agent’s conclusion.

Remote advice remains untrusted model input. The local coordinator should not treat text returned by the remote service as authorization to perform a side effect, and production prompts should define how conflicts or unsupported claims are handled.

## Handle outage at the service boundary

The coordinator maps discovery, remote invocation, and local synthesis failures to a failed `TravelResult`. The HTTP endpoint returns 503 while the coordinator health endpoint remains available:

```csharp
app.MapPost(
    "/api/travel",
    async (
        TravelRequest request,
        CoordinatorService service,
        CancellationToken ct) =>
    {
        var result = await service.RunAsync(request, ct);
        return result.Status == "Failed"
            ? Results.Json(result, statusCode: 503)
            : Results.Ok(result);
    });
```

The catch block deliberately avoids converting cancellation into a remote outage:

```csharp
catch (Exception ex)
    when (!ct.IsCancellationRequested)
{
    Add(
        "Failed",
        false,
        $"DestinationExpert is unavailable or the " +
        $"A2A exchange failed: {ex.Message}");

    return new TravelResult(
        "Failed",
        conversationId!,
        request.Message,
        remoteRequest,
        remoteResponse,
        null,
        cardView,
        total.ElapsedMilliseconds,
        events,
        "DestinationExpert is unavailable. The coordinator " +
        "is still running; start the remote service and try again.");
}
```

The sample uses one broad 503 category for card discovery, A2A exchange, and local synthesis failures. A production API should distinguish invalid cards, authentication failures, timeouts, remote 4xx/5xx responses, and local model failures where clients need different recovery actions.

## Understand what the session cache does not provide

The `_conversations` dictionary is process-local:

```csharp
private readonly ConcurrentDictionary<
    string,
    RemoteConversation> _conversations = new();
```

It does not survive coordinator restart, does not expire entries, and does not remove sessions. A concurrent first request can also construct an unused extra session before `GetOrAdd` returns the winner.

These are not minor implementation details. They define the durability and resource boundary:

- coordinator restart loses the local mapping;
- memory usage grows with caller-provided conversation IDs;
- there is no idle-session cleanup;
- there is no persistent session store or recovery process; and
- a remote Agent may have its own session semantics that are not represented in the coordinator cache.

A production design could use a bounded cache, per-user session repository, expiration policy, distributed lock, or a remote conversation identifier stored with the business conversation. It would still need to handle remote session invalidation and reauthentication.

## Verify the whole network boundary

The integration test starts two real Kestrel hosts on dynamically chosen ports:

```csharp
await using var destination =
    DestinationExpertApplication.Build(
        ["--urls", destinationUrl,
         "--DemoMode=true",
         $"--PublicUrl={destinationUrl}"]);

await using var coordinator =
    TravelCoordinatorApplication.Build(
        ["--urls", coordinatorUrl,
         "--DemoMode=true",
         $"--DestinationExpert:BaseUrl={destinationUrl}"]);

await destination.StartAsync();
await coordinator.StartAsync();
```

It verifies health, fetches the Agent Card over HTTP, posts a first travel request, checks the remote response and local final response, and confirms the destination’s invocation counter increased. It then reuses the first response’s conversation ID for a follow-up and confirms the second-day response. Finally, it stops the destination host, expects HTTP 503 from the coordinator, and verifies the coordinator health endpoint remains available.

This proves an actual A2A network exchange and session reuse in DemoMode. It does not prove Azure OpenAI behavior, persistent sessions, authentication, TLS policy, retries, streaming, or distributed tracing.

## Keep A2A distinct from in-process orchestration

The coordinator and destination are independent services. The coordinator does not expose the destination as a local specialist function, and the destination does not participate in a shared native MAF workflow graph.

The deterministic sequence is:

```text
discover remote Agent
    -> call remote Agent with a session
    -> receive remote advice
    -> call local coordinator Agent
    -> return both boundary results
```

That is application sequencing across an A2A boundary. It is not Magentic planning, a custom supervisor loop, or MCP tool discovery. MCP connects an Agent host to capabilities; A2A connects independently hosted Agents.

## Define production controls before adding more peers

The sample supports exactly one configured remote service and selects the first advertised interface. Before expanding to multiple remote Agents, define:

- trusted Agent Card sources and TLS requirements;
- authentication and authorization for both services;
- tenant and user identity propagation;
- supported protocol versions and interface binding validation;
- request and response size limits;
- timeouts, retries, circuit breaking, and cancellation;
- session expiration and persistence;
- prompt-injection handling for remote advice; and
- trace correlation across the service boundary.

None of these controls should be inferred from an Agent Card or caller-controlled conversation ID.

## Use sessions for continuity, not as a security boundary

The durable pattern from this chapter is:

```text
Agent Card discovery
    -> client-side AIAgent adapter
    -> conversation-to-AgentSession mapping
    -> remote A2A call
    -> local synthesis with labeled advice
    -> explicit outage and lifecycle policy
```

`AgentSession` provides conversational continuity through the MAF/A2A adapter. The application still owns identity, authorization, cache lifetime, failure handling, and the decision about what remote text the local Agent may use.

Treating those responsibilities separately makes it possible to evolve the sample from two local demo hosts into a production distributed-agent system without mistaking protocol support for durability or trust.

## Continue Exploring

This article is derived from Chapter 8, “Agent-to-Agent (A2A),” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 8 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-08)
