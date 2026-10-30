using OpenAI.Responses;

namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>Resolves an instance-method target for a generated tool invocation.</summary>
public delegate ValueTask<object?> ToolTargetResolver(
    GeneratedTool tool,
    FunctionCallResponseItem call,
    CancellationToken cancellationToken);

public static class ToolTargetResolvers
{
    public static ToolTargetResolver Default { get; } = ResolveDefaultAsync;

    public static ToolTargetResolver FromServiceProvider(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return (tool, call, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentNullException.ThrowIfNull(call);
            cancellationToken.ThrowIfCancellationRequested();

            if (tool.TargetInstance is not null)
            {
                return ValueTask.FromResult<object?>(tool.TargetInstance);
            }

            if (tool.MethodInfo.IsStatic || tool.TargetType is null)
            {
                return ValueTask.FromResult<object?>(null);
            }

            object target = services.GetService(tool.TargetType)
                ?? throw new InvalidOperationException(
                    $"Tool '{tool.ResponseTool.FunctionName}' requires target type '{tool.TargetType.FullName ?? tool.TargetType.Name}', but the service provider did not return an instance for that type.");
            return ValueTask.FromResult<object?>(target);
        };
    }

    public static ToolTargetResolver FromLookup(Func<GeneratedTool, FunctionCallResponseItem, object?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        return (tool, call, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(lookup(tool, call));
        };
    }

    private static ValueTask<object?> ResolveDefaultAsync(
        GeneratedTool tool,
        FunctionCallResponseItem call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return tool.TargetInstance is not null
            ? ValueTask.FromResult<object?>(tool.TargetInstance)
            : tool.MethodInfo.IsStatic || tool.TargetType is null
                ? ValueTask.FromResult<object?>(null)
                : ValueTask.FromResult(Activator.CreateInstance(tool.TargetType));
    }
}