# Designing Typed Message Boundaries for AI Agent Workflows in .NET

Sequential code can coordinate a sophisticated AI request and still hide the architecture.

A service might classify a request, validate the proposed plan, repair it, execute deterministic tools, and finally ask an agent to produce structured output. When all of those calls live inside one method, the order exists only as control flow. Stage inputs, outputs, failure rules, and ownership boundaries are difficult to inspect independently.

Chapter 10 of the Smart Travel Planner turns that implicit sequence into a Microsoft Agent Framework workflow:

```text
TravelWorkflowRequest
  -> ExecutionPlanMessage
  -> ValidatedPlanMessage
  -> ToolExecutionMessage
  -> TripPlanResponse
```

The strongest design choice is not simply using `WorkflowBuilder`. It is making each transition a typed, immutable contract that carries accumulated facts forward without relying on a shared mutable workflow object.

## Use a workflow when the stages are known

The travel-planning request already has a fixed sequence:

```text
classify -> validate or repair -> execute tools -> generate itinerary
```

No agent needs to choose the next specialist. There are no conditional branches, approval waits, parallel paths, or dynamic replanning decisions in this chapter. A linear workflow represents the known topology directly:

```csharp
public Workflow Create() =>
    new WorkflowBuilder(planning)
        .AddEdge(planning, validation)
        .AddEdge(validation, tools)
        .AddEdge(tools, travelAgent)
        .WithOutputFrom(travelAgent)
        .Build();
```

This is a better fit than manager-driven orchestration for a process whose stages and ordering are already application rules. The framework routes executor output along declared edges; executors do not call one another.

That last constraint matters. If `ExecutionPlanExecutor` invoked validation directly, the graph would be documentation layered over the same hidden coordinator rather than the owner of sequencing.

## Make state evolution visible in message types

The initial message carries the correlation key, validated API request, and original natural-language request:

```csharp
public sealed record TravelWorkflowRequest(
    Guid WorkflowRunId,
    TravelPlanRequest Request,
    string OriginalUserRequest);
```

The planning stage adds a candidate execution plan:

```csharp
public sealed record ExecutionPlanMessage(
    Guid WorkflowRunId,
    TravelPlanRequest Request,
    string OriginalUserRequest,
    ExecutionPlan ExecutionPlan);
```

Validation produces a new message rather than mutating the candidate in place:

```csharp
public sealed record ValidatedPlanMessage(
    Guid WorkflowRunId,
    TravelPlanRequest Request,
    string OriginalUserRequest,
    ExecutionPlan ExecutionPlan,
    ExecutionPlanValidationResult InitialValidation,
    ExecutionPlanValidationResult FinalValidation,
    bool Repaired);
```

Finally, deterministic execution adds ordered tool results:

```csharp
public sealed record ToolExecutionMessage(
    Guid WorkflowRunId,
    TravelPlanRequest Request,
    string OriginalUserRequest,
    ExecutionPlan ExecutionPlan,
    ExecutionPlanValidationResult Validation,
    bool Repaired,
    IReadOnlyList<ToolStepResult> ToolResults);
```

Each transition makes three categories explicit:

- facts carried forward unchanged;
- facts added by the current stage;
- facts replaced after validation or repair.

Downstream executors do not reload the request, plan, or validation result from a global workflow service. The message contains what the next stage needs.

## Prefer immutable envelopes over a workflow state bag

A tempting alternative is one mutable object:

```text
WorkflowState
  Request
  Plan
  Validation
  ToolResults
  TripPlan
  CurrentStage
  Failure
```

Every executor could read and write that object. It would also become difficult to determine which fields are valid at each stage, which component owns a mutation, and whether two runs accidentally share state.

Typed records make illegal timing more visible. `ExecutionPlanExecutor` cannot receive tool results because they do not exist in its input type. `ToolExecutionExecutor` receives a plan that has passed the validation stage. `TravelAgentExecutor` receives completed tool results rather than instructions to execute tools.

The messages still contain references such as `ExecutionPlan` and `IReadOnlyList<ToolStepResult>`; records do not recursively freeze mutable objects. The implementation treats them as immutable contracts by convention. Production code that exposes mutable dictionaries inside messages must preserve that discipline or introduce deeper immutable collections.

## Give each executor one business responsibility

The four concrete executors have deliberately narrow jobs.

`ExecutionPlanExecutor` asks the existing provider for a candidate plan and returns it with the original request facts:

```csharp
ExecutionPlan plan = await provider.CreateAsync(
    message.OriginalUserRequest,
    cancellationToken);

return new ExecutionPlanMessage(
    message.WorkflowRunId,
    message.Request,
    message.OriginalUserRequest,
    plan);
```

`ExecutionPlanValidationExecutor` performs deterministic validation, invokes the repair agent at most once when needed, validates the repaired plan, and rejects a still-invalid result.

`ToolExecutionExecutor` iterates validated steps in order, routes each one, and uses the Chapter 9 reliability pipeline. It records an expected tool failure as data and continues with independent later steps.

