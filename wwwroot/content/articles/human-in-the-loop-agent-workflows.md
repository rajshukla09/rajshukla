# Approval is a workflow state

Human-in-the-loop design works best when review is modeled as a durable state transition. The workflow pauses with enough information to explain the proposed action and resumes from a known checkpoint after a decision.

## Make the decision concrete

An approval request should include:

- The action the agent proposes.
- The evidence and assumptions behind it.
- The effect of approving or rejecting it.
- A stable workflow and decision identifier.

> A vague “approve?” prompt transfers uncertainty to the reviewer instead of controlling risk.

With Microsoft Agent Framework, keep orchestration state distinct from presentation. A web interface, message, or operational queue can collect the decision while the workflow owns the transition rules.

## Resume safely

After approval, verify that relevant conditions have not changed. Use idempotency keys for side effects and retain an audit record of both the proposal and the human decision.
