using System.Security.Cryptography;
using System.Text;

namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>Portable wire metadata. Deserializing this record never loads or invokes a .NET method.</summary>
public sealed record ToolDescriptor(string Name, string Description, int SchemaVersion, string ParametersJson,
    string ResultSchemaJson, string ResultEnvelopeSchemaJson, ToolResultFormat ResultFormat, string Fingerprint)
{
    internal static ToolDescriptor Create(GeneratedTool tool, ToolResultFormat format)
    {
        using JsonDocument parameters = JsonDocument.Parse(tool.ParametersJson);
        string inputSchema = ToolJsonSerialization.Canonicalize(parameters.RootElement);
        JsonObject result = ToolResultJsonSerializer.CreateSchema(tool.MethodInfo);
        bool envelope = ToolResultJsonSerializer.UsesEnvelope(tool.MethodInfo, format);
        JsonObject wire = envelope ? new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                ["result"] = result.DeepClone()
            },
            ["required"] = new JsonArray("schema", "result"),
            ["additionalProperties"] = false
        } : (JsonObject)result.DeepClone();
        using JsonDocument outputSchema = JsonDocument.Parse(result.ToJsonString());
        using JsonDocument wireSchema = JsonDocument.Parse(wire.ToJsonString());
        ToolResultFormat effectiveFormat = envelope ? ToolResultFormat.SchemaEnvelope : ToolResultFormat.Plain;
        ToolDescriptor descriptor = new(tool.Name, tool.Description, tool.SchemaVersion, inputSchema,
            ToolJsonSerialization.Canonicalize(outputSchema.RootElement), ToolJsonSerialization.Canonicalize(wireSchema.RootElement),
            effectiveFormat, string.Empty);
        string canonical = ToolJsonSerialization.Canonicalize(JsonSerializer.SerializeToElement(descriptor, ToolJsonSerialization.Options));
        return descriptor with { Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) };
    }

    public void RequireMatch(ToolDescriptor current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (this != current) throw new InvalidOperationException($"The registered contract for tool '{Name}' no longer matches its pinned descriptor.");
    }
}
