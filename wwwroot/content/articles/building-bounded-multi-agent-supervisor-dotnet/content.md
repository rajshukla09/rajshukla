# Building a Bounded Multi-Agent Supervisor in .NET

Incident investigation rarely follows one fixed checklist. A deployment clue may justify a log search, which may expose a database symptom, which may justify a second investigation by the same specialist with a narrower assignment.

That flexibility does not require giving a model unrestricted control of the application.

The Chapter 3 implementation in *Building AI Agents with .NET — Part 2* uses a supervisor `AIAgent` to select among four narrow specialist agents. The model chooses **who** should work next and supplies an assignment. Application code still owns:

- the allow-listed specialist catalog;
- the shared investigation context;
- the maximum number of specialist invocations;
- event correlation and persistence;
- failure handling; and
- the final root-cause synthesis boundary.

This is a useful middle ground between a hard-coded pipeline and an open-ended autonomous loop. The supervisor is adaptive, but the execution envelope remains deterministic.

## Separate delegation from investigation

The application represents each specialist as a narrow contract:

```csharp
public sealed record AgentDescriptor(
    string Name,
    string Description,
    string? McpServer = null,
    IReadOnlyList<string>? McpTools = null);

public interface ISpecializedAgent
{
    AgentDescriptor Descriptor { get; }

    Task<AgentFinding> InvestigateAsync(
        IncidentInvestigationContext context,
        string assignment,
        CancellationToken cancellationToken);
}

public sealed record AgentDelegate(
    AgentDescriptor Descriptor,
    Func<string, string, CancellationToken,
        Task<AgentFinding>> InvokeAsync);
```

`AgentDescriptor` is the public catalog entry. `ISpecializedAgent` owns domain work. `AgentDelegate` is the supervisor-facing function boundary: it accepts an assignment and a short selection reason, then returns a typed `AgentFinding`.

The supervisor does not receive database services, MCP clients, or arbitrary dependency-injection objects. It receives only the delegates the application chose to expose.

The four real specialists are deliberately narrow:

- `DeploymentAgent` investigates releases, rollout status, versions, and timing;
- `LogAnalysisAgent` investigates errors, exception patterns, traces, and correlated failures;
- `MetricsAgent` investigates latency, throughput, resource usage, and before/after comparisons; and
- `DatabaseAgent` investigates health, slow queries, connection failures, and query-plan regressions.

Each specialist has its own MCP server and catalog. That keeps domain capabilities separate even though all agents run in one application process.

## Let the supervisor choose who, not what authority exists

`MafSupervisorModel` converts the allow-listed delegates into MAF functions:

```csharp
var tools = agents.Select(candidate =>
    AIFunctionFactory.Create(
        (string assignment,
         string reasonSelected,
         CancellationToken ct) =>
            candidate.InvokeAsync(
                assignment,
                reasonSelected,
                ct),
        name: candidate.Descriptor.Name,
        description: candidate.Descriptor.Description))
    .ToArray();

AIAgent supervisor = new AzureOpenAIClient(
        new Uri(options.Value.Endpoint),
        new AzureKeyCredential(options.Value.ApiKey))
    .GetChatClient(options.Value.DeploymentName)
    .AsAIAgent(new ChatClientAgentOptions
    {
        Name = "SupervisorAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = Instructions,
            Tools = [.. tools]
        }
    });
```

The model can select one of those functions, but it cannot invent a fifth specialist or directly call an MCP tool. The model decides which permitted specialist is useful, what focused assignment it should receive, and why. The application decides which specialists exist, what each one can access, and whether another invocation is still allowed.

That distinction is especially important when the supervisor is instructed to continue until it has sufficient evidence. “Continue” is a model preference; the application budget remains the hard stop.

The supervisor instructions also prohibit private chain-of-thought from becoming an application contract. The delegate receives a short actionable `reasonSelected`, not hidden reasoning.

## Keep shared context as application state

The investigation context is a simple application-owned object:

```csharp
public sealed class IncidentInvestigationContext(
    string originalIncident)
{
    private readonly List<AgentFinding> _findings = [];
    private readonly List<AgentInvocation> _invocations = [];

    public string OriginalIncident { get; } = originalIncident;
    public IReadOnlyList<AgentFinding> Findings => _findings;
    public IReadOnlyList<AgentInvocation> AgentInvocations =>
        _invocations;

    public void Add(
        AgentFinding finding,
        AgentInvocation invocation)
    {
        _findings.Add(finding);
        _invocations.Add(invocation);
    }
}
```

The supervisor receives a compact description of the context before each delegation. The context is not a hidden MAF session and is not owned by the model. After a specialist returns, `IncidentInvestigator` adds the typed finding and invocation record under an application lock.

The model therefore sees accumulated evidence, while the application retains the authoritative collection used for persistence and final synthesis. This also makes repeated delegation explicit: the same specialist can be called again with a materially different assignment after new evidence arrives.

