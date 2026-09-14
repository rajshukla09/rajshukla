# Composing Concurrent Agent Reviews with a Separate Decision Workflow in .NET

Independent specialist reviews are a good candidate for parallel execution. Security, quality, and architecture reviewers can inspect the same software release without waiting for one another. The final release decision is different: it should consume the collected findings rather than compete with them as another independent branch.

The Chapter 5 implementation in *Building AI Agents with .NET — Part 2* makes that boundary explicit. It runs one native Microsoft Agent Framework workflow for concurrent review, reads the fan-in result, then runs a second workflow containing `ReleaseAgent`.

```text
release request
   -> SecurityAgent   -┐
   -> QualityAgent     ├-> collected findings
   -> ArchitectureAgent-┘
                            -> separate ReleaseAgent workflow
                            -> decision
```

This design is narrower than “make every agent concurrent.” It follows the dependency graph: independent evidence can fan out; a decision that depends on that evidence must wait for fan-in.

## Start with the dependency graph

The release-review application has four roles:

- `SecurityAgent` reviews authentication, authorization, secrets, payments, and security risk;
- `QualityAgent` reviews test evidence, rollback readiness, and regression risk;
- `ArchitectureAgent` reviews compatibility, dependencies, failure isolation, and rollback impact; and
- `ReleaseAgent` reconciles the findings into a deploy, conditional-deploy, or hold recommendation.

The first three agents receive the same release request and do not need another reviewer’s live context to begin. `ReleaseAgent` does need their conclusions. That makes the topology:

```text
independent reviewers -> fan-in evidence -> release decision
```

The important engineering step is deciding that dependency before choosing a builder API. Parallelism is justified by independence, not by the number of agents in the system.

## Build the native concurrent review

The implementation creates only the independent reviewers in the first workflow:

```csharp
AIAgent[] independentReviewers =
[
    security.Create(),
    quality.Create(),
    architecture.Create()
];

Workflow fanOutFanIn =
    AgentWorkflowBuilder.BuildConcurrent(
        independentReviewers);

var reviews = await InProcessExecution.RunAsync(
    fanOutFanIn,
    releaseRequest,
    cancellationToken: cancellationToken);

var reviewResult = WorkflowResultReader.Read(
    "Concurrent reviews",
    reviews.NewEvents);
```

`BuildConcurrent` is the native MAF fan-out/fan-in boundary. The same release text enters each branch. The workflow output is then read as an application result rather than passed as an opaque runtime object into another service.

`ReleaseAgent` is intentionally absent from this array. Adding it to the concurrent branch would allow the decision agent to run before the independent evidence was collected, which would change the business meaning of the workflow.

## Make fan-in an explicit application contract

MAF returns workflow events. The application’s `WorkflowResultReader` converts those events into a `ReviewResponse` containing the pattern name, final decision text, readable events, and raw events.

For the concurrent review stage there is no `ReleaseAgent` contribution yet, so the reader combines specialist contributions:

```csharp
var decision = readable
    .Where(x =>
        BusinessAgentNames.IsReviewAgent(x.Executor))
    .Select(x => $"{x.Executor}: {x.Text}")
    .LastOrDefault();
```

The actual implementation joins all readable reviewer contributions when the pattern is `Concurrent reviews`. That joined text is the fan-in artifact. It is not a hidden shared conversation and it is not a framework-owned decision object.

This boundary gives the application control over what the decision agent receives. It can preserve specialist names, omit infrastructure events, limit the size of the combined evidence, or replace the text projection with a stronger typed contract later.

The projection also avoids treating runtime executor identifiers as business names. Generated values such as `SecurityAgent_7fba...` are normalized to `SecurityAgent` before the readable result is assembled.

## Run the decision as a second workflow

After fan-in, the application creates a new sequential workflow containing only `ReleaseAgent`:

```csharp
Workflow decision =
    AgentWorkflowBuilder.BuildSequential(
        release.Create());

var decisionInput =
    $"Release request:\n{releaseRequest}\n\n" +
    $"Independent findings (fan-in):\n" +
    reviewResult.Decision;

var releaseDecision = await InProcessExecution.RunAsync(
    decision,
    decisionInput,
    cancellationToken: cancellationToken);

var result = WorkflowResultReader.Read(
    "Concurrent",
    releaseDecision.NewEvents);
```

This is two workflow executions composed by application code. It is not one native MAF graph with a special “fan-in then decision” node, and it is not a durable checkpoint between the executions.

The explicit boundary has practical benefits:

- the decision cannot start before the review projection exists;
- the decision input can be logged, bounded, redacted, or validated as an application contract;
- failure of the decision workflow can be distinguished from failure of an independent branch; and
- the persisted run can show which stage produced each event.

It also makes a limitation visible: if the first workflow succeeds and the second fails, the application needs a policy for preserving and retrying the collected evidence. The sample executes both synchronously inside one HTTP request and does not resume the second workflow after a process restart.

## Preserve both stages in the response

The concurrent implementation combines the projections from both executions:

```csharp
return result with
{
    Events =
    [
        .. reviewResult.Events,
        .. result.Events
    ],
    RawEvents =
    [
        .. reviewResult.RawEvents,
        .. result.RawEvents
    ]
};
```

The returned response therefore contains the reviewer lifecycle and the final decision lifecycle. Consumers can render the evidence-gathering phase separately from the decision phase instead of inferring it from one flattened model response.

