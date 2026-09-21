using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// A high-performance, NativeAOT-compliant <see cref="AIFunction"/> implementation
/// for calling TypeSafe System One operations with zero reflection.
/// </summary>
public sealed class TypeSafeFunction : AIFunction
{
    private readonly Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> _invoker;

    /// <summary>Initializes a new instance of <see cref="TypeSafeFunction"/>.</summary>
    public TypeSafeFunction(
        string name,
        string description,
        JsonElement jsonSchema,
        Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> invoker,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(invoker);

        Name = name;
        Description = description;
        JsonSchema = jsonSchema;
        _invoker = invoker;
        JsonSerializerOptions = serializerOptions ?? AgentFrameworkJsonContext.Default.Options;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string Description { get; }

    /// <inheritdoc />
    public override JsonElement JsonSchema { get; }

    /// <inheritdoc />
    public override JsonSerializerOptions JsonSerializerOptions { get; }

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _invoker(arguments, cancellationToken);
    }
}
