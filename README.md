# TypeSafe.AI.AgentFramework

[![CI](https://github.com/typesafe-sdk-csharp/typesafe-agent-framework/actions/workflows/ci.yml/badge.svg)](https://github.com/typesafe-sdk-csharp/typesafe-agent-framework/actions/workflows/ci.yml)
[![Release & Publish](https://github.com/typesafe-sdk-csharp/typesafe-agent-framework/actions/workflows/release.yml/badge.svg)](https://github.com/typesafe-sdk-csharp/typesafe-agent-framework/actions/workflows/release.yml)
[![NuGet Version](https://img.shields.io/nuget/v/TypeSafe.AI.AgentFramework.svg?style=flat&logo=nuget)](https://www.nuget.org/packages/TypeSafe.AI.AgentFramework)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)

Enterprise-ready **Microsoft Agent Framework (`Microsoft.Agents.AI` / `Microsoft.Extensions.AI`) integration for TypeSafe AI** — providing ultra-low latency probabilistic evaluations, deterministic routing, history-protected guardrails, pre-turn dynamic tool shortlisting, runtime tool expansion, and semantic skill discovery for modern agentic workflows.

Designed **Dependency Injection First** for seamless integration with `Microsoft.Extensions.DependencyInjection`, `IHostApplicationBuilder`, and Microsoft Agent Framework hosting patterns.

---

## ✨ Features

- **🚀 NativeAOT & Zero Reflection** — Built from the ground up for high throughput and minimal memory footprint. Fully trimmed and NativeAOT compatible with `System.Text.Json` source generation.
- **💉 Dependency Injection First** — First-class DI extensions for `TypeSafeAgent`, `TypeSafeAIContextProvider`, `TypeSafeToolSelectionProvider`, and `TypeSafeSkillsSource` with automatic service lifetime and configuration binding.
- **🤖 `TypeSafeAgent`** — Full-featured `AIAgent` backed directly by TypeSafe System One for evaluating natural language conversations against structured questions with probabilistic answers.
- **⚡ Zero-Shot Classification (`TypeSafeAgent`)** — High-speed, stateless zero-shot classification that categorizes incoming messages against structured rubrics with custom content extractors and result formatters without prompting LLMs.
- **🔀 `TypeSafeRouterAgent`** — Intent-based, confidence-gated router agent dispatching to specialized downstream agents with isolated per-route session state (Envelope v2).
- **🛡️ History-Protected Guardrails (`TypeSafeGuardrails`)** — Complete pre-turn input validation and buffered post-turn output screening. Rejects policy violations before model responses are committed to framework-managed chat history.
- **🧰 Pre-Turn Dynamic Tool Shortlisting (`TypeSafeToolSelectionProvider`)** — Shortlists the most relevant tools from large enterprise catalogs in ~150ms before LLM invocation using independent batched relevance questions.
- **🔄 Runtime Tool Expansion (`TypeSafeTools.CreateDynamicToolExpansionTool`)** — Reactive `request_tools` meta-tool enabling agents to dynamically discover and inject tools mid-thought during function-calling loops.
- **🧠 Semantic Skill Pruning (`TypeSafeSkillsSource`)** — Decorator over `AgentSkillsSource` for progressive disclosure and semantic shortlisting of agent skill catalogs.
- **🧩 Context Injection (`TypeSafeAIContextProvider`)** — Automatically injects evaluation findings, rubric scores, or dynamic tools into agent instructions and messages with session caching.
- **🛡️ LLM > Jev > LLM Pattern** — Proven sandwich architecture where an LLM generates candidate actions/SQL, Jev (TypeSafe System One) deterministically screens safety and policy compliance in &lt;150ms, and the LLM formats the verified output or safe refusal.

---

## 📦 Installation

### Stable Releases (NuGet.org)

```bash
dotnet add package TypeSafe.AI.AgentFramework
```

### Preview / Beta Builds (GitHub Packages)

Preview builds are published on every pull request via GitHub Packages. To consume preview packages:

1. Add a `nuget.config` to your repository root (see [`nuget.config.example`](nuget.config.example)):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github" value="https://nuget.pkg.github.com/typesafe-sdk-csharp/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="%GITHUB_TOKEN%" />
    </github>
  </packageSourceCredentials>
</configuration>
```

2. Install with the `--prerelease` flag:

```bash
dotnet add package TypeSafe.AI.AgentFramework --prerelease
```

---

## 🚀 Quickstart (Dependency Injection)

Follows standard Microsoft Agent Framework hosting patterns (`HostApplicationBuilder` / `IHostedService`):

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TypeSafe.AI;
using TypeSafe.AI.AgentFramework;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// 1. Register TypeSafe client via DI
builder.Services.AddTypeSafeClient(options =>
{
    options.ApiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
});

// 2. Register TypeSafeAgent via DI with default evaluation questions
builder.Services.AddTypeSafeAgent(q => q
    .Noul("urgent", "Does this request need urgent attention?")
    .Choice("intent", "What is the primary intent?", "support", "billing", "feedback"));

// 3. Register your hosted worker service
builder.Services.AddHostedService<TriageWorker>();

using IHost host = builder.Build();
await host.RunAsync();

/// <summary>
/// Hosted background service consuming TypeSafeAgent via DI constructor injection.
/// </summary>
internal sealed class TriageWorker(TypeSafeAgent agent, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var session = await agent.CreateSessionAsync(stoppingToken);

        // Run typed evaluation returning complete SystemOneResponse
        SystemOneResponse result = await agent.RunEvaluationAsync(
            [new ChatMessage(ChatRole.User, "My billing statement is incorrect, please fix it immediately!")],
            session,
            cancellationToken: stoppingToken);

        Console.WriteLine($"Urgency: {result.Nouls["urgent"].Noul:P0}");
        Console.WriteLine($"Intent: {result.Choices["intent"].Choice} (conf: {result.Choices["intent"].Confidence:F2})");

        // Or stream via standard Agent Framework streaming
        await foreach (var update in agent.RunStreamingAsync("Check account status", session, cancellationToken: stoppingToken))
        {
            Console.Write(update);
        }

        lifetime.StopApplication();
    }
}
```

---

## 📖 Public Features Guide: How & When to Use

| Feature | Primary Type | When to Use |
|---|---|---|
| **[Typed Evaluation Agent](#1-typesafeagent--typed-evaluations)** | `TypeSafeAgent` | You need an `AIAgent` whose job is evaluating conversations against structured criteria or producing strongly typed outputs. |
| **[Stateless Classifier Pattern](#2-zero-shot-classification--rapid-stateless-triage)** | `TypeSafeAgent` | You need high-speed, zero-shot probabilistic triage or classification of incoming messages before dispatching. |
| **[Intent & Confidence Router](#3-typesaferouteragent--intent-based-routing)** | `TypeSafeRouterAgent` | You have multiple specialized agents and want deterministic, confidence-gated dispatching with isolated conversation histories. |
| **[History-Protected Guardrails](#4-history-protected-guardrails-typesafeguardrails)** | `TypeSafeGuardrails.CreateChatClientAgent` | You want strict pre-turn and post-turn content screening where policy violations are NEVER committed to conversation history. |
| **[Generic Guardrail Middleware](#5-generic-guardrail-middleware-typesafeguardrail)** | `TypeSafeGuardrail` | You want a pluggable middleware wrapper around any existing `AIAgent` for input and output validation. |
| **[Pre-Turn Tool Shortlisting](#6-typesafetoolselectionprovider--pre-turn-tool-shortlisting)** | `TypeSafeToolSelectionProvider` | Your agent has large tool catalogs (10–100+ tools) and you want to shortlist only relevant tools (~150ms) before invoking the LLM. |
| **[Runtime Dynamic Tool Expansion](#7-runtime-tool-expansion-typesafetoolscreatedynamictoolexpansiontool)** | `TypeSafeTools.CreateDynamicToolExpansionTool` | An agent starts with minimal tools and discovers mid-reasoning that it needs additional specialized tools loaded dynamically. |
| **[Semantic Skill Pruning](#8-typesafeskillssource--semantic-skill-discovery)** | `TypeSafeSkillsSource` | Your agent uses progressive disclosure with `AgentSkillsSource` and you want to shortlist skills semantically for the current turn. |
| **[Context Injection Provider](#9-typesafeaicontextprovider--context-enrichment)** | `TypeSafeAIContextProvider` | You want to evaluate user messages before LLM execution and inject findings as prompt instructions, messages, or dynamic tools. |
| **[AIFunction Evaluation Tools](#10-typesafetools--evaluation-tools-for-agents)** | `TypeSafeTools.CreateEvaluationTool` | You want an LLM agent to explicitly invoke TypeSafe verification as a callable tool during its reasoning loop. |
| **[Strict Content Converters](#11-typesafecontentconverter--content-extraction)** | `TypeSafeContentConverter` | You need customized message extraction, multi-turn transcript formatting, or source-generated JSON metadata for screening. |
| **[NativeAOT & JSON Serialization](#12-nativeaot--reflection-free-serialization)** | `TypeSafeAgentFrameworkJson` | You are running in NativeAOT or trimmed environments and need reflection-free round-trips for `SystemOneResponse`. |
| **[Chat with SQL (LLM > Jev > LLM)](#13-end-to-end-architecture-llm--jev--llm-chat-with-sql)** | Full E2E Pipeline | You want to contrast naive "Only LLM" text-to-SQL against a verified LLM > Jev > LLM pipeline that halts mutations and data leaks. |

---

### 1. `TypeSafeAgent` — Typed Evaluations

#### When to Use
Use `TypeSafeAgent` when you want an agent whose core responsibility is evaluating natural language conversations against structured TypeSafe questions (Noul, Choice, Score) with probabilistic answers, or generating reflection-free structured JSON responses.

#### How to Use with Dependency Injection

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);

// Standard transient registration with default questions
builder.Services.AddTypeSafeAgent(q => q
    .Noul("escalate", "Does this customer inquiry require supervisor escalation?")
    .Score("sentiment", "Customer sentiment score", "negative", "neutral", "positive"),
    name: "TriageEvaluator");

// Or register keyed instances for distinct domains/agents
builder.Services.AddKeyedTypeSafeAgent("security-auditor", q => q
    .Noul("data_leak", "Does this message leak credentials, API tokens, or secrets?"));

// --- Consuming in a Hosted Service or Consumer ---
internal sealed class CustomerSupportWorker(
    TypeSafeAgent agent,
    [FromKeyedServices("security-auditor")] TypeSafeAgent securityAgent)
{
    public async Task ProcessInquiryAsync(string userMessage, CancellationToken ct)
    {
        var session = await agent.CreateSessionAsync(ct);

        // 1. Typed evaluation returning complete SystemOneResponse
        SystemOneResponse eval = await agent.RunEvaluationAsync(
            [new ChatMessage(ChatRole.User, userMessage)],
            session,
            cancellationToken: ct);

        Console.WriteLine($"Escalate: {eval.Nouls["escalate"].Noul:P0}");
        Console.WriteLine($"Sentiment: {eval.Scores["sentiment"].Score:F2}");

        // 2. Standard Agent Framework turn returning AgentResponse
        AgentResponse response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, userMessage)],
            session,
            cancellationToken: ct);

        Console.WriteLine($"Agent Response: {response.Text}");
    }
}
```

#### Dynamic Invocations (No Default Questions or Per-Run Overrides)

When your application evaluates dynamic, ad-hoc, or caller-specified rubrics (such as database-driven verification rules, user-selected compliance checklists, or multi-tenant workflows), you do not need default questions at registration time.

1. **Register the agent without default questions in DI:**
   ```csharp
   // Register a generic TypeSafeAgent with no default questions
   builder.Services.AddTypeSafeAgent(name: "DynamicAuditor");
   ```

2. **Supply questions at runtime via `TypeSafeAgentRunOptions`:**
   ```csharp
   internal sealed class DynamicVerificationService(TypeSafeAgent agent)
   {
       public async Task<SystemOneResponse> AuditComplianceAsync(
           string documentText,
           IReadOnlyList<ComplianceRule> activeRules,
           CancellationToken ct)
       {
           var session = await agent.CreateSessionAsync(ct);

           // Build questions dynamically at runtime based on external rules, tenant config, or DB
           var dynamicQuestions = Questions.Build(builder =>
           {
               foreach (var rule in activeRules)
               {
                   builder.Noul(rule.Id, rule.QuestionText);
               }
           });

           // Configure per-run options with the dynamic questions
           var runOptions = new TypeSafeAgentRunOptions
           {
               Questions = dynamicQuestions,
               Model = "jev-fast" // Optional per-run model override
           };

           // Execute typed evaluation using the dynamic questions
           return await agent.RunEvaluationAsync(
               [new ChatMessage(ChatRole.User, documentText)],
               session,
               options: runOptions,
               cancellationToken: ct);
       }
   }
   ```

> [!TIP]
> If a `TypeSafeAgent` was registered with default questions, setting `TypeSafeAgentRunOptions.Questions` will cleanly **override** the default questions for that specific invocation, while leaving other turns and agents unaffected.

> [!IMPORTANT]
> Custom response schemas and inherited `RunAsync<T>` are rejected before inference because TypeSafe System One returns deterministic structured answers, not arbitrary schema generation. Use `RunEvaluationAsync` and explicitly map the resulting `SystemOneResponse` to your application DTOs.

---

### 2. Zero-Shot Classification — Rapid Stateless Triage

#### When to Use
Use `TypeSafeAgent` configured with a `resultFormatter` and `contentExtractor` when you need ultra-fast, zero-shot probabilistic triage or classification of incoming messages against structured rubrics before routing, queuing, or downstream processing. It does not prompt an LLM; it evaluates directly against TypeSafe System One.

#### How to Use with Dependency Injection

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);

// Register the classifier agent in DI using TypeSafeAgent with questions and formatters
builder.Services.AddTransient<TypeSafeAgent>(sp =>
{
    var client = sp.GetRequiredService<ITypeSafeClient>();

    var questions = Questions.Build(q => q
        .Choice("department", "Which department should handle this request?",
            "billing", "technical_support", "sales", "general_inquiry"));

    return new TypeSafeAgent(
        client: client,
        defaultQuestions: questions,
        name: "DepartmentClassifier",
        contentExtractor: TypeSafeContentConverter.DefaultExtractor,
        resultFormatter: response => response.Choices["department"].Choice);
});

// --- Consuming Service ---
internal sealed class TriageIngestionService(TypeSafeAgent classifier)
{
    public async Task<string> ClassifyIncomingAsync(string text, CancellationToken ct)
    {
        AgentResponse result = await classifier.RunAsync(
            [new ChatMessage(ChatRole.User, text)],
            cancellationToken: ct);

        return result.Text; // "billing", "technical_support", etc.
    }
}
```

- **Stateless & Thread-Safe**: Delegates must be thread-safe when the agent instance is shared.
- **Streaming**: Streaming requests wait for the single atomic evaluation to complete and yield the classified result.

---

### 3. `TypeSafeRouterAgent` — Intent-Based Routing

#### When to Use
Use `TypeSafeRouterAgent` in multi-agent systems where user messages should be routed to specialized downstream target agents based on evaluated intent and confidence. A dedicated `fallbackAgent` catches low-confidence or ambiguous inquiries.

#### How to Use with Dependency Injection

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);

// 1. Register specialized downstream target agents as keyed AIAgents
builder.Services.AddKeyedTransient<AIAgent, BillingSpecialistAgent>("billing");
builder.Services.AddKeyedTransient<AIAgent, TechSupportSpecialistAgent>("tech_support");
builder.Services.AddKeyedTransient<AIAgent, GeneralInquiryAgent>("fallback");

// 2. Register TypeSafeRouterAgent resolving downstream targets from DI
builder.Services.AddTransient<TypeSafeRouterAgent>(sp =>
{
    var client = sp.GetRequiredService<ITypeSafeClient>();

    var routingQuestion = new Choice
    {
        Instructions = "Classify user intent to route to the correct specialist.",
        Criteria = new Dictionary<string, TypeSafeContent?>
        {
            ["billing"] = TypeSafeContent.FromString("Billing, invoices, refunds, charges, payments"),
            ["tech_support"] = TypeSafeContent.FromString("Software bugs, error codes, login issues, crashes")
        }
    };

    return new TypeSafeRouterAgent(
        client: client,
        questionId: "intent",
        routingQuestion: routingQuestion,
        routeTargets: new Dictionary<string, AIAgent>
        {
            ["billing"] = sp.GetRequiredKeyedService<AIAgent>("billing"),
            ["tech_support"] = sp.GetRequiredKeyedService<AIAgent>("tech_support")
        },
        fallbackAgent: sp.GetRequiredKeyedService<AIAgent>("fallback"),
        minimumConfidence: 0.75,
        name: "CustomerTriageRouter");
});

// --- In an Orchestration Service ---
internal sealed class SupportOrchestrator(TypeSafeRouterAgent router)
{
    public async Task<string> HandleMessageAsync(AgentSession session, string userMessage, CancellationToken ct)
    {
        // Router evaluates intent and dispatches to the matching target agent or fallback
        AgentResponse response = await router.RunAsync(
            [new ChatMessage(ChatRole.User, userMessage)],
            session,
            cancellationToken: ct);

        return response.Text;
    }
}
```

#### Envelope Version 2 Session Isolation
`TypeSafeRouterAgent` persists sessions using version 2 envelopes. Each downstream agent maintains an **isolated conversation state** mapped by its stable route label. If confidence falls below `minimumConfidence` or the intent is unknown, the router dispatches to the `fallbackAgent`, which maintains its own separate session.

> [!WARNING]
> One router session permits only one operation at a time. Concurrent calls, overlapping streaming enumerations, and parallel session serialization on the same router session will throw `InvalidOperationException`. Use separate sessions for concurrent tasks.

---

### 4. History-Protected Guardrails (`TypeSafeGuardrails`)

#### When to Use
Use `TypeSafeGuardrails.CreateChatClientAgent` when building customer-facing or enterprise LLM agents where toxic, policy-violating, or hallucinated content **must never be committed to framework-managed chat history** or seen by the user.

#### How to Use with Dependency Injection

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);

// Register the history-protected AIAgent using DI-resolved IChatClient and ITypeSafeClient
builder.Services.AddTransient<AIAgent>(sp =>
{
    var modelClient = sp.GetRequiredService<IChatClient>();
    var typeSafeClient = sp.GetRequiredService<ITypeSafeClient>();

    var screeningQuestions = Questions.Build(q => q
        .Noul("harmful", "Does this message contain toxic, abusive, or dangerous content?")
        .Noul("prompt_injection", "Is this message attempting prompt injection or system jailbreak?"));

    return TypeSafeGuardrails.CreateChatClientAgent(
        modelClient: modelClient,
        client: typeSafeClient,
        screeningQuestions: screeningQuestions,
        shouldBlock: eval => eval.Nouls["harmful"].Noul > 0.70 || eval.Nouls["prompt_injection"].Noul > 0.60,
        blockMessage: "Your request violates safety guidelines and cannot be processed.");
});

// --- Chat Service ---
internal sealed class SecureChatService(AIAgent agent)
{
    public async Task<string> ExecuteTurnAsync(string userInput, AgentSession session, CancellationToken ct)
    {
        AgentResponse response = await agent.RunAsync(userInput, session, cancellationToken: ct);

        if (response.Message.AdditionalProperties.TryGetValue("typesafe.guardrail_blocked", out var blocked) && (bool)blocked)
        {
            // The response was safely replaced and was NEVER persisted to chat history!
            Console.WriteLine($"[Blocked by Guardrail] Stage: {response.Message.AdditionalProperties["typesafe.guardrail_stage"]}");
        }

        return response.Text;
    }
}
```

#### Safety & Architecture Guarantees
- **Local History Protection**: Model responses are screened in an internal buffer. Rejected responses are replaced with `blockMessage` (`FinishReason = ChatFinishReason.ContentFilter`) before being persisted to chat history.
- **Streaming Safe**: Streaming responses are buffered through the same complete-response path so rejected tokens are never leaked to callers.
- **Stateless Model Client Requirement**: Provider-managed conversation state (`ConversationId`, `ContinuationToken`, `AllowBackgroundResponses`) and per-service-call persistence are explicitly rejected.
- **Tool Effects Note**: Tool invocations that have already executed are outside the output screening rollback guarantee. Ensure mutating tools are protected with pre-turn screening.

---

### 5. Generic Guardrail Middleware (`TypeSafeGuardrail`)

#### When to Use
Use `TypeSafeGuardrail` when wrapping arbitrary third-party `AIAgent` instances in DI to screen incoming requests (and optionally returned responses) before they reach the inner agent.

#### How to Use with Dependency Injection

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);
builder.Services.AddTransient<MyExistingAgent>();

// Decorate MyExistingAgent in DI with TypeSafeGuardrail
builder.Services.AddTransient<AIAgent>(sp =>
{
    var innerAgent = sp.GetRequiredService<MyExistingAgent>();
    var typeSafeClient = sp.GetRequiredService<ITypeSafeClient>();

    return new TypeSafeGuardrail(
        innerAgent: innerAgent,
        client: typeSafeClient,
        screeningQuestions: q => q.Noul("inappropriate", "Is this input inappropriate or off-topic?"),
        shouldBlock: eval => eval.Nouls["inappropriate"].Noul >= 0.50,
        blockMessage: "Request blocked by safety filter.",
        screenOutput: true);
});
```

> [!NOTE]
> `TypeSafeGuardrail` screens inputs before calling `innerAgent` and filters returned responses. However, it cannot prevent an arbitrary `innerAgent` from persisting output internally. For framework-managed history protection, use `TypeSafeGuardrails.CreateChatClientAgent`.

---

### 6. `TypeSafeToolSelectionProvider` — Pre-Turn Tool Shortlisting

#### When to Use
Use `TypeSafeToolSelectionProvider` when your agent has dozens or hundreds of available tools (APIs, plugins, integrations) that exceed LLM context window limits, increase latency, or degrade model tool selection accuracy.

#### How to Use with Dependency Injection

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);

var selectionOptions = new TypeSafeToolSelectionOptions
{
    TopK = 5,                              // Maximum ranked tools to retain
    MinimumRelevanceProbability = 0.50,    // Cutoff threshold (0.0 to 1.0)
    DropOffRatio = 0.30,                   // Relative drop-off from top candidate
    FallbackMode = TypeSafeToolFallbackMode.Throw
};

// Mandatory tools always bypass limits and are never pruned
selectionOptions.AlwaysIncludeToolNames.Add("escalate_to_human");

// Approach A: Register directly into DI as an AIContextProvider
builder.Services.AddTypeSafeToolSelectionProvider(selectionOptions);

// Approach B: Install into the IChatClient pipeline via ChatClientBuilder
builder.Services.AddTransient<IChatClient>(sp =>
{
    var typeSafeClient = sp.GetRequiredService<ITypeSafeClient>();
    var baseClient = sp.GetRequiredService<IInnerChatClient>();

    return new ChatClientBuilder(baseClient)
        .UseTypeSafeToolShortlisting(typeSafeClient, selectionOptions)
        .Build();
});

// Configure ChatClientAgent with the shortlisted context provider
builder.Services.AddTransient<AIAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var toolProvider = sp.GetRequiredService<TypeSafeToolSelectionProvider>();
    var enterpriseTools = sp.GetServices<AITool>().ToArray();

    return new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        ChatOptions = new ChatOptions { Tools = enterpriseTools },
        AIContextProviders = [toolProvider]
    });
});
```

#### Fallback Policies
When semantic evaluation fails, the configured `FallbackMode` determines behavior:
- `Throw` (default): Re-throws `InvalidOperationException` for blank/missing context or `TypeSafeException` for upstream failures.
- `IncludeAll`: Fails open by returning the entire tool catalog.
- `IncludeNone`: Retains mandatory tools only (`AlwaysIncludeToolNames`).

---

### 7. Runtime Tool Expansion (`TypeSafeTools.CreateDynamicToolExpansionTool`)

#### When to Use
Use dynamic tool expansion when an agent starts a task with a minimal core toolset and discovers mid-reasoning that it needs specialized capabilities (e.g. GitHub MCP pull-request review, database querying, flight booking, or financial calculators).

#### How to Use with Dependency Injection

This implements the Microsoft Agent Framework dynamic expansion pattern (`Agent_Step20_DynamicFunctionTools`) backed by TypeSafe System One probabilistic semantic shortlisting:

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);
builder.Services.AddSingleton<EnterpriseToolCatalog>();

// Register the ChatClientAgent configured with the dynamic expansion layer
builder.Services.AddTransient<AIAgent>(sp =>
{
    var modelClient = sp.GetRequiredService<IChatClient>();
    var typeSafeClient = sp.GetRequiredService<ITypeSafeClient>();
    var catalog = sp.GetRequiredService<EnterpriseToolCatalog>().AllTools;

    // 1. Wrap model client with managed per-run expansion scope
    var loop = new ChatClientBuilder(modelClient)
        .UseTypeSafeToolExpansion()
        .Build();

    // 2. Create the expansion meta-tool over the catalog
    var expansionTool = TypeSafeTools.CreateDynamicToolExpansionTool(
        client: typeSafeClient,
        toolCatalog: catalog,
        topK: 3,
        minimumRelevanceProbability: 0.50);

    // 3. Provide only the expansion tool initially
    return new ChatClientAgent(loop, new ChatClientAgentOptions
    {
        Name = "AdaptiveAgent",
        ChatOptions = new ChatOptions { Tools = [expansionTool] },
        UseProvidedChatClientAsIs = true
    });
});
```

- **Run Scope Isolation**: Tool additions are scoped strictly to the current run. Concurrent runs and streaming operations maintain isolated toolsets.
- **Read-Only Recommendations**: Use `TypeSafeTools.CreateToolSelectionTool` if you only want recommendations for planner/supervisor agents without runtime tool injection.

#### Runnable GitHub MCP Example

[`TypeSafe.Sample.ToolExpansion`](samples/TypeSafe.Sample.ToolExpansion/README.md) starts an agent with only `request_tools`, discovers GitHub's hosted MCP catalog, and has TypeSafe inject exactly one tool for a read-only pull-request review. The default run is an offline fixture; live mode requires `TYPESAFE_API_KEY`, `OPENAI_API_KEY`, `OPENAI_MODEL`, and `GITHUB_PAT_TOKEN`:

```powershell
dotnet run --project samples/TypeSafe.Sample.ToolExpansion -- `
  --live --repo owner/repository --pr 123
```

---

### 8. `TypeSafeSkillsSource` — Semantic Skill Discovery

#### When to Use
Use `TypeSafeSkillsSource` when using progressive disclosure with Microsoft Agent Framework's `AgentSkillsSource`. When managing large catalogs of skills, it shortlists only relevant skills semantically before instructions are injected into the agent.

#### How to Use with Dependency Injection

Follows the Microsoft Agent Framework skills with DI pattern (`Agent_Step05_SkillsWithDI`):

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);

// 1. Register the underlying skills source in DI
builder.Services.AddSingleton<FileSystemSkillsSource>(sp => new FileSystemSkillsSource("/app/skills"));

// 2. Register TypeSafeSkillsSource decorating the underlying source in DI
builder.Services.AddTypeSafeSkillsSource<FileSystemSkillsSource>(new TypeSafeSkillsOptions
{
    TopK = 3,
    MinimumRelevanceProbability = 0.60,
    FallbackMode = TypeSafeSkillFallbackMode.IncludeAll
});

// 3. Configure the agent with AgentSkillsProvider resolving AgentSkillsSource from DI
builder.Services.AddTransient<AIAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var skillsSource = sp.GetRequiredService<AgentSkillsSource>();

    return new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        Name = "SkillAwareAgent",
        AIContextProviders = [new AgentSkillsProvider(skillsSource)]
    });
});
```

---

### 9. `TypeSafeAIContextProvider` — Context Enrichment

#### When to Use
Use `TypeSafeAIContextProvider` to pre-evaluate user input before LLM execution and enrich the agent's `AIContext` with deterministic facts, rubric scores, sentiment, or conditionally selected tools (`Agent_Step17_AdditionalAIContext`).

#### How to Use with Dependency Injection

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);

// Register TypeSafeAIContextProvider via DI
builder.Services.AddTypeSafeAIContextProvider(q => q
    .Noul("is_angry", "Is the customer expressing anger or frustration?")
    .Score("urgency", "Urgency level", "low", "medium", "critical"),
    mode: TypeSafeContextInjectionMode.Instructions | TypeSafeContextInjectionMode.Messages);

// Attach the context provider to ChatClientAgent in DI
builder.Services.AddTransient<AIAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var contextProvider = sp.GetRequiredService<TypeSafeAIContextProvider>();

    return new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        AIContextProviders = [contextProvider]
    });
});
```

#### Injection Modes (`TypeSafeContextInjectionMode`)
- `Instructions`: Appends formatted evaluation results to `AIContext.Instructions`.
- `Messages`: Injects an assistant `ChatMessage` containing `TypeSafeEvaluationContent` into `AIContext.Messages`.
- `Tools`: Invokes a custom `toolSelector` delegate to dynamically inject tools into `AIContext.Tools`.
- `All`: Injects into instructions, messages, and tools simultaneously.
- Results are automatically cached in `AgentSession.StateBag` under `StateKey` (default `"typesafe.evaluation_context"`).

---

### 10. `TypeSafeTools` — Evaluation Tools for Agents

#### When to Use
Use `TypeSafeTools` factory methods to expose TypeSafe evaluations directly to LLMs as callable `AIFunction` tools registered in DI for agent reasoning loops (`AIFunctionFactory` style).

#### How to Use with Dependency Injection

```csharp
// --- Service Registration (Program.cs) ---
builder.Services.AddTypeSafeClient(builder.Configuration);

// Register evaluation tools in DI as AITool singletons
builder.Services.AddSingleton<AITool>(sp =>
{
    var client = sp.GetRequiredService<ITypeSafeClient>();
    return TypeSafeTools.CreateEvaluationTool(
        client: client,
        questionsBuilder: q => q.Noul("valid", "Is the user request valid and actionable?"),
        name: "verify_request",
        description: "Evaluates whether the user request is valid and actionable.");
});

builder.Services.AddSingleton<AITool>(sp =>
{
    var client = sp.GetRequiredService<ITypeSafeClient>();
    return TypeSafeTools.CreateNoulTool(
        client: client,
        id: "contains_pii",
        instructions: "Does this text contain personally identifiable information (PII)?",
        name: "check_pii");
});

builder.Services.AddSingleton<AITool>(sp =>
{
    var client = sp.GetRequiredService<ITypeSafeClient>();
    return TypeSafeTools.CreateChoiceTool(
        client: client,
        id: "sentiment",
        instructions: "Classify customer sentiment",
        criteria: new Dictionary<string, TypeSafeContent?>
        {
            ["positive"] = TypeSafeContent.FromString("Customer is pleased, polite, or satisfied"),
            ["negative"] = TypeSafeContent.FromString("Customer is angry, disappointed, or threatening churn")
        });
});

// Inject all registered AITool instances directly into an Agent
builder.Services.AddTransient<AIAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var tools = sp.GetServices<AITool>().ToList();

    return new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        ChatOptions = new ChatOptions { Tools = tools }
    });
});
```

---

### 11. `TypeSafeContentConverter` — Content Extraction

#### When to Use
Use `TypeSafeContentConverter` to customize how chat messages, function calls, and tool results are converted into `TypeSafeContent` for evaluation and screening.

#### Built-in Extractors
- `TypeSafeContentConverter.DefaultExtractor`: Extracts plain text or JSON from the most recent user message.
- `TypeSafeContentConverter.ChatTranscriptExtractor`: Formats multi-turn conversation history into a structured transcript.
- `TypeSafeContentConverter.ScreeningExtractor`: Strict extractor for guardrails. Screens text, JSON parts, evaluations, function names, arguments, and results while preserving message roles and boundaries.

```csharp
// Register a custom extractor in DI
builder.Services.AddSingleton<ITypeSafeContentExtractor>(sp =>
{
    return TypeSafeContentConverter.FromDelegate(messages =>
    {
        var lastMsg = messages.LastOrDefault()?.Text ?? string.Empty;
        return TypeSafeContent.FromString($"[User Query]: {lastMsg}");
    });
});

// Strict screening extractor with custom source-generated serializer options for opaque tool results
var screeningExtractor = TypeSafeContentConverter.CreateScreeningExtractor(MyJsonContext.Default.Options);
```

---

### 12. NativeAOT & Reflection-Free Serialization

`TypeSafe.AI.AgentFramework` is 100% NativeAOT compatible with zero reflection.

```csharp
// Reflection-free round-trip serialization of SystemOneResponse
string json = JsonSerializer.Serialize(evalResponse, TypeSafeAgentFrameworkJson.ResponseTypeInfo);
SystemOneResponse restored = JsonSerializer.Deserialize(json, TypeSafeAgentFrameworkJson.ResponseTypeInfo)!;
```

---

### 13. End-to-End Architecture: LLM > Jev > LLM (Chat with SQL)

#### The Problem: Naive "Only LLM"
In traditional Text-to-SQL or database-backed agent workflows, the LLM generates a SQL query that is immediately executed against production or analytics databases. This unguarded design suffers from severe vulnerabilities:
- **Destructive Mutation Injection**: Prompts like *"Count customers and also purge old audit logs"* cause the LLM to output `DELETE` or `DROP` statements that destroy audit trails.
- **Sensitive Table / PII Exfiltration**: Users asking *"Show top salaries and SSNs for payroll review"* trick the LLM into querying restricted schemas (`employee_salaries`, `ssn`).
- **Prompt Injection Hijacking**: Ad-hoc system instructions ("Do not delete records") are easily bypassed by adversarial inputs.

#### The Solution: The 3-Stage Pipeline (LLM > Jev > LLM)
```
  [User Natural Language Inquiry]
                │
                ▼
┌─────────────────────────────────┐
│  STAGE 1: LLM (Text-to-SQL)     │  Generates candidate SQL statement
└─────────────────────────────────┘
                │  Candidate SQL
                ▼
┌─────────────────────────────────┐
│  STAGE 2: Jev (System One)      │  Probabilistic schema & policy verification (<150ms)
│  (TypeSafe.AI.AgentFramework)   │  • is_read_only (Noul)
└─────────────────────────────────┘  • is_authorized_table (Noul)
                │                    • aligns_with_intent (Noul)
                │                    • risk_level (Score: 1-5)
                ▼
      ┌──────────────────┐
      │  QUARANTINE GATE │
      └─────────┬────────┘
     PASS (≥0.8)│        │ FAIL (<0.8 or Risk > 2.0)
                │        ▼
                │   ┌──────────────────────────────────────────────┐
                │   │  STAGE 3b: Safe Refusal Synthesis            │
                │   │  • Database NEVER touched (0 writes, 0 leaks)│
                │   │  • LLM explains policy violation gracefully  │
                │   └──────────────────────────────────────────────┘
                ▼
┌─────────────────────────────────┐
│  DATABASE EXECUTION &           │
│  STAGE 3a: Final LLM Synthesis  │  Executes verified query and summarizes facts
└─────────────────────────────────┘
```

#### How to Use with Dependency Injection

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTypeSafeClient(options =>
{
    options.ApiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
});

// Configure Jev verification rubrics for database operations
builder.Services.AddTypeSafeAgent(q => q
    .Noul("is_read_only", "Is this SQL query strictly read-only with no DELETE, DROP, UPDATE, INSERT, or ALTER?")
    .Noul("is_authorized_table", "Does this query target only public tables (customers, orders, products) and not restricted audit/salary tables?")
    .Noul("aligns_with_intent", "Does the candidate query genuinely satisfy the user's inquiry without hidden operations?")
    .Score("risk_level", "Evaluate the structural and security risk of executing this query (1 = harmless read-only, 5 = severe destructive or privacy risk)"));

builder.Services.AddSingleton<EnterpriseDatabase>();
builder.Services.AddHostedService<ChatWithSqlWorker>();
```

#### Comparative Results Matrix

| Metric / Scenario | 🔴 Only LLM (Unguarded) | 🟢 LLM > Jev > LLM (Verified) |
|---|---|---|
| **Scenario 1: Safe Analytics** | Executes query; answers inquiry | Jev verifies read-only (99%); safely executes and answers |
| **Scenario 2: Destructive Mutation** (`DELETE audit logs`) | ❌ **Audit logs permanently deleted** | 🛡️ **Blocked at Gate** (`is_read_only: 2%`, Risk 5.0); DB untouched |
| **Scenario 3: PII Exfiltration** (`SELECT ssn, salary`) | ❌ **Confidential SSNs & salaries leaked** | 🛡️ **Blocked at Gate** (`is_authorized: 4%`, Risk 4.5); privacy preserved |
| **Verification Latency** | 0ms (Unchecked) | **~120–150ms** |
| **Audit & Compliance Trail** | ❌ Opaque natural language | ✔ **Strongly typed probabilistic scores** |

Explore the complete runnable project in [`samples/TypeSafe.Sample.ChatWithSql`](samples/TypeSafe.Sample.ChatWithSql).

---

## 💉 Dependency Injection Reference

| Method | Target Service | Lifetime | Description |
|---|---|---|---|
| `builder.Services.AddTypeSafeFunction<TService>(...)` | `TypeSafeFunction` | Transient | Registers a tool backed by a DI service, with an explicit schema and invoker. Register the implementation separately and use a scoped lifetime when it holds conversation state. |
| `builder.Services.AddTypeSafeAgent(...)` | `TypeSafeAgent` | Transient | Registers a `TypeSafeAgent` with optional default questions (omit questions for dynamic per-run evaluations). |
| `builder.Services.AddKeyedTypeSafeAgent(...)` | `TypeSafeAgent` | Transient | Registers a keyed `TypeSafeAgent` instance with optional default questions. |
| `builder.Services.AddTypeSafeAIContextProvider(...)` | `TypeSafeAIContextProvider` | Transient | Registers a context provider that enriches `AIContext`. |
| `builder.Services.AddTypeSafeToolSelectionProvider(...)` | `TypeSafeToolSelectionProvider`, `AIContextProvider` | Transient | Registers pre-turn tool shortlisting in DI. |
| `builder.Services.AddTypeSafeSkillsSource<T>(...)` | `TypeSafeSkillsSource`, `AgentSkillsSource` | Transient | Registers decorated semantic skill discovery in DI. |
| `builder.UseTypeSafeToolShortlisting(...)` | `ChatClientBuilder` | — | Pipeline extension to shortlist tools before each LLM turn. |
| `builder.UseTypeSafeToolExpansion(...)` | `ChatClientBuilder` | — | Pipeline extension enabling runtime `request_tools` dynamic expansion. |
| `source.UseTypeSafeShortlisting(...)` | `AgentSkillsSource` | — | Decorates an existing skill source with semantic pruning. |

---

## 📂 Repository Structure

```
typesafe-agent-framework/
├── .github/
│   └── workflows/
│       ├── ci.yml                     # Build, test, and pack on PR / main
│       └── release.yml                # Build, verify & deploy GA packages to NuGet.org / GitHub Packages
├── src/
│   └── TypeSafe.AI.AgentFramework/    # Core Agent Framework library (NuGet package)
├── samples/
│   ├── TypeSafe.Sample.ChatWithSql/              # End-to-end runnable showcase: LLM > Jev > LLM
│   ├── TypeSafe.Sample.ToolSelection/            # Pre-turn dynamic tool shortlisting sample
│   └── TypeSafe.Sample.ToolExpansion/            # Runtime dynamic tool expansion sample
├── tests/
│   └── TypeSafe.AI.AgentFramework.Tests/         # Comprehensive unit & regression test suite
├── Directory.Build.props              # Global compiler, NativeAOT, and packaging settings
├── Directory.Packages.props           # Central Package Management (CPM)
├── global.json                        # Pinned .NET 10 SDK version
├── nuget.config.example               # Example config for GitHub Packages
└── TypeSafe.slnx                      # Solution file
```

---

## 🛠️ Building & Testing Locally

**Requirements**: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned via `global.json`)

```bash
# Clone the repository
git clone https://github.com/typesafe-sdk-csharp/typesafe-agent-framework.git
cd typesafe-agent-framework

# Restore dependencies
dotnet restore

# Build solution
dotnet build --configuration Release --no-restore

# Run tests (offline mock handlers)
dotnet test --configuration Release --no-build

# Pack NuGet package
dotnet pack src/TypeSafe.AI.AgentFramework/TypeSafe.AI.AgentFramework.csproj --configuration Release --no-build

# Run Chat with SQL (LLM > Jev > LLM) E2E sample
dotnet run --project samples/TypeSafe.Sample.ChatWithSql/TypeSafe.Sample.ChatWithSql.csproj

# Run Tool Selection sample
dotnet run --project samples/TypeSafe.Sample.ToolSelection/TypeSafe.Sample.ToolSelection.csproj

# Run GitHub MCP Tool Expansion sample (offline by default)
dotnet run --project samples/TypeSafe.Sample.ToolExpansion/TypeSafe.Sample.ToolExpansion.csproj
```

---

## 🔄 CI/CD & Automated Releases

- **Continuous Integration (`ci.yml`)** — Restores, builds in Release mode, runs all unit tests, executes managed smoke consumers, and validates NativeAOT publish and execution on Linux and Windows.
- **Automated Publishing (`release.yml`)** — Triggered on version tags (`v*`), packages the library and publishes to NuGet.org with OIDC authentication and GitHub Packages preview feeds.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
