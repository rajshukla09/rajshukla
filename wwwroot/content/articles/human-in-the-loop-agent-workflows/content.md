# Human-in-the-Loop Workflows with Microsoft Agent Framework

Human review is often presented as a button: show an AI-generated recommendation, ask someone to approve it, and continue. That view misses the architectural problem. The decision may arrive minutes or hours after the automated work. The process may restart in between. The request must remain correlated with the exact evidence that was reviewed, and rejection must be represented as a valid business outcome rather than a software failure.

A production design therefore treats human review as a workflow boundary with explicit routing, state, persistence, and continuation rules. The user interface is only one way to collect the decision.

This article follows a claims-review implementation built with Microsoft Agent Framework (MAF), ASP.NET Core, EF Core, SQLite, and two Azure OpenAI-backed `AIAgent`s. The important pattern is the separation of authority:

- AI normalizes the submitted claim and assesses risk.
- Deterministic C# validates the claim and decides whether review is mandatory.
- A human approves or rejects only when policy routes the claim to review.
- The workflow coordinates both paths and produces one typed result.

## Review belongs in the workflow

An approval dialog can collect an answer, but it cannot define what the system does while the answer is unavailable. That responsibility belongs to the workflow and its surrounding application services.

The claims flow has two possible paths:

```text
Submission -> Intake Agent -> Validation -> Risk Agent -> Approval Policy
                                                        |            |
                                              no review |            | review
                                                        v            v
                                                Final Decision   RequestPort
                                                                     |
                                                              human decision
                                                                     |
                                                                     v
                                                              Final Decision
```

This topology makes several rules visible. A low-risk claim must not accidentally enter the approval path. A claim marked for review must not bypass it. If review is required, waiting is a normal workflow state—not an exception and not a blocked HTTP request.

The approval request also needs more than a yes/no prompt. In this implementation it carries the workflow and approval identifiers, the validated claim, the risk assessment, the deterministic reason and triggered rules, and the request and expiry times. Those values give the reviewer context and give the application enough state to validate and audit the eventual response.

## AI assesses; deterministic policy routes

The Risk Agent returns structured output as `ClaimRiskAssessmentDraft`. Its instructions explicitly say not to approve or reject the claim and not to decide whether human review is required. More importantly, the application enforces that boundary after receiving the model output.

```csharp
internal static ClaimRiskAssessment? ToAssessment(ClaimRiskAssessmentDraft draft)
{
    if (!Enum.TryParse(draft.RiskLevel, true, out ClaimRiskLevel level)
        || draft.RiskScore is not (>= 0 and <= 100)
        || draft.RiskFactors is null
        || draft.RiskFactors.Count == 0
        || draft.RiskFactors.Any(string.IsNullOrWhiteSpace)
        || string.IsNullOrWhiteSpace(draft.Recommendation))
    {
        return null;
    }

    return new ClaimRiskAssessment(
        level,
        draft.RiskScore.Value,
        draft.RiskFactors,
        draft.Recommendation,
        false);
}
```

Even though the model-facing draft contains `RequiresHumanReview`, the mapping does not trust it. The application validates the risk level, score, factors, and recommendation, then sets the mapped review flag to `false`. A test supplies a draft in which the model asks for review and verifies that the mapped assessment still cannot acquire routing authority.

The separate `HumanApprovalPolicy` owns that authority. It evaluates the validated claim, the AI-generated assessment, and deterministic validation warnings against configured rules:

```csharp
public HumanApprovalRequirement Evaluate(
    ValidatedClaim c,
    ClaimRiskAssessment r,
    IReadOnlyList<string>? warnings = null)
{
    var o = opt.Value;
    if (!o.Enabled)
        return new(false, "Human approval is disabled.", []);

    var rules = new List<string>();
    if (c.ClaimedAmount > o.AmountThreshold)
        rules.Add($"Claimed amount exceeds {o.AmountThreshold:C0}.");
    if (r.RiskLevel == ClaimRiskLevel.High)
        rules.Add("Risk level is High.");
    if (r.RiskScore >= o.HighRiskScoreThreshold)
        rules.Add($"Risk score is {r.RiskScore}, at or above {o.HighRiskScoreThreshold}.");
    if (warnings?.Count > 0)
        rules.Add("Validation produced warnings requiring reviewer awareness.");

    if (rules.Count == 0)
        return new(false, "Low-risk claim can proceed without human approval.", []);

    return new(true, "Deterministic approval policy requires human review.", rules);
}
```

The sample configuration uses a claim-amount threshold of 50,000, a high-risk score threshold of 70, and a 30-minute approval window. High risk and any validation warning also trigger review. Because these are ordinary C# rules, identical inputs and configuration produce the same routing decision.

Keeping policy outside the graph is deliberate. `HumanApprovalPolicy` decides *why* review is required; the graph decides *where* the resulting message goes. Thresholds are not duplicated inside edge predicates, and changing an Agent prompt cannot silently change the authorization boundary.

