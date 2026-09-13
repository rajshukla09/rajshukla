# Multi-Agent Orchestration with Microsoft Agent Framework

Multiple agents are useful when a problem contains genuinely different responsibilities and a meaningful way for their work to move between them. They are not an automatic upgrade from a single well-instructed agent. Every additional participant introduces another model call, another context boundary, another failure point, and another execution path to observe.

The architectural question is therefore not “How many agents can we add?” It is “What collaboration structure does the work require?”

This article follows a software-release review implemented with Microsoft Agent Framework (MAF). Security, quality, and architecture specialists examine the same release from different perspectives. A release specialist makes a deploy, conditional-deploy, or hold decision when the selected topology includes it. The sample implements four native MAF patterns—sequential, concurrent, handoff, and group chat—so the differences are visible in code rather than implied by prompts.

## Why specialize at all?

A release review has several distinct concerns:

- `SecurityAgent` reviews authentication, authorization, secrets, payments, and security risk.
- `QualityAgent` reviews test evidence, rollback readiness, and regression risk.
- `ArchitectureAgent` reviews compatibility, dependencies, failure isolation, and rollback impact.
- `ReleaseAgent` reconciles available findings and produces the formal release decision.

Those boundaries make each role easier to instruct and evaluate. They also allow the orchestration to express real dependencies: independent reviewers can run in a fan-out, while a release decision must wait for their findings.

A single agent remains preferable when the responsibilities use the same context, tools, authority, and success criteria, or when splitting them would only create artificial personas. Specialization earns its cost when at least one of these is true:

- Different stages need different instructions, tools, or permissions.
- Independent perspectives should be collected without contaminating one another's initial analysis.
- Later judgment must consume clearly identified earlier results.
- Responsibility must move through a constrained escalation graph.
- Separate outputs need separate evaluation, audit, or operational ownership.

The sample's agents do not contain deterministic security or release rules. They are Azure OpenAI-backed `AIAgent` instances whose text is model-generated. The application defines their roles and topology; it does not manufacture their findings.

```csharp
public sealed class SecurityAgent(ReleaseReviewAgentFactory factory)
{
    public AIAgent Create() => factory.Create(
        "SecurityAgent",
        "Reviews release security and authentication risk.",
        "Review authentication, authorization, secrets, and payment-security risk. Treat the supplied release facts as mocked evidence.");
}
```

Architecturally, this wrapper owns one specialist definition. At runtime `ReleaseReviewAgentFactory` creates a named MAF agent using the configured Azure OpenAI chat client. The same pattern defines the other roles with their own descriptions and instructions.

## Orchestration is not agent intelligence

An agent produces judgment inside the context it receives. Orchestration determines which agent runs, when it runs, what context reaches it, and how its result moves forward.

The release-review sample keeps that distinction explicit:

```text
Deterministic application structure
-> participant set
-> sequential order or concurrent branches
-> legal handoff edges
-> group-chat manager and turn limit
-> persistence and result projection

Model-driven behavior
-> specialist findings
-> release recommendation
-> handoff choices within configured edges
-> conversation contributions
```

This is why changing topology is not a cosmetic graph change. The same `ArchitectureAgent` sees accumulated prior work in a sequence, only the original release request in an independent fan-out branch, a shared transcript in group chat, or a transferred task in handoff orchestration.

The application exposes four allow-listed pattern names through `ReviewExecutionService`. It resolves the selected `IReleaseReview`, executes it, and persists either the completed projection or a failed run. The model never chooses an arbitrary topology.

## Sequential execution: context as a dependency chain

Sequential orchestration fits when each stage should receive the result produced before it. The implemented order is:

```text
Release request
-> SecurityAgent
-> QualityAgent
-> ArchitectureAgent
-> ReleaseAgent
-> decision
```

The code expresses that topology directly:

```csharp
public async Task<ReviewResponse> RunAsync(
    string releaseRequest,
    CancellationToken cancellationToken)
{
    AIAgent[] agents =
    [
        security.Create(),
        quality.Create(),
        architecture.Create(),
        release.Create()
    ];

    Workflow workflow = AgentWorkflowBuilder.BuildSequential(agents);
    var execution = await InProcessExecution.RunAsync(
        workflow,
        releaseRequest,
        cancellationToken: cancellationToken);

    return WorkflowResultReader.Read("Sequential", execution.NewEvents);
}
```

`BuildSequential` makes array order part of the workflow definition. The initial release request enters `SecurityAgent`; later agents operate on the sequence's evolving context. `QualityAgent` is explicitly instructed to consider earlier findings, and `ReleaseAgent` is last because its decision depends on accumulated specialist work.

This topology is easy to reason about, but it serializes latency and expands downstream context. If three reviews do not actually depend on one another, forcing them into a sequence delays later work and lets earlier model output influence specialists that could have assessed the source evidence independently.

## Concurrent execution: fan out independent reviews

Security, quality, and architecture can also review the original release independently. That dependency structure supports a concurrent fan-out/fan-in:

