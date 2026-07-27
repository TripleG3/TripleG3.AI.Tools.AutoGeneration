namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>Scans trusted assemblies for methods that may be suitable for AI tool exposure.</summary>
public static class ToolCandidateScanner
{
    private const BindingFlags MethodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static IReadOnlyList<ToolCandidate> ScanAssembly(
        Assembly assembly,
        bool includeNonPublic = false,
        bool includeInherited = false)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        IEnumerable<Type> types = GetTypes(assembly).Where(type =>
            includeNonPublic || type.IsPublic || type.IsNestedPublic);

        return [.. types
            .Where(type => !type.IsSpecialName && !type.IsCompilerGenerated())
            .SelectMany(type => ScanType(type, includeNonPublic, includeInherited))];
    }

    public static IReadOnlyList<ToolCandidate> ScanType(
        Type type,
        bool includeNonPublic = false,
        bool includeInherited = false)
    {
        ArgumentNullException.ThrowIfNull(type);

        BindingFlags flags = MethodFlags;
        if (!includeNonPublic)
        {
            flags &= ~BindingFlags.NonPublic;
        }

        if (!includeInherited)
        {
            flags |= BindingFlags.DeclaredOnly;
        }

        return [.. type.GetMethods(flags)
            .Where(method => !method.IsSpecialName && !method.IsCompilerGenerated())
            .OrderBy(method => method.MetadataToken)
            .Select(method => CreateCandidate(type, method))];
    }

    private static ToolCandidate CreateCandidate(Type declaringType, MethodInfo method)
    {
        ToolAttribute? toolAttribute = method.GetCustomAttribute<ToolAttribute>(inherit: true);
        ParameterInfo[] parameters = method.GetParameters();
        bool hasCancellationToken = parameters.LastOrDefault()?.ParameterType == typeof(CancellationToken);
        List<string> reasons = [];
        List<string> warnings = [];

        if (toolAttribute is not null)
        {
            reasons.Add("The method already has ToolAttribute metadata.");
        }
        else if (method.IsPublic)
        {
            reasons.Add("The method is public.");
        }

        if (hasCancellationToken)
        {
            reasons.Add("The method has a trailing CancellationToken.");
        }
        else
        {
            warnings.Add("The method does not have a trailing CancellationToken.");
        }

        if (method.IsStatic)
        {
            reasons.Add("The method is static and does not require target resolution.");
        }

        if (method.ContainsGenericParameters)
        {
            warnings.Add("The method contains unresolved generic parameters.");
        }

        if (parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.IsOut || parameter.ParameterType.IsPointer))
        {
            warnings.Add("The method has ref, out, or pointer parameters.");
        }

        if (toolAttribute?.IsDeprecated == true)
        {
            warnings.Add("The method is deprecated.");
        }

        ToolCandidateClassification classification = toolAttribute is not null
            ? ToolCandidateClassification.AlreadyExposed
            : method.ContainsGenericParameters || parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.IsOut || parameter.ParameterType.IsPointer)
                ? ToolCandidateClassification.NotSuitable
                : warnings.Count > 0
                    ? ToolCandidateClassification.ReviewRequired
                    : ToolCandidateClassification.Recommended;

        return new ToolCandidate(
            declaringType.FullName ?? declaringType.Name,
            method.Name,
            null,
            null,
            classification,
            reasons,
            warnings,
            [.. parameters.Select(parameter => new ToolParameterCandidate(
                parameter.Name ?? $"arg{parameter.Position + 1}",
                FormatType(parameter.ParameterType),
                parameter.IsOptional || parameter.HasDefaultValue))],
            FormatType(method.ReturnType),
            hasCancellationToken,
            toolAttribute is not null);
    }

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static string FormatType(Type type) =>
        type.FullName ?? type.Name;
}

internal static class TypeExtensions
{
    public static bool IsCompilerGenerated(this Type type) =>
        type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null;

    public static bool IsCompilerGenerated(this MethodInfo method) =>
        method.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null;
}