`TravelAgentExecutor` gives the original request and completed results to `ITravelAgent`. It does not plan tool calls or execute them again.

This separation keeps agents inside appropriate stages. The classifier and repair agent participate in plan creation and repair. The travel agent performs final language reasoning. The workflow owns sequencing.

## Put common lifecycle behavior in an executor base class

Every stage needs consistent timing, logging, tracing, and live-event behavior. The implementation centralizes that around the typed executor contract:

```csharp
public abstract class WorkflowExecutor<TInput, TOutput>(
    string name,
    IWorkflowTraceRecorder traceRecorder,
    IWorkflowLiveEventPublisher liveEvents,
    TimeProvider timeProvider,
    ILogger logger)
    : Executor<TInput, TOutput>(name)
{
    public override sealed async ValueTask<TOutput> HandleAsync(
        TInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        // Record start, execute the stage, record completion or failure.
    }

    protected abstract ValueTask<TOutput> ExecuteAsync(
        TInput message,
        CancellationToken cancellationToken);
}
```

Concrete executors implement `ExecuteAsync`; they do not repeat the common lifecycle wrapper. The sealed `HandleAsync` method publishes executor-started, executor-completed, message-produced, or executor-failed events and records an `ExecutorExecutionTrace`.

This is a useful boundary only while the base class remains concerned with executor mechanics. Business decisions such as whether to repair a plan or continue after a tool failure still belong to concrete stages.

The base implementation obtains `WorkflowRunId` through reflection on the input message. All current input records expose that property, but the compiler does not enforce the requirement. A shared message interface would turn this runtime convention into a compile-time constraint.

## Treat validation as a stage, not a helper detail

Classifier output is typed, but it is still model output. The validation executor does not let a candidate plan proceed directly to tools.

Its state transition is explicit:

```text
candidate plan
  -> deterministic validation
  -> optional single repair attempt
  -> deterministic revalidation
  -> validated plan message or workflow failure
```

The output retains both initial and final validation results plus a `Repaired` flag. That gives downstream diagnostics a faithful account of how the plan became executable.

One repair attempt is an application rule. Unlimited model repair would make latency, cost, and failure behavior unpredictable. If the repaired plan remains invalid, the executor throws `RequestClassificationException`, and no tool stage runs.

Validation and repair are therefore separate from classification even though both concern the execution plan. The classifier proposes; application code decides whether the proposal is safe to execute.

## Represent expected tool failures as message data

The tool stage processes each validated step sequentially:

```csharp
foreach (ExecutionStep step in
    message.ExecutionPlan.Steps.OrderBy(step => step.Order))
{
    cancellationToken.ThrowIfCancellationRequested();
    ToolRouteDecision route = router.Route(step);

    try
    {
        object? output =
            await pipeline.ExecuteAsync(
                route,
                cancellationToken);

        results.Add(new ToolStepResult(
            step.Order,
            step.Tool,
            "Success",
            output,
            null));
    }
    catch (ToolExecutionFailedException exception)
    {
        results.Add(new ToolStepResult(
            step.Order,
            step.Tool,
            "Failure",
            null,
            exception.Message));
    }
}
```

An expected terminal tool failure becomes one failed `ToolStepResult`. Later independent steps continue, and the final agent can acknowledge missing data without inventing it.

Caller cancellation is different. It is checked before each step and is not converted into a normal failed result, so the workflow stops rather than starting more work after the caller has cancelled.

Unexpected router or executor defects also escape. Catching only the known tool failure type prevents programming errors from being mislabeled as an ordinary partial result.

## Remove tool orchestration from the final agent

Chapter 9 allowed both mandatory and model-selected tool calls. Chapter 10’s workflow-facing `TravelAgent` is configured without tools:

```csharp
_agent = new AzureOpenAIClient(
        new Uri(settings.Endpoint),
        new AzureKeyCredential(settings.ApiKey))
    .GetChatClient(settings.DeploymentName)
    .AsAIAgent(new ChatClientAgentOptions
    {
        Name = nameof(TravelAgent),
        ChatOptions = new ChatOptions
        {
            Instructions = TravelAgentInstructions.SystemPrompt
        },
        AIContextProviders =
        [
            memoryContextProvider,
            runtimeContextProvider
        ]
    });
```

`TravelAgentExecutor` constructs an application-level request from the accumulated workflow message:

```csharp
TravelAgentRequest request = new(
    message.OriginalUserRequest,
    message.Request,
    message.ToolResults,
    RuntimeContext: null,
    TravelerContext: null);

AgentResult<TripPlan> result =
    await travelAgent.ExecuteAsync(
        request,
        cancellationToken);
```

The agent receives completed results. It does not decide the stage order, repeat mandatory work, or call the tools itself. Structured-output reading, deterministic `TripPlan` validation, and at most one regeneration attempt remain inside the agent boundary.

