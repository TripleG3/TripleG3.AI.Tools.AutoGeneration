namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>Arguments owned by one invocation, with declaration defaults applied and host tokens excluded.</summary>
public sealed class ToolArguments
{
    private readonly object?[] parameters;

    internal ToolArguments(ImmutableDictionary<string, object?> values, object?[] parameters, string effectiveJson)
    {
        Values = values;
        this.parameters = parameters;
        EffectiveArgumentsJson = effectiveJson;
    }

    public ImmutableDictionary<string, object?> Values { get; }
    public string EffectiveArgumentsJson { get; }

    public T Get<T>(string name)
    {
        if (!Values.TryGetValue(name, out object? value))
            throw new KeyNotFoundException($"No bound tool argument '{name}'.");
        if (value is T typed) return typed;
        if (value is null && default(T) is null) return default!;
        throw new InvalidOperationException($"Tool argument '{name}' is not assignable to '{typeof(T)}'.");
    }

    internal object?[] CopyParameters() => (object?[])parameters.Clone();
}

public static class ToolArgumentBinder
{
    public static ToolArguments Bind(GeneratedTool tool, ToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(call);
        tool.ValidateCall(call);
        using JsonDocument declared = JsonDocument.Parse(tool.ParametersJson);
        using JsonDocument input = JsonDocument.Parse(call.ArgumentsJson);
        ToolSchemaValidator.Validate(input.RootElement, declared.RootElement, "arguments");
        return Bind(tool.MethodInfo, call, cancellationToken);
    }

    internal static ToolArguments Bind(MethodInfo method, ToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolAttribute? attribute = method.GetCustomAttribute<ToolAttribute>();
        if (attribute is not null && attribute.Name != call.Name && !(attribute.Aliases ?? []).Contains(call.Name, StringComparer.Ordinal))
            throw new InvalidOperationException($"Tool call '{call.Name}' does not match '{method.Name}'.");
        using JsonDocument document = JsonDocument.Parse(call.ArgumentsJson);
        JsonElement input = document.RootElement;
        if (input.ValueKind != JsonValueKind.Object) throw new JsonException("Tool arguments must be an object.");
        ToolJsonSerialization.RejectDuplicateProperties(input);
        JsonObject parameterSchema = ResponseToolDiscovery.CreateParameterSchema(method);
        using JsonDocument schema = JsonDocument.Parse(parameterSchema.ToJsonString());
        ToolSchemaValidator.Validate(input, schema.RootElement, "arguments");

        ParameterInfo[] parameters = method.GetParameters();
        ToolParameterAttribute[] methodAttributes = [.. method.GetCustomAttributes<ToolParameterAttribute>(true)];
        object?[] bound = new object?[parameters.Length];
        ImmutableDictionary<string, object?>.Builder values = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        JsonObject normalized = new();
        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            if (ToolParameterMetadata.Skip(parameter))
            {
                bound[index] = cancellationToken;
                continue;
            }
            ToolParameterAttribute? metadata = ToolParameterMetadata.GetAttribute(methodAttributes, parameter);
            string name = ToolParameterMetadata.GetName(parameter, metadata);
            Type type = ToolParameterMetadata.GetConversionType(parameter, metadata);
            if (input.TryGetProperty(name, out JsonElement argument))
                bound[index] = argument.Deserialize(type, ToolJsonSerialization.Options);
            else if (parameter.HasDefaultValue && parameter.DefaultValue is not DBNull)
                bound[index] = parameter.DefaultValue;
            else
                throw new JsonException($"Tool argument '{name}' has no value or usable declaration default.");
            if (bound[index] is { } value && !(Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType).IsInstanceOfType(value))
                throw new JsonException($"Tool argument '{name}' is incompatible with the method parameter.");
            values.Add(name, bound[index]);
            normalized.Add(name, JsonSerializer.SerializeToNode(bound[index], type, ToolJsonSerialization.Options));
        }
        using JsonDocument effective = JsonDocument.Parse(normalized.ToJsonString());
        ToolSchemaValidator.Validate(effective.RootElement, schema.RootElement, "arguments");
        return new(values.ToImmutable(), bound, ToolJsonSerialization.Canonicalize(effective.RootElement));
    }
}
