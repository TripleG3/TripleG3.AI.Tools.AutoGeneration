using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TripleG3.Cis;

namespace TripleG3.AI.Tools.AutoGeneration.Tests;

public sealed class ToolBindingTests
{
    [Fact]
    public async Task IndependentBindings_DoNotSerializeEachOtherOrShareState()
    {
        GeneratedTool definition = EchoDefinition();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ToolBinding first = definition.BindHandler(async (_, arguments, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return arguments.Get<string>("text");
        });
        await using ToolBinding second = definition.BindHandler((_, arguments, _) => ValueTask.FromResult<object?>(arguments.Get<string>("text")));
        Task<ToolInvocationResult> blocked = first.InvokeAsync(new("echo", "one", """{"text":"first"}""")).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            ToolInvocationResult completed = await second.InvokeAsync(new("echo", "two", """{"text":"second"}""")).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("second", completed.Result);
            Assert.Equal("two", second.State.Value.ToolCallId);
            Assert.NotEqual("two", first.State.Value.ToolCallId);
            Assert.False(typeof(IStateService<ToolInvocationState>).IsAssignableFrom(typeof(GeneratedTool)));
        }
        finally { release.TrySetResult(); }
        await blocked;
        Assert.Equal("one", first.State.Value.ToolCallId);
    }

    [Fact]
    public async Task OneBinding_SerializesItsOwnInvocationsAndClearsStateOnDispose()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> calls = [];
        ToolBinding binding = EchoDefinition().BindHandler(async (_, arguments, token) =>
        {
            string text = arguments.Get<string>("text");
            calls.Add(text);
            if (text == "first")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return text;
        });
        Task<ToolInvocationResult> first = binding.InvokeAsync(new("echo", "one", """{"text":"first"}""")).AsTask();
        await started.Task;
        Task<ToolInvocationResult> second = binding.InvokeAsync(new("echo", "two", """{"text":"second"}""")).AsTask();
        Assert.Single(calls);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(["first", "second"], calls);
        await binding.DisposeAsync();
        Assert.Equal(ToolInvocationState.Empty, binding.State.Value);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => binding.InvokeAsync(new("echo", "three", """{"text":"third"}""")).AsTask());
    }

    [Fact]
    public async Task Disposal_CancelsActiveAndQueuedWorkAndReleasesTheState()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int executed = 0;
        ToolBinding binding = EchoDefinition().BindHandler(async (_, _, token) =>
        {
            Interlocked.Increment(ref executed);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unexpected";
        });
        Task<ToolInvocationResult> first = binding.InvokeAsync(new("echo", "one", """{"text":"first"}""")).AsTask();
        await started.Task;
        Task<ToolInvocationResult> second = binding.InvokeAsync(new("echo", "two", """{"text":"second"}""")).AsTask();
        await binding.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, executed);
        Assert.Equal(ToolInvocationState.Empty, binding.State.Value);
    }

    [Fact]
    public async Task Handler_UsesSharedDefaultsAndOutputContractWithoutCallingStaticTarget()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<string, bool, CancellationToken, CopyResult>)Fixtures.StaticCopy).Single();
        await using ToolBinding binding = tool.BindHandler((call, arguments, _) =>
        {
            Assert.False(arguments.Get<bool>("overwrite"));
            Assert.Equal("fixture", arguments.Get<string>("path"));
            Assert.DoesNotContain("cancellationToken", arguments.EffectiveArgumentsJson, StringComparison.Ordinal);
            return ValueTask.FromResult<object?>(new CopyResult("copied", 5));
        });
        ToolInvocationResult result = await binding.InvokeAsync(new("static_copy", "copy", """{"path":"fixture"}"""));
        using JsonDocument output = JsonDocument.Parse(result.SerializedResult);
        Assert.Equal(5, output.RootElement.GetProperty("result").GetProperty("bytesCopied").GetInt32());
        Assert.Equal(result.SerializedResult, tool.SerializeResult(new CopyResult("copied", 5)));
        Assert.Equal("""{"overwrite":false,"path":"fixture"}""", result.EffectiveArgumentsJson);
        Assert.Equal(0, Fixtures.StaticInvocations);
    }

    [Fact]
    public async Task ReflectedTargetsAndAlternativeHandlers_HaveIdenticalWireResults()
    {
        ServiceCollection services = new();
        services.AddScoped<ServiceTools>();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        MethodInfo method = typeof(ServiceTools).GetMethod(nameof(ServiceTools.Copy))!;
        GeneratedTool declaration = FromMethod(method);
        await using ToolBinding live = declaration.Bind(ToolTargetResolvers.FromServiceProvider(scope.ServiceProvider));
        await using ToolBinding alternative = declaration.BindHandler((_, arguments, _) =>
            ValueTask.FromResult<object?>(new CopyResult(arguments.Get<string>("status"), 5)));
        ToolCall call = new("service_copy", "id", """{"status":"copied"}""");
        ToolInvocationResult actual = await live.InvokeAsync(call);
        ToolInvocationResult simulated = await alternative.InvokeAsync(call);
        Assert.Equal(actual.SerializedResult, simulated.SerializedResult);
        Assert.Equal(actual.EffectiveArgumentsJson, simulated.EffectiveArgumentsJson);
        Assert.Equal(live.Descriptor, alternative.Descriptor);
        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<ServiceTools>().Invocations);
    }

    [Fact]
    public async Task CallbackFailure_PreservesExceptionAndBindingRemainsUsable()
    {
        IOException expected = new("Expected domain failure");
        await using ToolBinding binding = EchoDefinition().BindHandler((_, arguments, _) =>
            arguments.Get<string>("text") == "fail" ? throw expected : ValueTask.FromResult<object?>("ok"));
        IOException error = await Assert.ThrowsAsync<IOException>(() => binding.InvokeAsync(new("echo", "bad", """{"text":"fail"}""")).AsTask());
        Assert.Same(expected, error);
        Assert.Equal(StateStatus.Error, binding.State.Status);
        Assert.Equal("ok", (await binding.InvokeAsync(new("echo", "good", """{"text":"ok"}"""))).Result);
    }

    [Fact]
    public async Task HandlerReturnTypeAndSelectedAlias_AreEnforced()
    {
        await using ToolBinding wrongResult = EchoDefinition().BindHandler((_, _, _) => ValueTask.FromResult<object?>(42));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongResult.InvokeAsync(new("echo", "wrong", """{"text":"value"}""")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongResult.InvokeAsync(new("not_selected", "wrong", """{"text":"value"}""")).AsTask());
    }

    [Fact]
    public async Task StatelessConvenienceCalls_DoNotLeaveSharedInvocationState()
    {
        GeneratedTool tool = FromMethod(typeof(ServiceTools).GetMethod(nameof(ServiceTools.Copy))!);
        ServiceTools target = new();
        ToolInvocationResult result = await tool.InvokeAsync(new ToolCall("service_copy", "call", """{"status":"done"}"""), target);
        Assert.Equal("done", Assert.IsType<CopyResult>(result.Result).Status);
        Assert.Null(tool.GetType().GetProperty("State"));
    }

    [Fact]
    public void Descriptor_RoundTripsAndPinsBothWireSchemasAndResultFormat()
    {
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<string, bool, CancellationToken, CopyResult>)Fixtures.StaticCopy).Single();
        ToolDescriptor descriptor = tool.Descriptor;
        string json = JsonSerializer.Serialize(descriptor, ToolJsonSerialization.Options);
        ToolDescriptor restored = JsonSerializer.Deserialize<ToolDescriptor>(json, ToolJsonSerialization.Options)!;
        restored.RequireMatch(tool.Descriptor);
        Assert.Equal(64, descriptor.Fingerprint.Length);
        Assert.Equal(2, descriptor.SchemaVersion);
        Assert.DoesNotContain("MethodInfo", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetInstance", json, StringComparison.Ordinal);
        Assert.NotEqual(tool.Describe(ToolResultFormat.Plain).Fingerprint, descriptor.Fingerprint);
        Assert.Throws<InvalidOperationException>(() => (restored with { SchemaVersion = 99 }).RequireMatch(tool.Descriptor));
        using JsonDocument input = JsonDocument.Parse(descriptor.ParametersJson);
        Assert.False(input.RootElement.GetProperty("properties").GetProperty("overwrite").GetProperty("default").GetBoolean());
        using JsonDocument output = JsonDocument.Parse(descriptor.ResultEnvelopeSchemaJson);
        Assert.True(output.RootElement.GetProperty("properties").TryGetProperty("result", out _));
    }

    [Fact]
    public void ProjectionsCannotMutateTheCachedMetadata()
    {
        GeneratedTool tool = EchoDefinition();
        string fingerprint = tool.Descriptor.Fingerprint;
        FunctionTool projection = tool.ResponseTool;
        projection.FunctionDescription = "Changed externally";
        projection.FunctionParameters = BinaryData.FromString("{}");
        Assert.NotEqual(projection.FunctionDescription, tool.ResponseTool.FunctionDescription);
        Assert.NotEqual("{}", tool.ResponseTool.FunctionParameters.ToString());
        Assert.Equal(fingerprint, tool.Descriptor.Fingerprint);
    }

    [Fact]
    public void Catalog_AllowsRepeatedReferencesButRejectsConflictingNames()
    {
        GeneratedTool first = EchoDefinition();
        Assert.Single(new GeneratedToolCatalog([first, first], []).AllTools);
        GeneratedTool duplicate = FromMethod(typeof(ConflictingTools).GetMethod(nameof(ConflictingTools.Echo))!);
        Assert.Throws<InvalidOperationException>(() => new GeneratedToolCatalog([first, duplicate], []));
    }

    [Fact]
    public async Task CapturedTargets_DoNotOverrideExplicitAlternativeHandlers()
    {
        ServiceTools captured = new();
        GeneratedTool tool = ResponseToolDiscovery.CreateTools((Func<string, CancellationToken, CopyResult>)captured.Copy).Single();
        ServiceCollection services = new();
        services.AddSingleton<ServiceTools>();
        using ServiceProvider provider = services.BuildServiceProvider();
        await using ToolBinding original = tool.Bind(ToolTargetResolvers.FromServiceProvider(provider));
        await original.InvokeAsync(new("service_copy", "captured", """{"status":"ok"}"""));
        Assert.Equal(1, captured.Invocations);
        Assert.Equal(0, provider.GetRequiredService<ServiceTools>().Invocations);
        await using ToolBinding replacement = tool.BindHandler((_, arguments, _) =>
            ValueTask.FromResult<object?>(new CopyResult(arguments.Get<string>("status"), 5)));
        await replacement.InvokeAsync(new("service_copy", "replacement", """{"status":"ok"}"""));
        Assert.Equal(1, captured.Invocations);
    }

    [Fact]
    public async Task AliasDefinitionsHaveTheirOwnNamesAndCollisionChecks()
    {
        GeneratedTool[] aliases = ResponseToolDiscovery.CreateTools((Func<string, CancellationToken, string>)Fixtures.Alias);
        GeneratedTool alias = aliases.Single(tool => tool.Name == "echo_alias");
        Assert.Equal("\"value\"", (await alias.InvokeAsync(new ToolCall("echo_alias", "alias", """{"text":"value"}"""))).SerializedResult);
        await Assert.ThrowsAsync<InvalidOperationException>(() => alias.InvokeAsync(
            new ToolCall("echo_original", "wrong-selected-name", """{"text":"value"}""")).AsTask());
        GeneratedTool collision = new(ResponseTool.CreateFunctionTool("echo_alias", aliases[0].ResponseTool.FunctionParameters, false, "conflict"),
            null, typeof(Fixtures).GetMethod(nameof(Fixtures.Echo))!);
        Assert.Throws<InvalidOperationException>(() => new GeneratedToolCatalog([.. aliases, collision], []));
    }

    internal static GeneratedTool EchoDefinition() =>
        ResponseToolDiscovery.CreateTools((Func<string, CancellationToken, string>)Fixtures.Echo).Single();

    internal static GeneratedTool FromMethod(MethodInfo method)
    {
        ToolAttribute metadata = method.GetCustomAttribute<ToolAttribute>()!;
        // Discovery from an open instance delegate is unnecessary: the descriptor still points at the original method.
        return new(ResponseTool.CreateFunctionTool(metadata.Name, BinaryData.FromString(
            """{"type":"object","properties":{"status":{"type":"string"}},"required":["status"],"additionalProperties":false}"""), false, metadata.Description),
            method.DeclaringType, method);
    }

    public sealed class ServiceTools
    {
        public int Invocations { get; private set; }
        [Tool("service_copy", "Copies a value.", "Copy", true)]
        public CopyResult Copy(string status, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations++;
            return new(status, 5);
        }
    }

    public static class ConflictingTools
    {
        [Tool("echo", "Conflicting echo.", "Echo", true)]
        public static string Echo(string status, CancellationToken cancellationToken) => status;
    }

    public static class Fixtures
    {
        public static int StaticInvocations;
        [Tool("echo", "Echo text.", "Echo", true)]
        public static string Echo(string text, CancellationToken cancellationToken) => text;
        [Tool("echo_original", "Echo with alias.", "Echo", true, Aliases = ["echo_alias"])]
        public static string Alias(string text, CancellationToken cancellationToken) => text;
        [Tool("static_copy", "Copy one item.", "Copy", true, SchemaVersion = 2)]
        public static CopyResult StaticCopy(string path, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StaticInvocations);
            throw new InvalidOperationException("This implementation must not execute in the handler test.");
        }
    }

    [ToolResult("An operation result.")]
    public sealed record CopyResult(string Status, int BytesCopied);
}
