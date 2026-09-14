# Building Bounded Evidence Context Bundles for AI Agents in .NET

As an investigation runs, its evidence store grows. A model prompt should not grow with it.

Sending every matching record to every specialist creates noisy prompts, repeated findings, and a context size that changes unpredictably as incidents run longer. The Chapter 9 implementation in *Building AI Agents with .NET — Part 2* treats context assembly as a deterministic application service instead: retrieve candidates, rank them, remove duplicates, and stop at explicit limits before a model sees anything.

This article focuses on that retrieval funnel. It uses the real incident-investigation implementation from the companion repository, where SQLite stores evidence and specialist Agents receive a fresh `ContextBundle` for each invocation.

## Why filtering alone is not enough

An incident and a component are useful boundaries, but a busy incident can still contain hundreds of records. A query that returns every record matching those fields does not answer the engineering question: which small subset best supports this task?

The implementation gives each specialist a retrieval profile. A profile specifies evidence categories, source Agents, an item limit, a character budget, and per-category limits. An assignment may narrow that policy, but it cannot broaden it. The database Agent, for example, prioritizes database evidence and can use selected log, application, and deployment findings:

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

This is a policy boundary, not a relevance score. It keeps unrelated categories out before ranking begins.

## Make the retrieval contract explicit

The supervisor describes the work; it does not compose SQL or pass the repository to a model. The context service receives a typed request containing the incident, target Agent, task, optional component and time range, source restrictions, compact execution state, and retrieval iteration:

```csharp
public sealed record ContextRequest(
    Guid IncidentId,
    string TargetAgent,
    string CurrentGoal,
    IReadOnlyCollection<EvidenceType>? AllowedEvidenceTypes = null,
    string? Component = null,
    int MaximumItems = 20,
    SupervisorExecutionState? ExecutionState = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyCollection<string>? SourceAgents = null,
    int RetrievalIteration = 1);
```

`EvidenceContextService` converts that request into a `RetrievalSpecification`, queries the exact incident boundary, and returns a `ContextBundle`. The boundary makes selection inspectable and testable independently of prompt construction.

## The four-stage retrieval funnel

The service composes the pipeline in a fixed order:

```csharp
var candidates = await evidenceRepository.QueryAsync(
    specification, cancellationToken);
var semanticCandidates = await semanticSearch.SearchAsync(
    specification, candidates, cancellationToken);
var combinedCandidates = candidates
    .Concat(semanticCandidates)
    .GroupBy(item => item.EvidenceId)
    .Select(group => group.First())
    .ToArray();
var ranked = evidenceRanker.Rank(
    combinedCandidates, specification, request.ExecutionState);
var deduplicated = evidenceDeduplicator.Deduplicate(
    ranked, out var duplicatesRemoved);
var selected = ApplyBudget(deduplicated, specification);
```

The order matters:

1. **Filter** by incident, permitted evidence types and sources, component, time window, and profile limits.
2. **Rank** the candidates for this task.
3. **Deduplicate** so repeated content cannot consume the budget.
4. **Budget** the final list by item count, approximate characters, and category limits.

The semantic-search abstraction exists, but this chapter registers `DisabledSemanticEvidenceSearch`, which returns no semantic candidates. The active implementation is deterministic metadata filtering and explainable term-based ranking; it is not embedding search or generic vector RAG.

## Explain why an item was selected

`ExplainableEvidenceRanker` returns a score plus individual `RankingFactor` values. It adds weight for exact incident membership, component match, profile category priority, time-window overlap, relevant source Agent, terms from the goal, terms from active hypotheses, recency, and application-assigned importance.

```csharp
if (specification.Components.Contains(
        evidence.Component, StringComparer.OrdinalIgnoreCase))
{
    factors.Add(new(
        "component", 30,
        "Exact affected component match."));
}

if (OverlapsWindow(evidence, specification.From, specification.To))
{
    factors.Add(new(
        "time-window", 20,
        "Evidence overlaps the requested incident window."));
}
```

