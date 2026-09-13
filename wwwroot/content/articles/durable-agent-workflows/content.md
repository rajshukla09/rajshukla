# Durable Agent Workflows with Microsoft Agent Framework

An in-memory workflow is reliable only for as long as its process remains alive. That is a poor fit for agentic systems that wait on people, call remote services, or run long enough to encounter deployments and infrastructure failures. If a process stops while a purchase request is waiting for a manager, the application must know which steps already ran, what it is waiting for, and where execution should continue.

This article follows a purchase-approval workflow implemented with Microsoft Agent Framework (MAF) and its Durable Task integration. A request is validated, checked against a budget limit, paused for manager approval, and then either rejected or sent to procurement. The example deliberately uses deterministic executors rather than an AI model so that the durability mechanics are visible without model behavior obscuring them.

The important result is not merely that the purchase record survives. The same orchestration instance survives host recreation and continues from its pending approval rather than replaying the workflow from the beginning.

## The failure mode of an in-memory workflow

Suppose a request reaches the approval step and the manager responds tomorrow. If the workflow exists only as objects and tasks in one process, a restart loses:

- the execution position;
- the record of completed steps;
- the pending interaction and the event that will satisfy it;
- the relationship between the eventual decision and this workflow run;
- the workflow's terminal result or failure details.

Saving the purchase or the conversation is not enough. That data can describe what happened, but it does not make a runtime capable of continuing the suspended computation. An application could write custom reconstruction logic, but then the application—not the workflow runtime—must infer the next step and prevent earlier effects from being repeated.

The implemented example instead uses this boundary:

```text
POST purchase
  → start a durable MAF workflow instance
  → validate
  → check budget
  → persist a wait for manager approval
  → process may stop
  → recreate the host and reload orchestration metadata
  → raise the pending approval event
  → continue the same instance at procurement
  → complete
```

The Durable Task scheduler stores orchestration history, execution position, pending events, runtime status, and workflow output. SQLite stores business data: the purchase, its business status, execution counters, and procurement record. These stores cooperate, but their responsibilities are intentionally different.

## Durability Is More Than Persistence

Persistence means that some data survives a process restart. Durability means that the system can use surviving state to continue safely after a retry, crash, restart, or partial execution.

A durable workflow therefore needs more than a database row:

1. A stable workflow-instance identity must correlate commands and events.
2. The runtime must persist the workflow's continuation point and pending interaction.
3. External events must be delivered to the correct suspended instance.
4. effects that might be attempted again must be idempotent.
5. Operators must be able to distinguish running, waiting, failed, and completed instances.

Chat history satisfies none of these requirements by itself. It may be valuable model context or an audit record, but it does not encode an executable continuation, guarantee event correlation, or make an external side effect safe to retry.

This distinction also explains why the sample does not use SQLite's `PendingStep` or `CompletedSteps` fields to choose where execution resumes. Those fields remain for compatibility and display. Durable Task metadata is the source of truth for orchestration state.

## Registering a durable MAF workflow

The application constructs the workflow once and registers it with both a Durable Task worker and client:

```csharp
var purchaseWorkflow = PurchaseWorkflow.Create(connection, TimeProvider.System);

builder.Services.AddDbContextFactory<PurchasesDb>(options => options.UseSqlite(connection));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(purchaseWorkflow);
builder.Services.ConfigureDurableWorkflows(
    workflows => workflows.AddWorkflow(purchaseWorkflow.Definition),
    worker => worker.UseDurableTaskScheduler(scheduler),
    client => client.UseDurableTaskScheduler(scheduler));
builder.Services.AddSingleton<PurchaseService>();
```

`ConfigureDurableWorkflows` is the persistence boundary between the MAF workflow definition and the Durable Task scheduler. The worker executes registered workflow code; the client starts instances and interacts with them. Both point to the same scheduler.

Before a failure, the worker can execute the graph normally. After host recreation, the new process registers the same definition and scheduler configuration. The definition supplies executable code, but it does not create a replacement run. Durable Task still owns the existing instance's history and position.

The sample uses `Microsoft.Agents.AI.Workflows` 1.17.0 and the preview `Microsoft.Agents.AI.DurableTask` 1.16.0-preview.260730.1. That version detail matters: this is an integration with preview surface area, not a claim that every MAF version exposes identical APIs or behavior.

## The purchase workflow graph

The graph makes the durable boundary explicit. The approval executor produces an approval request, and a `RequestPort` pauses the workflow until a matching decision arrives:

