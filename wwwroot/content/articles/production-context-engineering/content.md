# Production Context Engineering for AI Agents

An agent does not need everything the application knows. It needs the smallest trustworthy set of information that supports its current task.

That distinction becomes architectural in production. Conversation history grows, specialists generate detailed findings, external evidence changes, and workflows accumulate coordination state. Sending all of it to every model call increases cost and latency while making stale, duplicated, irrelevant, or cross-user data more likely to influence the result.

This article uses the multi-agent incident-investigation implementation from *Building AI Agents with .NET — Part 2*. The system investigates increased checkout latency after a deployment. Five specialists—log, deployment, database, infrastructure telemetry, and application—receive explicit, bounded context assembled from an SQLite evidence store. Their detailed findings return to that store, while the supervisor retains compact references and hypotheses for coordination.

Its central rule is simple:

> The evidence store can grow. An agent's working context should not.

## Context is not another name for chat history

Part 1 of the source separates three narrower concerns. An `AgentSession` carries the history needed for conversational follow-ups. Later, the session is serialized to JSON so a conversation can be reconstructed after restart. A separate traveler-memory store holds explicit, reusable preferences and injects only the memory belonging to the conversation's bound traveler.

Part 2 Chapter 9 addresses a different problem: selecting task-specific evidence for multiple specialists. Its agents do not depend on accumulated conversation history. The tests invoke a specialist twice with the same `SpecialistRequest` and verify the same result, while reflection verifies that the specialist holds no instance fields.

These concepts should remain distinct:

- **Current request** is the task assigned now: for example, “Measure database connection waits.”
- **Conversation or session state** is framework-managed turn history for follow-up dialogue. Chapter 9 does not use it.
- **Workflow or supervisor state** tracks coordination: pending and completed investigations, affected components, active hypotheses, evidence references, confidence, and retrieval outcomes.
- **Retrieved external knowledge** is the selected subset of evidence loaded from SQLite for one specialist invocation.
- **Agent output** is a structured `SpecialistFinding`; it becomes new stored evidence and a compact state update.
- **Durable application state** is data deliberately persisted outside process memory. In this sample, evidence is durable in SQLite; the current supervisor execution and demo queue are not durable workflows.
- **Long-term memory** is reusable subject-specific information across sessions, such as Part 1's traveler preferences. Chapter 9's incident evidence is not personal long-term memory.

Context is the material assembled for a particular model call. Memory, evidence, and state are possible sources of context, but they are not interchangeable with the context itself.

## The implemented context lifecycle

The incident workflow makes the lifecycle explicit:

```text
Incident request
  → Supervisor selects a specialist and task
  → retrieval profile constrains permissible evidence
  → typed specification filters the SQLite evidence store
  → candidates are ranked, deduplicated, and budgeted
  → a fresh ContextBundle is passed to the specialist
  → the specialist returns a structured finding
  → the full finding is persisted as EvidenceRecord
  → compact reference and hypothesis update supervisor state
  → later specialists retrieve relevant evidence on demand
```

This is capture, storage, retrieval, selection, injection, and update as separate operations. The language model analyzes the selected bundle; deterministic application services control which evidence can reach it.

The boundary is represented by a small interface:

```csharp
public interface IContextService
{
    Task<ContextBundle> GetContextAsync(
        ContextRequest request,
        CancellationToken cancellationToken);
}
```

The supervisor does not compose SQL or prompt text. It describes the incident, target agent, task, allowed evidence types, component, time window, source agents, current compact state, and retrieval iteration. The context service converts that request into an inspectable bundle.

## External evidence and compact execution state

`EvidenceRecord` is the durable evidence model. SQLite and EF Core store the incident ID, source agent, evidence type, component, observed time range, summary, detailed content, importance, and creation time:

```csharp
public sealed class EvidenceRecord
{
    public Guid EvidenceId { get; set; }
    public Guid IncidentId { get; set; }
    public required string SourceAgent { get; set; }
    public EvidenceType EvidenceType { get; set; }
    public required string Component { get; set; }
    public DateTimeOffset? ObservedFrom { get; set; }
    public DateTimeOffset? ObservedTo { get; set; }
    public required string Summary { get; set; }
    public required string DetailedContent { get; set; }
    public int Importance { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; }
}
```

