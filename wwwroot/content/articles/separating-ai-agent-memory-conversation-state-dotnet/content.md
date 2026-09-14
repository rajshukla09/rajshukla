# Separating Long-Term AI Agent Memory from Conversation State in .NET

An agent remembering an earlier turn is not the same as an application remembering a person.

Within one conversation, a Microsoft Agent Framework `AgentSession` can preserve the context needed for follow-up requests. A user might ask for a three-day itinerary and then say, “Make the second day less busy.” The session helps the model interpret that reference.

A reusable preference has a different owner and lifetime. “Vegetarian,” “step-free access,” or “relaxed pace” may need to apply across several independent conversations. Storing those values only inside one session makes them difficult to inspect, replace, delete, or reuse safely.

The Chapter 7 Smart Travel Planner separates three kinds of state:

```text
conversation -> user-visible sequence of turns
AgentSession -> framework state for continuing that conversation
traveler memory -> explicit person-owned preferences across conversations
```

That distinction is the foundation of a maintainable memory-aware agent. Long-term memory should be an application-owned resource with an explicit identity boundary—not an accidental side effect of accumulating prompt history.

## Start with ownership and lifetime

State that happens to contain similar words can still have different ownership rules.

One traveler may open several conversations. Each conversation owns its lifecycle and one framework session. The traveler owns one current set of reusable preferences. An execution trace belongs to a single agent run.

The sample models the durable identity separately:

```csharp
public sealed record TravelerProfile
{
    public required Guid TravelerId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

The profile intentionally contains no preferences and no session state. `TravelerId` is the partition key that connects a memory record to its owner and binds conversations to that owner.

This identifier is not authentication. The sample creates and resolves application identities, but production code still needs to bind them to authorized principals. Treating a GUID as a secret would leave the most important isolation rule unenforced.

## Store explicit preferences, not arbitrary dialogue

The long-term memory model contains a narrow list of supported preferences:

```csharp
public sealed record TravelerMemory
{
    public Guid TravelerId { get; init; }

    [MaxLength(20)]
    public IReadOnlyCollection<string> FoodPreferences { get; init; } = [];

    [MaxLength(20)]
    public IReadOnlyCollection<string> ActivityInterests { get; init; } = [];

    [StringLength(100)]
    public string? TravelPace { get; init; }

    [StringLength(100)]
    public string? BudgetPreference { get; init; }

    [MaxLength(20)]
    public IReadOnlyCollection<string> AccessibilityRequirements { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
}
```

This is deliberate memory. The model does not extract hidden traits from ordinary conversation, and tool results, itineraries, execution traces, and entire messages are not copied into the record.

A typed schema makes the stored categories inspectable and gives validation a predictable surface. It also places a useful constraint on product scope: adding a new durable category is an intentional application change rather than an unrestricted key/value write.

The request model omits `TravelerId` and `UpdatedAt`. The route selects the owner, and the application clock supplies the timestamp. A client cannot change ownership inside the body or choose its own audit time.

## Normalize without inventing meaning

`TravelerMemoryService` owns the management rules between the API and persistence. It removes blank values, trims surrounding whitespace, and de-duplicates list entries case-insensitively:

```csharp
private static IReadOnlyCollection<string> Clean(
    IReadOnlyCollection<string>? values) =>
    values?
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];
```

These operations remove representational noise. They do not reinterpret the preference. The service does not translate “cheap” into a numeric budget, infer dietary restrictions, or ask a model to classify the input before saving it.

Saving creates a complete new record with the route-owned identifier and injected time:

```csharp
return store.Upsert(new TravelerMemory
{
    TravelerId = travelerId,
    FoodPreferences = Clean(request.FoodPreferences),
    ActivityInterests = Clean(request.ActivityInterests),
    TravelPace = Clean(request.TravelPace),
    BudgetPreference = Clean(request.BudgetPreference),
    AccessibilityRequirements =
        Clean(request.AccessibilityRequirements),
    UpdatedAt = timeProvider.GetUtcNow()
});
```

The API uses whole-record replacement rather than an implicit merge. If a later PUT omits the travel pace, the new record has no travel pace; the previous value does not survive invisibly. That makes replacement and deletion behavior predictable for clients and tests.

## Bind conversations to an authoritative owner

Memory isolation depends on more than keying a dictionary by `TravelerId`. Every conversation must carry an immutable ownership link.

Chapter 7 adds `TravelerId` to the live conversation state, public metadata, and persisted conversation document:

```csharp
public sealed record ConversationDocument(
    Guid Id,
    Guid TravelerId,
    JsonElement AgentSession,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset ExpirationTime,
    int MessageCount);
```

Conversation creation requires an existing traveler. Once created, message requests do not accept another traveler ID. The controller loads the conversation metadata and resolves its stored owner:

```csharp
ConversationMetadata? conversation =
    conversationService.Get(conversationId);

if (conversation is null ||
    !travelerStore.Exists(conversation.TravelerId))
{
    return NotFound();
}

SendMessageResult result =
    await conversationService.SendMessageAsync(
        conversationId,
        conversation.TravelerId,
        request.Message,
        cancellationToken);
