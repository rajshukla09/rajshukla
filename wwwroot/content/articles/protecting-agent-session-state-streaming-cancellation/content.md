# Protecting Agent Session State During Streaming and Cancellation

Streaming an AI response changes more than perceived latency. It creates a state-management problem: output can reach the client before the agent turn has completed.

If the user cancels after receiving several fragments, should that incomplete turn become part of the conversation used by the next request? If the provider fails after the response begins, can the application safely continue from the previous turn?

The Chapter 3 Smart Travel Planner handles this with a commit-on-success boundary:

```text
last successful AgentSession
  → clone working session
  → stream the active turn
  → complete: commit working session
  → cancel or fail: discard working session
```

This pattern treats the stored session as committed conversation state. Streaming happens against a copy, so partial output can be delivered without prematurely advancing the authoritative session.

## Separate agent identity from conversation state

Reusing one `AIAgent` does not make independent invocations part of the same conversation. The agent owns stable behavior and instructions. An `AgentSession` carries the framework-managed state that allows later turns to build on earlier ones.

Chapter 3 gives the application its own opaque conversation ID and maps it to a framework session:

```text
conversation ID
  → ConversationState
      → AgentSession
      → lifecycle metadata
      → per-conversation turn lock
```

The application exposes only metadata to clients:

```csharp
public sealed record ConversationMetadata(
    Guid ConversationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int MessageCount);
```

`AgentSession` and synchronization primitives remain internal. A client chooses which application conversation to continue; it never sends or manipulates a framework session object.

The in-memory store creates a separate session mapping for every conversation:

```csharp
private readonly ConcurrentDictionary<Guid, ConversationState>
    _conversations = new();
```

`ConcurrentDictionary` makes add, lookup, and removal safe across requests. It does not make the mutable `ConversationState` or its `AgentSession` safe for concurrent turns.

## Serialize turns per conversation

A conversation is ordered. If two follow-up messages target the same session concurrently, both could begin from the same previous state or race while modifying the session.

Each `ConversationState` therefore owns a lock:

```csharp
public SemaphoreSlim TurnLock { get; } = new(1, 1);
```

The conversation service acquires it before executing a turn and always releases it:

```csharp
await conversation.TurnLock.WaitAsync(cancellationToken);
try
{
    // Execute one turn for this conversation.
}
finally
{
    conversation.TurnLock.Release();
}
```

The scope matters. This is not one global application lock. Two requests for the same conversation are serialized, while requests for different conversations can still run independently.

Cancellation is passed into `WaitAsync`, so a request can stop while waiting behind an active turn rather than waiting indefinitely to acquire the conversation.

## Why streaming needs a working session

The non-streaming path waits for a complete typed result:

```csharp
AgentResponse<TripPlan> result = await _agent.RunAsync<TripPlan>(
    message,
    session,
    cancellationToken: cancellationToken);
```

The service records the message only after the call returns and validation succeeds. There is a clear completion point before the API sends the result.

Streaming is different:

```csharp
await foreach (AgentResponseUpdate update in _agent
    .RunStreamingAsync(message, session, cancellationToken: cancellationToken)
    .WithCancellation(cancellationToken))
{
    yield return update.Text;
}
```

Several updates may already be visible in the browser when cancellation or a provider failure interrupts the turn. Running this operation directly against the stored session would make an incomplete execution operate on state that the application considers authoritative.

The implementation avoids rollback by delaying the commit. Before streaming, `TravelAgent` clones the session through Microsoft Agent Framework serialization:

```csharp
public async Task<AgentSession> CloneSessionAsync(
    AgentSession session,
    CancellationToken cancellationToken = default)
{
    JsonElement state = await _agent.SerializeSessionAsync(
        session,
        cancellationToken: cancellationToken);

    return await _agent.DeserializeSessionAsync(
        state,
        cancellationToken: cancellationToken);
}
```

The application does not copy framework session properties itself. Serialization and deserialization stay behind `ITravelAgent`, alongside the other Microsoft Agent Framework operations.

## Stream first, commit only after completion

`ConversationService` performs the entire state transition:

```csharp
AgentSession workingSession = await travelAgent.CloneSessionAsync(
    conversation.Session,
    cancellationToken);

await foreach (string update in travelAgent
    .StreamMessageAsync(message, workingSession, cancellationToken)
    .WithCancellation(cancellationToken))
{
    yield return update;
}

cancellationToken.ThrowIfCancellationRequested();
conversation.ReplaceSession(workingSession);
conversation.RecordMessage(timeProvider.GetUtcNow());
```

Four details make this boundary work:

- The working session starts from the last successful conversation state.
- Every streaming update is produced against that working session.
- `ThrowIfCancellationRequested` checks again after enumeration finishes.
- Session replacement and metadata updates occur only after successful completion.

If iteration throws, cancellation is requested, or cloning fails, execution never reaches `ReplaceSession`. The working session becomes unreachable and the previous session remains stored.

This is transaction-like behavior, not a database transaction. There is no rollback operation. The design prevents uncommitted state from replacing committed state in the first place.

Message count and last-activity time follow the same rule. They describe successfully completed turns, not requests that merely started or emitted some text.

## Preserve the commit order with the turn lock

Clone-and-commit is safe only if another turn cannot start from the same stored session while the first turn is active.

Without the per-conversation lock, two requests could both clone version A:

```text
turn 1: clone A → working B
turn 2: clone A → working C
```

