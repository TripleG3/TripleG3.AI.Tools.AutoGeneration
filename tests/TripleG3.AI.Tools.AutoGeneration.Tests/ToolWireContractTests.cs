namespace TripleG3.AI.Tools.AutoGeneration.Tests;

public sealed class ToolWireContractTests
{
    [Theory]
    [InlineData("""{"text":"ok","unexpected":true}""")]
    [InlineData("""{"text":"one","text":"two"}""")]
    [InlineData("""{"Text":"wrong case"}""")]
    [InlineData("""{"text":null}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public async Task InvalidArguments_AreRejectedBeforeCallingTheHandler(string json)
    {
        int calls = 0;
        await using ToolBinding binding = ToolBindingTests.EchoDefinition().BindHandler((_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult<object?>("unexpected");
        });
        await Assert.ThrowsAnyAsync<JsonException>(() => binding.InvokeAsync(new("echo", "call", json)).AsTask());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CSharpDefaultsAreAppliedButExplicitValuesAreNeverRewritten()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<int, CancellationToken, int>)Fixtures.Defaults).Single();
        await using ToolBinding binding = tool.BindHandler((_, args, _) => ValueTask.FromResult<object?>(args.Get<int>("count")));
        Assert.Equal(5, (await binding.InvokeAsync(new("defaults", "default", "{}"))).Result);
        Assert.Equal(0, (await binding.InvokeAsync(new("defaults", "explicit", """{"count":0}"""))).Result);
    }

    [Fact]
    public async Task CustomEnumWireNames_AreIdenticalInSchemaBindingAndResult()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<Mode, CancellationToken, Mode>)Fixtures.Enum).Single();
        using JsonDocument schema = JsonDocument.Parse(tool.ParametersJson);
        Assert.Contains(schema.RootElement.GetProperty("properties").GetProperty("mode").GetProperty("enum").EnumerateArray(),
            value => value.GetString() == "no-overwrite");
        await using ToolBinding binding = tool.BindHandler((_, args, _) => ValueTask.FromResult<object?>(args.Get<Mode>("mode")));
        ToolInvocationResult result = await binding.InvokeAsync(new("enum", "call", """{"mode":"no-overwrite"}"""));
        Assert.Equal(Mode.NoOverwrite, result.Result);
        Assert.Equal("\"no-overwrite\"", result.SerializedResult);
        await Assert.ThrowsAnyAsync<JsonException>(() => binding.InvokeAsync(new("enum", "bad", """{"mode":0}""")).AsTask());
        await Assert.ThrowsAnyAsync<JsonException>(() => binding.InvokeAsync(new("enum", "bad", """{"mode":"NoOverwrite"}""")).AsTask());
    }

    [Fact]
    public async Task BinaryData_IsDecodedFromBase64AndSerializedToTheSameShape()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<BinaryData, CancellationToken, BinaryData>)Fixtures.Binary).Single();
        await using ToolBinding binding = tool.BindHandler((_, args, _) =>
        {
            BinaryData bytes = args.Get<BinaryData>("data");
            Assert.Equal("hello", bytes.ToString());
            return ValueTask.FromResult<object?>(bytes);
        });
        ToolInvocationResult result = await binding.InvokeAsync(new("binary", "call", """{"data":"aGVsbG8="}"""));
        Assert.Equal("\"aGVsbG8=\"", result.SerializedResult);
        await Assert.ThrowsAnyAsync<JsonException>(() => binding.InvokeAsync(new("binary", "bad", """{"data":{"unencoded":true}}""")).AsTask());
        await Assert.ThrowsAnyAsync<JsonException>(() => binding.InvokeAsync(new("binary", "bad", """{"data":"not base64"}""")).AsTask());
    }

    [Fact]
    public async Task NestedRecords_EnforceDeclaredRequiredPropertiesAndNullability()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<Request, CancellationToken, Request>)Fixtures.Nested).Single();
        await using ToolBinding binding = tool.BindHandler((_, args, _) => ValueTask.FromResult<object?>(args.Get<Request>("request")));
        ToolInvocationResult result = await binding.InvokeAsync(new("nested", "call", """{"request":{"display_name":"hello","modes":["no-overwrite",null]}}"""));
        Assert.Equal("hello", Assert.IsType<Request>(result.Result).Name);
        using JsonDocument response = JsonDocument.Parse(result.SerializedResult);
        Assert.Equal("hello", response.RootElement.GetProperty("display_name").GetString());
        Assert.Equal("no-overwrite", response.RootElement.GetProperty("modes")[0].GetString());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("modes")[1].ValueKind);
        foreach (string invalid in new[]
        {
            """{"request":{"modes":[]}}""",
            """{"request":{"display_name":null,"modes":[]}}""",
            """{"request":{"display_name":"hello","modes":[],"extra":true}}""",
            """{"request":{"display_name":"hello","display_name":"again","modes":[]}}"""
        })
            await Assert.ThrowsAnyAsync<JsonException>(() => binding.InvokeAsync(new("nested", "invalid", invalid)).AsTask());
    }

    [Fact]
    public async Task NullableValuesAndGenericJson_AreNotRestrictedToObjects()
    {
        GeneratedTool json = ResponseToolDiscovery.CreateTools((Func<JsonElement, CancellationToken, JsonElement>)Fixtures.Json).Single();
        ToolInvocationResult result = await json.InvokeAsync(new ToolCall("json", "call", """{"value":[true,"text",null]}"""));
        Assert.Equal("[true,\"text\",null]", result.SerializedResult);
        GeneratedTool nullable = ResponseToolDiscovery.CreateTools((Func<Mode?, CancellationToken, Mode?>)Fixtures.Nullable).Single();
        Assert.Null((await nullable.InvokeAsync(new ToolCall("nullable", "call", """{"value":null}"""))).Result);
    }

    [Fact]
    public void MisleadingOptionalMetadataAndIncompatibleSchemaTypes_FailDiscovery()
    {
        Assert.Throws<InvalidOperationException>(() => ResponseToolDiscovery.CreateTools((Func<string, CancellationToken, string>)Fixtures.MissingDefault));
        Assert.Throws<InvalidOperationException>(() => ResponseToolDiscovery.CreateTools((Func<int, CancellationToken, int>)Fixtures.WrongType));
    }

    [Fact]
    public async Task CancellationToken_IsInjectedButNeverAdvertisedOrTakenFromJson()
    {
        using CancellationTokenSource source = new();
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<string, CancellationToken, string>)Fixtures.Token).Single();
        Assert.DoesNotContain("cancellationToken", tool.ParametersJson, StringComparison.Ordinal);
        Assert.Equal("value", (await tool.InvokeAsync(new ToolCall("token", "id", """{"value":"value"}"""), cancellationToken: source.Token)).Result);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync(
            new ToolCall("token", "cancelled", """{"value":"value"}"""), cancellationToken: source.Token).AsTask());
        await Assert.ThrowsAnyAsync<JsonException>(() => tool.InvokeAsync(
            new ToolCall("token", "invalid", """{"value":"value","cancellationToken":null}""")).AsTask());
    }

    [Fact]
    public async Task ParameterBindingCanBeUsedWithoutExecutingAnyImplementation()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<int, CancellationToken, int>)Fixtures.Defaults).Single();
        FunctionCallResponseItem call = ResponseItem.CreateFunctionCallItem("call", "defaults", BinaryData.FromString("{}"));
        object?[] values = call.ToParameters(tool);
        Assert.Equal(5, values[0]);
        Assert.IsType<CancellationToken>(values[1]);
        await using ToolBinding binding = tool.BindHandler((_, arguments, _) => ValueTask.FromResult<object?>(arguments.Get<int>("count")));
        Assert.Equal("5", (await binding.InvokeAsync(new("defaults", "call", "{}"))).SerializedResult);
    }

    [Fact]
    public async Task PlainAndEnvelopeFormats_AreExplicitAndShareTheResultSchema()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools(
            (Func<string, bool, CancellationToken, ToolBindingTests.CopyResult>)ToolBindingTests.Fixtures.StaticCopy).Single();
        await using ToolBinding plain = tool.BindHandler((_, _, _) => ValueTask.FromResult<object?>(new ToolBindingTests.CopyResult("done", 5)), ToolResultFormat.Plain);
        await using ToolBinding envelope = tool.BindHandler((_, _, _) => ValueTask.FromResult<object?>(new ToolBindingTests.CopyResult("done", 5)), ToolResultFormat.SchemaEnvelope);
        ToolCall call = new("static_copy", "call", """{"path":"fixture"}""");
        using JsonDocument direct = JsonDocument.Parse((await plain.InvokeAsync(call)).SerializedResult);
        using JsonDocument wrapped = JsonDocument.Parse((await envelope.InvokeAsync(call)).SerializedResult);
        Assert.True(JsonElement.DeepEquals(direct.RootElement, wrapped.RootElement.GetProperty("result")));
        Assert.Equal(plain.Descriptor.ResultSchemaJson, envelope.Descriptor.ResultSchemaJson);
        Assert.NotEqual(plain.Descriptor.Fingerprint, envelope.Descriptor.Fingerprint);
    }

    [Fact]
    public async Task NonNullableResultsCannotBeReplacedByNull()
    {
        await using ToolBinding binding = ToolBindingTests.EchoDefinition().BindHandler((_, _, _) => ValueTask.FromResult<object?>(null));
        await Assert.ThrowsAnyAsync<JsonException>(() => binding.InvokeAsync(new("echo", "call", """{"text":"value"}""")).AsTask());
    }

    [Fact]
    public async Task ExactRequiredMetadataWinsOverAnOptionalSignature()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<int, CancellationToken, int>)Fixtures.RequiredDefault).Single();
        await Assert.ThrowsAnyAsync<JsonException>(() => tool.InvokeAsync(new ToolCall("required_default", "call", "{}")).AsTask());
        Assert.Equal(9, (await tool.InvokeAsync(new ToolCall("required_default", "call", """{"value":9}"""))).Result);
    }

    public enum Mode
    {
        [JsonStringEnumMemberName("no-overwrite")] NoOverwrite,
        [JsonStringEnumMemberName("overwrite")] Overwrite
    }

    public sealed record Request([property: JsonPropertyName("display_name")] string Name, IReadOnlyList<Mode?> Modes);

    public static class Fixtures
    {
        [Tool("defaults", "Default count.", "Count", true)]
        public static int Defaults(int count = 5, CancellationToken cancellationToken = default) => count;
        [Tool("required_default", "Explicit required metadata.", "Value", true)]
        public static int RequiredDefault([ToolParameter("value", "Required even with a language default.")] int value = 5, CancellationToken cancellationToken = default) => value;
        [Tool("enum", "Return mode.", "Mode", true)]
        public static Mode Enum(Mode mode, CancellationToken cancellationToken) => mode;
        [Tool("nullable", "Return nullable mode.", "Mode", true)]
        public static Mode? Nullable(Mode? value, CancellationToken cancellationToken) => value;
        [Tool("binary", "Return data.", "Data", true)]
        public static BinaryData Binary(BinaryData data, CancellationToken cancellationToken) => data;
        [Tool("nested", "Return request.", "Request", true)]
        public static Request Nested(Request request, CancellationToken cancellationToken) => request;
        [Tool("json", "Return JSON.", "JSON", true)]
        public static JsonElement Json(JsonElement value, CancellationToken cancellationToken) => value;
        [Tool("missing_default", "Invalid metadata.", "Invalid", true)]
        public static string MissingDefault([ToolParameter("value", "Value.", false)] string value, CancellationToken cancellationToken) => value;
        [Tool("wrong_type", "Invalid type.", "Invalid", true)]
        public static int WrongType([ToolParameter<string>("value", "Value.")] int value, CancellationToken cancellationToken) => value;
        [Tool("token", "Return value.", "Token", true)]
        public static string Token(string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }
    }
}