```csharp
var validate = new ValidatePurchase(connectionString, time);
var budget = new BudgetCheck(connectionString, time);
var approval = new AwaitManagerApproval();
var procurement = new Procurement(connectionString, time);
var port = RequestPort.Create<ApprovalRequest, ApprovalDecision>("manager-approval");

var workflow = new WorkflowBuilder(validate)
    .AddEdge(validate, budget)
    .AddEdge(budget, approval)
    .AddEdge(approval, port)
    .AddEdge(port, procurement)
    .WithOutputFrom(procurement)
    .WithName("PurchaseApproval")
    .Build();
```

Architecturally, each executor has one responsibility. Validation rejects malformed requests. The budget check rejects amounts above the sample's fixed limit. `AwaitManagerApproval` converts the budget result into an `ApprovalRequest`. Procurement handles the manager's decision and produces the terminal result.

Before interruption, validation and budget checking have finished and the workflow has reached the port. Through the durable adapter, the port becomes a Durable Task external-event wait. The scheduler records that wait and publishes the generated pending event in orchestration custom status. That stored continuation—not a live C# call stack—is what survives.

## Starting an instance with stable identity

`PurchaseService.Start` first creates a SQLite business record with a new GUID. It then uses the same GUID string as the durable instance ID:

```csharp
await workflows.RunAsync(
    workflow.Definition,
    new StartMessage(id, request.Description, request.Amount),
    id.ToString(),
    ct);

await WaitUntilApprovalOrCompletion(id.ToString(), ct);
return new(id, PurchaseStatus.WaitingForApproval);
```

The shared identifier connects the API resource, SQLite record, orchestration metadata, and later approval command. Once `RunAsync` starts the instance, the validation and budget executors can run. The service then waits only until the orchestration is completed or exposes a pending approval event:

```csharp
private async Task WaitUntilApprovalOrCompletion(string instanceId, CancellationToken ct)
{
    while (true)
    {
        var metadata = await durableTasks.GetInstanceAsync(instanceId, true, ct);
        if (metadata?.IsCompleted == true ||
            (metadata is not null && PendingApprovalEvent(metadata) is not null))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
    }
}
```

This polling loop is an API coordination mechanism; it is not what makes the workflow durable. Durability comes from the scheduler's stored orchestration history and external-event wait. When the process stops after this point, its in-memory objects disappear. The purchase remains in SQLite, while the scheduler retains the workflow's position and pending event.

There is an important sample limitation here: `Start` returns `WaitingForApproval` after the wait method returns, even if the workflow completed or failed early. A production API should inspect terminal metadata and map the response accordingly. It should also impose a timeout or asynchronous status contract rather than poll indefinitely.

## Discovering and raising the pending event

The external event name is generated by the adapter, so the application does not hardcode `"manager-approval"` as the Durable Task event name. It reads the pending interaction from serialized custom status:

```csharp
private static string? PendingApprovalEvent(OrchestrationMetadata metadata)
{
    if (metadata.SerializedCustomStatus is not { Length: > 0 } status)
        return null;

    using var document = JsonDocument.Parse(status);
    if (!TryGetProperty(document.RootElement, "pendingEvents", out var pendingEvents) ||
        pendingEvents.GetArrayLength() != 1 ||
        !TryGetProperty(pendingEvents[0], "eventName", out var eventName))
        return null;

    return eventName.GetString();
}
```

At runtime, the instance ID answers *which workflow run?* The generated event name answers *which pending interaction in that run?* This sample expects exactly one pending event. A workflow with concurrent interactions would need a richer selection and correlation policy.

After a restart, the recreated host asks Durable Task for the same instance. If it is still active and has a pending event, the service raises the decision and waits for completion:

```csharp
var metadata = await durableTasks.GetInstanceAsync(id.ToString(), true, ct);
if (metadata is null) return null;
if (metadata.IsCompleted) return await Get(id, ct);

var eventName = PendingApprovalEvent(metadata);
if (eventName is null) return null;

await durableTasks.RaiseEventAsync(
    id.ToString(),
    eventName,
    JsonSerializer.Serialize(new ApprovalDecision(id, approved, manager)),
    ct);

await durableTasks.WaitForInstanceCompletionAsync(id.ToString(), true, ct);
return await Get(id, ct);
```