The persisted run follows the same principle. It stores raw envelopes, projected agent execution windows, readable events, final decision, and timing data. The raw events remain available for diagnostics while the readable projection shields the UI from package-specific runtime names and streaming chunks.

## Project streaming events into readable contributions

Agent responses may arrive as multiple update events. `ReadableEventProjector` groups events by normalized business agent and joins chunks in order:

```csharp
private static string JoinChunks(
    IEnumerable<string?> chunks)
{
    var text = new StringBuilder();
    foreach (var chunk in chunks.Select(x => x!))
        text.Append(chunk);
    return text.ToString().Trim();
}
```

Appending rather than joining with a separator matters. The source chunks can contain the leading or trailing spaces required to reconstruct text correctly.

Infrastructure events such as `ResponseUpdate` and `OutputMessages` are not shown as business contributions. The projection creates stable `AgentStarted`, `AgentCompleted`, and `WorkflowCompleted` events instead.

The tests use chunks such as `"risk "` and `"found"` and assert that the readable contribution is `"risk found"`. They also verify that generated executor IDs normalize and that infrastructure executors do not appear in the result.

This is an adapter boundary, not a claim that the event names are a permanent domain API. Package upgrades should be tested against the projection.

## Compare concurrency without overstating evidence

The application computes comparison metrics from persisted runs. For concurrent execution it records branch counts, execution order, slowest branch, and timing overlap.

The comparison test supplies fixture timestamps where branches overlap:

```csharp
new()
{
    AgentName = "ArchitectureAgent_aabbcc",
    StartedAt = start.AddMilliseconds(5),
    CompletedAt = start.AddMilliseconds(80),
    DurationMilliseconds = 75,
    Status = "Completed"
}
```

The test proves that the comparison algorithm recognizes overlap in supplied data. It does not execute live concurrent MAF agents and therefore does not prove that a provider, scheduler, or host achieved parallel wall-clock behavior in production.

That distinction belongs in the article and in the UI. A metric derived from observed events is useful, but it should not claim more than the event data supports. Missing native timestamps fall back to receipt time, and reconstructed execution windows are approximations.

## Understand failure boundaries

There are at least three distinct failure points:

1. An independent reviewer may fail during fan-out.
2. Fan-in projection may fail to produce usable evidence.
3. The separate `ReleaseAgent` workflow may fail after reviewers complete.

The sample’s `ReviewExecutionService` persists failed application runs rather than fabricating a successful decision. It executes inside the HTTP request, with no retry, durable queue, or checkpoint recovery.

A production design must decide whether fan-in requires every reviewer, a quorum, or best-available evidence. It must also decide whether a successful first stage can be retained for a retry of the second stage, and how to prevent duplicate external side effects if a decision eventually triggers deployment.

The current sample’s ReleaseAgent produces a recommendation, not an authorized deployment. A model-generated `DECISION: DEPLOY` line is output to evaluate, not a command to execute.

## Keep the decision context bounded

Joining all reviewer contributions is simple and transparent, but it can grow with output size. A production fan-in adapter should define limits for:

- maximum contribution length per reviewer;
- maximum combined decision prompt size;
- truncation and redaction rules;
- required evidence fields; and
- behavior when one branch has no readable contribution.

The chapter’s implementation uses text projections because it is teaching native topology and observable event handling. A stricter application could convert each reviewer response into a validated `ReviewFinding` record before creating the decision input.

That conversion would also make comparison and evaluation more reliable. It should not be mistaken for behavior already implemented by the sample.

## Test the topology without calling Azure OpenAI

The Chapter 5 tests inspect deterministic topology and projection behavior rather than running live MAF agents.

Native topology tests verify that sequential review uses the expected ordered array, concurrent review calls `BuildConcurrent` and creates the “Independent findings (fan-in)” decision input, handoff declares four native transfer edges, and group chat has three participants with a six-turn `RoundRobinGroupChatManager` limit.

Projection tests verify chunk joining, generated business-agent-name normalization, removal of framework executors and stream events, and stable workflow-started/workflow-completed events.

Persistence tests recreate an EF Core context and verify that run graphs and ordered children survive. Comparison tests verify supplied branch timing, fan-out/fan-in execution order, handoff paths, and group-chat contribution metrics.

The suite does not execute live Azure OpenAI, prove actual branch overlap, verify model handoff decisions, or prove that group chat produces a parsed release decision. Those require separate integration and evaluation tests.

## Choose this composition when dependencies justify it

Concurrent fan-out/fan-in is a good fit when reviewers can independently inspect the same source request and a later stage must reconcile their findings. It is not automatically faster: wall-clock time is bounded by the slowest branch plus the decision stage, while model calls and token cost increase.

Use a sequential workflow when each participant genuinely needs the previous participant’s context. Use native handoff when responsibility should transfer through explicit allowed edges. Use group chat when a bounded shared conversation is the product requirement. Use a custom supervisor or Magentic orchestration when the next action is not known ahead of time.

The practical design is:

```text
identify independent evidence
    -> fan out with BuildConcurrent
    -> project and validate fan-in
    -> run the dependent decision separately
    -> persist both observable stages
```

The separate decision workflow is not extra ceremony. It makes the dependency, failure boundary, prompt construction, and audit trail explicit.

## Continue Exploring

This article is derived from Chapter 5, “Multi-Agent Orchestration Patterns,” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 5 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-05)
