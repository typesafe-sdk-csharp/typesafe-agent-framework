using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// Extension methods for registering TypeSafe Microsoft Agent Framework services in <see cref="IServiceCollection"/>.
/// </summary>
public static class TypeSafeAgentFrameworkServiceCollectionExtensions
{
    /// <summary>
    /// Registers a function whose implementation is resolved from DI with the specified lifetime.
    /// Register <typeparamref name="TService"/> separately; its instance is captured when the function is resolved.
    /// </summary>
    public static IServiceCollection AddTypeSafeFunction<TService>(
        this IServiceCollection services,
        string name,
        string description,
        JsonElement jsonSchema,
        Func<TService, AIFunctionArguments, CancellationToken, ValueTask<object?>> invoker,
        ServiceLifetime lifetime = ServiceLifetime.Transient,
        JsonSerializerOptions? serializerOptions = null)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(invoker);

        var schema = jsonSchema.Clone();
        services.Add(new ServiceDescriptor(typeof(TypeSafeFunction), sp =>
        {
            var implementation = sp.GetRequiredService<TService>();
            return new TypeSafeFunction(name, description, schema,
                (arguments, cancellationToken) => invoker(implementation, arguments, cancellationToken),
                serializerOptions);
        }, lifetime));
        return services;
    }

    /// <summary>
    /// Registers a <see cref="TypeSafeAgent"/> instance with the specified lifetime.
    /// </summary>
    public static IServiceCollection AddTypeSafeAgent(
        this IServiceCollection services,
        Func<QuestionBuilder, QuestionBuilder>? questions = null,
        string name = "TypeSafeAgent",
        string? defaultModel = null,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(services);

        var factory = (IServiceProvider sp) =>
        {
            var client = sp.GetRequiredService<ITypeSafeClient>();
            return questions is not null
                ? new TypeSafeAgent(client, questions, name, defaultModel)
                : new TypeSafeAgent(client, name: name, defaultModel: defaultModel);
        };

        services.Add(new ServiceDescriptor(typeof(TypeSafeAgent), factory, lifetime));
        return services;
    }

    /// <summary>
    /// Registers a keyed <see cref="TypeSafeAgent"/> instance with the specified lifetime.
    /// </summary>
    public static IServiceCollection AddKeyedTypeSafeAgent(
        this IServiceCollection services,
        object serviceKey,
        Func<QuestionBuilder, QuestionBuilder>? questions = null,
        string name = "TypeSafeAgent",
        string? defaultModel = null,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);

        var factory = (IServiceProvider sp, object? _) =>
        {
            var client = sp.GetRequiredService<ITypeSafeClient>();
            return questions is not null
                ? new TypeSafeAgent(client, questions, name, defaultModel)
                : new TypeSafeAgent(client, name: name, defaultModel: defaultModel);
        };

        services.Add(new ServiceDescriptor(typeof(TypeSafeAgent), serviceKey, factory, lifetime));
        return services;
    }

    /// <summary>
    /// Registers a <see cref="TypeSafeAIContextProvider"/> instance with the specified lifetime.
    /// </summary>
    public static IServiceCollection AddTypeSafeAIContextProvider(
        this IServiceCollection services,
        Func<QuestionBuilder, QuestionBuilder> questions,
        TypeSafeContextInjectionMode mode = TypeSafeContextInjectionMode.Instructions,
        string? model = null,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(questions);

        var factory = (IServiceProvider sp) =>
        {
            var client = sp.GetRequiredService<ITypeSafeClient>();
            return new TypeSafeAIContextProvider(client, questions, mode, model);
        };

        services.Add(new ServiceDescriptor(typeof(TypeSafeAIContextProvider), factory, lifetime));
        return services;
    }

    /// <summary>
    /// Wraps the specified <see cref="AgentSkillsSource"/> with semantic shortlisting via <see cref="TypeSafeSkillsSource"/>.
    /// </summary>
    /// <param name="source">The underlying skills source.</param>
    /// <param name="client">The TypeSafe client used for inference.</param>
    /// <param name="topK">The maximum number of skills to retain per turn. Default is 5.</param>
    /// <param name="minimumRelevanceProbability">Optional threshold below which skills are pruned.</param>
    /// <param name="fallbackMode">Fallback mode when inference fails.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <returns>A new <see cref="TypeSafeSkillsSource"/> instance decorating <paramref name="source"/>.</returns>
    public static TypeSafeSkillsSource UseTypeSafeShortlisting(
        this AgentSkillsSource source,
        ITypeSafeClient client,
        int topK = 5,
        double? minimumRelevanceProbability = 0.50,
        TypeSafeRelevanceFallbackMode fallbackMode = TypeSafeRelevanceFallbackMode.Throw,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(client);

        return new TypeSafeSkillsSource(
            source,
            client,
            new TypeSafeSkillsOptions
            {
                TopK = topK,
                MinimumRelevanceProbability = minimumRelevanceProbability,
                FallbackMode = fallbackMode
            },
            loggerFactory);
    }

    /// <summary>
    /// Wraps the specified <see cref="AgentSkillsSource"/> with semantic shortlisting via <see cref="TypeSafeSkillsSource"/>.
    /// </summary>
    /// <param name="source">The underlying skills source.</param>
    /// <param name="client">The TypeSafe client used for inference.</param>
    /// <param name="options">Configuration options controlling shortlisting behavior.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <returns>A new <see cref="TypeSafeSkillsSource"/> instance decorating <paramref name="source"/>.</returns>
    public static TypeSafeSkillsSource UseTypeSafeShortlisting(
        this AgentSkillsSource source,
        ITypeSafeClient client,
        TypeSafeSkillsOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        return new TypeSafeSkillsSource(source, client, options, loggerFactory);
    }

    /// <summary>
    /// Registers a decorated <see cref="TypeSafeSkillsSource"/> in the service collection.
    /// </summary>
    public static IServiceCollection AddTypeSafeSkillsSource<TInnerSource>(
        this IServiceCollection services,
        TypeSafeSkillsOptions? options = null,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
        where TInnerSource : AgentSkillsSource
    {
        ArgumentNullException.ThrowIfNull(services);

        var factory = (IServiceProvider sp) =>
        {
            var inner = sp.GetRequiredService<TInnerSource>();
            var client = sp.GetRequiredService<ITypeSafeClient>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new TypeSafeSkillsSource(inner, client, options, loggerFactory, ownsInnerSource: false);
        };

        services.Add(new ServiceDescriptor(typeof(TypeSafeSkillsSource), factory, lifetime));
        services.Add(new ServiceDescriptor(typeof(AgentSkillsSource), sp => sp.GetRequiredService<TypeSafeSkillsSource>(), lifetime));
        return services;
    }

    /// <summary>
    /// Registers a <see cref="TypeSafeToolSelectionProvider"/> in the service collection.
    /// </summary>
    public static IServiceCollection AddTypeSafeToolSelectionProvider(
        this IServiceCollection services,
        TypeSafeToolSelectionOptions? options = null,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(services);

        var factory = (IServiceProvider sp) =>
        {
            var client = sp.GetRequiredService<ITypeSafeClient>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new TypeSafeToolSelectionProvider(client, options, loggerFactory);
        };

        services.Add(new ServiceDescriptor(typeof(TypeSafeToolSelectionProvider), factory, lifetime));
        services.Add(new ServiceDescriptor(typeof(AIContextProvider), sp => sp.GetRequiredService<TypeSafeToolSelectionProvider>(), lifetime));
        return services;
    }

    /// <summary>
    /// Adds dynamic tool shortlisting via <see cref="TypeSafeToolSelectionProvider"/> to the chat client builder pipeline.
    /// </summary>
    public static ChatClientBuilder UseTypeSafeToolShortlisting(
        this ChatClientBuilder builder,
        ITypeSafeClient client,
        int topK = 5,
        double? minimumRelevanceProbability = 0.50,
        double? dropOffRatio = null,
        bool includeStructuredMetadata = true,
        TypeSafeRelevanceFallbackMode fallbackMode = TypeSafeRelevanceFallbackMode.Throw,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);

        var options = new TypeSafeToolSelectionOptions
        {
            TopK = topK,
            MinimumRelevanceProbability = minimumRelevanceProbability,
            DropOffRatio = dropOffRatio,
            IncludeStructuredMetadata = includeStructuredMetadata,
            FallbackMode = fallbackMode
        };

        var provider = new TypeSafeToolSelectionProvider(client, options, loggerFactory);
        return builder.UseAIContextProviders(provider);
    }

    /// <summary>
    /// Adds dynamic tool shortlisting via <see cref="TypeSafeToolSelectionProvider"/> using configured options.
    /// </summary>
    public static ChatClientBuilder UseTypeSafeToolShortlisting(
        this ChatClientBuilder builder,
        ITypeSafeClient client,
        TypeSafeToolSelectionOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var provider = new TypeSafeToolSelectionProvider(client, options, loggerFactory);
        return builder.UseAIContextProviders(provider);
    }
}

