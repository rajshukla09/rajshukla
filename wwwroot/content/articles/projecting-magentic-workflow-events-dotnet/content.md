# Projecting Native Magentic Workflow Events into Auditable .NET State

Native orchestration is valuable partly because the framework owns the difficult coordination loop. A Magentic manager can create a plan, delegate to participants, evaluate progress, revise the plan, and stop when the objective is complete.

That ownership creates an application-design question: what should a .NET API persist and show to operators when the framework does not expose its private planning ledger as a typed domain object?

The Enterprise Architecture Assessment in *Building AI Agents with .NET — Part 2* answers with a projection boundary. The application runs a native Microsoft Agent Framework Magentic workflow, reads observable event envelopes, extracts meaningful public text, recognizes manager-authored audit labels, and stores a safe application model:

```text
native workflow envelopes
    -> observable event projection
    -> manager audit-label parsing
    -> AssessmentRun state
    -> EF Core persistence and UI views
```

The goal is not to recreate hidden chain-of-thought or claim that `PLAN:` is a native MAF event type. It is to make the part of execution that the application can honestly observe useful for operators, history, and comparison.

## Let Magentic own the orchestration loop

The orchestrator constructs the native workflow directly:

```csharp
Workflow workflow = new MagenticWorkflowBuilder(managerAgent)
    .AddParticipants(participants)
    .WithName("Enterprise Architecture Magentic Assessment")
    .WithDescription(
        "Plans, delegates, evaluates, and replans " +
        "an enterprise architecture assessment.")
    .RequirePlanSignoff(false)
    .WithMaxRounds(options.Value.MaxTurns)
    .WithMaxStalls(options.Value.MaxStalls)
    .WithMaxResets(options.Value.MaxResets)
    .Build();

var execution = await InProcessExecution.RunAsync(
    workflow,
    objective,
    cancellationToken: cancellationToken);
```

There is no application `while` loop selecting the next participant. The manager and Magentic runtime own planning, delegation, progress evaluation, revision, and completion. The application supplies the manager, six participant agents, and native guardrail values.

That distinction should remain visible in the code and in documentation. Reconstructing a custom supervisor loop around native Magentic events would create a second orchestrator competing with the framework.

## Define what is observable before persisting it

The installed workflow package exposes event envelopes, but not a public typed Magentic plan ledger. The orchestrator converts each event into a small application record:

```csharp
var nativeEvents = execution.NewEvents
    .Select(x => new NativeMagenticEvent(
        x.GetType().Name,
        NativeEventText.ReadExecutor(x),
        NativeEventText.Read(
            x.GetType().GetProperty("Data")?.GetValue(x))))
    .ToArray();

var finalMessage = nativeEvents
    .Where(x => x.RuntimeType ==
        nameof(WorkflowOutputEvent))
    .Select(x => x.Text)
    .LastOrDefault(NativeEventText.IsMeaningful);
```

The projection deliberately keeps three things: the concrete runtime event name, an observable executor identifier when present, and meaningful text extracted from the event payload. It does not serialize arbitrary runtime objects or assert that their private fields represent a stable framework contract.

The final assistant message is selected only from `WorkflowOutputEvent` values whose text passes the same meaningful-content filter. A missing final message remains missing; it is not replaced with a fabricated recommendation.

## Extract text from envelopes without `ToString()` shortcuts

MAF event payloads can contain strings, collections, chat messages, and nested content objects. Calling `Data.ToString()` is not a safe projection strategy: collection or message payloads may become type names or punctuation rather than the text an operator needs.

`NativeEventText.Read` walks a bounded set of known content properties:

```csharp
private static void Collect(
    object? value,
    List<string> parts,
    HashSet<object> seen,
    int depth)
{
    if (value is null || depth > 8)
        return;

    if (value is string text)
    {
        if (IsMeaningful(text))
            parts.Add(text.Trim());
        return;
    }

    if (!value.GetType().IsValueType &&
        !seen.Add(value))
        return;

    if (value is System.Collections.IEnumerable sequence)
    {
        foreach (var item in sequence)
            Collect(item, parts, seen, depth + 1);
        return;
    }

    foreach (var name in new[]
        { "Text", "Content", "Contents", "Message",
          "Messages", "Result", "Value" })
    {
        var property = value.GetType().GetProperty(name);
        if (property is not null &&
            property.GetIndexParameters().Length == 0)
        {
            Collect(property.GetValue(value),
                parts, seen, depth + 1);
        }
    }
}
```

The depth limit prevents a malformed or unexpectedly recursive payload from turning event projection into an unbounded traversal. Reference tracking prevents cycles. Meaningful values are deduplicated before being joined.

