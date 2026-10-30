using System.Globalization;

namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>The shared wire contract for tool schemas, bound arguments, and results.</summary>
public static class ToolJsonSerialization
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Canonicalize(JsonElement value)
    {
        JsonNode? Order(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => new JsonObject(element.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => KeyValuePair.Create(property.Name, Order(property.Value)))),
            JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(Order).ToArray()),
            _ => JsonNode.Parse(element.GetRawText())
        };
        return Order(value)?.ToJsonString(Options) ?? "null";
    }

    internal static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException($"Duplicate tool argument property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false),
                new BinaryDataConverter(),
                new TimeSpanConverter()
            }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class BinaryDataConverter : JsonConverter<BinaryData>
    {
        public override BinaryData Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? BinaryData.FromBytes(reader.GetBytesFromBase64())
                : throw new JsonException("BinaryData tool values must be base64 strings.");
        public override void Write(Utf8JsonWriter writer, BinaryData value, JsonSerializerOptions options) =>
            writer.WriteBase64StringValue(value.ToMemory().Span);
    }

    private sealed class TimeSpanConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? TimeSpan.Parse(reader.GetString()!, CultureInfo.InvariantCulture)
                : throw new JsonException("TimeSpan tool values must be invariant strings.");
        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
    }
}

internal static class ToolSchemaValidator
{
    // Validates the subset emitted by ToolJsonSchemaBuilder; not an arbitrary JSON Schema engine.
    internal static void Validate(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            IEnumerable<string?> types = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(item => item.GetString())
                : [type.GetString()];
            bool accepted = types.Any(name => name switch
            {
                "null" => value.ValueKind == JsonValueKind.Null,
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) && decimal.Truncate(number) == number,
                "number" => value.ValueKind == JsonValueKind.Number,
                _ => throw new InvalidOperationException($"Unsupported generated schema type '{name}'.")
            });
            if (!accepted) throw new JsonException($"Tool value '{path}' does not match its declared type.");
        }
        if (schema.TryGetProperty("contentEncoding", out JsonElement encoding) && encoding.GetString() == "base64" &&
            value.ValueKind == JsonValueKind.String)
        {
            try { _ = Convert.FromBase64String(value.GetString()!); }
            catch (FormatException exception) { throw new JsonException($"Tool value '{path}' is not base64.", exception); }
        }
        if (schema.TryGetProperty("enum", out JsonElement choices) &&
            !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value)))
            throw new JsonException($"Tool value '{path}' is not a declared enum value.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required))
                foreach (JsonElement name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _))
                        throw new JsonException($"Tool value '{path}' is missing required property '{name.GetString()}'.");
            bool hasProperties = schema.TryGetProperty("properties", out JsonElement properties);
            bool hasAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additional);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                    Validate(property.Value, propertySchema, $"{path}.{property.Name}");
                else if (hasAdditional && additional.ValueKind == JsonValueKind.False)
                    throw new JsonException($"Unknown tool value '{path}.{property.Name}'.");
                else if (hasAdditional && additional.ValueKind == JsonValueKind.Object)
                    Validate(property.Value, additional, $"{path}.{property.Name}");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray()) Validate(item, items, $"{path}[{index++}]");
        }
    }
}
