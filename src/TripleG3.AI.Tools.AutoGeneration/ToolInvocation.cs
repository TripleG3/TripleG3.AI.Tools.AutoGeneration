using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenAI.Realtime;
using OpenAI.Responses;
using TripleG3.Cis;

namespace TripleG3.AI.Tools.AutoGeneration;

internal static class ToolParameterMetadata
{
    public static ToolParameterAttribute? GetAttribute(IReadOnlyList<ToolParameterAttribute> methodAttributes, ParameterInfo parameter)
    {
        ToolParameterAttribute[] attributes = [.. parameter.GetCustomAttributes<ToolParameterAttribute>(true)];
        if (attributes.Length > 1) throw new InvalidOperationException($"Tool parameter '{parameter.Name}' can only have one ToolParameterAttribute.");
        return attributes.Length == 1 ? attributes[0] : methodAttributes.FirstOrDefault(attribute => string.Equals(attribute.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
    }
    public static Type GetType(ParameterInfo parameter) => parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() ?? parameter.ParameterType : parameter.ParameterType;
    public static string GetName(ParameterInfo parameter, ToolParameterAttribute? attribute) => attribute?.Name ?? parameter.Name ?? $"arg{parameter.Position + 1}";
    public static bool Skip(ParameterInfo parameter) => GetType(parameter) == typeof(CancellationToken);
    public static Type GetSchemaType(ParameterInfo parameter, ToolParameterAttribute? attribute) => attribute?.Type ?? GetType(parameter);
    public static Type GetConversionType(ParameterInfo parameter, ToolParameterAttribute? attribute) => attribute?.Type is { } type && (GetType(parameter) == typeof(object) || GetType(parameter).IsAssignableFrom(type)) ? type : GetType(parameter);
}

public static class FunctionCallResponseItemExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false), new TimeSpanConverter() } };
    public static object?[] ToParameters(this FunctionCallResponseItem call, GeneratedTool tool) => call.ToParameters(tool, CancellationToken.None);
    public static object?[] ToParameters(this FunctionCallResponseItem call, GeneratedTool tool, CancellationToken cancellationToken) => call.ToParameters(tool.MethodInfo, cancellationToken);
    public static object?[] ToParameters(this FunctionCallResponseItem call, MethodInfo method) => call.ToParameters(method, CancellationToken.None);
    public static object?[] ToParameters(this FunctionCallResponseItem call, MethodInfo method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call); ArgumentNullException.ThrowIfNull(method);
        ToolAttribute? tool = method.GetCustomAttribute<ToolAttribute>();
        if (tool is not null && !string.Equals(tool.Name, call.FunctionName, StringComparison.Ordinal) && !(tool.Aliases ?? []).Contains(call.FunctionName, StringComparer.Ordinal)) throw new InvalidOperationException($"Tool call '{call.FunctionName}' cannot be converted for method '{FormatMethod(method)}' because the method is registered as tool '{tool.Name}'.");
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.FunctionArguments.ToString()) ? "{}" : call.FunctionArguments.ToString());
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Tool call '{call.FunctionName}' arguments must be a JSON object.");
        ParameterInfo[] parameters = method.GetParameters(); object?[] values = new object?[parameters.Length]; ToolParameterAttribute[] methodAttributes = [.. method.GetCustomAttributes<ToolParameterAttribute>(true)];
        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index]; ToolParameterAttribute? attribute = ToolParameterMetadata.GetAttribute(methodAttributes, parameter); Type type = ToolParameterMetadata.GetType(parameter);
            if (ToolParameterMetadata.Skip(parameter)) { values[index] = type == typeof(CancellationToken) ? cancellationToken : type.IsValueType ? Activator.CreateInstance(type) : null; continue; }
            string name = ToolParameterMetadata.GetName(parameter, attribute);
            if (TryGet(document.RootElement, name, out JsonElement argument)) values[index] = Convert(call, method, parameter, attribute, name, argument);
            else if (parameter.HasDefaultValue) values[index] = parameter.DefaultValue;
            else if (parameter.IsOptional) values[index] = Type.Missing;
            else throw new InvalidOperationException($"Tool call '{call.FunctionName}' did not provide required argument '{name}' for method '{FormatMethod(method)}'.");
        }
        return values;
    }
    private static object? Convert(FunctionCallResponseItem call, MethodInfo method, ParameterInfo parameter, ToolParameterAttribute? attribute, string name, JsonElement argument)
    {
        Type parameterType = ToolParameterMetadata.GetType(parameter); if (argument.ValueKind == JsonValueKind.Null) { if (!parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) is not null) return null; throw new InvalidOperationException($"Tool call '{call.FunctionName}' provided null for non-nullable argument '{name}' on method '{FormatMethod(method)}'."); }
        try { Type conversionType = ToolParameterMetadata.GetConversionType(parameter, attribute); object? value = conversionType == typeof(JsonElement) ? argument.Clone() : conversionType == typeof(JsonNode) ? JsonNode.Parse(argument.GetRawText()) : conversionType == typeof(BinaryData) ? BinaryData.FromString(argument.GetRawText()) : (Nullable.GetUnderlyingType(conversionType) ?? conversionType).IsEnum ? ParseEnum(argument, Nullable.GetUnderlyingType(conversionType) ?? conversionType) : argument.Deserialize(conversionType, Options); if (value is not null && !(Nullable.GetUnderlyingType(parameterType) ?? parameterType).IsInstanceOfType(value)) throw new InvalidOperationException("Converted value cannot be assigned."); return value; }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or JsonException or NotSupportedException) { throw new InvalidOperationException($"Tool call '{call.FunctionName}' argument '{name}' could not be converted to '{ToolJsonSchemaBuilder.FormatType(parameterType)}' for method '{FormatMethod(method)}'. {exception.Message}", exception); }
    }
    private static object ParseEnum(JsonElement argument, Type type) { if (argument.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(argument.GetString())) throw new ArgumentException($"Enum '{type.FullName}' values must be JSON strings."); string value = argument.GetString()!.Trim(); string? match = Enum.GetNames(type).FirstOrDefault(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)); return match is null ? throw new ArgumentException($"Value '{value}' is not valid for enum '{type.FullName}'.") : Enum.Parse(type, match); }
    private static bool TryGet(JsonElement arguments, string name, out JsonElement value) { if (arguments.TryGetProperty(name, out value)) return true; foreach (JsonProperty property in arguments.EnumerateObject()) if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; } value = default; return false; }
    private static string FormatMethod(MethodInfo method) => $"{method.DeclaringType?.FullName}.{method.Name}";
    private sealed class TimeSpanConverter : JsonConverter<TimeSpan> { public override TimeSpan Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.Number ? TimeSpan.FromSeconds(reader.GetDouble()) : TimeSpan.Parse(reader.GetString() ?? throw new JsonException("TimeSpan cannot be empty."), CultureInfo.InvariantCulture); public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture)); }
}