Scores are ordering signals, not probabilities or calibrated confidence. Stable tie-breakers use creation time and evidence ID, so the same inputs produce a predictable order.

After ranking, `DeterministicEvidenceDeduplicator` removes repeated IDs and normalized duplicates keyed by source Agent, evidence type, component, and summary. Because ranking happens first, the highest-ranked copy survives.

## Enforce a budget before prompt construction

`ApplyBudget` walks the ranked list and accepts an item only if it fits all configured limits:

```csharp
var itemSize = item.Evidence.Summary.Length
    + item.Evidence.DetailedContent.Length;

if (usedCharacters + itemSize > specification.ContextBudget)
{
    continue;
}

usedCharacters += itemSize;
categoryCounts[evidenceType] = categoryCount + 1;
selected.Add(item);
```

The budget counts characters, not model tokens, so it is deliberately approximate. The resulting bundle reports both configured limits and actual usage through `ContextBudgetUsage` and `RetrievalDiagnostics`. Each selected item also carries its evidence ID, source, type, component, score, factors, and retrieval reason.

This keeps durable storage and working context separate. A test persists 75 relevant database records, requests five items, and verifies that five are selected, the character budget is respected, and a highly relevant record remains in the bundle. The store can grow without making one specialist prompt grow with it.

## Adaptive retrieval must still be bounded

Sometimes the first bundle is insufficient. A specialist can return an `AdditionalContextRequirement` containing an allowed evidence type, component, optional time range, missing information, reason, and bounded keywords. The supervisor validates it against the specialist's retrieval profile before changing the next request.

The next iteration is a fresh `ContextBundle`; the first prompt is not appended to the second. The supervisor also stops on explicit boundaries: maximum specialist invocations, maximum retrieval iterations per task, maximum investigation rounds, timeout or cancellation, invalid requirements, and repeated evidence selections.

```csharp
var validation = validator.Validate(
    assignment.TargetAgent,
    finding.AdditionalContextRequired);
if (!validation.IsValid)
{
    finalStatus = RetrievalAttemptStatus.InvalidRequest;
    break;
}

requestTypes = [finding.AdditionalContextRequired.RequiredEvidenceType];
component = finding.AdditionalContextRequired.Component;
from = finding.AdditionalContextRequired.From;
to = finding.AdditionalContextRequired.To;
```

Tests cover the successful two-iteration path, invalid requests, repeated retrieval detection, iteration limits, and specialist timeouts. These are application safety tests around the loop; they do not assert that a language model reasons correctly.

## What this pattern does—and does not—guarantee

The funnel prevents unbounded prompt growth, duplicate evidence, and category leakage within the implemented incident boundary. It does not guarantee that selected evidence is true, that a model will interpret it correctly, or that character counts map cleanly to provider token limits.

The sample also does not implement semantic retrieval: the semantic interface is disabled. SQLite persistence protects stored evidence, but the active supervisor execution state and demo queue are process-local. Authentication, authorization, tenant isolation, distributed locking, workflow recovery, and model-quality evaluation remain separate production concerns.

## A practical implementation checklist

- Define a typed retrieval request instead of letting Agents query storage directly.
- Give each Agent a profile that can be narrowed by an assignment.
- Filter before ranking, rank before deduplication, and budget before injection.
- Keep ranking factors and budget diagnostics beside selected content.
- Count characters only as an explicit approximation; measure provider tokens separately in production.
- Rebuild context for each invocation rather than appending prompts indefinitely.
- Validate additional-context requests and cap retries, invocations, and time.
- Test negative boundaries: another incident, unrelated categories, duplicates, oversized bundles, and invalid adaptive requests.

The central design rule is simple: durable evidence may grow, but an Agent's working context must remain deliberately bounded.

## Continue Exploring

This article is derived from Chapter 9, “Production-Ready Context Engineering for Multi-Agent Systems,” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 9 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-09)
