# Agents need durable boundaries

Long-running workflows fail in ordinary ways: processes restart, dependencies time out, users take hours to approve a step, and duplicate messages arrive. An agent loop held only in memory cannot handle those conditions reliably.

## Persist decisions, not implementation detail

Durable state should capture the workflow's meaningful progress: the current step, accepted inputs, tool outcomes, approvals, and the next permitted transition. It should not serialize an entire runtime object graph.

- Make tool operations idempotent where possible.
- Assign stable identifiers to workflow runs and external actions.
- Checkpoint before crossing an irreversible boundary.
- Distinguish retryable failures from decisions requiring human review.

## Recovery is a feature

A durable workflow is designed to resume, not merely to run. Test recovery from each boundary the same way you test the happy path.
