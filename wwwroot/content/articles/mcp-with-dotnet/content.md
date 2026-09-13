# MCP with .NET

An AI application can call a local C# function directly, but that creates a tight coupling: the application must know the function at build time, share its implementation dependencies, and define its own discovery and invocation conventions. Those assumptions become awkward when capabilities live in another process, change independently, or need to be shared by several AI applications.

Model Context Protocol (MCP) provides a standard boundary for advertising and invoking capabilities. An MCP client can ask a server what tools it exposes, inspect their descriptions and input schemas, and call a selected tool through the protocol. The application still decides which server to trust, which discovered tools to permit, and how calls are authorized and bounded.

This article follows a working .NET enterprise-assistant sample. Its MCP protocol and stdio transport are real. Its GitHub, documentation, and customer records are deterministic in-memory data, not production integrations. That separation makes the protocol path easy to inspect without pretending the sample solves enterprise connectivity.

## The boundary MCP creates

The implementation contains four distinct roles:

- The **host** is the ASP.NET Core enterprise-assistant application. It owns the user request, the Microsoft Agent Framework `AIAgent`, and application policy.
- The **MCP client** is `EnterpriseMcpClient`. It starts and connects to the configured server, discovers tools, validates calls, and invokes them.
- The **MCP server** is another process running the same assembly in `--mcp-server` mode over standard input and output.
- The **tools** are methods on `EnterpriseMcpTools`, published by the server through MCP.

The concrete path is:

```text
User -> ASP.NET Core host -> AIAgent -> model-callable function
                                      -> EnterpriseMcpClient
                                      -> MCP over stdio
                                      -> Enterprise MCP server
                                      -> EnterpriseMcpTools method
                                      -> result back to the model
                                      -> final answer
```

The Agent does not call `EnterpriseMcpTools.FindCustomer` directly. It sees model-callable functions created from tools that the application discovered and accepted. The MCP client and server own the protocol exchange; the host remains the policy boundary around it.

## Running one assembly in two modes

The normal process starts the ASP.NET Core API. When the MCP client launches the assembly with `--mcp-server`, `Program.cs` takes a separate branch:

```csharp
if (args.Contains("--mcp-server", StringComparer.Ordinal))
{
    var mcpHost = Host.CreateApplicationBuilder(args);
    mcpHost.Logging.ClearProviders();
    mcpHost.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<EnterpriseMcpTools>();
    await mcpHost.Build().RunAsync();
    return;
}
```

`AddMcpServer` registers the server infrastructure, `WithStdioServerTransport` selects the process transport, and `WithTools<EnterpriseMcpTools>` publishes the annotated tool type. Clearing logging providers is important for a stdio server: arbitrary output on the protocol stream can corrupt communication.

This is a deployment choice, not an MCP requirement. The sample uses one assembly and a child process to keep setup small. A different system could host its server elsewhere and use a transport supported by its MCP stack.

## Exposing C# methods as MCP tools

The server publishes one tool type with three capabilities:

- `search_github`
- `search_documentation`
- `find_customer`

The customer lookup shows the complete shape:

```csharp
[McpServerTool(Name = "find_customer", ReadOnly = true, Idempotent = true, OpenWorld = false)]
[Description("Find CRM customer records by customer name or account details.")]
public static string FindCustomer(
    [Description("Customer name or account text to find.")] string query) =>
    JsonSerializer.Serialize(
        Customers.Where(item => HasAllTerms(item.ToString()!, query)));
```

`McpServerTool` marks the method as an MCP tool and gives it the protocol name `find_customer`. Its annotations say that the operation is read-only, idempotent, and does not interact with an open-ended external world. The type and parameter descriptions become discovery metadata that a client—and eventually the model—can inspect.

The method accepts one required string argument named `query` and returns serialized JSON. The SDK derives the tool's input schema from that signature. The other two methods use the same contract over different fixed datasets.

The annotations describe the tool; they do not authorize a caller. A `ReadOnly` hint tells the host something useful about behavior, but it does not prove that the current user may view the returned customer or source-control data.

## Connecting over stdio

`EnterpriseMcpClient` is registered as a singleton, so the API reuses one client connection and child process. Connection creation is protected by a `SemaphoreSlim`. On first use it locates the current assembly and configures `StdioClientTransport`:

```csharp
var assemblyPath = typeof(global::Program).Assembly.Location;
var transport = new StdioClientTransport(
    new StdioClientTransportOptions
    {
        Name = _options.ServerName,
        Command = "dotnet",
        Arguments = [assemblyPath, "--mcp-server"],
        WorkingDirectory = Path.GetDirectoryName(assemblyPath),
        InheritEnvironmentVariables = false,
        EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
        StandardErrorLines = line => _logger.LogDebug("MCP server: {Line}", line),
    },
    _loggerFactory);

_client = await McpClient.CreateAsync(
    transport,
    loggerFactory: _loggerFactory,
    cancellationToken: cancellationToken);
```

