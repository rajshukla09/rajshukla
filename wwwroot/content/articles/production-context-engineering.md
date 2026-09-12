# Context is part of the system

Production context engineering is the discipline of deciding what an AI system should know, when it should know it, and how that knowledge is verified. It reaches beyond prompt wording into retrieval, tool results, conversation state, policies, and runtime constraints.

## Design context as a pipeline

A useful context pipeline makes each stage explicit:

- Select information relevant to the current decision.
- Transform it into a compact, model-readable representation.
- Preserve provenance and freshness.
- Measure whether it improved the result.

> More context is not automatically better context. Every token should have a reason to be present.

In production, context should be observable and testable. Capture which sources were selected, which were discarded, and the version of any instructions applied. That evidence turns an opaque prompt into an engineering surface.

## A practical boundary

Keep stable behavioral instructions separate from request-specific evidence. This makes both easier to version, inspect, and evaluate—and reduces the chance that retrieved text quietly changes system behavior.