The repository always starts with an exact incident boundary, then applies evidence-type, source-agent, component, time-range, and maximum-age constraints. Indexes support incident/time and incident/type access. There are no embeddings, vector index, or vector query in the implementation.

The supervisor deliberately does not copy `DetailedContent` into its execution state. It keeps `EvidenceReference` values containing an ID, source, type, and summary, plus short active hypotheses. A test stores a distinctive raw diagnostic payload, confirms that SQLite retains it, and confirms it does not appear in serialized supervisor state.

That separation has two benefits. Detailed evidence remains available for future retrieval, while coordination state stays smaller and less likely to leak raw diagnostics into every downstream request. It does not make the supervisor durable: `InvestigationSupervisor` creates `SupervisorExecutionState` in memory, and the demo uses an in-memory `Channel<Guid>` and observability store. A host restart can therefore interrupt the active investigation even though already persisted evidence survives.

## Retrieval profiles establish agent boundaries

Different specialists need different evidence. `AgentRetrievalProfileProvider` defines allowed and prioritized categories, source agents, item limits, character budgets, per-category limits, and maximum evidence age for each specialist.

For example, the database specialist prioritizes database evidence and can use selected log, application, and deployment findings:

```csharp
[nameof(DatabaseAgent)] = new(
    nameof(DatabaseAgent),
    [
        EvidenceType.Database,
        EvidenceType.Log,
        EvidenceType.Application,
        EvidenceType.Deployment
    ],
    [
        nameof(DatabaseAgent),
        nameof(LogInvestigationAgent),
        nameof(ApplicationAgent),
        nameof(DeploymentAgent)
    ],
    16,
    6_000,
    "Database findings and selected log or application evidence suggesting database impact.")
```

An assignment can narrow this policy but cannot broaden it. `RetrievalSpecificationFactory` intersects requested evidence types and sources with the profile, chooses the explicit component or affected components from supervisor state, and takes the lower of the requested and configured item limits.

This is the multi-agent context boundary: specialists do not share a transcript and do not query the store directly. Each receives the current task and its own `ContextBundle`. Outputs move between agents indirectly as persisted evidence selected for a later task. The supervisor owns agent choice, task assignment, context retrieval, result persistence, and compact-state updates.

The tests make that behavior concrete. Deployment and database specialists receive different evidence for the same incident. A source filter excludes unrelated telemetry. Component and time-window filters exclude otherwise valid evidence. Evidence from another incident never crosses the incident boundary.

## Selection is filtering, ranking, deduplication, and budgeting

Metadata filtering is necessary but insufficient. A busy incident can contain hundreds of records that all match the incident and component. The context service therefore runs a deterministic pipeline:

```csharp
var specification = specificationFactory.Create(request);
var candidates = await evidenceRepository.QueryAsync(
    specification,
    cancellationToken);
var semanticCandidates = await semanticSearch.SearchAsync(
    specification,
    candidates,
    cancellationToken);
var combinedCandidates = candidates
    .Concat(semanticCandidates)
    .GroupBy(item => item.EvidenceId)
    .Select(group => group.First())
    .ToArray();
var ranked = evidenceRanker.Rank(
    combinedCandidates,
    specification,
    request.ExecutionState);
var deduplicated = evidenceDeduplicator.Deduplicate(
    ranked,
    out var duplicatesRemoved);
var selected = ApplyBudget(deduplicated, specification);
```

The semantic-search abstraction is real, but its registered implementation is `DisabledSemanticEvidenceSearch`, which returns no candidates. The active behavior is deterministic metadata filtering and explainable term-based ranking. Calling it vector search, hybrid retrieval, or embedding-based RAG would be inaccurate.

`ExplainableEvidenceRanker` adds weights for exact incident membership, component match, profile evidence priority, time-window overlap, relevant source agent, goal-term overlap, active-hypothesis terms, recency, and application-assigned importance. Stable tie breakers use creation time and evidence ID. The score orders evidence; it is not a probability or calibrated confidence value.