An `AgentResult<TripPlan>` communicates expected structured-output or validation failure as a typed application result. `TravelAgentExecutor` decides that a failed final agent result stops the workflow by throwing `AgentExecutionException`.

## Run the graph through one application service

Controllers depend on `ITravelWorkflowService`, not on all four executors. The service creates the input message, builds a fresh workflow, and invokes the MAF in-process runner:

```csharp
TravelWorkflowRequest input = new(
    runId,
    request,
    originalRequest);

Workflow workflow = workflowFactory.Create();

Run run = await InProcessExecution.RunAsync(
    workflow,
    input,
    cancellationToken: cancellationToken);
```

The final response is obtained from a `WorkflowOutputEvent` whose data is a `TripPlanResponse`. Completion without that typed output is treated as an application error.

The same service surrounds the MAF run with workflow-level tracing, diagnostic persistence, and safe live events. Those facilities observe the graph; they do not replace its message contracts.

## Correlate every stage without creating shared state

Every workflow message carries `WorkflowRunId`. The identifier connects executor traces, tool traces, agent execution metadata, persisted live events, and the run envelope.

Carrying the ID through messages avoids requiring a mutable global “current workflow” field. It also makes a transition independently attributable when diagnostics are persisted.

The sample stores workflow diagnostics in SQLite and streams safe progress events through SignalR. This persistence supports inspection and replay of diagnostics. It does not make `InProcessExecution` durable, resume a partially completed graph after process loss, or provide workflow checkpoints.

That distinction keeps this article separate from durable orchestration. Diagnostic persistence answers “what was observed?” Durable execution must also answer “where can this computation safely resume?”

## Define failure behavior at stage boundaries

The workflow has more than one kind of failure:

- invalid classifier output may receive one repair attempt;
- a still-invalid plan stops before tool execution;
- an expected tool timeout or permanent failure becomes a failed step and later steps continue;
- a final agent failure stops the workflow;
- caller cancellation stops downstream executors;
- unexpected infrastructure exceptions escape and mark the run failed.

The executor base records the stage as completed, failed, or cancelled and then rethrows failures. `TravelWorkflowService` marks the overall run accordingly and attempts to persist partial diagnostics without allowing a diagnostic write failure to replace the original workflow error.

This is more precise than one catch-all policy. Whether failure is data or control flow depends on which boundary owns the recovery decision.

## Test contracts separately from the live model

The Chapter 10 suite contains focused coverage for the pieces reused by the workflow:

- classifier and validator tests cover no-tool, single-tool, multi-tool, invalid, and repaired plans;
- router and pipeline tests cover deterministic dispatch, retries, timeout, and tool failure behavior;
- structured-output and `AgentResult<T>` tests cover safe agent success/failure envelopes;
- `WorkflowMessageTests` confirms a `ToolExecutionMessage` carries its accumulated context;
- workflow trace tests preserve completed executor order and failure details;
- live-event tests verify increasing persisted sequence numbers and replay after a given sequence;
- workflow-run store tests exercise diagnostic persistence.

The current suite does not execute the four-node `TravelPlanningWorkflow` through `InProcessExecution` with fake stage dependencies. It also does not directly prove that MAF rejects an incompatible edge type, that cancellation prevents every downstream executor, or that each typed transition preserves every field.

Those are valuable next tests. A true workflow composition test should run the graph without Azure OpenAI, capture the executor sequence, and assert the final typed output. Live model behavior belongs in a separate integration suite.

## Understand the implementation limits

The Chapter 10 workflow is deliberately constrained:

- execution is linear and in process;
- there are no conditional edges, parallel branches, fan-out/fan-in, request ports, approvals, or dynamic routing;
- no workflow checkpoint or resume mechanism survives process loss;
- SQLite stores diagnostics, not an executable continuation;
- the background live-run queue is process-local;
- executor correlation relies on a reflected `WorkflowRunId` property rather than a generic interface constraint;
- `ExecutionStep.Arguments` and `ToolStepResult.Output` use `object`, reducing compile-time guarantees within otherwise typed envelopes;
- tool results are serialized into the final model prompt rather than transported as a model-native structured context object;
- safe live-event enums include some granular events that the current path does not emit;
- diagnostics failures are intentionally isolated, so observability storage is best effort rather than transactional with workflow execution.

These are not reasons to abandon typed workflows. They identify exactly where the next design decisions belong.

## Let messages document the architecture

The full orchestration can be read from its type transitions:

```text
TravelWorkflowRequest
  -> candidate ExecutionPlanMessage
  -> validated ValidatedPlanMessage
  -> executed ToolExecutionMessage
  -> validated TripPlanResponse
```

Each executor adds one meaningful fact, preserves correlation, and hands a new contract to the next stage. The workflow graph owns order; application code owns validation and recovery policy; agents remain bounded reasoning components.

That is the practical value of typed message boundaries: the orchestration becomes inspectable as data flow instead of remaining hidden in a long chain of service calls.

## Continue Exploring

This article is derived from Chapter 10, “Agent Workflows,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 10 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-10)
