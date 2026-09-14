# Testing RequestPort Workflows with Microsoft Agent Framework and .NET

A human-in-the-loop workflow is not proven by a unit test that calls an approval policy and checks a Boolean. The production behavior spans several boundaries: an AI-assisted stage proposes facts, deterministic code decides whether review is required, the workflow selects one of two edges, and a `RequestPort` publishes a typed request instead of producing the final result.

Calling Azure OpenAI in every test would not solve that problem. It would make the tests slower and nondeterministic while leaving the graph's routing and suspension behavior implicit.

The claims-review implementation in *Building AI Agents with .NET — Part 2* uses a more useful split:

- controlled test agents replace the nondeterministic model calls;
- the real validators, approval policy, executors, and MAF workflow remain in the test;
- in-memory SQLite preserves the persistence behavior;
- executor traces and workflow events expose the branch that actually ran.

This produces deterministic tests without reducing the workflow to a mock of itself.

## Replace the AI boundary, not the workflow

The production executors depend on narrow application interfaces:

```csharp
public interface IClaimIntakeAgent
{
    Task<AgentResult<ClaimDraft>> ExecuteAsync(
        ClaimIntakeAgentRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRiskAssessmentAgent
{
    Task<AgentResult<ClaimRiskAssessment>> ExecuteAsync(
        RiskAssessmentAgentRequest request,
        CancellationToken cancellationToken = default);
}
```

The test implementations return predictable application results. The intake double normalizes the submitted fields, while the risk double derives a score from the claimed amount, a fraud keyword, and validation warnings:

```csharp
internal sealed class TestRiskAssessmentAgent : IRiskAssessmentAgent
{
    public Task<AgentResult<ClaimRiskAssessment>> ExecuteAsync(
        RiskAssessmentAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int score = (int)Math.Min(100, request.Claim.ClaimedAmount / 1000);
        if (request.Claim.IncidentSummary.Contains(
                "fraud", StringComparison.OrdinalIgnoreCase))
        {
            score = 85;
        }

        var assessment = new ClaimRiskAssessment(
            score >= 70 ? ClaimRiskLevel.High : ClaimRiskLevel.Low,
            score,
            ["Test risk factor"],
            "Test risk assessment.",
            false);

        return Task.FromResult(
            AgentResult<ClaimRiskAssessment>.Success(
                assessment,
                nameof(TestRiskAssessmentAgent)));
    }
}
```

The excerpt is shortened, but it preserves the test double's essential contract: stable input produces stable typed output and cancellation is honored. It does not pretend to test prompting, deployment configuration, network behavior, or model compliance.

The important point is where the substitution stops. Tests still construct the production `ClaimIntakeAgentExecutor`, `ClaimValidationExecutor`, `RiskAssessmentAgentExecutor`, `HumanApprovalRequestExecutor`, and `ClaimDecisionExecutor`. They also use the production `HumanApprovalPolicy` and `ClaimReviewWorkflow`.

## Keep the real conditional graph under test

The workflow has two mutually exclusive edges after risk assessment:

```csharp
return new WorkflowBuilder(intake)
    .AddEdge(intake, validation)
    .AddEdge(validation, risk)
    .AddEdge<RiskAssessmentMessage>(
        risk,
        approvalRequest,
        message => message!.ApprovalRequirement.IsRequired,
        "Human approval required")
    .AddEdge<RiskAssessmentMessage>(
        risk,
        decision,
        message => !message!.ApprovalRequirement.IsRequired,
        "Human approval not required")
    .AddEdge(approvalRequest, approvalPort)
    .AddEdge(approvalPort, decision)
    .WithOutputFrom(decision)
    .Build();
```

Testing only `HumanApprovalPolicy.Evaluate` would prove the predicate's input but not that the graph connects the selected branch to the correct executor. A graph-level test should assert both the external outcome and the internal route.

For a low-risk claim, the suite verifies that the run completes, no approval row exists, and the trace contains the risk and decision executors but not the approval-request executor:

```csharp
Assert.Equal(WorkflowExecutionStatus.Completed, outcome.Status);
Assert.Empty(await db.ClaimApprovals.ToListAsync());

var executors = await ExecutorNamesAsync(db, workflowId);
Assert.Contains(nameof(RiskAssessmentAgentExecutor), executors);
Assert.Contains(nameof(ClaimDecisionExecutor), executors);
Assert.DoesNotContain(nameof(HumanApprovalRequestExecutor), executors);
```

For a review-required claim, the assertions reverse the critical part of that route: the run is waiting, one pending approval is persisted, the approval executor ran, and the final decision executor did not.

These tests catch wiring errors that a policy-only unit test cannot see: reversed conditions, a missing edge, or an approval path that accidentally reaches a final decision before a reviewer responds.

## Assert the request boundary as a workflow event

`RequestPort<ClaimApprovalRequest, ClaimApprovalDecision>` crosses an unusual boundary. Its output is not the request object returned from an ordinary executor. During streaming execution, MAF publishes a `RequestInfoEvent` containing the native request data.

The suite builds the smallest real workflow that can exercise that behavior:

```csharp
var requestPort =
    RequestPort.Create<ClaimApprovalRequest, ClaimApprovalDecision>(
        "portable-approval-test");

var workflow = new WorkflowBuilder(source)
    .AddEdge(source, requestPort)
    .Build();

var streamingRun =
    await InProcessExecution.RunStreamingAsync(workflow, expected);
```

It then watches for the native request event rather than assuming an implementation-specific final output:

```csharp
await foreach (var workflowEvent in streamingRun.WatchStreamAsync())
{
    if (workflowEvent is not RequestInfoEvent requestEvent)
    {
        continue;
    }

    dynamic nativeRequest = requestEvent.Request;
    portable = (PortableValue)nativeRequest.Data;
    break;
}

var actual = MafApprovalPayload.Deserialize(portable);
Assert.Equivalent(expected, actual, strict: true);
```