`McpClient.CreateAsync` establishes the protocol connection using that transport. The child process receives `--mcp-server`, enters the server branch shown earlier, and communicates with its parent over stdio.

The environment is not inherited wholesale. The transport supplies only its default environment variables. That reduces accidental credential leakage into the child process, although it is not a complete sandbox or security boundary.

## Discovering a permitted tool catalog

Each assistant request begins with discovery:

```csharp
var client = await GetClientAsync(cancellationToken);
var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
var allowed = tools
    .Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true)
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

`ListToolsAsync` obtains the live server catalog. The host does not automatically expose everything returned by the server: this sample retains only tools whose annotations advertise `ReadOnlyHint == true`.

Each accepted result becomes an `McpToolDescriptor` containing the configured server, tool name, description, discovered JSON input schema, and the SDK's `McpClientTool` used for invocation. The array is also the request catalog against which later calls are checked.

Discovery and permission are different operations:

```text
Server advertises tools -> client discovers them -> host filters them
-> accepted request catalog -> model may select from that catalog
```

In a production application the filter is likely to include authenticated user, tenant, data classification, explicit server allow-lists, and per-tool policy. Asking the model not to use an unauthorized tool is not an authorization control; the tool should never enter that request's callable catalog.

## Adapting discovered MCP tools to a MAF Agent

MCP discovery returns protocol tools, while Microsoft Agent Framework expects functions that the model can call. The sample introduces a request-scoped `ModelTool` delegate around each accepted descriptor. `MafEnterpriseModelRunner` converts those delegates with `AIFunctionFactory.Create`:

```csharp
var functions = tools.Select(tool => AIFunctionFactory.Create(
    (string query, CancellationToken ct) => tool.InvokeAsync(query, ct),
    name: tool.Name,
    description: tool.Description)).ToArray();

AIAgent agent = new AzureOpenAIClient(
        new Uri(settings.Endpoint),
        new AzureKeyCredential(settings.ApiKey))
    .GetChatClient(settings.DeploymentName)
    .AsAIAgent(new ChatClientAgentOptions
    {
        Name = "EnterpriseKnowledgeAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = EnterpriseAgentInstructions.SystemPrompt,
            Tools = [.. functions]
        }
    });

AgentResponse response = await agent.RunAsync(
    query,
    cancellationToken: cancellationToken);
```

The model-facing function name combines server and tool names, for example `enterprise-knowledge__find_customer`. Its description includes the server identity and the description discovered from MCP.

At this point the model may call zero or more supplied functions. The model decides whether it needs a tool, which tool to use, what query to send, and whether another tool call is useful. C# still controls which functions exist, how their delegates behave, and how many calls are allowed.

## Validating before protocol invocation

A model-selected function first enters an application delegate created by `EnterpriseAssistantAgent`. The delegate reserves a call in `InvocationLedger`, creates the `query` argument as a `JsonElement`, and asks `EnterpriseMcpClient` to invoke the named capability using the request catalog.

The MCP client verifies that the server matches the configured `enterprise-knowledge` server and that the selected tool was discovered for this request:

```csharp
var descriptor = requestCatalog.SingleOrDefault(tool =>
    tool.Server.Equals(call.Server, StringComparison.OrdinalIgnoreCase)
    && tool.Name.Equals(call.Tool, StringComparison.OrdinalIgnoreCase))
    ?? throw new McpToolValidationException(
        $"MCP tool '{call.Server}/{call.Tool}' was not discovered for this request.");

ValidateArguments(call.Arguments, descriptor.InputSchema);
```

`ValidateArguments` enforces the subset of JSON Schema needed by the three sample tools: required properties must be present, undeclared properties are rejected, and properties declared as strings must receive JSON strings. It is intentionally not a general JSON Schema validator.

Only after those checks does the SDK call cross the MCP boundary:

```csharp
var arguments = call.Arguments.ToDictionary(
    pair => pair.Key,
    pair => (object?)pair.Value.Deserialize<object>());

CallToolResult result = await descriptor.ClientTool.CallAsync(
    arguments,
    cancellationToken: cancellationToken);
```

The result reader prefers `StructuredContent` when it is a `JsonElement`; otherwise it takes the first text content block and attempts to parse it as JSON. It returns a cloned `JsonElement` together with the server, tool, and success flag.

The host also bounds model-selected calls. `InvocationLedger.Reserve` rejects a call after the configured maximum—five by default:

```csharp
if (_reserved >= maxToolCalls)
{
    LimitReached = true;
    throw new McpToolCallLimitException(maxToolCalls);
}