## Enforce the agent budget around every delegation

The central orchestration boundary reserves an invocation before calling a specialist:

```csharp
lock (gate)
{
    if (reservedInvocations >=
        options.Value.MaxAgentInvocations)
    {
        limitReached = true;
        throw new AgentInvocationLimitException(
            options.Value.MaxAgentInvocations);
    }

    sequence = ++reservedInvocations;
}
```

The default `MaxAgentInvocations` is eight, and configuration constrains it to 1 through 20. The sequence number becomes the correlation key for supervisor decisions, agent lifecycle events, and tool-completion events.

The budget is enforced outside the model. A scripted or live supervisor can request another delegation, but `AgentInvocationLimitException` prevents it from starting after the limit. The orchestration catches that specific exception, persists a limit event, and still runs root-cause synthesis over the findings already collected.

This is not the same as asking the model to stop after eight calls. The model instruction can guide behavior; the lock is the enforcement mechanism.

Each specialist also creates a fresh per-invocation `ToolBudget` for its own MCP calls:

```csharp
var budget = new ToolBudget(
    limits.Value.MaxToolCallsPerAgent);
```

Those two budgets protect different boundaries. The supervisor budget limits how many specialist delegations one investigation can start. The specialist budget limits how many MCP tools one specialist invocation can call. In the current design, the tool budget is recreated for every specialist invocation, including a repeated invocation of the same specialist.

## Scope each specialist to its own MCP catalog

`McpSpecializedAgent` discovers tools for its declared specialty and creates model-callable functions only for that catalog:

```csharp
protected abstract string Specialty { get; }

public AgentDescriptor Descriptor => new(
    GetType().Name,
    Responsibility,
    Specialty,
    mcp.DiscoverTools(Specialty)
        .Select(x => x.Name)
        .ToArray());
```

During investigation, the specialist again obtains the tools for that specialty and wraps each one with a `ToolBudget`, timing, and failure telemetry. The deployment specialist therefore sees deployment capabilities; it does not receive the logs or database catalog merely because another specialist has them.

The official MCP client still validates that the selected tool was discovered from the named server before calling `CallAsync`. The model chooses a tool within the specialist’s catalog, but the application controls the server-to-specialty mapping.

This creates two independent selection steps:

```text
Supervisor model chooses specialist
    -> specialist model chooses MCP tool
        -> application validates and invokes tool
```

Confusing those steps leads to a common design error: exposing every enterprise capability to the supervisor and relying on role instructions to keep it away from the wrong specialist.

## Return typed findings, not an unbounded transcript

The specialist returns an `AgentFinding`:

```csharp
public sealed record AgentFinding(
    string AgentName,
    string Summary,
    IReadOnlyList<EvidenceItem> Evidence,
    double Confidence,
    string? SuggestedNextInvestigation,
    IReadOnlyList<ToolInvocation> ToolInvocations);
```

The production specialist agent is instructed to return concise labeled fields: finding, evidence, confidence, and suggested next investigation. The application stores the resulting text and the tool invocation summaries, then adds the finding to shared context.

This is deliberately not a full conversation transcript. The next supervisor turn receives the incident and compact summaries such as:

```text
1. DeploymentAgent: v4.8 was deployed near incident onset;
   suggested: correlate release timing with errors and metrics.
```

Compact context limits uncontrolled growth and gives the root-cause agent a stable application-level input. The model-generated summary is still untrusted evidence; it does not become a deterministic authorization or deployment command.

## Make failures observable and preserve useful evidence

Specialist failures are recorded against the reserved agent sequence. The orchestrator adds a failure finding to context, persists an `AgentFailed` event, and rethrows so the supervisor’s normal execution stops:

```csharp
catch (Exception exception)
{
    fatal = exception;

    lock (gate)
    {
        context.Add(
            new AgentFinding(
                agent.Descriptor.Name,
                $"Investigation failed: {exception.Message}",
                [],
                0,
                null,
                []),
            new AgentInvocation(
                sequence,
                agent.Descriptor.Name,
                watch.ElapsedMilliseconds,
                false,
                reason));
    }

    await events.AppendAsync(
        runId,
        new(
            InvestigationEventTypes.AgentFailed,
            "Failed",
            agent.Descriptor.Name,
            sequence,
            ErrorSummary: exception.Message),
        ct);

    throw;
}
```

The behavior is intentionally specific. A failed specialist does not silently look like a successful empty finding, and the event retains both the specialist and the invocation sequence. The orchestration then runs root-cause synthesis over the evidence that remains and marks the termination reason as a fatal failure with best-available evidence.

The sample does not continue delegating after a specialist failure. If a production process needs partial retry or alternate-specialist fallback, that is a new policy decision rather than something the current implementation already provides.

## Keep final synthesis outside the supervisor loop

After supervisor execution ends—because it declared completion, hit the budget, or failed—the application invokes a separate `RootCauseAgent`:

```csharp
conclusion = await rootCause.SynthesizeAsync(
    context,
    limitReached,
    cancellationToken);
```

The root-cause agent is tool-free. It receives the original incident, accumulated findings, and whether the agent budget was reached. Its prompt requires four labeled lines, which the application parses into `RootCauseConclusion`:

```csharp
return new RootCauseConclusion(
    Line("Summary"),
    Line("Likely Root Cause"),
    double.TryParse(Line("Confidence"),
        out var confidence)
        ? Math.Clamp(confidence, 0, 1)
        : 0.5,
    Line("Recommended Action"));
```

This separation gives the investigation loop one responsibility—collect evidence—and gives synthesis another—form a conclusion from available evidence. It also makes a limit or specialist failure visible to the final agent rather than hiding the incomplete run.

The parser is intentionally lightweight. It is not strict structured-output validation, and arbitrary model formatting can produce fallback values. A production implementation would need a stronger contract if the conclusion drives consequential automation.

## Persist an execution narrative, not a checkpoint

The SQLite store persists the run, ordered events, readable summaries, and serialized final response. Each event contains the run ID, agent, invocation sequence, tool server/tool, status, reason, duration, and error summary where applicable.

The event sequence for a successful delegation resembles:

```text
InvestigationStarted
SupervisorDecision
AgentStarted
McpCapabilitiesDiscovered
McpToolCompleted
AgentCompleted
RootCauseGenerated
InvestigationCompleted
```

`SignalRInvestigationUpdatePublisher` sends an `InvestigationUpdated` notification to the run group. The UI then refetches the persisted run; the hub does not carry the full investigation payload.

This is an observable history, not durable execution. Active supervisor and specialist calls run in process through model and MCP clients. If the host stops during execution, the historical events survive in SQLite, but there is no checkpoint from which the model delegation loop resumes.

That distinction matters when describing recovery behavior: the implementation supports historical inspection after restart, not active-run continuation.

## Test the deterministic shell around the models

The Chapter 3 tests use scripted supervisors, fake specialists, fake root-cause synthesis, real MCP stdio discovery, and an in-memory SQLite store.

The scripted supervisor receives the actual delegate catalog and selects by descriptor name. Tests prove multiple selections and meaningful repeated selection of `LogAnalysisAgent`, including sequential invocation numbers.

The limit test requests two delegations with `MaxAgentInvocations` set to one. It verifies that only one invocation is recorded, a limit event is persisted, and root-cause synthesis still returns the evidence accumulated before the stop.

The failure test supplies a specialist that throws. It verifies a failed invocation remains correlated to the specialist and that best-available synthesis still completes.

Other tests exercise the real four-server MCP catalog, unknown-tool rejection, multiple stdio catalogs, agent-to-server mapping, ordered event projection, failed-tool correlation, and reconstruction from SQLite after creating a new context.

These tests do not assert live Azure OpenAI supervisor routing, live specialist tool choice, browser SignalR delivery, arbitrary root-cause formatting, or restart continuation of an active model loop. They prove the deterministic shell around those model-driven components.

## Know the boundaries before production use

The implementation demonstrates a strong application pattern, but it intentionally leaves several production decisions open:

- supervisor and specialist model calls have no durable queue or checkpoint;
- active work is synchronous and in process;
- specialist failure stops normal supervisor execution rather than triggering a retry policy;
- per-specialist tool budgets reset on every specialist invocation;
- there is no authentication or authorization context in the investigation or MCP calls;
- assignments, arguments, results, and error text may be logged or persisted;
- the root-cause parser is prose-based rather than strict structured output;
- MCP data is deterministic demonstration data rather than production systems; and
- the model decides delegation and findings, but deterministic business actions are not automatically authorized by those outputs.

The boundaries are useful precisely because they are explicit. Adding identity, retries, durable orchestration, stronger output contracts, or side-effect approval can happen around the supervisor without turning the model into the owner of those policies.

## Use dynamic delegation when the uncertainty is real

A dynamic supervisor earns its complexity when the next useful specialist depends on evidence discovered during the investigation, when repeated focused work is meaningful, and when the application can define a safe catalog and stop policy.

If the stages, order, and dependencies are known in advance, a linear workflow is easier to test and operate. If independent reviewers can run without shared context, fan-out/fan-in may be a better fit. The Chapter 3 design is for the case where the investigation path is not known up front but the available responsibilities and authority still are.

The durable pattern is:

```text
allow-list specialists
    -> let supervisor select a bounded delegate
    -> pass compact typed findings into shared context
    -> enforce invocation and tool budgets in code
    -> persist correlated events
    -> synthesize from available evidence
```

That gives the model room to investigate without giving it ownership of the system’s safety envelope.

## Continue Exploring

This article is derived from Chapter 3, “Dynamic Multi-Agent Orchestration,” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 3 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-03)
