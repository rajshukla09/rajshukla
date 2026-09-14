# Controlling Model-Selected MCP Tool Execution in .NET

Runtime tool discovery is useful because an AI application does not need to compile every external capability into its own code. It is also a trust boundary: an MCP server supplies names, descriptions, annotations, and schemas that can influence which operations a model attempts to call.

The wrong abstraction is:

```text
MCP server advertises a tool
    -> model may execute it
```

Discovery tells the host what a server exposes. It does not decide what the current application request may execute.

The Enterprise Knowledge Assistant in *Building AI Agents with .NET — Part 2* places deterministic controls between discovery and invocation:

```text
discover server catalog
    -> filter accepted capabilities
    -> create a request-scoped catalog
    -> expose wrappers to the model
    -> reserve an invocation
    -> validate server, catalog membership, and arguments
    -> call MCP
```

The model still decides whether it needs a tool, which permitted tool to select, and what query to send. It never becomes the enforcement point.

## Treat discovery as input, not authority

The MCP client obtains the live catalog with the official client API and initially retains only tools annotated as read-only:

```csharp
var client = await GetClientAsync(cancellationToken);
var tools = await client.ListToolsAsync(
    cancellationToken: cancellationToken);

var allowed = tools
    .Where(tool =>
        tool.ProtocolTool.Annotations?.ReadOnlyHint == true)
    .Select(tool => new McpToolDescriptor(
        _options.ServerName,
        tool.Name,
        tool.Description ?? string.Empty,
        tool.ProtocolTool.InputSchema)
    {
        ClientTool = tool,
    })
    .ToArray();
```

This is a policy filter, not authentication or authorization. `ReadOnlyHint` describes intended tool behavior. It does not prove that a server is trustworthy, that the tool has no side effects, or that the current user may read its data.

For the chapter's local demonstration server, all three tools are read-only:

- `search_github`
- `search_documentation`
- `find_customer`

In a production system, this first filter would normally also consider an allow-list of trusted servers and tools, the authenticated user, tenant, roles, data classification, and the requested operation. Capabilities rejected here should never be presented to the model.

## Freeze permission into a request-scoped catalog

Discovery returns `McpToolDescriptor` records. Each one carries the server, protocol tool name, description, discovered input schema, and the SDK's `McpClientTool` instance:

```csharp
public sealed record McpToolDescriptor(
    string Server,
    string Name,
    string Description,
    JsonElement InputSchema)
{
    [JsonIgnore]
    public McpClientTool ClientTool { get; init; } = null!;
}
```

`EnterpriseAssistantAgent.QueryAsync` keeps that accepted array for the duration of one assistant request:

```csharp
var catalog = await mcpClient.DiscoverToolsAsync(
    cancellationToken);

var ledger = new InvocationLedger(
    options.Value.MaxToolCalls,
    logger);

var modelTools = catalog
    .Select(descriptor =>
        CreateModelTool(descriptor, catalog, ledger))
    .ToArray();
```

The catalog has two jobs.

First, it is the source of the model-callable wrappers. A tool excluded from the catalog does not become a function the model can select.

Second, the same catalog is passed back into the invocation path. That creates a defense-in-depth check: even after a wrapper is selected, execution must still correspond to a capability accepted for this request.

This is stronger than comparing a model-produced string with a global tool registry. Permission is captured at the request boundary and carried to the last application-controlled step before protocol invocation.

The current sample rediscovers tools on each assistant request even though it reuses a singleton MCP connection. It does not persist or cache an authorization decision across requests.

## Adapt capabilities without bypassing policy

The MCP descriptor is not handed directly to Azure OpenAI. The application first creates a `ModelTool` delegate around it. `MafEnterpriseModelRunner` then converts only those wrappers into Microsoft Agent Framework functions:

```csharp
var functions = tools.Select(tool =>
    AIFunctionFactory.Create(
        (string query, CancellationToken ct) =>
            tool.InvokeAsync(query, ct),
        name: tool.Name,
        description: tool.Description))
    .ToArray();
```

Those functions enter `ChatClientAgentOptions.ChatOptions.Tools`, and the `AIAgent` may choose among them during `RunAsync`.

The adapter is important because the model-facing function still returns through application code. It does not invoke `EnterpriseMcpTools` directly and does not receive an unrestricted MCP client.

The wrapper's qualified name combines server and tool:

```csharp
var functionName =
    $"{descriptor.Server}__{descriptor.Name}";
```