Nothing reconstructs a new workflow from the SQLite status. The scheduler locates the original instance, consumes the external event, and resumes it after the `RequestPort`, at procurement. Approval produces a purchase order; rejection produces a terminal `Rejected` result without creating a procurement record.

## Protecting effects from duplicate execution

Durable runtimes can replay orchestration logic, clients can retry requests, and networks can lose responses after accepting commands. Any step that crosses into an external system must assume that the request might be observed more than once.

The procurement executor derives a stable idempotency key from the run and operation:

```csharp
var key = $"{message.RunId}:Procurement";
var procurement = await db.Procurements
    .SingleOrDefaultAsync(record => record.IdempotencyKey == key, cancellationToken);

if (procurement is null)
{
    procurement = new ProcurementRecord
    {
        IdempotencyKey = key,
        PurchaseRunId = message.RunId,
        OrderReference = $"PO-{message.RunId:N}"
    };

    db.Procurements.Add(procurement);
    run.ProcurementExecutions++;
}
```

The database reinforces the lookup with a unique index:

```csharp
builder.Entity<ProcurementRecord>()
    .HasIndex(record => record.IdempotencyKey)
    .IsUnique();
```

Before failure, an order may have been created even if the caller never received the response. After recovery or a repeated approval, the stable key lets the executor find the existing operation instead of intentionally creating another. The unique constraint is the final local guard against two records with the same key.

This is not a universal exactly-once guarantee. The orchestration checkpoint and SQLite transaction are not atomic, and a real procurement system would be another consistency boundary. The downstream service should accept the same idempotency key, and the application must handle races and ambiguous outcomes explicitly.

## What the recovery tests establish

The integration test starts a laptop purchase in Host A and checks that validation and budget each ran once. It disposes Host A while the workflow waits, creates Host B with the same database and scheduler, finds the same request still waiting, and approves it. The completed result reports one validation, one budget check, and one procurement execution. Repeating the approval does not add a second procurement record.

A separate rejection test verifies that rejection is terminal: procurement remains at zero, and a later attempt to approve does not change the result.

These tests demonstrate the intended boundary when a compatible scheduler is available. Their environment guard, however, only probes TCP port 8080. If the port cannot be reached, each test returns silently rather than being reported by xUnit as skipped. A green local test run alone therefore does not prove that the restart path executed; the test environment must independently confirm that the expected scheduler is running.

## Failure, recovery, and observability boundaries

Durable execution improves recovery, but it does not erase failure modes. This implementation has several distinct boundaries:

- A validation or budget exception can fail the orchestration before approval.
- The API process can disappear without losing the scheduler-owned continuation.
- The scheduler itself is external infrastructure and needs its own availability, backup, and operational plan.
- SQLite and the orchestration scheduler can disagree temporarily because their writes do not share a transaction.
- A pending approval can remain forever because the sample has no expiry, cancellation, or escalation policy.
- The decision endpoint waits synchronously for workflow completion and has no explicit timeout.

Useful observability must join business and orchestration identities. The sample's status view combines the SQLite purchase record with Durable Task metadata to report running, waiting, failed, or completed state. Execution counters make replay and duplicate-effect errors visible in tests. Production systems should carry the stable instance ID through structured logs, traces, approval events, and downstream calls; expose pending-event age and retry counts; and alert on stuck, failed, or repeatedly replayed instances.

Operators also need to know which store they are inspecting. SQLite can explain the business request and procurement outcome. Scheduler history explains where workflow execution is paused or why it failed. Treating either view as the whole truth makes recovery investigations harder.

## Production trade-offs

This example proves a specific restart-and-resume path; it is not a complete purchasing service. Before adopting the pattern, account for:

- the operational cost of an external Durable Task scheduler and a preview MAF integration package;
- authentication and authorization for approval decisions, including validation that the caller is an eligible manager;
- approval expiry, cancellation, escalation, and late-event behavior;
- API timeouts and asynchronous completion notifications;
- schema migrations instead of `EnsureCreated` for SQLite;
- reconciliation between scheduler and business-store state;
- idempotency propagated all the way to external services;
- concurrency policies for repeated or competing decisions;
- retention, privacy, and deletion requirements for orchestration history and payloads.

The architectural payoff is precise: process lifetime no longer defines workflow lifetime. Achieving that payoff requires persisted continuation state, stable correlation, retry-safe effects, and enough telemetry to understand execution across hosts—not simply saving a transcript or status field.

## Continue Exploring

This implementation belongs to *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2)