If both completed, the last replacement would overwrite the other turn's state. The conversation would no longer have a deterministic order.

Holding `TurnLock` across clone, stream, and commit gives each turn one serial state transition:

```text
committed A
  → turn 1 working copy
  → committed B
  → turn 2 working copy
  → committed C
```

The lock therefore protects conversation ordering, not just thread safety around a property assignment.

## Carry real streaming across the HTTP boundary

The agent method returns `IAsyncEnumerable<string>` and yields nonempty `AgentResponseUpdate.Text` values as Microsoft Agent Framework produces them. It does not wait for a complete answer and split it afterward.

The controller exposes those fragments as newline-delimited JSON:

```csharp
Response.ContentType = "application/x-ndjson";
Response.Headers.CacheControl = "no-cache";

await foreach (string delta in updates.WithCancellation(cancellationToken))
{
    await WriteUpdateAsync(
        new ConversationStreamUpdate("generating", delta),
        cancellationToken);
}
```

`WriteUpdateAsync` serializes one object, writes a newline, and flushes the response body:

```csharp
await JsonSerializer.SerializeAsync(
    Response.Body,
    update,
    StreamJsonOptions,
    cancellationToken);

await Response.WriteAsync("\n", cancellationToken);
await Response.Body.FlushAsync(cancellationToken);
```

Flushing is important because the endpoint is intended to expose each update while the response remains open. The wire contract uses `generating`, `completed`, `cancelled`, and `failed` statuses rather than leaking `AgentResponseUpdate` through HTTP.

Once the first bytes have been written, the endpoint cannot replace the response with a conventional error document. A failure after streaming starts is represented by a final `failed` update with a controlled error message when the connection remains writable.

## Propagate cancellation through every layer

The browser creates an `AbortController` for the active request and retains the response reader. Pressing Stop marks the request as cancelled, then cancels the reader if streaming has begun or aborts the fetch if it has not:

```javascript
activeRequest.cancelled = true;
setStatus('cancelled');

if (activeRequest.reader) {
  activeRequest.reader.cancel().catch(() => {});
} else {
  activeRequest.controller.abort();
}
```

ASP.NET Core exposes request cancellation through the controller's `CancellationToken`. The same token travels through `ConversationService`, `TravelAgent`, and `RunStreamingAsync`.

Cancellation is cooperative. Passing the token does not forcibly terminate arbitrary work; each asynchronous operation must observe it. Dropping the token at any layer could leave agent execution running after the client has stopped listening.

The browser implementation removes the partial assistant message after intentional cancellation. That is a UI choice. It cannot retract bytes already delivered, and it does not determine whether server-side session state commits. The service's clone-and-commit boundary makes that decision.

Stopping also differs from deleting. Cancellation abandons the active turn but keeps the conversation at its last completed state. Deletion removes the conversation ID and its stored session entirely.

## Test what the application can guarantee

The conversation endpoint tests replace `IConversationService` with a fake. The streaming success test reads the response using `ResponseHeadersRead`, observes separate NDJSON lines, and then sends another message to verify continuity:

```csharp
using HttpResponseMessage response = await client.SendAsync(
    request,
    HttpCompletionOption.ResponseHeadersRead);

string? generating = await reader.ReadLineAsync();
string? firstDelta = await reader.ReadLineAsync();
string? secondDelta = await reader.ReadLineAsync();
```

The cancellation test reads initial output, cancels the client read, disposes the response stream to model a disconnect under `TestServer`, and then sends another message. It verifies that the cancelled message is absent and that the successful follow-up is the only committed message.

The fake service mirrors commit-on-success by adding the message only after every update has been yielded. This tests the HTTP streaming and cancellation behavior, but it does not execute the real `ConversationService`, serialize a real `AgentSession`, or call Microsoft Agent Framework.

`StreamingUiTests` inspect the JavaScript and verify that it uses a stream reader, appends deltas progressively, supports Stop, restores UI controls, and does not call `response.text()`. These are source-level contract checks rather than browser automation.

No automated test calls Azure OpenAI or judges whether a model preserves conversational meaning. The repository tests deterministic application behavior and leaves live provider integration and model-quality evaluation as separate concerns.

## Understand the boundary of the sample

The store is a process-local `ConcurrentDictionary`. Every conversation disappears on restart, and separate API instances do not share state. The per-conversation semaphore is also process-local; it is not distributed coordination.

There is no idle expiration, maximum lifetime, capacity limit, background cleanup, durable transcript, retention policy, or recovery mechanism. Deletion can also race with an already resolved conversation because the sample does not coordinate deletion with `TurnLock`.

The streaming path emits conversational text rather than a progressively materialized `TripPlan`. The separate buffered path still uses `RunAsync<TripPlan>` when the application needs a complete typed object that can be validated as a whole.

Finally, clone-and-commit protects the session reference stored by this application. Its correctness still depends on the framework's session serialization representing the state required to resume the conversation.

## Treat streamed turns as provisional work

Streaming optimizes responsiveness, but output visibility and state acceptance are different events. A fragment reaching the browser does not mean the turn should become the foundation for future requests.

By cloning the last successful session, serializing turns per conversation, propagating cancellation, and committing only after full completion, the application keeps that distinction explicit:

```text
visible output can be partial
committed conversation state cannot
```

That is the essential production lesson. Stream optimistically for the user experience; advance authoritative session state conservatively.

## Continue Exploring

This article is derived from Chapter 3, “Building Multi-Turn Conversations,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 3 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-03)
