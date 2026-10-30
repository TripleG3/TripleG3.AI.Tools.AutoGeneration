namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>A filtered, immutable projection of a generated tool catalog.</summary>
public sealed class ToolSet
{
    private readonly GeneratedTool[] _tools;
    private readonly Dictionary<string, GeneratedTool> _toolsByName;

    internal ToolSet(IEnumerable<GeneratedTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = [.. tools];
        _toolsByName = _tools.ToDictionary(
            tool => tool.ResponseTool.FunctionName,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<GeneratedTool> Tools => Array.AsReadOnly(_tools);

    public ImmutableArray<ToolDescriptor> DescribeTools(ToolResultFormat format = ToolResultFormat.Automatic) =>
        [.. _tools.Select(tool => tool.Describe(format))];

    public GeneratedTool[] CreateTools() => [.. _tools];

    public FunctionTool[] CreateResponsesTools() =>
        [.. _tools.Select(tool => tool.ToResponsesTool())];

    public OpenAI.Realtime.RealtimeFunctionTool[] CreateRealtimeTools() =>
        [.. _tools.Select(tool => tool.ToRealtimeTool())];

    public bool TryGetTool(string name, out GeneratedTool tool) =>
        _toolsByName.TryGetValue(name, out tool!);
}
