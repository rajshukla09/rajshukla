# Building Your First AI Agent with Microsoft Agent Framework and .NET

The difficult part of adding a language model to an ASP.NET Core application is not making the first network call. It is deciding where provider configuration, agent behavior, HTTP validation, and failure handling belong so that the model does not leak into every layer of the system.

The smallest useful Microsoft Agent Framework application needs four boundaries:

```text
HTTP client
  → ASP.NET Core controller
  → application-facing agent service
  → Microsoft Agent Framework AIAgent
  → Azure OpenAI chat client and deployment
```

This article builds that path using a travel-planning API. The domain is intentionally simple. The useful result is the architecture: callers depend on an HTTP contract, the controller depends on an application capability, and one component owns the framework agent and model-provider configuration.

The implementation uses Microsoft Agent Framework 1.17.0, Azure OpenAI, and .NET 9. It performs one standalone invocation per request. Conversations, tools, memory, workflows, persistence, streaming, and structured model output are deliberately outside this first boundary.

## Start with validated provider configuration

An agent backed by Azure OpenAI needs three values with different meanings:

- `Endpoint` identifies the Azure OpenAI resource.
- `ApiKey` authenticates the application.
- `DeploymentName` identifies the deployed chat model the application calls.

The deployment name is an Azure resource configuration value; it does not have to match the underlying model name. Chapter 1 represents these settings with a normal .NET options class:

```csharp
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string Endpoint { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string DeploymentName { get; init; } = string.Empty;
}
```

The composition root binds and validates that configuration when the host starts:

```csharp
builder.Services
    .AddOptions<AzureOpenAIOptions>()
    .Bind(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

`ValidateOnStart` moves missing or malformed configuration to application startup rather than allowing the first customer request to discover it. The options object is injected into the component that constructs the agent; controllers never read endpoint, credential, or deployment settings.

The checked-in `appsettings.json` contains placeholders and an empty key. For local development, the sample expects .NET user secrets:

```bash
dotnet user-secrets set "AzureOpenAI:Endpoint" \
  "https://YOUR-RESOURCE.openai.azure.com/" \
  --project src/SmartTravelPlanner.Api

dotnet user-secrets set "AzureOpenAI:ApiKey" \
  "YOUR-API-KEY" \
  --project src/SmartTravelPlanner.Api

dotnet user-secrets set "AzureOpenAI:DeploymentName" \
  "YOUR-CHAT-DEPLOYMENT" \
  --project src/SmartTravelPlanner.Api
```

User secrets keep development credentials out of source control. They are not a production credential strategy; deployed systems need an appropriate secret-management and identity design.

## Adapt the model client into an agent

The most important Chapter 1 code is the boundary between the provider client and Microsoft Agent Framework:

```csharp
_agent = new AzureOpenAIClient(
        new Uri(settings.Endpoint),
        new AzureKeyCredential(settings.ApiKey))
    .GetChatClient(settings.DeploymentName)
    .AsAIAgent(
        name: nameof(TravelAgent),
        instructions: TravelAgentInstructions.SystemPrompt);
```

Each call has a separate responsibility.

`AzureOpenAIClient` represents access to the configured Azure OpenAI resource. `GetChatClient` selects the deployment that will answer chat requests. `AsAIAgent` adapts that provider-facing chat client into Microsoft Agent Framework's `AIAgent` abstraction and supplies the stable agent name and instructions.

The underlying deployment has not become a different model. The adapter gives application code a framework-level agent abstraction around the configured chat client:

```text
AzureOpenAIClient
  → ChatClient for one deployment
  → AsAIAgent(name, instructions)
  → AIAgent