Deduplication then removes repeated evidence IDs and normalized duplicates based on source agent, evidence type, component, and summary. Because ranking happens first, the highest-ranked copy is retained.

Finally, `ApplyBudget` walks the ranked list and accepts only items that fit maximum item count, approximate character count, and per-category limits:

```csharp
foreach (var item in evidence)
{
    if (selected.Count >= specification.MaximumItems)
        break;

    var evidenceType = item.Evidence.EvidenceType;
    categoryCounts.TryGetValue(evidenceType, out var categoryCount);
    if (specification.PerCategoryLimits is not null &&
        specification.PerCategoryLimits.TryGetValue(evidenceType, out var limit) &&
        categoryCount >= limit)
        continue;

    var itemSize = item.Evidence.Summary.Length
        + item.Evidence.DetailedContent.Length;
    if (usedCharacters + itemSize > specification.ContextBudget)
        continue;

    usedCharacters += itemSize;
    categoryCounts[evidenceType] = categoryCount + 1;
    selected.Add(item);
}
```

The budget uses characters, not model tokens, and the output reports it as an approximation. A growth test persists 75 relevant database records, requests five, and verifies that the bundle contains five, stays within its character budget, and retains the highly relevant, high-importance record. Storage growth is therefore independent of working-context growth.

Relevance wins over token maximization. The selector considers task terms, component, time, source, evidence age, hypotheses, and profile priority; it removes repetition and stops at bounded limits. This directly addresses stale context, irrelevant retrieval, duplicated findings, and uncontrolled prompt growth.

## Constructing the specialist input

The supervisor retrieves context immediately before invocation and packages it with the current task and small deterministic constraints:

```csharp
context = await contextService.GetContextAsync(
    new ContextRequest(
        state.IncidentId,
        assignment.TargetAgent,
        assignment.Task,
        requestTypes,
        component,
        ExecutionState: state,
        From: from,
        To: to,
        SourceAgents: assignment.SourceAgents,
        RetrievalIteration: iteration),
    timeout.Token);

finding = await specialist.InvestigateAsync(
    new SpecialistRequest(
        state.IncidentId,
        assignment.Task,
        context,
        new Dictionary<string, string>
        {
            ["currentGoal"] = currentGoal,
            ["retrievalIteration"] = iteration.ToString()
        }),
    timeout.Token);
```

When live-model mode is enabled, the shared `IInvestigationLanguageModel` turns only this selected bundle into the agent request. The system instruction tells the specialist to analyze only supplied structured context, and the user content contains the incident summary, current task, evidence IDs, types, components, summaries, and detailed content:

```csharp
var evidence = string.Join(
    "\n",
    request.Context.Evidence.Select(item =>
        $"[{item.EvidenceId}] {item.EvidenceType} {item.Component}: " +
        $"{item.Summary} | {item.DetailedContent}"));

var response = await agent.RunAsync(
    $"""
    Incident: {request.Context.IncidentSummary}
    Task: {request.CurrentTask}
    Selected evidence:
    {evidence}
    """,
    cancellationToken: cancellationToken);
```

The implementation does not append the full supervisor state, every previous agent response, credentials, or hidden reasoning. The final recommendation call is smaller still: it receives the goal, compact hypotheses, and evidence-reference summaries rather than the full evidence store.

## Updating evidence and state

After a specialist returns, the supervisor creates a new `EvidenceRecord` and saves it through `IEvidenceRepository`. It then updates compact coordination state:

```csharp
var evidence = ToEvidence(state.IncidentId, finding);
await evidenceRepository.AddAsync(evidence, timeout.Token);
UpdateCompactState(state, assignment, finding, evidence);
```

`UpdateCompactState` removes the specialist from pending work, marks it completed, adds an `EvidenceReference`, and optionally adds an `ActiveHypothesis` with the evidence ID. Later retrieval can rank against that hypothesis without copying the earlier detailed payload into state.

There is no transaction spanning evidence persistence and the in-memory state update. If durable coordination were required, the state store, recovery semantics, and consistency boundary would have to be designed explicitly rather than inferred from evidence persistence.