For example, `find_customer` becomes `enterprise-knowledge__find_customer`. Qualification makes the capability's origin visible at the model boundary. The server and tool are still validated as separate values before execution; the qualified name alone is not the security control.

## Reserve capacity before crossing the protocol boundary

Model-driven tool use needs a deterministic resource bound. The sample creates one `InvocationLedger` per assistant request and reserves a sequence number before constructing and sending the MCP call:

```csharp
public int Reserve()
{
    lock (_gate)
    {
        if (_reserved >= maxToolCalls)
        {
            LimitReached = true;
            throw new McpToolCallLimitException(maxToolCalls);
        }

        return ++_reserved;
    }
}
```

The default maximum is five and configuration constrains it to the range 1 through 20. The lock makes reservation safe if calls arrive concurrently through the same request-scoped ledger.

Reserving before invocation provides a useful guarantee: two calls cannot both observe the last available slot and exceed the limit. Once reserved, a failed or cancelled call still consumes its slot. That is a conservative choice because an attempted external operation has already entered the execution path.

When the model attempts one call beyond the budget, `Reserve` throws before `IMcpClient.InvokeAsync`. The assistant catches only `McpToolCallLimitException` and returns a bounded response asking the caller to narrow the request. Other validation, transport, provider, and cancellation failures continue to propagate.

A call rejected during reservation is not included in the completed invocation records because reservation occurs before the wrapper's `try`/`finally`. Instead, `ToolCallLimitReached` reports that boundary in the final response.

## Validate again immediately before `CallAsync`

The wrapper constructs an `McpToolCall` from the descriptor captured during catalog creation:

```csharp
var arguments = new Dictionary<string, JsonElement>
{
    ["query"] = JsonSerializer.SerializeToElement(query),
};

var result = await mcpClient.InvokeAsync(
    new McpToolCall(
        descriptor.Server,
        descriptor.Name,
        arguments),
    requestCatalog,
    cancellationToken);
```

`EnterpriseMcpClient.InvokeAsync` applies three checks before calling the SDK tool.

The configured server must match:

```csharp
if (!call.Server.Equals(
        _options.ServerName,
        StringComparison.OrdinalIgnoreCase))
{
    throw new McpToolValidationException(
        $"MCP server '{call.Server}' is not connected.");
}
```

The server and tool pair must exist in the request catalog:

```csharp
var descriptor = requestCatalog.SingleOrDefault(tool =>
    tool.Server.Equals(
        call.Server,
        StringComparison.OrdinalIgnoreCase)
    && tool.Name.Equals(
        call.Tool,
        StringComparison.OrdinalIgnoreCase))
    ?? throw new McpToolValidationException(
        $"MCP tool '{call.Server}/{call.Tool}' " +
        "was not discovered for this request.");
```

Finally, arguments are checked against the schema stored in that accepted descriptor. Only then does the invocation cross the protocol boundary:

```csharp
ValidateArguments(call.Arguments, descriptor.InputSchema);

CallToolResult result = await descriptor.ClientTool.CallAsync(
    arguments,
    cancellationToken: cancellationToken);
```

This ordering is the core pattern: validate the intended operation using request-specific policy data while execution is still local and deterministic.

## Be precise about schema enforcement

The sample tools accept one required string named `query`, so the validator deliberately supports only the subset needed by those contracts.

It builds an allowed-property set from `properties`, reads `required`, and rejects three cases:

```csharp
foreach (var name in required.Where(
             name => !arguments.ContainsKey(name)))
{
    throw new McpToolValidationException(
        $"Missing required MCP argument '{name}'.");
}

foreach (var argument in arguments)
{
    if (!allowed.Contains(argument.Key))
    {
        throw new McpToolValidationException(
            $"MCP argument '{argument.Key}' is not " +
            "in the discovered input schema.");
    }

    // Reject a non-string value when the schema says string.
}
```

That validates missing required properties, undeclared properties, and string types. It is not a general JSON Schema engine. It does not enforce nested objects, arrays, enumerations, unions, numeric bounds, formats, or every possible schema keyword.

There is also an application-level gap that the schema does not close: a whitespace-only `query` is still a valid JSON string. In the demonstration tools, splitting that string produces no terms and LINQ's `All` returns true, so the tool returns every record. A production implementation should add business validation such as a non-empty search expression and result-size limits rather than assuming type validation is sufficient.

Protocol schema validation and business validation solve different problems. Both belong before sensitive results are released.

## Record execution without changing its outcome