```

The distinction prevents two design decisions from being conflated:

- The chat client answers, “Which configured model deployment do we call?”
- The agent answers, “What role does that model perform in this application?”

Microsoft Agent Framework owns agent execution through `AIAgent`. The Azure OpenAI client owns provider communication. Application code still owns configuration, dependency injection, validation, logging, API contracts, and error policy.

## Instructions are a stable behavioral contract

The current user request is not the agent's identity. “Plan a three-day trip to Jaipur” describes one invocation. The travel agent's role, normal responsibilities, output shape, and behavioral boundaries should apply to every invocation.

The implementation keeps that stable contract in `TravelAgentInstructions.SystemPrompt`. Its essential structure is:

```csharp
public const string SystemPrompt = """
    You are a professional travel-planning assistant. Your role is to create practical,
    realistic, and easy-to-follow travel itineraries.

    Responsibilities:
    - Identify the destination and requested trip duration.
    - Create one day-by-day section for every requested travel day.
    - Respect traveller preferences when provided.
    - State important assumptions when information is missing.
    - Finish with concise practical tips.

    Behavioral boundaries:
    - Help users plan trips, but do not perform bookings.
    - Never claim real-time verification of prices, availability, weather, opening hours,
      visa rules, local restrictions, or travel times.
    - Do not present uncertain information as confirmed fact.

    Return a concise Markdown response with a trip overview,
    one section for every requested day, and practical tips.
    """;
```

This is more useful than repeating “act as a travel planner” in every user prompt. It gives reviewers one place to inspect the role and boundaries, and it keeps behavioral content out of provider construction and transport code.

Instructions remain probabilistic guidance. They do not provide deterministic authorization, input validation, booking prevention, or factual verification. Hard business and security rules belong in normal application code and in the capabilities the application actually exposes.

The source includes a small contract test that protects important instruction clauses:

```csharp
string instructions = TravelAgentInstructions.SystemPrompt;

Assert.Contains("Your role", instructions, StringComparison.OrdinalIgnoreCase);
Assert.Contains("Responsibilities", instructions, StringComparison.OrdinalIgnoreCase);
Assert.Contains("Behavioral boundaries", instructions, StringComparison.OrdinalIgnoreCase);
Assert.Contains("do not perform bookings", instructions, StringComparison.OrdinalIgnoreCase);
Assert.Contains(
    "one day section for every requested travel day",
    instructions,
    StringComparison.OrdinalIgnoreCase);
```

This test does not evaluate model quality or prove that every response will comply. It detects accidental deletion of the explicit prompt contract during code changes.

## Put `AIAgent` behind an application capability

The controller could construct an Azure client and invoke `AIAgent` directly, but that would mix transport, provider, and agent concerns. Chapter 1 instead gives the application a narrow capability:

```csharp
public interface ITravelAgent
{
    Task<string> CreateItineraryAsync(
        string prompt,
        CancellationToken cancellationToken);
}
```

`TravelAgent` owns the `AIAgent` and exposes only the operation the application needs:

```csharp
public async Task<string> CreateItineraryAsync(
    string prompt,
    CancellationToken cancellationToken)
{
    _logger.LogInformation("Travel agent execution starting");

    AgentResponse result = await _agent.RunAsync(
        prompt,
        cancellationToken: cancellationToken);

    string response = result.Text;
    _logger.LogInformation(
        "Travel agent execution completed with {ResponseLength} characters",
        response.Length);
    return response;
}
```

`RunAsync` represents one completed agent execution. It receives the current user request, uses the instructions already associated with the `AIAgent`, calls the underlying provider asynchronously, and returns an `AgentResponse`. This first application uses only `AgentResponse.Text`.

The request's cancellation token is passed through the service and into `RunAsync`, allowing the underlying operation to observe cancellation. The log records execution boundaries and response length without recording the user's prompt or generated itinerary.

The component is registered behind its interface:

```csharp
builder.Services.AddSingleton<ITravelAgent, TravelAgent>();
```

The rest of the application now knows it has a travel-planning capability. It does not depend on `AIAgent`, `ChatClient`, `AzureOpenAIClient`, or `AgentResponse`. That separation is what makes deterministic endpoint tests possible without calling a live model.

## Keep the HTTP boundary ordinary

The API uses small request and response records:

```csharp
public sealed record TravelPlanRequest(string Prompt);