return ++_reserved;
```

This is deterministic resource control around a model-driven loop. Reaching the limit stops further retrieval and returns a bounded explanatory answer. It does not attempt another MCP call.

## An end-to-end request

Consider the API request:

```json
{
  "query": "Tell me about Contoso."
}
```

The implemented flow is:

```text
POST /api/assistant/query
-> EnterpriseAssistantAgent.QueryAsync
-> EnterpriseMcpClient.DiscoverToolsAsync
-> MCP ListToolsAsync over stdio
-> retain three read-only tools
-> create ModelTool delegates
-> AIFunctionFactory.Create
-> AIAgent.RunAsync
-> model selects enterprise-knowledge__find_customer
-> InvocationLedger.Reserve
-> validate server, request catalog, and query argument
-> McpClientTool.CallAsync
-> stdio server dispatches EnterpriseMcpTools.FindCustomer("Contoso")
-> JSON customer result returns to the model
-> model synthesizes the answer
-> API returns answer plus invocation telemetry
```

The deterministic customer result identifies Contoso as an active manufacturing customer owned by Adele, with a sample latest-activity value. That data crosses an actual MCP client/server round trip even though it comes from an in-memory array.

A broader query such as “Find Contoso and check the documentation for authentication guidance” can cause sequential calls to `find_customer` and `search_documentation`. There is no hard-coded workflow ordering those tools. The Agent may select another available function after receiving the first result, subject to the same validation and call budget.

The response includes the synthesized answer and ordered `ToolInvocationResponse` records with server, tool, duration, success, arguments, result, and error. That visibility is useful in the sample, but capturing raw enterprise arguments and results is a significant production risk.

## What MCP does not solve

MCP standardizes capability discovery and invocation. It does not decide whether a capability is safe, whether a user is authorized, or whether the result should be trusted.

It also does not provide the following concerns in this implementation:

- Agent orchestration or workflow state. There is one Agent here; MCP does not coordinate multiple Agents or persist a process.
- Durable execution. The client connection and catalog are process-local, and there is no business persistence or restart recovery.
- Automatic trust. Server-supplied names, descriptions, annotations, schemas, and results are input from another system.
- Complete schema enforcement. The sample validates only the simple argument shapes its tools use.
- Reliability policy. There are no application-defined per-tool timeouts, retries, circuit breakers, result-size limits, or remote-service failover.
- Production integrations. The tool methods search fixed arrays; they do not call GitHub, a document platform, CRM, or SQL database.
- Resources or prompts. This sample exercises MCP tools only and should not be read as a demonstration of every protocol capability.

MCP makes the boundary consistent. The host and server still require careful service design on both sides of it.

## Security and production considerations

Before connecting the same architecture to enterprise systems, strengthen the controls around the protocol path:

- **Authenticate and authorize twice:** restrict the catalog before it reaches the model, then enforce authorization again immediately before or inside the tool. Read-only does not mean universally readable.
- **Carry trusted identity:** the sample tools receive only a query string. A production design needs an authenticated user or workload identity, tenant context, and auditable authorization decisions without trusting model-generated identity fields.
- **Pin server trust:** dynamic discovery must not become dynamic trust. Configure approved servers and transports, validate endpoint or executable provenance, minimize the child environment, and plan credential acquisition and rotation.
- **Treat metadata and results as untrusted:** external tool descriptions can influence model behavior, and returned documents can contain prompt-injection text. Keep application instructions, user content, tool metadata, arguments, and results at distinct trust levels.
- **Use full contract validation:** enforce the discovered schema with a suitable JSON Schema implementation when tools accept nested objects, arrays, enums, numeric constraints, or unions. Also apply business validation inside the server.
- **Bound resource use:** combine call counts with cancellation, deadlines, per-tool timeouts, response-size limits, model-token budgets, rate limits, and cost controls.
- **Protect telemetry:** this sample logs and returns serialized arguments and complete results. Production telemetry should redact, truncate, classify, restrict, or omit sensitive content and follow an explicit retention policy.
- **Design for failure:** add policies for server startup failures, authentication errors, throttling, transient transport faults, malformed responses, and partial upstream results. Decide which failures may be returned to the model and which must terminate the request safely.
- **Constrain side effects:** these tools are read-only. Write-capable tools need stronger confirmation, idempotency, concurrency control, audit, and possibly human approval around the operation.

The accompanying tests exercise the real stdio boundary without calling Azure OpenAI. They verify discovery of exactly three tools, invocation of `find_customer`, rejection of an undiscovered `delete_customer` tool, wrong-type argument rejection, delivery of discovered tools to a scripted model boundary, the HTTP endpoint, and enforcement of the call limit. They do not prove live model tool selection, production authorization, or real enterprise integrations.

For a .NET developer, the durable lesson is the separation of responsibilities: MCP publishes and invokes capabilities; Microsoft Agent Framework presents accepted functions to the model; the model selects among them; and deterministic application code remains responsible for trust, validation, resource limits, and observability.

## Continue Exploring

- [Building AI Agents with .NET — Part 2 on Amazon](https://www.amazon.com/dp/B0HJHDMCZB)
- [Companion source for Building AI Agents with .NET — Part 2](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-2)
