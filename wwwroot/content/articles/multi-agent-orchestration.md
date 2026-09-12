# Start with boundaries

Multiple agents are useful when a system has genuinely different responsibilities, context needs, or trust boundaries. They are not an automatic upgrade over a well-designed single agent with tools.

## Choose the coordination shape

- **Sequential handoff** when each specialist produces a clear input for the next.
- **Parallel review** when independent perspectives can be evaluated together.
- **Supervisor routing** when work must be classified and delegated dynamically.

Every handoff should carry an explicit contract: the task, relevant evidence, constraints, and expected output. Without that boundary, orchestration becomes a chain of ambiguous conversations.

Measure the system by outcome quality, latency, cost, and recoverability—not by the number of agents involved.
