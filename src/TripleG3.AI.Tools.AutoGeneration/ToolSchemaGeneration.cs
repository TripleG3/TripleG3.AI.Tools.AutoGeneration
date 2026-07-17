using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TripleG3.AI.Tools.AutoGeneration;

public static class ToolEnumSchemaMetadata
{
    public const string EnumDescriptionsSchemaPropertyName = "x-enum-descriptions";

    public static JsonObject CreateEnumSchema(Type enumType)
    {
        Type type = Nullable.GetUnderlyingType(enumType) ?? enumType;
        if (!type.IsEnum) throw new ArgumentException("The supplied type must be an enum type.", nameof(enumType));
        JsonArray values = [];
        JsonObject descriptions = [];
        foreach (string name in Enum.GetNames(type))
        {
            string wireName = GetWireName(type, name);
            values.Add(wireName);
            if (GetDescription(type, name) is { } description) descriptions[wireName] = description;
        }
        JsonObject schema = new() { ["type"] = "string", ["enum"] = values };
        if (descriptions.Count > 0) schema[EnumDescriptionsSchemaPropertyName] = descriptions;
        return schema;
    }

    public static string? CreateEnumValueDescription(Type enumType)
    {
        Type type = Nullable.GetUnderlyingType(enumType) ?? enumType;
        if (!type.IsEnum) return null;
        string[] values = [.. Enum.GetNames(type).Select(name => (Name: GetWireName(type, name), Description: GetDescription(type, name))).Where(value => value.Description is not null).Select(value => $"{value.Name} — {value.Description}")];
        return values.Length == 0 ? null : $"Allowed values: {string.Join("; ", values)}";
    }

