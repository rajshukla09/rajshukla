# Persisting Microsoft Agent Framework Sessions Safely in .NET

A conversation ID is not a conversation. It is only a key that remains useful while the state behind it still exists.

An in-memory dictionary can preserve agent context across HTTP requests, but it loses every conversation when the application restarts. Persisting only timestamps and message counts is also insufficient: a newly created `AgentSession` does not contain the framework-managed context from earlier turns.

The Chapter 5 Smart Travel Planner persists both sides of the boundary:

```text
application-owned identity and lifecycle metadata
  +
framework-owned serialized AgentSession
  → durable conversation document
```

The implementation uses a local JSON snapshot to make the mechanics visible. The important pattern is not the choice of JSON. It is preserving framework state through its supported serialization contract without turning a live runtime object into the storage schema.

## Persist reconstruction data, not runtime objects

`ConversationState` is designed for a running process. It contains an `AgentSession`, mutable lifecycle data, a private synchronization lock, and a `SemaphoreSlim` used to coordinate conversation operations.

Serializing that class directly would mix durable facts with process-local machinery. Chapter 5 introduces a separate storage contract:

```csharp
public sealed record ConversationDocument(
    Guid Id,
    JsonElement AgentSession,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset ExpirationTime,
    int MessageCount);

public sealed record ConversationStoreDocument(
    int Version,
    IReadOnlyCollection<ConversationDocument> Conversations);
```

The document contains enough information to reconstruct a live conversation after restart. It deliberately excludes locks, services, loggers, `TimeProvider`, and dependency-injection objects. Those belong to the new process and are recreated when `ConversationState` is restored.

This separation also prevents ordinary runtime refactoring from silently redefining the durable format.

## Treat serialized agent state as opaque

The application owns conversation IDs and lifecycle metadata. Microsoft Agent Framework owns the internal representation of `AgentSession`.

The storage layer therefore depends on a narrow adapter:

```csharp
public interface IAgentSessionSerializer
{
    JsonElement SerializeSession(AgentSession session);

    AgentSession DeserializeSession(JsonElement session);
}
```

`TravelAgent` already owns the configured `AIAgent`, so it implements the adapter through the framework-supported methods:

```csharp
public JsonElement SerializeSession(AgentSession session) =>
    _agent.SerializeSessionAsync(session)
        .AsTask().GetAwaiter().GetResult();

public AgentSession DeserializeSession(JsonElement session) =>
    _agent.DeserializeSessionAsync(session)
        .AsTask().GetAwaiter().GetResult();
```

The JSON store never inspects framework fields, attempts to serialize `AgentSession` with `JsonSerializer`, or rebuilds a session by guessing at message history. It stores the resulting `JsonElement` and returns it to a compatible configured agent during restoration.

The synchronous blocking adapter exists because this chapter retains the existing synchronous `IConversationStore` contract. An asynchronous persistence abstraction would be a reasonable later design, but it is not what this implementation provides.

Dependency injection registers one `TravelAgent` singleton through both application roles:

```csharp
builder.Services.AddSingleton<TravelAgent>();
builder.Services.AddSingleton<ITravelAgent>(services =>
    services.GetRequiredService<TravelAgent>());
builder.Services.AddSingleton<IAgentSessionSerializer>(services =>
    services.GetRequiredService<TravelAgent>());
```

Conversation execution and session serialization therefore use the same configured agent instance.

## Keep runtime lookup separate from durable documents

`JsonConversationStore` maintains two dictionaries:

```csharp
private readonly ConcurrentDictionary<Guid, ConversationState>
    _conversations = new();

private readonly ConcurrentDictionary<Guid, ConversationDocument>
    _documents = new();
```

The first supports live request lookup. The second holds the snapshot representations that will be written to disk.

Adding a conversation creates the runtime state, captures its document, and flushes the complete snapshot:

```csharp
public ConversationState Add(AgentSession session)
{
    ConversationState conversation;
    do
    {
        conversation = new ConversationState(
            Guid.NewGuid(),
            session,
            _timeProvider.GetUtcNow(),
            _options.ExpirationTimeout);
    }
    while (!_conversations.TryAdd(conversation.Id, conversation));

    Capture(conversation);
    Flush();
    return conversation;
}
```

Updates recapture the current session and lifecycle metadata. Deletion removes both representations before flushing. This preserves the store abstraction used by `ConversationService`; controllers and HTTP contracts do not know that persistence is JSON-backed.

## Persist only after successful state changes

Persistence follows the same successful-mutation rule used by the conversation lifecycle.

After an agent turn completes, the service records activity and then updates the store:

```csharp
TripPlan plan = await travelAgent.SendMessageAsync(
    message,
    conversation.Session,
    cancellationToken);

conversation.RecordMessage(
    timeProvider.GetUtcNow(),
    LifecycleOptions);

conversationStore.Update(conversation);
```

If agent execution throws or is cancelled, neither `RecordMessage` nor `Update` runs. The previous durable representation remains the recovery point.

Lifecycle-only changes must be persisted too. When a message request discovers that the expiration deadline has passed, the agent is not called, but the newly derived `Expired` state is still saved:

```csharp
if (status == SessionStatus.Expired)
{
    conversationStore.Update(conversation);
    return new SendMessageResult(SendMessageOutcome.Expired);
}
```

Explicit expiration also updates the state before persistence. Deletion and cleanup remove documents and flush the resulting collection. Read operations normally should not write, although this implementation calls `Update` from `Get` and `ListActive` because refreshing lifecycle status during a read can itself transition a conversation to `Idle` or `Expired`.

