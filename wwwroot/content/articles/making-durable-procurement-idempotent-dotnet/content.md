# Making Durable Procurement Idempotent in .NET

Durable orchestration changes the failure model of an application. A workflow can wait for a human decision, be replayed by its runtime, or receive the same business command more than once. If the final step creates a purchase order, sends a payment, or calls another system, “the executor ran once in my test” is not a sufficient safety argument.

The purchase approval sample in *Building AI Agents with .NET — Part 2* keeps the durable runtime state and the business side effect separate. Durable Task owns orchestration position and external-event waiting. SQLite owns purchase details, status, order reference, and a unique procurement idempotency key.

The resulting boundary is:

```text
durable workflow may replay or receive a repeated decision
    -> Procurement derives a stable business key
    -> database uniqueness prevents a second order record
    -> existing order reference is returned
```

This is not an exactly-once guarantee for every external system. It is a concrete application pattern for making the business write repeat-safe.

## Separate orchestration state from business state

The workflow validates a purchase, checks the budget, waits for manager approval, and then runs procurement:

```csharp
var port = RequestPort.Create<
    ApprovalRequest,
    ApprovalDecision>("manager-approval");

var workflow = new WorkflowBuilder(validate)
    .AddEdge(validate, budget)
    .AddEdge(budget, approval)
    .AddEdge(approval, port)
    .AddEdge(port, procurement)
    .WithOutputFrom(procurement)
    .WithName("PurchaseApproval")
    .Build();
```

Durable Task Scheduler owns the workflow instance ID, checkpoints, pending external event, runtime status, and continuation. The application database owns `PurchaseRun` and `ProcurementRecord` rows:

```csharp
public sealed class ProcurementRecord
{
    public int Id { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public Guid RunId { get; set; }
    public string OrderReference { get; set; } = "";
}

protected override void OnModelCreating(ModelBuilder builder) =>
    builder.Entity<ProcurementRecord>()
        .HasIndex(record => record.IdempotencyKey)
        .IsUnique();
```

The unique index is the final arbiter. An in-memory Boolean or a check performed only by application code cannot protect against two workers racing to create the same order.

The `PurchaseRun` compatibility fields are not the durable checkpoint. The source explicitly treats Durable Task metadata as the workflow-state source. SQLite is the business and audit store.

## Derive a stable key from the business identity

The approved procurement path derives its key from the durable purchase run ID and the business step:

```csharp
var key = $"{message.RunId}:Procurement";
```

That key is stable across workflow replay, host recreation, and repeated approval requests for the same purchase. It is not based on an executor instance, process ID, timestamp, or random GUID.

The suffix identifies the side effect being protected. If a future workflow legitimately creates separate shipment and invoice records, those operations should have distinct keys, such as `run-id:Shipment` and `run-id:Invoice`.

Idempotency keys should represent the intended business operation, not merely the current implementation method name. Changing the workflow graph while retaining the same business operation should not accidentally create a second external effect.

## Check and insert inside the business boundary

`Procurement` handles rejection separately, then checks the unique business record before creating an approved order:

```csharp
await using var db = CreateDb();

if (!message.Approved)
{
    var rejected = await db.Runs.SingleAsync(
        item => item.Id == message.RunId,
        ct);
    rejected.Status = PurchaseStatus.Rejected;
    rejected.UpdatedAt = _time.GetUtcNow();
    await db.SaveChangesAsync(ct);
    return new(message.RunId, "Rejected");
}

var key = $"{message.RunId}:Procurement";
var record = await db.Procurements.SingleOrDefaultAsync(
    item => item.IdempotencyKey == key,
    ct);

if (record is null)
{
    record = new()
    {
        RunId = message.RunId,
        IdempotencyKey = key,
        OrderReference = $"PO-{message.RunId:N}"
    };
    db.Procurements.Add(record);
    // Update the purchase run and save the business outcome.
}

return new(message.RunId, record.OrderReference);
```

On the first execution, the record is created and the purchase run becomes completed. On a later execution, the existing record supplies the original order reference. The caller receives the same business result instead of another order number.

The unique constraint remains necessary even though the code checks first. Two concurrent executions can both observe no record between the read and insert. A production implementation should handle a uniqueness violation as an idempotent race, reload the existing record, and return its reference. The sample demonstrates the key and unique index but does not implement a separate concurrent-insert recovery branch.

## Keep rejection terminal and side-effect free

Rejection is a business result, not a procurement failure. The rejected branch updates the purchase status and returns without inserting a `ProcurementRecord`:

```csharp
if (!message.Approved)
{
    rejected.Status = PurchaseStatus.Rejected;
    await db.SaveChangesAsync(ct);
    return new(message.RunId, "Rejected");
}
```

That distinction prevents a rejected approval from being retried as an order. The service also treats a completed durable instance as terminal when a later decision request arrives:

```csharp
if (metadata.IsCompleted)
{
    return await Get(id, ct);
}
```

Repeated approval after completion therefore reads the existing business view instead of raising another event into a finished orchestration.

## Raise the generated external event, not a guessed name