    public static string? GetDescription(Type enumType, string enumValueName) => (Nullable.GetUnderlyingType(enumType) ?? enumType).GetField(enumValueName, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<ToolEnumDescriptionAttribute>()?.Description;

    private static string GetWireName(Type enumType, string name) => enumType.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? name;
}

public static class ToolJsonSchemaBuilder
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) } };

    public static NullabilityInfo CreateNullabilityInfo(ParameterInfo parameter) => new NullabilityInfoContext().Create(parameter);
    public static JsonObject CreateParameterTypeSchema(Type type, NullabilityInfo? nullability = null) => CreateSchema(type, null, nullability, []);
    public static void AddDefaultValue(JsonObject schema, Type declaredType, object? defaultValue)
    {
        if (defaultValue is DBNull || ReferenceEquals(defaultValue, Type.Missing)) return;
        schema["default"] = defaultValue is null ? null : JsonSerializer.SerializeToNode(defaultValue, defaultValue.GetType(), Options);
    }

    internal static JsonObject CreateSchema(Type declaredType, MemberInfo? member, NullabilityInfo? nullability, HashSet<Type> visited)
    {
        Type type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        JsonObject schema = type == typeof(string) || type == typeof(char) || type == typeof(TimeOnly) || type == typeof(TimeSpan) ? new() { ["type"] = "string" }
            : type == typeof(bool) ? new() { ["type"] = "boolean" }
            : IsInteger(type) ? new() { ["type"] = "integer" }
            : IsNumber(type) ? new() { ["type"] = "number" }
            : type == typeof(DateTime) || type == typeof(DateTimeOffset) ? new() { ["type"] = "string", ["format"] = "date-time" }
            : type == typeof(DateOnly) ? new() { ["type"] = "string", ["format"] = "date" }
            : type == typeof(Guid) ? new() { ["type"] = "string", ["format"] = "uuid" }
            : type == typeof(Uri) ? new() { ["type"] = "string", ["format"] = "uri" }
            : type == typeof(byte[]) || type == typeof(BinaryData) ? new() { ["type"] = "string", ["contentEncoding"] = "base64" }
            : type.IsEnum ? ToolEnumSchemaMetadata.CreateEnumSchema(type)
            : TryDictionary(type, out Type? value) ? new() { ["type"] = "object", ["additionalProperties"] = CreateSchema(value, null, null, visited) }
            : TryEnumerable(type, out Type? item) ? new() { ["type"] = "array", ["items"] = CreateSchema(item, null, null, visited) }
            : CreateObjectSchema(type, visited);
        schema["x-dotnet-type"] = FormatType(declaredType);
        string? description = member?.GetCustomAttribute<ToolSchemaAttribute>(true)?.Description ?? member?.GetCustomAttribute<ToolResultAttribute>(true)?.Description ?? type.GetCustomAttribute<ToolSchemaAttribute>(true)?.Description ?? type.GetCustomAttribute<ToolResultAttribute>(true)?.Description;
        string? enumDescription = ToolEnumSchemaMetadata.CreateEnumValueDescription(type);
        if (!string.IsNullOrWhiteSpace(description) || enumDescription is not null) schema["description"] = Join(description, enumDescription);
        if (member?.GetCustomAttribute<DefaultValueAttribute>(true) is { } defaultValue) AddDefaultValue(schema, declaredType, defaultValue.Value);
        if (Nullable.GetUnderlyingType(declaredType) is not null || nullability?.ReadState == NullabilityState.Nullable)
        {
            if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out string? typeName) && typeName is not "null") schema["type"] = new JsonArray(typeName, "null");
        }
        return schema;
    }

    private static JsonObject CreateObjectSchema(Type type, HashSet<Type> visited)
    {
        JsonObject schema = new() { ["type"] = "object" };
        if (type == typeof(object) || type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type)) { schema["additionalProperties"] = true; return schema; }
        if (!visited.Add(type)) { schema["additionalProperties"] = true; return schema; }
        try
        {
            JsonObject properties = []; JsonArray required = []; bool extensionData = false;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(property => property.MetadataToken))
            {
                JsonIgnoreAttribute? ignored = property.GetCustomAttribute<JsonIgnoreAttribute>(true);
                if (property.GetMethod is null || property.GetIndexParameters().Length > 0 || ignored?.Condition == JsonIgnoreCondition.Always) continue;
                bool constructorBound = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(constructor => constructor.IsPublic || constructor.GetCustomAttribute<JsonConstructorAttribute>(true) is not null).SelectMany(constructor => constructor.GetParameters()).Any(parameter => string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(parameter.Name, GetName(property), StringComparison.OrdinalIgnoreCase));
                if (property.SetMethod is null && property.GetCustomAttribute<JsonIncludeAttribute>(true) is null && !constructorBound) continue;
                if (property.GetCustomAttribute<JsonExtensionDataAttribute>(true) is not null) { extensionData = true; continue; }
                string name = GetName(property); NullabilityInfo info = new NullabilityInfoContext().Create(property);
                properties[name] = CreateSchema(property.PropertyType, property, info, visited);
                bool constructorOptional = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(constructor => constructor.IsPublic || constructor.GetCustomAttribute<JsonConstructorAttribute>(true) is not null)
                    .SelectMany(constructor => constructor.GetParameters())
                    .Any(parameter => (string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)) && (parameter.HasDefaultValue || parameter.IsOptional));
                if (ignored?.Condition is not JsonIgnoreCondition.WhenWritingDefault and not JsonIgnoreCondition.WhenWritingNull && !constructorOptional && (property.GetCustomAttribute<JsonRequiredAttribute>(true) is not null || property.GetCustomAttribute<RequiredMemberAttribute>(true) is not null || (Nullable.GetUnderlyingType(property.PropertyType) is null && (property.PropertyType.IsValueType || info.ReadState == NullabilityState.NotNull)))) required.Add(name);
            }
            schema["properties"] = properties; schema["required"] = required; schema["additionalProperties"] = extensionData; return schema;
        }
        finally { visited.Remove(type); }
    }

    internal static bool TryDictionary(Type type, out Type value) { Type? found = type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) && candidate.GetGenericArguments()[0] == typeof(string)); value = found?.GetGenericArguments()[1] ?? typeof(object); return found is not null; }
    internal static bool TryEnumerable(Type type, out Type item) { if (type == typeof(string) || type == typeof(byte[])) { item = typeof(object); return false; } Type? found = type.IsArray ? null : type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)); item = type.IsArray ? type.GetElementType() ?? typeof(object) : found?.GetGenericArguments()[0] ?? typeof(object); return type.IsArray || found is not null; }
    internal static bool IsInteger(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) || type == typeof(nint) || type == typeof(nuint);
    internal static bool IsNumber(Type type) => type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    internal static string GetName(MemberInfo member) => member.GetCustomAttribute<JsonPropertyNameAttribute>(true)?.Name ?? Options.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;
    internal static string Join(string? first, string? second) => string.IsNullOrWhiteSpace(first) ? second ?? string.Empty : string.IsNullOrWhiteSpace(second) ? first.Trim() : $"{first.Trim()}{(first.TrimEnd().EndsWith('.') ? " " : ". ")}{second}";
    internal static string FormatType(Type type) => Nullable.GetUnderlyingType(type) is { } nullable ? $"{FormatType(nullable)}?" : type.IsArray ? $"{FormatType(type.GetElementType() ?? typeof(object))}[]" : !type.IsGenericType ? type.FullName ?? type.Name : $"{(type.FullName ?? type.Name).Split('`')[0]}<{string.Join(", ", type.GetGenericArguments().Select(FormatType))}>";
}