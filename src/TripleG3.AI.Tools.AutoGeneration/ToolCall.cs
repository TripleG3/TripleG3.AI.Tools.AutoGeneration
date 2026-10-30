namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>Transport-neutral representation of a model tool call.</summary>
public sealed record ToolCall
{
    public ToolCall(string name, string callId, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(argumentsJson);
        Name = name;
        CallId = callId;
        ArgumentsJson = argumentsJson;
    }

    public string Name { get; }
    public string CallId { get; }
    public string ArgumentsJson { get; }
}

/// <summary>Transport-neutral result produced by a generated tool invocation.</summary>
public sealed record ToolInvocationResult(
    string CallId,
    string ToolName,
    object? Result,
    string SerializedResult,
    ToolInvocationState State)
{
    public string EffectiveArgumentsJson { get; init; } = "{}";
}