```

The service checks the binding again before retrieving memory:

```csharp
if (conversation.TravelerId != travelerId)
{
    return new SendMessageResult(
        SendMessageOutcome.NotFound);
}
```

These checks protect different entry boundaries. They enforce application ownership before model invocation; they are not prompt instructions or a substitute for production authorization. The model never decides who owns a conversation or which memory partition to read.

Persisting `TravelerId` with the conversation matters as much as storing it at runtime. Without that field in the durable document and restoration path, isolation would disappear after an application restart.

## Retrieve current memory for every turn

The application stores each traveler’s preferences once. It does not copy them into every conversation document.

During message processing, `ConversationService` loads the conversation, verifies ownership, acquires the per-conversation turn lock, and checks lifecycle status. Only then does it retrieve current memory:

```csharp
string memoryContext =
    memoryService?.BuildContext(travelerId) ?? string.Empty;

string enrichedMessage = string.IsNullOrEmpty(memoryContext)
    ? message
    : $"{memoryContext}\n\nCurrent request:\n{message}";

TripPlanResponse response =
    await travelAgent.SendMessageAsync(
        enrichedMessage,
        conversation.Session,
        cancellationToken);
```

This lookup happens once per turn. Replacing or deleting memory affects the next invocation in both existing and new conversations. No session or conversation record needs to be rewritten.

The agent run therefore combines three inputs with different responsibilities:

```text
AgentSession -> context from this conversation
current TravelerMemory -> reusable preferences
current request -> immediate user intent
```

Missing memory is valid. In that case, the original message passes through unchanged. An unknown traveler is different: that is an identity failure and must be handled before agent invocation.

## Label memory as advisory context

`BuildContext` converts the typed record into a compact, clearly labelled block:

```text
Durable traveller preferences (apply when relevant; this is not conversation history):
- Food: vegetarian
- Activity interests: museums
- Travel pace: relaxed
- Budget: moderate
- Accessibility: not specified
```

The “apply when relevant” language matters. A stored default should not override an explicit current request. A traveler with a relaxed default can still ask for one packed day without silently changing the durable record.

The sample then concatenates this context with the current request before calling the agent. It does not persist a separate handcrafted copy of that enriched string in the conversation document. The framework session remains the serialization boundary for framework-managed interaction state.

This approach is intentionally transitional. Because the enriched text is sent as the message, it can become part of the session-managed interaction. The chapter explicitly identifies richer invocation-time context handling as later work rather than claiming that string concatenation perfectly preserves authorship.

## Keep memory lifecycle independent

Memory and conversations have different lifecycle operations:

- deleting a conversation does not delete traveler memory;
- deleting memory does not delete the traveler profile or conversations;
- replacing memory does not rewrite existing sessions;
- deleting a traveler removes the profile and then attempts to remove its current memory.

The last operation is application-level sequencing, not an atomic cascade. A failure between deleting the profile and deleting memory can leave an orphaned record. Likewise, deleting long-term memory cannot erase an old preference already mentioned in historical conversation state.

These boundaries are important for privacy expectations. “Forget my preference” and “delete every conversation in which I mentioned it” are different operations and may require different retention workflows.

## Test the deterministic memory boundary

The Chapter 7 tests avoid live Azure OpenAI and verify the behavior owned by the application.

`TravelerMemoryTests` confirms that explicit preferences can be saved and retrieved, survive a store restart, are replaced rather than merged, and remain isolated when one of two travelers is deleted. Another test builds context for one traveler and asserts that the other traveler’s value is absent.

The composition test creates two conversations for one traveler, sends a message only to the second, and checks that:

- the fake agent receives the stored food and pace preferences;
- the untouched conversation still has zero messages;
- the active conversation records one message;
- the reusable memory remains in its own store.

Endpoint tests verify that an unknown traveler cannot create a conversation or save memory. They also submit a legacy `travelerId` property in the message JSON and confirm that the server continues to resolve the owner from the stored conversation binding.

The tests do not show that a live model will always apply preferences correctly, and they do not exercise authentication or authorization. The current conversation-store restart test also does not directly assert restoration of `TravelerId`. That ownership link deserves an explicit persistence regression test in a production system.

## Understand the persistence limits

The sample uses separate local JSON snapshots for traveler profiles, traveler memories, and conversations. This keeps ownership visible, but it is not a production data platform.

The memory store uses a `ConcurrentDictionary<Guid, TravelerMemory>`, writes an ordered version-1 document to a temporary file, and moves that file over the destination under a process-local lock. It reloads non-empty traveler IDs at startup.

Important boundaries remain:

- there is no production authentication or authorization;
- files are not encrypted and no consent or retention workflow is implemented;
- there is no transaction across profile, memory, and conversation files;
- a persistence failure after an in-memory upsert can leave memory newer than disk;
- file locks do not coordinate multiple application processes;
- there are no concurrency tokens, conditional updates, or history records;
- malformed memory JSON causes the store to start empty rather than recover valid records individually;
- the document version is written but not enforced or migrated;
- a concurrent PUT may become visible immediately before or after one turn’s lookup.

Production storage should provide protected access, explicit retention, version-aware updates, operational health signals, and a consistency model appropriate for the deployment.

## Make memory an application resource

Long-term agent memory becomes easier to reason about when it is treated like any other owned application resource:

```text
authorized user identity
  -> explicit typed preferences
  -> normalized current record
  -> conversation stores immutable owner ID
  -> retrieve that owner's memory for each turn
  -> supply it as advisory invocation context
```

The model can use memory, but it does not own identity, persistence, isolation, or deletion. `AgentSession` continues one conversation; the memory store carries approved preferences across conversations.

That separation makes personalization inspectable and testable while avoiding the common mistake of calling an ever-growing conversation transcript “long-term memory.”

## Continue Exploring

This article is derived from Chapter 7, “Building Memory-Aware Agents,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 7 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-07)