public sealed class GeneratedTool(FunctionTool responseTool, Type? targetType, MethodInfo methodInfo, object? targetInstance = null, bool isRequired = false, bool isLongRunning = false, bool isAlwaysAvailable = false, bool isCategoryTool = false, string? categoryName = null, string? categoryDescription = null, IReadOnlyList<GeneratedTool>? categoryTools = null, string? friendlyUIDescription = null, IReadOnlyList<string>? categoryNames = null) : StateService<ToolInvocationState>
{
    public FunctionTool ResponseTool { get; } = responseTool ?? throw new ArgumentNullException(nameof(responseTool)); public Type? TargetType { get; } = targetType; public object? TargetInstance { get; } = targetInstance; public MethodInfo MethodInfo { get; } = Register(responseTool, methodInfo, isCategoryTool); public bool IsRequired { get; } = isRequired; public bool IsLongRunning { get; } = isLongRunning; public bool IsAlwaysAvailable { get; } = isAlwaysAvailable; public bool IsCategoryTool { get; } = isCategoryTool; public string? CategoryName { get; } = categoryName; public string? CategoryDescription { get; } = categoryDescription; public IReadOnlyList<GeneratedTool> CategoryTools { get; } = categoryTools is null ? [] : [.. categoryTools]; public IReadOnlyList<string> CategoryNames { get; } = categoryNames is null ? [] : [.. categoryNames]; public string FriendlyUIDescription { get; } = string.IsNullOrWhiteSpace(friendlyUIDescription) ? responseTool.FunctionName : friendlyUIDescription.Trim();
    public FunctionTool ToResponsesTool() => ResponseTool;
    public RealtimeFunctionTool ToRealtimeTool() => new(ResponseTool.FunctionName)
    {
        FunctionDescription = ResponseTool.FunctionDescription,
        FunctionParameters = ResponseTool.FunctionParameters
    };
    public string SerializeResult(object? result) => ToolResultJsonSerializer.Serialize(result, MethodInfo);
    public FunctionCallOutputResponseItem CreateFunctionCallOutput(FunctionCallResponseItem call, object? result) { ArgumentNullException.ThrowIfNull(call); return ResponseItem.CreateFunctionCallOutputItem(call.CallId, SerializeResult(result)); }
    public async ValueTask<FunctionCallOutputResponseItem> InvokeAndCreateFunctionCallOutputAsync(FunctionCallResponseItem call, object? target = null, CancellationToken cancellationToken = default) { State<ToolInvocationState> state = await InvokeAsync(call, target, cancellationToken).ConfigureAwait(false); return CreateFunctionCallOutput(call, GetReadyInvocationValueOrThrow(state).Result); }
    public ValueTask<State<ToolInvocationState>> InvokeAsync(FunctionCallResponseItem call, object? target = null, CancellationToken cancellationToken = default) => SetAsync(token => InvokeCoreAsync(call, target, token), cancellationToken);
    public static ToolInvocationState GetReadyInvocationValueOrThrow(State<ToolInvocationState> state) => state.Status is StateStatus.Ready && state.Value is { } value ? value : throw new ToolInvocationException(string.IsNullOrWhiteSpace(state.ErrorMessage) ? $"Tool invocation completed with state '{state.Status}'." : state.ErrorMessage);
    private async ValueTask<ToolInvocationState> InvokeCoreAsync(FunctionCallResponseItem call, object? target, CancellationToken token) { DateTimeOffset started = DateTimeOffset.UtcNow; object? result = IsCategoryTool ? ToolCategoryBuilder.Build(this) : await InvokeMethodAsync(call, target, token).ConfigureAwait(false); return ToolInvocationState.Completed(this, call, result, started, DateTimeOffset.UtcNow); }
    private async Task<object?> InvokeMethodAsync(FunctionCallResponseItem call, object? target, CancellationToken token) { object? effectiveTarget = target ?? TargetInstance; if (!MethodInfo.IsStatic && effectiveTarget is null) throw new InvalidOperationException($"Tool method '{MethodInfo.DeclaringType?.FullName}.{MethodInfo.Name}' requires a target instance."); object? result; try { result = MethodInfo.Invoke(effectiveTarget, call.ToParameters(MethodInfo, token)); } catch (TargetInvocationException exception) when (exception.InnerException is not null) { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; } if (result is Task task) { await task.ConfigureAwait(false); return MethodInfo.ReturnType.IsGenericType ? MethodInfo.ReturnType.GetProperty(nameof(Task<object>.Result))?.GetValue(result) : null; } if (MethodInfo.ReturnType == typeof(ValueTask)) { await ((ValueTask)result!).ConfigureAwait(false); return null; } if (MethodInfo.ReturnType.IsGenericType && MethodInfo.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>)) { Task taskValue = (Task)MethodInfo.ReturnType.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(result, null)!; await taskValue.ConfigureAwait(false); return taskValue.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(taskValue); } return result; }
    private static MethodInfo Register(FunctionTool tool, MethodInfo method, bool category) { ArgumentNullException.ThrowIfNull(method); if (!category) { ParameterInfo[] parameters = method.GetParameters(); ParameterInfo[] tokens = [.. parameters.Where(parameter => ToolParameterMetadata.GetType(parameter) == typeof(CancellationToken))]; if (tokens.Length != 1 || tokens[0].Position != parameters.Length - 1) throw new InvalidOperationException($"Generated tool '{tool.FunctionName}' mapped to method '{method.DeclaringType?.FullName}.{method.Name}' must declare exactly one CancellationToken as its last parameter."); } return method; }
}

public sealed record ToolInvocationState(string ToolName, string ToolCallId, string ArgumentsJson, string FriendlyUIDescription, string? CategoryName, bool IsCategoryTool, string MethodName, string? TargetTypeName, string? ResultTypeName, object? Result, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc)
{ public static ToolInvocationState Completed(GeneratedTool tool, FunctionCallResponseItem call, object? result, DateTimeOffset started, DateTimeOffset completed) => new(call.FunctionName, call.CallId, call.FunctionArguments.ToString(), tool.FriendlyUIDescription, tool.CategoryName, tool.IsCategoryTool, $"{tool.MethodInfo.DeclaringType?.FullName}.{tool.MethodInfo.Name}", tool.TargetType?.FullName, result?.GetType().FullName, result, started, completed); }
public sealed class ToolInvocationException(string message) : InvalidOperationException(message);