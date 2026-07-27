namespace TripleG3.AI.Tools.AutoGeneration;

public sealed record ToolGenerationRequest(
    string TypeName,
    string OutputPath,
    string Namespace,
    IReadOnlyList<string>? MethodNames = null,
    string? ToolNamePrefix = null,
    bool IncludeInheritedMethods = false,
    bool IncludeNonPublicMethods = false,
    bool Overwrite = false);

public sealed record GeneratedToolDescription(
    string Name,
    string MethodName,
    string Description,
    IReadOnlyList<string> Warnings);

public sealed record ToolGenerationDiagnostic(
    string Severity,
    string Message,
    string? MethodName = null);

public sealed record ToolGenerationResult(
    string OutputPath,
    string Source,
    IReadOnlyList<GeneratedToolDescription> GeneratedTools,
    IReadOnlyList<ToolGenerationDiagnostic> Diagnostics,
    bool WroteFile);

/// <summary>Generates reviewable wrapper source for methods on a trusted type.</summary>
public static class ToolSourceGenerator
{
    public static ToolGenerationResult Generate(
        Type type,
        ToolGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Namespace);

        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        if (request.IncludeNonPublicMethods)
        {
            flags |= BindingFlags.NonPublic;
        }

        if (!request.IncludeInheritedMethods)
        {
            flags |= BindingFlags.DeclaredOnly;
        }

        HashSet<string>? selectedNames = request.MethodNames is null
            ? null
            : request.MethodNames.ToHashSet(StringComparer.Ordinal);
        MethodInfo[] methods = [.. type.GetMethods(flags)
            .Where(method => !method.IsSpecialName && !method.ContainsGenericParameters)
            .Where(method => selectedNames is null || selectedNames.Contains(method.Name))
            .OrderBy(method => method.MetadataToken)];

        List<ToolGenerationDiagnostic> diagnostics = [];
        List<GeneratedToolDescription> descriptions = [];
        List<string> wrapperMethods = [];
        HashSet<string> toolNames = new(StringComparer.Ordinal);

        foreach (MethodInfo method in methods)
        {
            if (!IsSupported(method, out string? reason))
            {
                diagnostics.Add(new("warning", reason!, method.Name));
                continue;
            }

            ToolAttribute? existingAttribute = method.GetCustomAttribute<ToolAttribute>(inherit: true);
            string toolName = existingAttribute?.Name ?? CreateToolName(request.ToolNamePrefix, method.Name);
            if (!toolNames.Add(toolName))
            {
                diagnostics.Add(new("error", $"The generated tool name '{toolName}' is duplicated.", method.Name));
                continue;
            }

            string description = existingAttribute?.Description ?? CreateDescription(method);
            descriptions.Add(new(toolName, method.Name, description, []));
            wrapperMethods.Add(RenderMethod(type, method, toolName, description));
        }

        string source = RenderClass(type, request, wrapperMethods);
        bool wroteFile = false;
        if (request.Overwrite || !File.Exists(request.OutputPath))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(request.OutputPath, source);
            wroteFile = true;
        }
        else
        {
            diagnostics.Add(new("error", $"The output file already exists: '{request.OutputPath}'."));
        }

        return new(request.OutputPath, source, descriptions, diagnostics, wroteFile);
    }

    private static bool IsSupported(MethodInfo method, out string? reason)
    {
        if (method.ContainsGenericParameters)
        {
            reason = "Generic methods with unresolved type parameters are not supported.";
            return false;
        }

        if (method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef || parameter.IsOut || parameter.ParameterType.IsPointer))
        {
            reason = "Methods with ref, out, or pointer parameters are not supported.";
            return false;
        }

        reason = null;
        return true;
    }

    private static string CreateToolName(string? prefix, string methodName) =>
        $"{prefix ?? string.Empty}{(methodName.EndsWith("Async", StringComparison.Ordinal) ? methodName[..^5] : methodName)}".ToLowerInvariant();

    private static string CreateDescription(MethodInfo method) =>
        $"Invokes {method.Name} on {method.DeclaringType?.Name ?? "the target service"}.";

    private static string RenderClass(Type type, ToolGenerationRequest request, IReadOnlyList<string> methods)
    {
        string className = $"{type.Name.TrimStart('I')}Tools";
        string dependency = methods.Any(method => method.Contains("_service", StringComparison.Ordinal))
            ? $"private readonly {FormatType(type)} _service;"
            : string.Empty;
        string constructor = string.IsNullOrEmpty(dependency)
            ? string.Empty
            : $"public {className}({FormatType(type)} service) => _service = service;";
        string body = string.Join(Environment.NewLine + Environment.NewLine, methods);
        return $$"""
    using TripleG3.AI.Tools.AutoGeneration;

namespace {{request.Namespace}};

public sealed class {{className}}
{
    {{dependency}}

    {{constructor}}

{{body}}
}
""";
    }

    private static string RenderMethod(Type type, MethodInfo method, string toolName, string description)
    {
        string returnType = FormatType(method.ReturnType);
        string parameters = string.Join(", ", method.GetParameters().Select(parameter =>
            $"{FormatType(parameter.ParameterType)} {parameter.Name ?? $"arg{parameter.Position + 1}"}"));
        string invocationArguments = string.Join(", ", method.GetParameters().Select(parameter => parameter.Name ?? $"arg{parameter.Position + 1}"));
        string target = method.IsStatic ? FormatType(type) : "_service";
        string cancellation = method.GetParameters().LastOrDefault()?.ParameterType == typeof(CancellationToken)
            ? string.Empty
            : string.IsNullOrWhiteSpace(parameters) ? "CancellationToken cancellationToken" : ", CancellationToken cancellationToken";
        string forwarded = method.GetParameters().Length == 0
            ? string.Empty
            : invocationArguments;

        return $$"""
    [Tool("{{toolName}}", "{{description}}", "Invoking {{method.Name}}")]
    public {{returnType}} {{method.Name}}Tool({{parameters}}{{cancellation}}) =>
        {{target}}.{{method.Name}}({{forwarded}});
""";
    }

    private static string FormatType(Type type) =>
        type.IsArray ? $"{FormatType(type.GetElementType() ?? typeof(object))}[]" :
        type.FullName?.Replace('+', '.') ?? type.Name;
}
