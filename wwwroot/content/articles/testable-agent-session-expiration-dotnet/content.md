# Designing Testable Agent Session Expiration in .NET

Conversation continuity answers which agent session belongs to the next request. It does not answer whether that session should still be usable.

Once an application retains Microsoft Agent Framework `AgentSession` instances across requests, it owns a lifecycle problem. Abandoned conversations consume resources. Expiration can race with an active turn. Cleanup can remove state while another request is preparing to use it. Rules based directly on the system clock are awkward to test.

The Chapter 4 Smart Travel Planner addresses those concerns with an application-owned lifecycle around each framework session:

```text
conversation ID
  → ConversationState
      → AgentSession
      → lifecycle timestamps and status
      → per-conversation coordination
```

The central pattern is to make time and lifecycle policy explicit, evaluate eligibility before model execution, and coordinate expiration with the same lock that protects conversation turns.

## Keep lifecycle policy outside the agent

`AgentSession` carries the framework state needed for a continuing interaction. It does not decide how long the application's conversation should remain available.

The application defines five lifecycle states:

```csharp
public enum SessionStatus
{
    Created,
    Active,
    Idle,
    Expired,
    Removed
}
```

They have deliberately different meanings:

- `Created` means the conversation exists but has not completed a message.
- `Active` means at least one message completed successfully within the idle window.
- `Idle` means inactivity crossed the idle threshold, but another turn is still allowed.
- `Expired` means the deadline passed or an operator explicitly expired the conversation; new turns are rejected.
- `Removed` is a terminal marker applied before the entry leaves the store.

These are application states around `AgentSession`, not framework session states. The model is never asked whether a conversation should expire.

## Represent time policy with validated options

Hard-coded timeout values tend to spread through request handlers and cleanup jobs. Chapter 4 gives the policy one configuration model:

```csharp
public sealed class SessionLifecycleOptions
{
    public const string SectionName = "SessionLifecycle";

    [Range(1, 1_440)]
    public int IdleTimeoutMinutes { get; init; } = 15;

    [Range(2, 10_080)]
    public int ExpirationTimeoutMinutes { get; init; } = 30;

    public TimeSpan IdleTimeout =>
        TimeSpan.FromMinutes(IdleTimeoutMinutes);

    public TimeSpan ExpirationTimeout =>
        TimeSpan.FromMinutes(ExpirationTimeoutMinutes);
}
```

The checked-in configuration uses 15 minutes for idle status and 30 minutes for expiration. Startup validation checks both individual ranges and their relationship:

```csharp
builder.Services
    .AddOptions<SessionLifecycleOptions>()
    .Bind(builder.Configuration.GetSection(
        SessionLifecycleOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        settings => settings.ExpirationTimeoutMinutes
            > settings.IdleTimeoutMinutes,
        "ExpirationTimeoutMinutes must be greater than IdleTimeoutMinutes.")
    .ValidateOnStart();
```

The relational rule matters. An idle state is useful only if the session can enter it before reaching the hard expiration boundary.

Validating on startup prevents a malformed lifecycle policy from remaining hidden until the first conversation request.

## Treat the clock as a dependency

Lifecycle code repeatedly asks what time it is. Calling `DateTimeOffset.UtcNow` directly would couple the rules to wall-clock time and force tests to wait for real timeout periods.