Every successfully reserved wrapper starts a stopwatch and records the result in a `finally` block:

```csharp
try
{
    var result = await mcpClient.InvokeAsync(
        new McpToolCall(
            descriptor.Server,
            descriptor.Name,
            arguments),
        requestCatalog,
        cancellationToken);

    succeeded = result.Succeeded;
    resultText = result.Content.GetRawText();
    return resultText;
}
catch (Exception exception)
{
    error = exception.Message;
    throw;
}
finally
{
    ledger.Complete(new ToolInvocationResponse(
        sequence,
        descriptor.Server,
        descriptor.Name,
        stopwatch.ElapsedMilliseconds,
        succeeded,
        JsonSerializer.Serialize(arguments),
        resultText,
        error));
}
```

The catch records an error but rethrows it. Telemetry does not convert an MCP failure into a successful model result.

Completed records are sorted by their reserved sequence rather than completion time. That preserves application-observed invocation order if operations finish out of order.

The implementation records complete arguments and results in response telemetry and application logs. That is convenient for deterministic sample data and risky for enterprise data. Production telemetry should follow an explicit policy for redaction, truncation, retention, and caller visibility. Observability must not become an alternate data-exfiltration path.

## Test the enforcement boundary without a live model

The Chapter 2 suite separates MCP protocol tests from model behavior.

Two tests use the real stdio transport. One calls `DiscoverToolsAsync` and asserts that exactly three expected tools and their `query` schemas are discovered. Another invokes `find_customer` through `EnterpriseMcpClient` and asserts the deterministic Contoso result.

A catalog test attempts to invoke an undiscovered `delete_customer` tool:

```csharp
await Assert.ThrowsAsync<McpToolValidationException>(() =>
    client.InvokeAsync(
        Call("delete_customer", "Contoso"),
        catalog));
```

A schema test sends numeric JSON where the discovered contract requires a string and expects the same validation exception before the tool call.

For assistant-level tests, `IEnterpriseModelRunner` is replaced by a `ScriptedModel`. The scripted boundary receives the real request-scoped `ModelTool` wrappers and chooses one deterministically:

```csharp
return await tools
    .Single(tool => tool.Name.EndsWith(
        "__find_customer",
        StringComparison.Ordinal))
    .InvokeAsync("Contoso", CancellationToken.None);
```

That arrangement exercises discovery, wrapper creation, the invocation ledger, catalog validation, real MCP stdio execution, and telemetry without calling Azure OpenAI.

The budget test configures two calls and scripts three attempts. It proves that only two invocations are recorded, the limit flag is set, and the controlled fallback answer is returned.

## Understand what remains unproven

The seven tests establish meaningful deterministic boundaries, but they do not prove everything implied by a production agent:

- `MafEnterpriseModelRunner` and `AIFunctionFactory.Create` are not executed by the tests;
- Azure OpenAI and live model tool selection are not tested;
- there is no rejected non-read-only tool fixture, so read-only filtering is implemented but not directly tested against a mixed catalog;
- missing required and extra-property validation exist but lack focused tests;
- there is no authenticated user or tenant context and no per-user capability catalog;
- there is no second authorization decision inside the server-side tool;
- the validator covers only the sample's small JSON Schema subset;
- the MCP server is a local child process, not an authenticated remote service;
- there are no per-tool timeouts, retries, circuit breakers, response-size limits, or production enterprise integrations;
- the connection and catalog are process-local, with no persistence or restart recovery.

Tool metadata and returned content also remain untrusted inputs. A remote server can influence the model through descriptions, while retrieved documents can contain instruction-like text. Keeping tools read-only does not eliminate prompt injection or unauthorized disclosure.

## Keep the model inside an application-owned envelope

Model-selected execution does not require model-controlled authority.

Let MCP describe capabilities. Let Microsoft Agent Framework expose the accepted wrappers. Let the model reason about which permitted tool may help. Then require every call to return through deterministic application code that owns request-specific permission, validation, budgets, cancellation, and telemetry.

The implementation is intentionally small, but its ordering is the durable lesson:

```text
discover -> filter -> scope -> wrap -> reserve -> validate -> invoke
```

Each step narrows what can happen before the operation crosses into another process. That is the foundation for adding real identity, authorization, richer schema enforcement, and production reliability without asking the model to police itself.

## Continue Exploring

This article is derived from Chapter 2, “Enterprise Knowledge Assistant with MCP,” in *Building AI Agents with .NET — Part 2*.

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Chapter 2 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2/tree/main/chapter-02)