This is a pragmatic compatibility adapter around observable runtime envelopes, not a promise that every future MAF event shape will be understood. A package upgrade should be accompanied by projection tests.

## Use manager-authored labels as an audit protocol

The manager instructions ask for intentional public lines such as:

```text
PLAN: Inspect scale; Compare cost
DELEGATION: ArchitectureAgent - inspect payment boundary
PROGRESS: scale evidence collected
REVISION: Validate selective payment extraction
CONFLICT: scale benefit versus operating cost
COMPLETE: Keep the modular core
```

These labels are observable text authored by the manager. They are not native typed Magentic ledger entries, and they are not private reasoning. The application can parse them as an audit convention while preserving the manager’s natural-language output for later review.

`AssessmentRunState` splits each line once at the first colon, normalizes the tag, and applies only the tags it understands:

```csharp
var split = line.IndexOf(':');
if (split < 1)
    continue;

var tag = line[..split].Trim().ToUpperInvariant();
var body = line[(split + 1)..].Trim();

switch (tag)
{
    case "PLAN":
        SetPlan(body, "Initial manager plan");
        break;
    case "REVISION":
        Revise(body);
        break;
    case "DELEGATION":
        Delegate(body);
        break;
    case "PROGRESS":
        run.ProgressEvaluations.Add(body);
        break;
    case "CONFLICT":
        run.Conflicts.Add(new(body, [body], string.Empty));
        break;
    case "QUESTION":
        run.OpenQuestions.Add(body);
        break;
    case "COMPLETE":
        run.CompletionReason = body;
        break;
}
```

Unknown labels are ignored. Repeated complete lines are deduplicated through `_observedManagerLines`, so replaying the same observable output does not create duplicate audit state.

The application must keep this convention modest. It should not treat a model-authored `COMPLETE:` line as proof that every business requirement passed, nor should it store arbitrary model prose as an executable instruction.

## Preserve initial and current plans

The parser turns a plan’s semicolon-delimited task list into an `AssessmentPlan` with a version:

```csharp
var plan = new AssessmentPlan(
    run.CurrentPlan is null
        ? 1
        : run.CurrentPlan.Version + 1,
    body.Split(
        ';',
        StringSplitOptions.RemoveEmptyEntries |
        StringSplitOptions.TrimEntries)
        .Select(x => new AssessmentPlanItem(x))
        .ToArray(),
    rationale,
    DateTimeOffset.UtcNow);

run.InitialPlan ??= plan;
run.CurrentPlan = plan;
```

The first plan remains available for comparison. A later `REVISION:` updates `CurrentPlan` and records a `PlanRevision` containing the previous version, new version, evidence, change, and observation timestamp:

```csharp
var old = run.CurrentPlan?.Version ?? 0;
SetPlan(body, "Replanned from new evidence");
run.PlanRevisions.Add(new(
    old,
    run.CurrentPlan!.Version,
    run.ProgressEvaluations.LastOrDefault() ?? string.Empty,
    body,
    DateTimeOffset.UtcNow));
```

This projection answers useful operational questions—what was initially proposed, what changed, and what evidence accompanied the change—without claiming to reproduce the private manager ledger.

The parser is intentionally conservative. If a plan label is absent, no placeholder plan is created. The tests explicitly assert that missing native artifacts do not produce fake state.

## Correlate participant observations carefully

When an observed native event identifies a specialist executor, `AssessmentRunState` creates an invocation on an `Invoked` event and completes the most recent open invocation on a `Completed` event:

```csharp
if (IsSpecialist(nativeEvent.Executor) &&
    nativeEvent.RuntimeType.Contains(
        "Invoked", StringComparison.OrdinalIgnoreCase))
{
    if (!run.AgentInvocations.Any(
            x => x.Agent == nativeEvent.Executor &&
                 x.CompletedAt is null))
    {
        run.AgentInvocations.Add(new(
            Guid.NewGuid(),
            nativeEvent.Executor,
            nativeEvent.Text ?? string.Empty,
            DateTimeOffset.UtcNow));
    }
}
```

On completion, the projection attaches the observed result to the open invocation and records a readable `AgentCompleted` event. This gives the UI a useful timeline while acknowledging that timestamps are application observation times, not exact internal execution timestamps.

A projection should not infer participant work when the runtime event does not identify a participant. Missing executor data is left missing. That rule prevents the audit view from becoming more confident than the underlying evidence.

## Persist an audit view, not private model state

The repository persists the run objective, status, plans, revisions, participant invocations, tool invocations, progress evaluations, event sequence, final recommendation, and configuration snapshot. The additional observability payload stores completed tasks, open questions, conflicts, and findings.