This test proves three useful things together: the real `RequestPort` emits the expected event, its portable payload can cross the framework boundary, and application code can reconstruct the complete typed approval request.

The production service must also normalize that boundary. It validates the workflow correlation, derives a stable approval ID when the portable payload does not provide one, supplies a safe default reason, and ensures the expiry is not earlier than the configured approval timeout. Those normalization functions have focused tests of their own.

## Use a relational in-memory database for persistence behavior

The test fixture uses SQLite's in-memory mode:

```csharp
var options = new DbContextOptionsBuilder<ClaimsDbContext>()
    .UseSqlite("Data Source=:memory:")
    .Options;

var db = new ClaimsDbContext(options);
db.Database.OpenConnection();
db.Database.EnsureCreated();
```

Keeping the connection open preserves the database for the lifetime of the context. This lets the suite exercise EF Core mappings and SQLite query behavior without sharing state between tests.

That choice matters for assertions such as:

- a waiting run has exactly one pending approval;
- an expired approval cannot be decided and is excluded from the pending list;
- repeating the same approval decision is idempotent, while changing the outcome conflicts;
- workflow status selects the newest approval for a run;
- approved records retain the snapshot fields required for the final decision.

These are persistence rules, not model behaviors. They should remain deterministic even if the live risk agent changes its wording or scoring rationale.

## Test continuation from persisted facts

The sample deliberately does not keep an in-memory registry of suspended workflow objects. When a reviewer decides, the application validates the persisted run and approval, then starts a small workflow whose only executor is the final decision stage:

```csharp
public Workflow CreateDecisionContinuation() =>
    new WorkflowBuilder(decision)
        .WithOutputFrom(decision)
        .Build();
```

The restart-oriented test seeds an approved record and its claim and risk snapshots, constructs a fresh decision executor, and runs that continuation directly:

```csharp
var executor = new ClaimDecisionExecutor(db, TimeProvider.System);
var workflow = new ClaimReviewWorkflow(
        null!, null!, null!, null!, executor)
    .CreateDecisionContinuation();

var run = await InProcessExecution.RunAsync(
    workflow,
    decision,
    cancellationToken: CancellationToken.None);
```

The assertion reads a typed `ClaimDecisionResponse` from `WorkflowOutputEvent` and verifies its workflow ID and completed status. This demonstrates that the final decision can be reconstructed from persisted application state without a live continuation registry.

It does **not** demonstrate restoration of the original suspended `RequestPort` execution. The service disposes the streaming run after persisting the request and later starts a separate decision-only MAF workflow. Tests and documentation should name that behavior precisely: it is application-managed continuation from persisted snapshots, not native workflow checkpoint restoration.

## Test the model trust boundary separately

The production risk agent receives structured model output, but the model is not allowed to decide whether human review is required. Mapping code validates the risk level, score, factors, and recommendation, then forces `RequiresHumanReview` to `false`. The deterministic policy derives the real requirement afterward.

The suite captures that boundary directly:

```csharp
var draft = new ClaimRiskAssessmentDraft(
    "High",
    85,
    ["High amount"],
    "Review the risk factors.",
    true);

var assessment = RiskAssessmentAgent.ToAssessment(draft);

Assert.NotNull(assessment);
Assert.Equal(ClaimRiskLevel.High, assessment.RiskLevel);
Assert.False(assessment.RequiresHumanReview);
```

Separate policy tests then prove that low-risk claims skip review while high amounts and high risk scores require it. This prevents a model-provided Boolean from silently becoming authorization logic.

## Organize the test suite by boundary

A useful suite for this kind of workflow has several layers:

1. Pure rule tests cover claim validation, risk-draft mapping, and approval policy thresholds.
2. Graph execution tests use controlled agents with the real executors and assert both outcome and route.
3. Request-boundary tests run a real `RequestPort` and inspect `RequestInfoEvent` payloads.
4. Persistence tests exercise approval state, expiry, idempotency, and query behavior through SQLite.
5. Continuation tests prove the final stage can be reconstructed from persisted facts.
6. Separate integration tests should cover live Azure OpenAI configuration and behavior.

The first five layers can run without credentials or network access. The sixth should be opt-in because it has different failure modes and reproducibility guarantees.

## Know what these tests do not prove

The Chapter 1 suite gives strong deterministic coverage of the application's workflow contracts, but its limits are important:

- it does not call Azure OpenAI or assert live model responses;
- it does not validate prompts against a deployed model;
- it does not restore a native MAF checkpoint or resume the original `RequestPort` run;
- its restart test reconstructs the decision continuation in the same test process using persisted SQLite state;
- it does not exercise an ASP.NET process restart, browser UI, HTTP polling, or SignalR delivery;
- it does not test concurrent reviewers racing to decide the same approval;
- an in-process `Channel` remains the work queue, so queued and running work is not durable across host failure.

Those limitations do not weaken the deterministic suite. They define where integration, concurrency, and recovery tests must begin.

## Prove orchestration without making the model deterministic

Reliable workflow tests do not need to force a probabilistic model into unit-test behavior. They need a boundary where model-backed components can be replaced while the real orchestration stays intact.

For a conditional `RequestPort` workflow, the most valuable assertions are not only the returned status. Prove which executors ran, which did not, what request event crossed the human boundary, what state was persisted, and what facts are sufficient to complete the later decision.

That approach keeps tests fast and repeatable while still exercising the architecture that production depends on.

## Continue Exploring

This article is derived from Chapter 1, “Human-in-the-Loop Workflows,” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 1 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-01)
