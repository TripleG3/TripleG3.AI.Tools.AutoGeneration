namespace TripleG3.AI.Tools.AutoGeneration;

public enum ToolResultFormat { Automatic, Plain, SchemaEnvelope }

public static class ToolResultJsonSerializer
{
    public static string Serialize(object? result, MethodInfo method, ToolResultFormat format = ToolResultFormat.Automatic)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        Type type = Unwrap(method.ReturnType);
        if (type == typeof(void))
        {
            if (result is not null) throw new InvalidOperationException("A void tool handler must return null.");
        }
        else if (result is not null && !(Nullable.GetUnderlyingType(type) ?? type).IsInstanceOfType(result))
            throw new InvalidOperationException($"The tool handler result is not assignable to '{type}'.");
        JsonElement value = type == typeof(void)
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(result, type, ToolJsonSerialization.Options);
        JsonObject schema = CreateSchema(method);
        using JsonDocument schemaDocument = JsonDocument.Parse(schema.ToJsonString());
        ToolSchemaValidator.Validate(value, schemaDocument.RootElement, "result");
        return UsesEnvelope(method, format)
            ? new JsonObject { ["schema"] = schema, ["result"] = JsonNode.Parse(value.GetRawText()) }.ToJsonString(ToolJsonSerialization.Options)
            : value.GetRawText();
    }

    public static JsonObject CreateSchema(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        Type type = Unwrap(method.ReturnType);
        NullabilityInfo nullable = new NullabilityInfoContext().Create(method.ReturnParameter);
        if (method.ReturnType.IsGenericType && method.ReturnType != type)
            nullable = nullable.GenericTypeArguments[0];
        JsonObject schema = type == typeof(void) ? new() { ["type"] = "null" } :
            ToolJsonSchemaBuilder.CreateSchema(type, null, nullable, [], output: true);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return schema;
    }

    internal static bool UsesEnvelope(MethodInfo method, ToolResultFormat format) => format switch
    {
        ToolResultFormat.Plain => false,
        ToolResultFormat.SchemaEnvelope => true,
        ToolResultFormat.Automatic => HasMetadata(Unwrap(method.ReturnType), []),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static Type Unwrap(Type type) => type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask)
        ? typeof(void) : type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? type.GetGenericArguments()[0] : type;

    private static bool HasMetadata(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(void)) return false;
        if (type.GetCustomAttribute<ToolResultAttribute>(true) is not null) return true;
        if (!visited.Add(type)) return false;
        if (ToolJsonSchemaBuilder.TryDictionary(type, out Type value)) return HasMetadata(value, visited);
        if (ToolJsonSchemaBuilder.TryEnumerable(type, out Type item)) return HasMetadata(item, visited);
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Any(property =>
            property.GetCustomAttribute<ToolResultAttribute>(true) is not null || HasMetadata(property.PropertyType, visited));
    }
}