## Structuring the MAF workflow

Each stage is a typed MAF `Executor`. The Intake Executor calls the normalization Agent, the Validation Executor applies deterministic claim rules, and the Risk Executor calls the risk Agent before attaching the result of `HumanApprovalPolicy` to `RiskAssessmentMessage`.

`ClaimReviewWorkflow.Create` builds the graph:

```csharp
public Workflow Create()
{
    var approvalPort = RequestPort.Create<ClaimApprovalRequest, ClaimApprovalDecision>(
        "claim-human-approval");

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
}
```

The two conditional edges are mutually exclusive and use the policy result already carried by the message. Both paths converge on `ClaimDecisionExecutor`, so callers receive the same `ClaimDecisionResponse` contract whether processing completes automatically or after review.

There are defensive checks at both destinations. `HumanApprovalRequestExecutor` throws if a message not requiring review enters the approval path. `ClaimDecisionExecutor` refuses to automatically process a `RiskAssessmentMessage` whose policy result requires review. The graph expresses the intended topology; the Executors enforce its trust boundaries.

## The no-review-required path

When `ApprovalRequirement.IsRequired` is `false`, MAF routes the risk message directly to `ClaimDecisionExecutor`. The approval request Executor and `RequestPort` are never invoked.

```csharp
if (message is RiskAssessmentMessage riskMessage)
{
    if (riskMessage.ApprovalRequirement.IsRequired)
    {
        throw new InvalidOperationException(
            "A claim requiring human review cannot bypass the approval path.");
    }

    var decidedAt = time.GetUtcNow();
    var automaticDecision = new ClaimDecisionDto(
        "Approved",
        riskMessage.Claim.ClaimedAmount,
        riskMessage.ApprovalRequirement.Reason,
        null,
        decidedAt);
    var automaticSummary = new WorkflowSummaryDto(
        riskMessage.WorkflowRunId,
        "Completed",
        null,
        null,
        null,
        null,
        null);

    return new ClaimDecisionResponse(
        riskMessage.WorkflowRunId,
        "Completed",
        automaticDecision,
        automaticSummary);
}
```

The implementation automatically approves the validated amount and records the policy reason. It does not create a pending approval record. The workflow host receives the output event, persists the final decision, records `WorkflowCompleted`, and marks the run `Completed` in the same execution period.

The workflow test for this branch verifies more than the result: it asserts that `RiskAssessmentAgentExecutor` and `ClaimDecisionExecutor` ran, `HumanApprovalRequestExecutor` did not run, and the approval table remained empty.

Automatic approval is a sample business rule, not a universal recommendation. A real claims system might route the no-review branch to additional deterministic adjudication rather than directly authorizing payment. The architectural point is that this behavior is explicit and testable.

## The human-review-required path

When policy requires review, the graph enters `HumanApprovalRequestExecutor`. That Executor constructs the typed request and assigns its review window:

```csharp
var requirement = message.ApprovalRequirement;
if (!requirement.IsRequired)
{
    throw new InvalidOperationException(
        "A claim that does not require human review cannot enter the approval path.");
}

var now = time.GetUtcNow();
return ValueTask.FromResult(new ClaimApprovalRequest(
    Guid.NewGuid(),
    message.WorkflowRunId,
    message.Claim,
    message.RiskAssessment,
    requirement.Reason,
    requirement.TriggeredRules,
    now,
    now.AddMinutes(options.Value.ApprovalTimeoutMinutes)));
```

The request flows into `RequestPort<ClaimApprovalRequest, ClaimApprovalDecision>`. When the port is reached, streaming MAF execution emits a `RequestInfoEvent`. The host watches the event stream, extracts the typed request—including handling MAF's portable payload representation—and persists a pending approval with the native request ID, claim and risk snapshots, rule information, and timestamps. It then changes the logical run to `WaitingForApproval`.

At that point the host returns a waiting outcome and disposes the streaming run. No controller request, thread, `DbContext`, dependency-injection scope, callback, or live workflow object is retained while the reviewer decides. SQLite contains the application-visible workflow state; the in-memory MAF execution no longer exists.

This distinction is essential: the sample uses a native MAF `RequestPort` to model and emit the human boundary, but it does not later send the delayed answer back into that released port execution.

## How approval or rejection continues processing

The approval API loads the pending record and creates a typed decision using both correlation identifiers:

```csharp
var decision = new ClaimApprovalDecision(
    id,
    approval.WorkflowRunId,
    outcome,
    request.Reviewer,
    request.Comment,
    DateTimeOffset.UtcNow);

var result = await approvalStore.DecideAsync(decision, cancellationToken);
```

The store rejects missing and expired approvals. It accepts one effective decision, treats an identical repeat as already decided, and reports a conflict when a later decision contradicts the stored outcome or reviewer. On acceptance, the controller persists first and then enqueues a `NativeApprovalResponseCommand`; the HTTP request does not execute the final workflow stage itself.