.NET's `TimeProvider` makes the clock explicit:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
```

The store and conversation service receive that dependency and call:

```csharp
DateTimeOffset now = timeProvider.GetUtcNow();
```

Production uses `TimeProvider.System`. Tests supply a manual implementation whose value advances immediately. The lifecycle logic remains the same in both environments.

This is a broadly useful design rule: when business behavior depends on elapsed time, inject the source of time instead of hiding it in the method.

## Store deadlines, not only status labels

`ConversationState` owns the lifecycle data associated with one application conversation:

```csharp
public Guid Id { get; }
public AgentSession Session { get; }
public DateTimeOffset CreatedAt { get; }
public DateTimeOffset LastActivityAt { get; private set; }
public DateTimeOffset ExpirationTime { get; private set; }
public SemaphoreSlim TurnLock { get; } = new(1, 1);
```

A new conversation initializes `LastActivityAt` to its creation time and calculates `ExpirationTime` from the configured expiration timeout. It remains `Created` until a message finishes successfully.

Successful activity updates all related lifecycle data together:

```csharp
public void RecordMessage(
    DateTimeOffset timestamp,
    SessionLifecycleOptions options)
{
    lock (_lifecycleLock)
    {
        _messageCount++;
        LastActivityAt = timestamp;
        ExpirationTime = timestamp.Add(options.ExpirationTimeout);
        _status = SessionStatus.Active;
    }
}
```

The expiration deadline therefore slides forward only after a successful turn. Merely receiving a request does not refresh it.

The state uses a small private lock for consistent reads and writes of status, count, and timestamps. This is separate from `TurnLock`, which coordinates longer session operations such as an agent call, explicit expiration, deletion, and cleanup.

## Derive idle and expired state from current time

Lifecycle status is refreshed when the application inspects or uses the conversation:

```csharp
public SessionStatus RefreshStatus(
    DateTimeOffset now,
    SessionLifecycleOptions options)
{
    lock (_lifecycleLock)
    {
        if (_status is SessionStatus.Expired or SessionStatus.Removed)
        {
            return _status;
        }

        if (now >= ExpirationTime)
        {
            _status = SessionStatus.Expired;
        }
        else if (_messageCount > 0
            && now - LastActivityAt >= options.IdleTimeout)
        {
            _status = SessionStatus.Idle;
        }

        return _status;
    }
}
```

Several boundary choices are visible here:

- Expiration wins over idle when both thresholds have been crossed.
- A never-used `Created` conversation does not become idle because idle evaluation requires a completed message.
- `Expired` and `Removed` are terminal within this object.
- Equality counts: the session becomes idle or expired exactly at the configured boundary.

An idle session may still receive a message. When that turn succeeds, `RecordMessage` moves it back to `Active` and extends the deadline.

## Reject expiration before invoking the model

A lifecycle policy is useful only if it is enforced before expensive or stateful work begins.

The service first resolves the conversation, then acquires its turn lock, then evaluates current status:

```csharp
await conversation.TurnLock.WaitAsync(cancellationToken);
try
{
    SessionStatus status = conversation.RefreshStatus(
        timeProvider.GetUtcNow(),
        LifecycleOptions);

    if (status == SessionStatus.Removed)
    {
        return new SendMessageResult(SendMessageOutcome.NotFound);
    }

    if (status == SessionStatus.Expired)
    {
        return new SendMessageResult(SendMessageOutcome.Expired);
    }

    TripPlan plan = await travelAgent.SendMessageAsync(
        message,
        conversation.Session,
        cancellationToken);

    conversation.RecordMessage(
        timeProvider.GetUtcNow(),
        LifecycleOptions);

    return new SendMessageResult(
        SendMessageOutcome.Success,
        plan);
}
finally
{
    conversation.TurnLock.Release();
}
```

Status is evaluated after acquiring the lock. That ordering prevents a request from making an eligibility decision, waiting behind another operation, and then acting on stale lifecycle state.

An expired conversation never reaches `TravelAgent` or Azure OpenAI. A successful result refreshes activity only after the agent call completes.

The controller preserves a useful distinction in HTTP semantics: an unknown or removed conversation produces `404 Not Found`, while a retained but expired conversation produces `410 Gone`.

## Coordinate expiration, deletion, and cleanup with turns

A thread-safe dictionary protects collection operations. It does not coordinate operations performed on the `ConversationState` retrieved from that dictionary.

Chapter 4 reuses the per-conversation `TurnLock` for every state-changing lifecycle operation. Explicit expiration waits for any active turn, then marks the session expired:

```csharp
conversation.TurnLock.Wait();
try
{
    conversation.Expire(timeProvider.GetUtcNow());
    return true;
}
finally
{
    conversation.TurnLock.Release();
}
```

Deletion follows the same boundary. It marks the state `Removed` before removing the dictionary entry. A request that already holds a reference must acquire the same lock and re-check status before it can continue.

Cleanup also re-evaluates each candidate while holding its lock:

```csharp
foreach (ConversationState conversation in conversationStore.GetAll())
{
    conversation.TurnLock.Wait();
    try
    {
        if (conversation.RefreshStatus(now, LifecycleOptions)
            != SessionStatus.Expired)
        {
            continue;
        }

        conversation.MarkRemoved();
        if (conversationStore.Delete(conversation.Id))
        {
            removed++;
        }
    }
    finally
    {
        conversation.TurnLock.Release();
    }
}
```

The initial store snapshot is only a set of candidates. The decision to remove a session is made again under the same coordination boundary as message execution.

Because the lock belongs to each conversation, cleanup of one session does not impose one global execution lock over every conversation.

## Expose management without leaking framework state

The API returns lifecycle metadata rather than `AgentSession`:

```csharp
public sealed record ConversationMetadata(
    Guid ConversationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset ExpirationTime,
    int MessageCount,
    SessionStatus Status);