The approval wait is implemented with a typed `RequestPort`. The durable adapter exposes the pending external event through serialized custom status. `PurchaseService` reads that status and raises the generated event name:

```csharp
var eventName = PendingApprovalEvent(metadata);
if (eventName is null)
{
    return null;
}

await durableTasks.RaiseEventAsync(
    id.ToString(),
    eventName,
    JsonSerializer.Serialize(
        new ApprovalDecision(id, approved, manager)),
    ct);
```

The service does not hard-code an event name derived from an internal executor. It checks that exactly one pending event is present and extracts its `eventName` property. This keeps the API coupled to the durable runtime’s public status shape rather than to a private assumption about request-port naming.

The decision endpoint waits for instance completion and then reads the combined business/durable view. That is convenient for the sample, but it also means a slow scheduler or stalled procurement keeps the HTTP request open. A production API may enqueue the decision and return an accepted status instead.

## Make replay-safe executors deterministic

Durable workflow code can be replayed by the runtime. The workflow’s validation, budget check, approval routing, and procurement executor therefore avoid non-deterministic orchestration decisions. Business writes happen through fresh EF Core contexts:

```csharp
protected async Task Save(
    PurchaseRun run,
    Action<PurchaseRun>? change,
    CancellationToken ct)
{
    await using var db = CreateDb();
    var current = await db.Runs.SingleAsync(
        item => item.Id == run.Id,
        ct);
    change?.Invoke(current);

    current.UpdatedAt = clock.GetUtcNow();
    await db.SaveChangesAsync(ct);
}
```

The executor records counters such as `ValidateExecutions`, `BudgetExecutions`, and `ProcurementExecutions` in the business row. Those counters are useful diagnostics: the restart test expects validation and budget checks to remain at one, and procurement to remain at one after repeated approval.

The durable scheduler remains the authority for orchestration progress. Application counters are observations and business audit values; they are not a replacement checkpoint.

## Test replay and repeated commands with real infrastructure

The Chapter 6 test starts a purchase above the approval path, disposes the first host, creates a new host, and reads the same run ID:

```csharp
using (var host = Host())
{
    var response = await client.PostAsJsonAsync(
        "/api/purchases",
        new SubmitPurchase(
            "Laptop fleet renewal",
            50000));
    id = (await response.Content
        .ReadFromJsonAsync<PurchaseStarted>())!.RunId;
}

using (var restarted = Host())
{
    var restored = await client
        .GetFromJsonAsync<PurchaseView>($"/api/purchases/{id}");
    Assert.Equal(
        PurchaseStatus.WaitingForApproval,
        restored!.Status);
}
```

After recreation, the test approves the same instance and asserts one validation, one budget check, and one procurement execution. It then submits approval again and asserts that `ProcurementExecutions` remains one.

The rejection test verifies a terminal rejected state and zero procurement executions. These assertions test the relationship between durable instance state and business idempotency rather than merely testing a helper method.

There is an important test caveat: both tests return early when no scheduler accepts a TCP connection on `localhost:8080`. That is not an xUnit skip. A reported passing test run without a compatible Durable Task Scheduler does not prove restart recovery or native durable continuation.

## Do not confuse durable history with exactly-once effects

Durable Task can replay orchestration code and resume a waiting instance, but it cannot make an arbitrary external system exactly once by itself. The SQLite unique key protects the sample’s business procurement record. It does not atomically coordinate SQLite with a remote ERP, payment gateway, or shipping provider.

If procurement calls an external service, use a design appropriate to that service:

- send the same stable idempotency key to the provider if it supports one;
- store an outbox command in the business database;
- make the consumer deduplicate that command;
- record provider response identifiers; and
- define reconciliation for ambiguous timeouts.

The sample’s `ProcurementRecord` is a durable application boundary, not a universal distributed transaction.

## Identify the remaining operational gaps

The implementation deliberately does not provide:

- approval expiry;
- an explicit cancellation API;
- authentication or authorization for the manager decision;
- an overall polling timeout in `Start`;
- a non-blocking decision endpoint;
- atomic commits spanning Durable Task checkpoints and SQLite writes;
- scheduler provisioning or health recovery; or
- exactly-once guarantees for external procurement systems.

The SQLite database uses `EnsureCreated`, not migrations. The Durable Task integration uses preview packages and requires a compatible external scheduler. Those constraints should be part of deployment and test documentation.

## Use idempotency as a business contract

The useful pattern is not “check a flag before doing work.” It is:

```text
stable workflow/business identity
    -> operation-specific idempotency key
    -> database uniqueness
    -> repeat returns the original result
```

Durable orchestration supplies restart-safe progress and external-event delivery. The business store supplies the identity and uniqueness rule for the side effect. Keeping those responsibilities separate makes the design easier to test and prevents SQLite status columns from being mistaken for workflow checkpoints.

When a workflow can replay, be retried, or receive repeated commands, every consequential business operation should answer one question explicitly: what stable key proves that this is the same operation as the one already completed?

## Continue Exploring

This article is derived from Chapter 6, “Durable Purchase Approval,” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 6 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-06)