```text
                       -> SecurityAgent     -|
Release request -> fan -> QualityAgent      |-> collected findings
                       -> ArchitectureAgent -|
                                                   |
                                                   v
                                            ReleaseAgent
                                                   |
                                                   v
                                               decision
```

The implementation deliberately uses two native workflows. The first contains only the independent reviewers:

```csharp
AIAgent[] independentReviewers =
[
    security.Create(),
    quality.Create(),
    architecture.Create()
];

Workflow fanOutFanIn =
    AgentWorkflowBuilder.BuildConcurrent(independentReviewers);

var reviews = await InProcessExecution.RunAsync(
    fanOutFanIn,
    releaseRequest,
    cancellationToken: cancellationToken);

var reviewResult = WorkflowResultReader.Read(
    "Concurrent reviews",
    reviews.NewEvents);
```

`BuildConcurrent` sends the same release request into the three native branches and produces a fan-in collection. None of these reviewers consumes another reviewer's live context. That independence is the reason for concurrency—not merely the fact that there are three agents.

The release specialist is not a fourth concurrent branch. It requires the collected evidence, so the application runs it afterward in a separate workflow:

```csharp
Workflow decision =
    AgentWorkflowBuilder.BuildSequential(release.Create());

var decisionInput =
    $"Release request:\n{releaseRequest}\n\n" +
    $"Independent findings (fan-in):\n{reviewResult.Decision}";

var releaseDecision = await InProcessExecution.RunAsync(
    decision,
    decisionInput,
    cancellationToken: cancellationToken);

var result = WorkflowResultReader.Read(
    "Concurrent",
    releaseDecision.NewEvents);
```

At runtime the application reads the first execution's observable results, constructs the downstream input, and starts the one-agent decision workflow. This is deterministic composition of two known stages, not one native graph containing all four participants.

That boundary matters for failures and durability. The concurrent reviews can complete and the release workflow can still fail. The sample executes both synchronously inside one HTTP request and does not persist a checkpoint between them from which the second workflow can resume.

## How results are combined

MAF returns workflow events, not the application's final release contract. `WorkflowResultReader` converts each public event into a `ReviewEvent`, projects readable agent contributions, and selects the last non-empty `ReleaseAgent` contribution as the final decision.

The concurrent review stage has no `ReleaseAgent`, so its intermediate result is built by joining the specialist contributions:

```csharp
var decision = readable
    .Where(x => BusinessAgentNames.Normalize(x.Executor) == "ReleaseAgent")
    .Select(x => x.Text)
    .LastOrDefault(x => !string.IsNullOrWhiteSpace(x))
    ?? (pattern.Contains("Concurrent reviews", StringComparison.Ordinal)
        ? string.Join(
            "\n\n",
            readable
                .Where(x => BusinessAgentNames.IsReviewAgent(x.Executor))
                .Select(x => $"{x.Executor}: {x.Text}"))
        : "Workflow completed without a ReleaseAgent decision.");
```

For concurrent review, that joined text is the fan-in evidence passed to the second workflow. For sequential, concurrent-plus-release, and handoff execution, the final `ReleaseAgent` contribution becomes the decision.

This adapter is also an isolation layer around runtime event shapes. `ReadableEventProjector` joins streamed response chunks, normalizes generated executor identifiers to business agent names, removes infrastructure events from the normal view, and preserves handoff envelopes as orchestration facts. Raw envelopes are retained separately for persistence.

The projection is intentionally cautious. If a native event has no usable timestamp, the reader falls back to receipt time. Repeated appearances of the same normalized business agent are grouped into one stored execution window, so persisted timing is an observable approximation rather than an exact record of private runtime state.

## Handoff: controlled transfer of responsibility

Not every process is a fixed pipeline or independent fan-out. In a handoff topology, one agent owns the task until it transfers responsibility through an allowed edge.

The sample starts with `ReleaseAgent` and configures four legal transfers:

```csharp
Workflow workflow = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(releaseAgent)
    .WithHandoff(
        releaseAgent,
        securityAgent,
        "Authentication, authorization, secrets, or payment-security concern")
    .WithHandoff(
        securityAgent,
        architectureAgent,
        "The security finding depends on boundaries, dependencies, or rollback design")
    .WithHandoff(
        securityAgent,
        releaseAgent,
        "Security review is complete and a release decision is needed")
    .WithHandoff(
        architectureAgent,
        releaseAgent,
        "Architecture review is complete and a release decision is needed")
    .Build();
```

The application defines the permitted transfer graph and the reasons associated with its edges. Native handoff orchestration selects within those constraints at runtime. There is no custom `while` loop acting as a supervisor, and agents cannot invent a participant outside the configured graph.

Handoff is appropriate when ownership and escalation are the real business concepts. It is less appropriate when every specialist must always contribute or when the entire order is already known.

## Group chat: shared context with bounded turns

Group chat gives specialists a common evolving conversation instead of an input/output pipeline. The sample includes Security, Quality, and Architecture and uses a deterministic round-robin manager:

```csharp
AIAgent[] participants =
[
    security.Create(),
    quality.Create(),
    architecture.Create()
];

Workflow workflow = AgentWorkflowBuilder
    .CreateGroupChatBuilderWith(
        agents => new RoundRobinGroupChatManager(agents)
        {
            MaximumIterationCount = MaximumTurns
        })
    .AddParticipants(participants)
    .Build();
```

`MaximumTurns` is six. At runtime the manager determines participant order, while each agent generates its contribution using the shared conversation. The limit prevents an unbounded exchange, but it does not bound tokens, cost, or elapsed time by itself.

The implemented group chat does not contain `ReleaseAgent`. Consequently `WorkflowResultReader` returns its fixed “completed without a ReleaseAgent decision” fallback rather than a formal deploy/hold result. A production process that requires a decision would need an additional aggregation or decision stage; that extension is not present in the source.

## Fixed topology versus dynamic delegation

Sequential, concurrent, handoff, and group chat all begin with collaboration structures known by the application. Another implementation in the same system uses a custom dynamic supervisor for incident investigation: the supervisor `AIAgent` chooses among Deployment, Log Analysis, Metrics, and Database specialists exposed as functions, and a separate Root Cause Agent synthesizes accumulated findings.

That design answers a different question: the application knows the permitted specialists and budgets, but the next specialist and repeated delegation are model-selected. Magentic orchestration goes further by supporting planning and replanning around an open-ended objective.

Use adaptive orchestration only when the execution plan genuinely cannot be encoded upfront. A model-driven supervisor adds decision variability, more context assembly, termination policy, repeated-agent handling, and another layer of failure analysis. When the dependency chain or fan-out is already known, an explicit topology is usually easier to test and operate.

## Multi-agent does not automatically mean better

Splitting one request across several agents creates costs that a single-agent design may avoid:

- **Coordination:** inputs and outputs need contracts. Fan-in, handoff eligibility, turn order, termination, and partial results become application concerns.
- **Latency:** sequential calls accumulate; concurrent fan-in waits for the slowest branch; group chat adds turns; a downstream decision adds another call.
- **Context:** sequential and conversational patterns carry growing model-generated context. Concurrent branches reduce cross-contamination but require explicit aggregation afterward.
- **Reliability:** every Agent or provider call can fail. Concurrent work raises partial-success questions; a composed second workflow can fail after the first succeeds; handoff and chat introduce path-specific failures.
- **Cost:** more specialists usually mean more model requests and more tokens. Parallelism may reduce wall-clock time without reducing spend.
- **Observability:** logs need correlation by run, workflow, agent, branch, and handoff. Runtime event order must not be mistaken for business topology, especially during fan-out.
- **Evaluation:** each specialist and the combined decision need quality criteria. Testing builder configuration does not establish that a live model chooses good handoffs or produces useful reviews.

The smallest architecture that preserves the real responsibility boundaries is usually the strongest starting point. A single agent with tools can be the right answer. Multiple agents become justified when their separation improves authority, context isolation, collaboration, or evaluation enough to offset operational complexity.

## Failure and production boundaries

The sample persists completed and failed application runs, raw event envelopes, readable events, reconstructed agent windows, and final decisions in SQLite. History survives process restart, but active workflow execution does not. Every pattern runs in-process and synchronously inside its HTTP request through `InProcessExecution.RunAsync`.

A production implementation must decide more than which builder to call:

- Define whole-workflow and per-agent timeouts, cancellation semantics, retries, and retry budgets.
- Decide whether fan-in requires every branch, a quorum, or best-available evidence, and whether successful branches can be reused after another branch fails.
- Persist durable orchestration state if work must survive host loss; historical event storage is not a checkpoint engine.
- Apply identity and authorization at each specialist's tools and data boundaries rather than trusting role prompts.
- Version prompts, model configuration, topology, input, and aggregation rules so a decision can be reproduced and audited.
- Add correlation and tracing around model calls, branches, handoffs, event projection, and downstream side effects.
- Bound group-chat turns, tokens, result sizes, cost, and termination conditions.
- Treat every model-produced finding as untrusted input to later agents and deterministic business actions.
- Use idempotency and explicit approval around consequential external effects; a release recommendation should not deploy software merely because an agent emitted `DECISION: DEPLOY`.

The repository's seventeen tests verify the deterministic shell: expected builder calls and participant declarations, four handoff edges, the six-turn group-chat limit, event projection, business-name normalization, persisted fixture graphs, comparison metrics, and availability of all four UI patterns. They do not call Azure OpenAI or execute every live native pattern. In particular, fixture timestamps prove the comparison logic can recognize supplied overlap; they do not prove that live concurrent Agent calls overlapped.

The durable principle is straightforward: specialize only where boundaries are real, choose topology from data dependencies and ownership, keep orchestration policy explicit, and measure the resulting system by correctness, latency, cost, recoverability, and operational clarity—not by agent count.

## Continue Exploring

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Companion source for Building AI Agents with .NET — Part 2](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2)
