using System.Runtime.ExceptionServices;
using OpenAI.Realtime;
using TripleG3.Cis;

namespace TripleG3.AI.Tools.AutoGeneration;

internal static class ToolParameterMetadata
{
    internal static ToolParameterAttribute? GetAttribute(IReadOnlyList<ToolParameterAttribute> attributes, ParameterInfo parameter)
    {
        ToolParameterAttribute[] own = [.. parameter.GetCustomAttributes<ToolParameterAttribute>(true)];
        if (own.Length > 1) throw new InvalidOperationException($"Tool parameter '{parameter.Name}' has multiple metadata declarations.");
        return own.Length == 1 ? own[0] : attributes.FirstOrDefault(attribute => string.Equals(attribute.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
    }
    internal static Type GetType(ParameterInfo parameter) => parameter.ParameterType;
    internal static string GetName(ParameterInfo parameter, ToolParameterAttribute? attribute) => attribute?.Name ?? parameter.Name ?? $"arg{parameter.Position + 1}";
    internal static bool Skip(ParameterInfo parameter) => parameter.ParameterType == typeof(CancellationToken);
    internal static Type GetSchemaType(ParameterInfo parameter, ToolParameterAttribute? attribute) => attribute?.Type ?? parameter.ParameterType;
    internal static Type GetConversionType(ParameterInfo parameter, ToolParameterAttribute? attribute) => GetSchemaType(parameter, attribute);
}

public static class FunctionCallResponseItemExtensions
{
    public static object?[] ToParameters(this FunctionCallResponseItem call, GeneratedTool tool) => call.ToParameters(tool, CancellationToken.None);
    public static object?[] ToParameters(this FunctionCallResponseItem call, GeneratedTool tool, CancellationToken cancellationToken) =>
        ToolArgumentBinder.Bind(tool, new ToolCall(call.FunctionName, call.CallId, call.FunctionArguments.ToString()), cancellationToken).CopyParameters();
    public static object?[] ToParameters(this FunctionCallResponseItem call, MethodInfo method) => call.ToParameters(method, CancellationToken.None);
    public static object?[] ToParameters(this FunctionCallResponseItem call, MethodInfo method, CancellationToken cancellationToken) =>
        ToolArgumentBinder.Bind(method, new ToolCall(call.FunctionName, call.CallId, call.FunctionArguments.ToString()), cancellationToken).CopyParameters();
}

/// <summary>Reusable tool metadata; no invocation state or execution semaphore is cached here.</summary>
public sealed class GeneratedTool
{
    private readonly Lazy<ToolDescriptor> descriptor;
    private readonly bool? strictModeEnabled;

    public GeneratedTool(FunctionTool responseTool, Type? targetType, MethodInfo methodInfo, object? targetInstance = null,
        bool isRequired = false, bool isLongRunning = false, bool isAlwaysAvailable = false, bool isCategoryTool = false,
        string? categoryName = null, string? categoryDescription = null, IReadOnlyList<GeneratedTool>? categoryTools = null,
        string? friendlyUIDescription = null, IReadOnlyList<string>? categoryNames = null)
    {
        ArgumentNullException.ThrowIfNull(responseTool);
        ArgumentNullException.ThrowIfNull(methodInfo);
        ValidateMethod(methodInfo, isCategoryTool);
        Name = responseTool.FunctionName;
        Description = responseTool.FunctionDescription ?? string.Empty;
        ParametersJson = responseTool.FunctionParameters?.ToString() ?? "{}";
        strictModeEnabled = responseTool.StrictModeEnabled;
        TargetType = targetType;
        TargetInstance = targetInstance;
        MethodInfo = methodInfo;
        IsRequired = isRequired;
        IsLongRunning = isLongRunning;
        IsAlwaysAvailable = isAlwaysAvailable;
        IsCategoryTool = isCategoryTool;
        CategoryName = categoryName;
        CategoryDescription = categoryDescription;
        CategoryTools = categoryTools is null ? ImmutableArray<GeneratedTool>.Empty : categoryTools.ToImmutableArray();
        CategoryNames = categoryNames is null ? ImmutableArray<string>.Empty : categoryNames.ToImmutableArray();
        FriendlyUIDescription = string.IsNullOrWhiteSpace(friendlyUIDescription) ? Name : friendlyUIDescription.Trim();
        SchemaVersion = isCategoryTool ? 1 : methodInfo.GetCustomAttribute<ToolAttribute>()?.SchemaVersion ?? 1;
        if (string.IsNullOrWhiteSpace(Name) || SchemaVersion < 1) throw new InvalidOperationException("A tool requires a name and positive schema version.");
        descriptor = new(() => ToolDescriptor.Create(this, ToolResultFormat.Automatic));
    }

    public string Name { get; }
    public string Description { get; }
    public string ParametersJson { get; }
    public int SchemaVersion { get; }
    public FunctionTool ResponseTool => ToResponsesTool();
    public Type? TargetType { get; }
    public object? TargetInstance { get; }
    public MethodInfo MethodInfo { get; }
    public bool IsRequired { get; }
    public bool IsLongRunning { get; }
    public bool IsAlwaysAvailable { get; }
    public bool IsCategoryTool { get; }
    public string? CategoryName { get; }
    public string? CategoryDescription { get; }
    public IReadOnlyList<GeneratedTool> CategoryTools { get; }
    public IReadOnlyList<string> CategoryNames { get; }
    public string FriendlyUIDescription { get; }
    public ToolDescriptor Descriptor => descriptor.Value;

    public FunctionTool ToResponsesTool()
    {
        FunctionTool projection = OpenAI.Responses.ResponseTool.CreateFunctionTool(Name, BinaryData.FromString(ParametersJson), false, Description);
        projection.StrictModeEnabled = strictModeEnabled;
        return projection;
    }
    public RealtimeFunctionTool ToRealtimeTool() => new(Name)
    {
        FunctionDescription = Description,
        FunctionParameters = BinaryData.FromString(ParametersJson)
    };

    public ToolDescriptor Describe(ToolResultFormat format = ToolResultFormat.Automatic) =>
        format == ToolResultFormat.Automatic ? Descriptor : ToolDescriptor.Create(this, format);
    public string SerializeResult(object? result, ToolResultFormat format = ToolResultFormat.Automatic) =>
        ToolResultJsonSerializer.Serialize(result, MethodInfo, format);

    public ToolBinding Bind(ToolTargetResolver targetResolver, ToolResultFormat format = ToolResultFormat.Automatic)
    {
        ArgumentNullException.ThrowIfNull(targetResolver);
        return new(this, async (call, arguments, token) =>
        {
            if (IsCategoryTool) return ToolCategoryBuilder.Build(this);
            FunctionCallResponseItem request = ResponseItem.CreateFunctionCallItem(call.CallId, call.Name, BinaryData.FromString(call.ArgumentsJson));
            object? target = await targetResolver(this, request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await InvokeMethodAsync(target, arguments).ConfigureAwait(false);
        }, format);
    }

    /// <summary>Substitutes the entire invocation, including static methods, without a target resolver or fallback.</summary>
    public ToolBinding BindHandler(ToolInvocationHandler handler, ToolResultFormat format = ToolResultFormat.Automatic) =>
        new(this, handler ?? throw new ArgumentNullException(nameof(handler)), format);

    public async ValueTask<ToolInvocationResult> InvokeAsync(ToolCall call, object? target = null, CancellationToken cancellationToken = default)
    {
        await using ToolBinding binding = Bind((_, _, _) => ValueTask.FromResult(target ?? TargetInstance));
        return await binding.InvokeAsync(call, cancellationToken).ConfigureAwait(false);
    }

    public FunctionCallOutputResponseItem CreateFunctionCallOutput(FunctionCallResponseItem call, object? result)
    {
        ValidateCall(new(call.FunctionName, call.CallId, call.FunctionArguments.ToString()));
        return ResponseItem.CreateFunctionCallOutputItem(call.CallId, SerializeResult(result));
    }

    public async ValueTask<FunctionCallOutputResponseItem> InvokeAndCreateFunctionCallOutputAsync(
        FunctionCallResponseItem call, object? target = null, CancellationToken cancellationToken = default)
    {
        ToolInvocationResult result = await InvokeAsync(new ToolCall(call.FunctionName, call.CallId, call.FunctionArguments.ToString()), target, cancellationToken).ConfigureAwait(false);
        return ResponseItem.CreateFunctionCallOutputItem(call.CallId, result.SerializedResult);
    }

    public async ValueTask<State<ToolInvocationState>> InvokeAsync(FunctionCallResponseItem call, object? target = null, CancellationToken cancellationToken = default)
    {
        ToolInvocationResult result = await InvokeAsync(new ToolCall(call.FunctionName, call.CallId, call.FunctionArguments.ToString()), target, cancellationToken).ConfigureAwait(false);
        return new(result.State, StateStatus.Ready, string.Empty);
    }

    public static ToolInvocationState GetReadyInvocationValueOrThrow(State<ToolInvocationState> state) =>
        state.Status == StateStatus.Ready && state.Value is { } value ? value :
        throw new ToolInvocationException(string.IsNullOrWhiteSpace(state.ErrorMessage) ? $"Tool invocation completed with state '{state.Status}'." : state.ErrorMessage);

    internal void ValidateCall(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Name != Name) throw new InvalidOperationException($"Tool call '{call.Name}' does not target the selected tool '{Name}'.");
    }

    internal static void ValidateMethod(MethodInfo method, bool category)
    {
        if (category) return;
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Count(parameter => ToolParameterMetadata.Skip(parameter)) != 1 || !ToolParameterMetadata.Skip(parameters[^1]))
            throw new InvalidOperationException($"Tool method '{method.DeclaringType?.FullName}.{method.Name}' must declare exactly one trailing CancellationToken.");
        if (method.ContainsGenericParameters || parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer) ||
            method.ReturnType.IsByRef || method.ReturnType.IsPointer ||
            (method.ReturnType == typeof(void) && method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() is not null))
            throw new InvalidOperationException($"Tool method '{method.Name}' has an unsupported generic, pointer, by-reference or async-void signature.");
    }