```

Chapter 4 adds endpoints to list usable sessions, retrieve metadata, explicitly expire or delete one, and trigger cleanup. The active listing evaluates status at request time and excludes `Expired` and `Removed` entries.

Cleanup is manual in this implementation. `POST /api/sessions/cleanup` invokes it and returns only the number of entries removed. There is no hosted background cleanup service.

## Test lifecycle transitions without waiting

The lifecycle tests use a small manual `TimeProvider`:

```csharp
private sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow =
        new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) =>
        _utcNow = _utcNow.Add(duration);
}
```

One test records successful activity, advances ten minutes, and observes `Idle`. It advances another twenty minutes and observes `Expired`. Minutes of production time collapse into immediate deterministic transitions.

Another test advances directly to expiration and verifies both the outcome and the absence of an agent call:

```csharp
context.Clock.Advance(LifecycleOptions.ExpirationTimeout);

SendMessageResult result = await context.Service.SendMessageAsync(
    session.ConversationId,
    "Plan Jaipur.");

Assert.Equal(SendMessageOutcome.Expired, result.Outcome);
Assert.Equal(0, context.Agent.MessageCallCount);
```

The suite also verifies that cleanup removes only expired sessions, multiple independent sessions can complete concurrently, and a deleted session cannot be retrieved or messaged. Endpoint tests cover list, explicit expiration, `410 Gone`, and cleanup using a fake conversation service.

The tests do not exercise a real `AgentSession`, Azure OpenAI, same-session contention, cleanup racing an active turn, idle reactivation, failed-turn activity timestamps, or invalid lifecycle configuration at host startup. Those remain additional test cases for a production implementation.

## Know the reliability boundary

Lifecycle management makes process-local state more disciplined; it does not make it durable.

The store remains a `ConcurrentDictionary` in one API process. Restarting the process loses every conversation. Multiple API instances do not share state, and `SemaphoreSlim` does not provide distributed coordination.

Cleanup occurs only when its endpoint is called. There is no automatic schedule, capacity limit, memory budget, retention record, or audit history after removal. The synchronous `Wait()` calls used by explicit expiration, deletion, and cleanup can also block request threads while a long-running turn owns the lock.

An in-memory commit can still disappear in a crash. A conversation ID is only a key; it does not contain the messages, serialized session, or lifecycle metadata required for recovery.

These limits define the exact guarantee: while requests reach the same running process, the application can evaluate and coordinate session lifetime consistently. Persistence and multi-instance recovery require a durable state design.

## Make time part of the architecture

Session expiration is not merely a timestamp comparison. It is a policy enforced at the boundary between an application conversation and model execution.

By validating timeout configuration, injecting `TimeProvider`, refreshing status under per-conversation coordination, and testing transitions with a manual clock, the application turns an implicit resource leak into explicit behavior:

```text
find conversation
  → acquire its lock
  → evaluate lifecycle now
  → reject or execute
  → record only successful activity
```

That pattern applies beyond AI agents. Any stateful, time-limited resource becomes easier to reason about when the policy, clock, and concurrency boundary are visible in code.

## Continue Exploring

This article is derived from Chapter 4, “Managing Agent Sessions,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 4 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-04)