The store serializes the current `AgentSession` on every capture. Reusing an old serialized value would restore stale conversational context after restart.

## Replace snapshots atomically

Writing directly over the destination file creates a failure window. If serialization or writing stops after the existing file is truncated, the application may lose the previous usable snapshot as well as the new one.

`Flush` builds the current ordered document collection, writes a temporary file, and then moves it over the destination:

```csharp
string temporaryPath = _filePath + ".tmp";

File.WriteAllText(
    temporaryPath,
    JsonSerializer.Serialize(
        new ConversationStoreDocument(1, documents),
        JsonOptions));

File.Move(temporaryPath, _filePath, true);
```

A private lock serializes flush operations within the process:

```csharp
private readonly object _fileLock = new();
```

That makes the fixed `.tmp` path usable for this single-process implementation and prevents two local flushes from interleaving.

The temp-file replacement prevents readers from observing the intermediate write. It is safer than overwriting the destination in place, but it is not a transaction across the agent call, memory mutation, and filesystem. It also provides no coordination between multiple application processes.

## Restore valid conversations independently

Persistence becomes part of startup, so recovery behavior deserves an explicit boundary.

The store handles several cases without preventing the API from starting:

- A missing file starts with an empty store.
- An empty file starts with an empty store.
- Malformed JSON is logged and treated as an empty store.
- A root without a `conversations` array loads no records.
- An invalid conversation element is skipped without discarding valid siblings.

Before session deserialization, the loader rejects documents with an empty ID, negative message count, invalid or `Removed` status, missing creation time, activity before creation, or expiration before last activity.

For a valid document, it restores framework state first and then constructs the runtime object:

```csharp
AgentSession session =
    _sessionSerializer.DeserializeSession(document.AgentSession);

ConversationState state = new(
    document.Id,
    session,
    document.CreatedAt,
    document.LastActivityAt,
    document.ExpirationTime,
    document.MessageCount,
    document.Status);
```

If framework deserialization fails, the record is logged and skipped. Creating an empty replacement session would preserve the ID while silently discarding the conversation context the caller expects.

Duplicate IDs use a first-valid-record-wins rule. Session restoration occurs before `TryAdd`, so an invalid first record does not reserve the ID and block a later valid one.

The loader does not rewrite a partially valid file immediately. A later successful mutation produces a fresh snapshot from the valid runtime collection.

## Test restart recovery without a live model

`JsonConversationStoreTests` use a unique temporary directory and inject the file-path constructor. The session serializer is replaced with a deterministic test adapter:

```csharp
private sealed class TestSessionSerializer : IAgentSessionSerializer
{
    public JsonElement SerializeSession(AgentSession session) =>
        JsonSerializer.SerializeToElement(new { kind = "test" });

    public AgentSession DeserializeSession(JsonElement session) => null!;
}
```

The central restart test creates one store, records activity, explicitly expires another conversation, and then constructs a second store over the same file:

```csharp
JsonConversationStore restarted = CreateStore(clock);

Assert.Equal(2, restarted.GetAll().Count);
ConversationMetadata restoredOne =
    AssertRestored(restarted, one.Id);

Assert.Equal(1, restoredOne.MessageCount);
Assert.Equal(SessionStatus.Active, restoredOne.Status);
```

Other tests verify durable deletion, durable cleanup, missing and empty files, malformed JSON, and an invalid record. The lifecycle tests continue to exercise expiration and independent sessions, while endpoint tests substitute a fake conversation service.

These tests do not exercise real Microsoft Agent Framework session serialization, confirm conversation meaning after an actual process restart, simulate interrupted writes, assert atomic replacement behavior, test file permission or disk-full failures, validate duplicate-record recovery, or run concurrent store mutations. Those remain important integration and fault-injection concerns for production.

## Understand the consistency boundary

The implementation logs serialization and flush failures instead of propagating them. That choice keeps the sample API running, but it weakens the durability guarantee.

If `Capture` fails for an existing conversation, the previous document can remain in `_documents`, and `Flush` may write that stale representation. If a file write or move fails, the in-memory mutation still appears successful to its caller while the disk snapshot remains older.

There is no retry queue, health signal, transaction log, backup rotation, fsync policy, or startup reconciliation with another source of truth. The root document writes `Version = 1`, but the loader does not enforce that value or implement migrations.

The local file is also unsuitable for horizontal scaling. Each process would have independent runtime dictionaries and a process-local lock; shared access to one file would not provide distributed concurrency control. Hosted local disk may be ephemeral.

Finally, the persisted `JsonElement` is framework-owned and must remain compatible with the configured agent and framework version used to restore it. The sample does not define an upgrade compatibility strategy.

These limitations are precisely why `IConversationStore` and `IAgentSessionSerializer` matter. A database-backed implementation can replace the local snapshot without pushing storage details into controllers or the agent-facing conversation contract.

## Make restoration an explicit contract

Durable conversational continuity requires more than saving an identifier or a transcript-shaped approximation. The application must preserve its own lifecycle facts and the framework state required to continue execution.

Chapter 5 makes that recovery path explicit:

```text
successful conversation change
  → serialize current AgentSession
  → capture application metadata
  → atomically replace durable snapshot
  → restart
  → validate each document
  → deserialize AgentSession
  → recreate runtime locks
  → continue by the same conversation ID
```

The design is intentionally modest, but the boundary is sound: persist only what is needed to reconstruct the resource, and let each owner serialize the state it understands.

## Continue Exploring

This article is derived from Chapter 5, “Persisting Conversations and State,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 5 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-05)