    private async ValueTask<object?> InvokeMethodAsync(object? target, ToolArguments arguments)
    {
        if (!MethodInfo.IsStatic && (target is null || MethodInfo.DeclaringType?.IsInstanceOfType(target) != true))
            throw new InvalidOperationException($"Tool '{Name}' requires a target assignable to '{MethodInfo.DeclaringType}'.");
        object? result;
        try { result = MethodInfo.Invoke(target, arguments.CopyParameters()); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            return MethodInfo.ReturnType.IsGenericType ? MethodInfo.ReturnType.GetProperty(nameof(Task<object>.Result))!.GetValue(result) : null;
        }
        if (MethodInfo.ReturnType == typeof(ValueTask))
        {
            await ((ValueTask)result!).ConfigureAwait(false);
            return null;
        }
        if (MethodInfo.ReturnType.IsGenericType && MethodInfo.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            Task valueTask = (Task)MethodInfo.ReturnType.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(result, null)!;
            await valueTask.ConfigureAwait(false);
            return valueTask.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(valueTask);
        }
        return result;
    }
}

public sealed record ToolInvocationState(string ToolName, string ToolCallId, string ArgumentsJson, string FriendlyUIDescription,
    string? CategoryName, bool IsCategoryTool, string MethodName, string? TargetTypeName, string? ResultTypeName,
    object? Result, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc)
{
    public static ToolInvocationState Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty,
        null, false, string.Empty, null, null, null, default, default);
    public static ToolInvocationState Completed(GeneratedTool tool, FunctionCallResponseItem call, object? result, DateTimeOffset started, DateTimeOffset completed) =>
        new(call.FunctionName, call.CallId, call.FunctionArguments.ToString(), tool.FriendlyUIDescription, tool.CategoryName,
            tool.IsCategoryTool, $"{tool.MethodInfo.DeclaringType?.FullName}.{tool.MethodInfo.Name}", tool.TargetType?.FullName,
            result?.GetType().FullName, result, started, completed);
}

public sealed class ToolInvocationException(string message) : InvalidOperationException(message);