## Retrieval on demand without context accumulation

A specialist can return an `AdditionalContextRequirement` specifying an allowed evidence type, component, time range, missing information, reason, and bounded keyword list. The database demo does this after its first pass to request database evidence for `orders-database`.

The supervisor validates that request against the agent's retrieval profile and structural limits. It then changes the typed retrieval constraints and performs another bounded retrieval. Crucially, the next invocation receives a fresh `ContextBundle`; the first prompt is not appended to the second.

Adaptive retrieval has hard stops:

- maximum specialist invocations;
- maximum retrieval iterations per task;
- maximum investigation rounds;
- retrieval timeout and cancellation;
- duplicate selected-ID detection;
- validation of requested evidence type, component, time range, and keyword size.

Compact outcomes distinguish sufficient context, additional context provided, invalid request, iteration limit, repeated retrieval, and timeout. Tests verify the two-iteration path, iteration limit, repeated-selection stop, invalid requests, and specialist timeout.

This is retrieval on demand: the specialist asks for a narrower category when the first bundle is insufficient, but the application remains responsible for authorization-like policy boundaries and termination. The model never emits SQL and never gains direct repository access.

## Failure modes the architecture makes visible

The implementation addresses several common context failures directly:

- **Context overload:** item, character, category, and age limits bound each bundle.
- **Stale information:** maximum evidence age, time windows, and recency ranking reduce stale selection; they do not guarantee truth.
- **Irrelevant retrieval:** incident, profile, source, component, and time filters constrain candidates before ranking.
- **Duplicated context:** deterministic ID and normalized-content deduplication runs before budgeting.
- **Missing workflow state:** the bundle includes incident summary and active hypotheses, but active supervisor state remains process-local and can still be lost on restart.
- **Uncontrolled conversation growth:** specialists receive explicit requests rather than shared conversation history.
- **Cross-incident leakage:** the repository's first filter is `IncidentId`, and tests prove ranking never crosses that boundary.

Cross-incident isolation is not user authorization. The sample has no authentication or authorization and accepts the supervisor's incident identity as trusted application input. A production system must derive tenant, user, and data-access filters from an authenticated server-side identity, not from model output or client-supplied fields.

## What context engineering does not solve

This architecture controls what information reaches an agent. It does not provide:

- workflow durability or restart recovery;
- authorization, tenant isolation, or permission evaluation;
- an immutable business audit history;
- distributed locking or multi-instance consistency;
- tracing, metrics, alerts, or operational observability by itself;
- guaranteed model reasoning quality or factual correctness;
- generic RAG, embeddings, vector search, or semantic reranking.

SQLite makes evidence persistent, but persistence alone does not make the active workflow durable. Retrieval diagnostics explain selection factors, but they are not hidden reasoning or a compliance audit. A bounded prompt reduces noise, but it cannot guarantee that the model interprets correct evidence correctly.

## Production principles derived from the implementation

Treat context construction as an application service with a typed contract. Keep durable source material separate from compact coordination state. Establish the incident, tenant, and permission boundary before relevance ranking. Give each specialist an explicit retrieval profile and allow assignments to narrow it, never expand it.

Filter before ranking, rank before deduplication, and budget before injection. Keep provenance, evidence IDs, ranking factors, retrieval reasons, and budget usage alongside selected content so engineers can explain what entered a model call. Use stable, deterministic selection when structured keys are sufficient; add semantic retrieval only when measured retrieval quality justifies its complexity.

Rebuild context for each invocation instead of appending prompts indefinitely. Validate requests for more context and bound iterations, time, invocations, and repeated selections. Persist full findings once, pass references through orchestration state, and retrieve details only for agents whose tasks require them.

Finally, test the negative boundaries: another incident's evidence must not appear, irrelevant categories must stay out, growth must remain bounded, repeated findings must collapse, invalid adaptive requests must stop, and raw detail must not leak into compact state. Those are architectural correctness tests, not prompt-style preferences.

## Continue Exploring

The production context-engineering implementation belongs to *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2)