Configuration is captured when a run is created:

```csharp
Configuration = new AssessmentConfigurationSnapshot
{
    ModelDeployment = configuration[
        "AzureOpenAI:DeploymentName"] ?? "",
    MaxTurns = limits.MaxTurns,
    MaxAgentInvocations = limits.MaxAgentInvocations,
    MaxToolCallsPerAgent = limits.MaxToolCallsPerAgent,
    InstructionVersion =
        MagenticAssessmentOrchestrator.InstructionVersion
};
```

This snapshot matters for auditability. A later configuration change should not make an old run appear to have used today’s model deployment, limits, or instruction version.

`AssessmentRunRepository.Save` replaces the aggregate and its child rows within a transaction. A recreated EF Core context can load the same plans, revisions, invocations, tool records, events, and configuration values. That is durable history, not durable execution: an interrupted Magentic run cannot resume from these rows.

## Compare runs with deterministic metrics

The application comparison service computes metrics from persisted state rather than asking a model to judge which run was better:

```csharp
return new(
    run.Id,
    run.Objective,
    run.Status,
    run.StartedAt,
    run.DurationMilliseconds,
    run.AgentInvocations.Count,
    knownAgents,
    run.ToolInvocations.Count,
    distinctTools,
    callsPerAgent,
    run.PlanRevisions.Count,
    failedTools,
    cacheHits,
    run.LimitReached,
    run.FinalRecommendation?.Length ?? 0,
    run.InitialPlan,
    run.CurrentPlan,
    run.CompletionReason,
    run.FinalRecommendation,
    run.Configuration);
```

These metrics can show that one run was faster, used fewer agents, hit a limit, or revised its plan more often. They do not establish qualitative recommendation correctness. Human or domain-specific evaluation remains necessary.

## Test projection separately from live Magentic behavior

The Chapter 4 tests do not execute Azure OpenAI or a real Magentic workflow. That is intentional. They test the deterministic projection and persistence shell with synthetic native envelopes and explicit manager labels.

The planning tests verify that a `PLAN`, `DELEGATION`, and `PROGRESS` sequence produces one versioned plan, one invocation, and one progress evaluation. A native-entry-point test checks that the orchestrator uses `MagenticWorkflowBuilder`, `AddParticipants`, and `InProcessExecution.RunAsync` without an application `while` loop.

The native projection test supplies `ExecutorInvokedEvent`, `ExecutorCompletedEvent`, and duplicate `WorkflowOutputEvent` records. It verifies participant completion, plan parsing, progress deduplication, and readable lifecycle events. The missing-artifacts test confirms that null output does not create placeholder plans or agent records.

Replanning tests verify that the initial plan remains version one while a `REVISION` creates version two and a conflict is retained. Persistence tests recreate the EF context and verify that configuration snapshots and child collections survive. MCP tests separately cover six read-only stdio catalogs and cache behavior.

These tests do not prove that a live manager will emit every requested label, create a good plan, revise at the right time, or respect every native limit at runtime. They prove that when observable artifacts exist, the application projects them without inventing missing data.

## Understand the operational limits

The sample has important boundaries:

- `MaxTurns`, `MaxStalls`, and `MaxResets` configure native Magentic guardrails, but the tests do not execute a live run to prove their behavior;
- `MaxAgentInvocations` is persisted as configuration but is not enforced by the visible orchestrator code;
- manager labels are text conventions, not typed native plan or revision objects;
- internal Magentic state and hidden reasoning are not exposed or persisted;
- observed timestamps represent when the application processed an event;
- SQLite history does not checkpoint and resume an active run;
- MCP tools return deterministic demonstration data; and
- the recommendation parser and label parser are intentionally lightweight.

These limitations are not flaws to conceal. They define where a production implementation needs stronger contracts, durable orchestration, identity and authorization, structured outputs, or integration tests.

## Build the projection boundary deliberately

Native orchestration and application auditability solve different problems. Let Magentic own planning and replanning. Let the application project only observable envelopes and explicitly requested public labels. Persist the resulting audit view with configuration and version information, but do not claim that it is the framework’s private execution ledger.

The practical sequence is:

```text
run native Magentic
    -> extract meaningful observable text
    -> parse explicit public audit labels
    -> preserve missing data as missing
    -> persist versioned projections
    -> compare runs with deterministic metrics
```

That boundary gives operators a useful history without turning an implementation-specific event shape into a fabricated guarantee about how the orchestration engine thinks.

## Continue Exploring

This article is derived from Chapter 4, “Magentic Orchestration,” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 4 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-04)
