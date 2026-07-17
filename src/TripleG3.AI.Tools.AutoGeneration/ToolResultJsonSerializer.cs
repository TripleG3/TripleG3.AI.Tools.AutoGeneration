using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TripleG3.AI.Tools.AutoGeneration;

public static class ToolResultJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    public static string Serialize(object? result, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method); Type type = Unwrap(method.ReturnType); if (type == typeof(object) && result is not null) type = result.GetType();
        if (!HasMetadata(type, [])) return JsonSerializer.Serialize(result, Options);
        return new JsonObject { ["schema"] = CreateSchema(type), ["result"] = result is null ? null : JsonSerializer.SerializeToNode(result, result.GetType(), Options) }.ToJsonString();
    }
    private static JsonObject CreateSchema(Type type) { JsonObject schema = ToolJsonSchemaBuilder.CreateSchema(type, null, null, []); schema["$schema"] = "https://json-schema.org/draft/2020-12/schema"; return schema; }
    private static Type Unwrap(Type type) => type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask) ? typeof(void) : type.IsGenericType && type.GetGenericTypeDefinition() is var definition && (definition == typeof(Task<>) || definition == typeof(ValueTask<>)) ? type.GetGenericArguments()[0] : type;
    private static bool HasMetadata(Type type, HashSet<Type> visited) { type = Nullable.GetUnderlyingType(type) ?? type; if (type == typeof(void)) return false; if (type.GetCustomAttribute<ToolResultAttribute>(true) is not null) return true; if (ToolJsonSchemaBuilder.TryDictionary(type, out Type value)) return HasMetadata(value, visited); if (ToolJsonSchemaBuilder.TryEnumerable(type, out Type item)) return HasMetadata(item, visited); if (!visited.Add(type)) return false; try { return type.GetMembers(BindingFlags.Instance | BindingFlags.Public).OfType<PropertyInfo>().Any(property => property.GetCustomAttribute<ToolResultAttribute>(true) is not null || HasMetadata(property.PropertyType, visited)); } finally { visited.Remove(type); } }
}