public sealed record TravelPlanResponse(string Response);
```

The controller validates the request before invoking the agent and maps the returned text into the response contract:

```csharp
[HttpPost("plan", Name = "CreateTravelPlan")]
public async Task<ActionResult<TravelPlanResponse>> CreatePlanAsync(
    [FromBody] TravelPlanRequest request,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        ModelState.AddModelError(
            nameof(request.Prompt),
            "Prompt is required and cannot contain only whitespace.");
        return ValidationProblem(ModelState);
    }

    string response = await _travelAgent.CreateItineraryAsync(
        request.Prompt,
        cancellationToken);

    return Ok(new TravelPlanResponse(response));
}
```

The controller is responsible for HTTP input validation, status codes, and response mapping. It does not create the model client, define the agent instructions, or interpret framework response objects.

The complete runtime path is now:

```text
POST /api/travel/plan
  → ASP.NET Core binds TravelPlanRequest
  → TravelController rejects a blank prompt
  → ITravelAgent.CreateItineraryAsync
  → AIAgent.RunAsync
  → ChatClient calls the configured Azure OpenAI deployment
  → AgentResponse.Text
  → TravelPlanResponse with HTTP 200
```

The user request and agent instructions meet inside agent execution, but the application retains control of everything around that probabilistic boundary.

## Return safe failures without hiding operational detail

Provider calls can fail because of credentials, quotas, service availability, cancellation, or network conditions. The sample adds ASP.NET Core Problem Details and a global exception handler. It logs the actual exception on the server, then returns a controlled response:

```csharp
logger.LogError(
    error?.Error,
    "An unhandled error occurred while processing the request");

context.Response.StatusCode = StatusCodes.Status500InternalServerError;
await Results.Problem(
    statusCode: StatusCodes.Status500InternalServerError,
    title: "Unable to create a travel plan",
    detail: "The travel-planning request could not be completed. Please try again.")
    .ExecuteAsync(context);
```

This preserves diagnostic detail in server logs without returning provider exception text to the caller. The endpoint test proves that a fake provider failure produces HTTP 500, includes the safe title, and excludes the injected internal message.

A production service would normally classify failures more precisely rather than map every unhandled exception to the same status. Chapter 1 establishes only the safe outer boundary.

## Test the deterministic seams

Live model output varies, so the endpoint tests replace `ITravelAgent` with a fake. This is possible because the HTTP layer depends on the application interface rather than the framework or provider:

```csharp
builder.ConfigureServices(services =>
{
    services.RemoveAll<ITravelAgent>();
    services.AddSingleton<ITravelAgent>(Agent);
});
```

The tests verify three deterministic behaviors:

- A whitespace prompt returns `400 Bad Request` and never calls the agent.
- A valid prompt is passed through exactly once and the returned itinerary is mapped to `TravelPlanResponse`.
- An agent exception becomes a controlled `500` response without leaking provider details.

They do not call Azure OpenAI, verify `AsAIAgent` integration against a deployment, or judge itinerary accuracy. This is the right scope for these application-boundary tests, but it leaves live integration and model-behavior evaluation as separate test problems.

## What this first agent does not provide

This is an intentionally narrow implementation. Every API request is a standalone `RunAsync` call; no `AgentSession` is created or reused. The agent cannot remember a previous itinerary or interpret a follow-up such as “make day two quieter.”

The response is free-form text wrapped in JSON, not a typed itinerary. The application cannot reliably validate individual days, activities, or assumptions. There are also no tools, retrieval, memory store, workflow, streaming response, persistence layer, human approval, or durable execution.

Those omissions are useful because they keep the fundamental boundary visible:

- The model supplies generative capability.
- The `AIAgent` supplies framework execution, identity, and instructions.
- `TravelAgent` supplies the application-facing capability.
- ASP.NET Core supplies configuration, dependency injection, validation, transport, and failure handling.

Adding advanced behavior later should strengthen one of these boundaries rather than collapse them.

## Where to go next technically

The most immediate limitation is the string response. An application that needs to display, validate, store, or process an itinerary should request a typed result and validate it deterministically. After that, session continuity, streaming, persistence, tools, and memory can be added as separate architectural capabilities rather than bundled into the first model call.

## Continue Exploring

This article is derived from Chapter 1, “Build Your First Agent,” in *Building AI Agents with .NET — Part 1*.

- [Building AI Agents with .NET — Part 1 on Amazon](https://www.amazon.com/dp/B0HFMX5V9P)
- [Chapter 1 companion source on GitHub](https://github.com/rajshukla09/building-ai-agents-with-dotnet-part-1/tree/main/chapter-01)
