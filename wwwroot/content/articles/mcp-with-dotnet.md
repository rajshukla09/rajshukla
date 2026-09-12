# MCP creates a stable tool boundary

Model Context Protocol gives AI applications a consistent way to discover and invoke tools or retrieve contextual resources. In a .NET system, that boundary can keep model-facing integration separate from domain services.

## Keep tools narrow

A useful MCP tool has a clear purpose, a constrained input schema, and a response designed for another program to interpret. Treat tool descriptions as part of the contract.

- Validate inputs before reaching domain logic.
- Return structured, bounded results.
- Make authorization decisions outside the model.
- Instrument each invocation.

MCP standardizes connectivity; it does not replace sound service design, security, or operational controls.