The background worker consumes that command in a fresh dependency-injection scope. `ResumeAsync` reloads the run and approval, checks both IDs, verifies that the run is still `WaitingForApproval`, and confirms that the stored approval status matches the queued outcome.

Because the original streaming run has been released, continuation uses a small second MAF graph:

```csharp
public Workflow CreateDecisionContinuation() =>
    new WorkflowBuilder(decision)
        .WithOutputFrom(decision)
        .Build();
```

Only `ClaimDecisionExecutor` runs in this second execution period. Intake, validation, risk assessment, and approval policy are not repeated. The Executor reloads the validated claim snapshot associated with the approval and converts the human response into the final business result.

An approval produces `Completed`, preserves the reviewer and decision time, and sets the approved amount to the claim amount. A rejection produces `Rejected` with an approved amount of zero. Rejection is not `Failed`: the workflow operated correctly and reached a valid negative business decision.

The two execution periods share one logical `WorkflowRunId`, but this is application-managed continuation—not native checkpoint restoration. The implementation does not serialize the original `StreamingRun`, restore its exact execution position, or deliver the delayed answer through `SendResponseAsync`.

## Persistence, recovery, and observation

Claim submission also follows a persist-before-enqueue pattern. The API creates a `Queued` workflow-run record, saves it, and then writes a typed start item to an in-memory `Channel<ClaimWorkflowQueueItem>`. A `BackgroundService` consumes start and resume items serially, creating a new scope for each operation.

SQLite stores workflow runs, approvals, claim and risk snapshots, final decisions, ordered workflow events, timing data, errors, and Executor traces. The Blazor client reconstructs current state and history by polling HTTP endpoints. A SignalR endpoint exists in the sample, but the client does not use it and the server does not publish workflow events through it.

At startup the worker searches for approvals already marked `Approved` or `Rejected` whose runs still say `WaitingForApproval`. It reconstructs and requeues those continuations. This closes one useful crash window: the human decision was persisted, but its process-local resume message was lost before completion.

That recovery is intentionally narrow. It does not restore the original MAF run, recover a lost queued start, or generally restart an arbitrary `Running` workflow.

## Production considerations and limitations

The sample establishes the workflow and trust boundaries, but several aspects need stronger infrastructure before production use:

- **Durable dispatch:** the unbounded, single-reader `Channel<T>` is process-local. Database writes and queue writes are not atomic. Use a transactional outbox and durable broker or a durable workflow runtime, with retry and lease semantics appropriate to the deployment.
- **Native durability:** application snapshots support the decision-only continuation, but they are not MAF workflow checkpoints. If the requirement is to resume the same orchestration instance after host failure, use a supported durable workflow hosting model and test restart behavior directly.
- **Identity and authorization:** reviewer names arrive in the request body in this sample. Production code must authenticate reviewers, authorize the specific action, derive identity from trusted claims, and retain a tamper-resistant audit record.
- **Decision concurrency:** idempotency checks are present, but multi-instance concurrency needs database-enforced optimistic concurrency or conditional updates. Business side effects need their own idempotency keys; workflow replay alone cannot make an external write exactly once.
- **Snapshot integrity:** the reviewed claim, risk assessment, policy version, configuration, prompt/model provenance, and triggered rules should be immutable or versioned. If source data can change while review is pending, define whether the decision applies to the snapshot or requires revalidation.
- **Expiry and cancellation:** the store checks expiry when a decision arrives, and a hosted service periodically marks old approvals expired. The sample's cancellation updates persisted state but does not reliably interrupt active MAF or Azure OpenAI work, and terminal-state transition guards need strengthening.
- **Failure recovery:** startup recovery covers decided approvals still waiting, not lost start messages or all interrupted running states. Continuation failure persistence and retry behavior also require hardening.
- **Scale and backpressure:** one reader serializes every start and resume, while the queue is unbounded. Production hosting needs bounded capacity, concurrency limits, provider-rate-limit handling, and isolation so a slow Agent call cannot indefinitely delay approvals.
- **Data minimization:** an approval view contains claim and risk snapshots. Protect sensitive data in storage and transit, restrict what reviewers see, define retention, and avoid leaking payloads through logs or telemetry.
- **Verification scope:** the tests use controlled Agent implementations and in-memory SQLite. They prove deterministic policy, both MAF branches, `RequestPort` event/payload handling, approval-store behavior, and decision-only continuation. They do not prove live Azure OpenAI behavior, browser rendering, hosted-service restart recovery, or durable delivery.

The core design remains useful beyond claims processing: let AI produce bounded assessments, let deterministic policy decide when human authority is required, represent that boundary explicitly in the graph, persist before releasing execution, and treat the human response as typed workflow input with clear business semantics.

## Continue Exploring

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Companion source for Building AI Agents with .NET — Part 2](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2)
