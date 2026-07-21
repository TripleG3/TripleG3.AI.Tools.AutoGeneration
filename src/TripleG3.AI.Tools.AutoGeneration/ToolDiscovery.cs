using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TripleG3.AI.Tools.AutoGeneration;

public sealed class GeneratedToolCatalog
{
    private readonly GeneratedTool[] _primaryTools; private readonly GeneratedTool[] _allTools; private readonly GeneratedTool[] _requiredTools; private readonly GeneratedToolCategory[] _categories; private readonly Dictionary<string, GeneratedTool> _tools; private readonly Dictionary<string, GeneratedTool[]> _toolsByCategory; private readonly string[] _categoryNames;
    public GeneratedToolCatalog(IEnumerable<GeneratedTool> primaryTools, IEnumerable<GeneratedToolCategory> categories)
    {
        _primaryTools = [.. primaryTools]; _categories = [.. categories]; _allTools = [.. _primaryTools.Concat(_categories.SelectMany(category => category.Tools)).DistinctBy(tool => tool.ResponseTool.FunctionName)]; _requiredTools = [.. _allTools.Where(tool => tool.IsRequired)]; _tools = _allTools.ToDictionary(tool => tool.ResponseTool.FunctionName, StringComparer.Ordinal); IGrouping<string, (string CategoryName, GeneratedTool Tool)>[] categoryGroups = [.. _allTools.SelectMany(tool => tool.CategoryNames.Select(categoryName => (CategoryName: categoryName, Tool: tool))).GroupBy(item => item.CategoryName, StringComparer.Ordinal)]; _categoryNames = [.. categoryGroups.Select(group => group.Key)]; _toolsByCategory = categoryGroups.ToDictionary(group => group.Key, group => group.Select(item => item.Tool).ToArray(), StringComparer.Ordinal);
    }
    public static GeneratedToolCatalog Empty { get; } = new([], []); public IReadOnlyList<GeneratedTool> PrimaryTools => _primaryTools; public IReadOnlyList<GeneratedTool> AllTools => _allTools; public IReadOnlyList<GeneratedTool> RequiredTools => _requiredTools; public IReadOnlyList<GeneratedToolCategory> Categories => _categories; public IReadOnlyList<string> CategoryNames => _categoryNames;
    public GeneratedTool[] CreatePrimaryTools() => [.. _primaryTools]; public GeneratedTool[] CreateAllTools() => [.. _allTools]; public FunctionTool[] CreatePrimaryResponsesTools() => [.. _primaryTools.Select(tool => tool.ToResponsesTool())]; public FunctionTool[] CreateAllResponsesTools() => [.. _allTools.Select(tool => tool.ToResponsesTool())]; public RealtimeFunctionTool[] CreatePrimaryRealtimeTools() => [.. _primaryTools.Select(tool => tool.ToRealtimeTool())]; public RealtimeFunctionTool[] CreateAllRealtimeTools() => [.. _allTools.Select(tool => tool.ToRealtimeTool())]; public GeneratedTool[] CreateToolsForCategory(string categoryName) => _toolsByCategory.TryGetValue(NormalizeCategoryName(categoryName), out GeneratedTool[]? tools) && tools is not null ? [.. tools] : []; public bool TryGetTool(string name, out GeneratedTool tool) => _tools.TryGetValue(name, out tool!); public bool TryGetToolsForCategory(string categoryName, out IReadOnlyList<GeneratedTool> tools) { if (_toolsByCategory.TryGetValue(NormalizeCategoryName(categoryName), out GeneratedTool[]? categoryTools) && categoryTools is not null) { tools = categoryTools; return true; } tools = []; return false; } public bool TryGetCategory(string name, out GeneratedToolCategory category) { category = _categories.FirstOrDefault(item => string.Equals(item.CategoryTool.ResponseTool.FunctionName, name, StringComparison.Ordinal))!; return category is not null; }
    private static string NormalizeCategoryName(string categoryName) { ArgumentException.ThrowIfNullOrWhiteSpace(categoryName); return categoryName.Trim(); }
}

public sealed class GeneratedToolCategory(string name, string description, GeneratedTool categoryTool, IEnumerable<GeneratedTool> tools)
{ private readonly GeneratedTool[] _tools = [.. tools]; public string Name { get; } = name; public string Description { get; } = description; public GeneratedTool CategoryTool { get; } = categoryTool; public IReadOnlyList<GeneratedTool> Tools => _tools; public GeneratedTool[] CreateTools() => [.. _tools]; }

public static class ToolCategoryBuilder
{
    internal static MethodInfo CategoryToolMethod { get; } = typeof(ToolCategoryBuilder).GetMethod(nameof(GetCategoryTools), BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("Could not locate the internal tool category method.");
    public static IReadOnlyList<ToolCategoryToolInfo> Build(GeneratedTool categoryTool) => !categoryTool.IsCategoryTool ? throw new ArgumentException("The supplied tool is not a tool category.", nameof(categoryTool)) : [.. categoryTool.CategoryTools.Select(tool => { ToolAttribute? attribute = tool.MethodInfo.GetCustomAttribute<ToolAttribute>(); return new ToolCategoryToolInfo { Name = tool.ResponseTool.FunctionName, Description = tool.ResponseTool.FunctionDescription ?? string.Empty, ParametersJson = tool.ResponseTool.FunctionParameters?.ToString() ?? "{}", IsRequired = tool.IsRequired, IsAlwaysAvailable = tool.IsAlwaysAvailable, SchemaVersion = attribute?.SchemaVersion ?? 1, IsDeprecated = attribute?.IsDeprecated ?? false, DeprecationMessage = attribute?.DeprecationMessage, Aliases = attribute?.Aliases ?? [], Categories = tool.CategoryNames, Replaces = attribute?.Replaces ?? [] }; })];
    [Tool("__tool_category", "Internal tool category placeholder.", "List category tools", true)] private static IReadOnlyList<ToolCategoryToolInfo> GetCategoryTools() => [];
}

[ToolResult("A model-readable descriptor for one tool exposed by a selected tool category.")]
public sealed class ToolCategoryToolInfo
{
    [ToolResult("The exact tool function name to call.")] public string Name { get; init; } = string.Empty;
    [ToolResult("The tool description supplied to the model.")] public string Description { get; init; } = string.Empty;
    [ToolResult("The JSON schema for the tool parameters.")] public string ParametersJson { get; init; } = string.Empty;
    public bool IsRequired { get; init; } public bool IsAlwaysAvailable { get; init; } public int SchemaVersion { get; init; } = 1; public bool IsDeprecated { get; init; } public string? DeprecationMessage { get; init; } public IReadOnlyList<string> Aliases { get; init; } = []; public IReadOnlyList<string> Categories { get; init; } = []; public IReadOnlyList<string> Replaces { get; init; } = [];
}

public static class ResponseToolDiscovery
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly ConcurrentDictionary<string, GeneratedToolCatalog> Cache = new(StringComparer.Ordinal);
    [MethodImpl(MethodImplOptions.NoInlining)] public static GeneratedTool[] CreateTools() => CreateToolCatalog([Assembly.GetCallingAssembly()]).CreatePrimaryTools();
    [MethodImpl(MethodImplOptions.NoInlining)] public static GeneratedToolCatalog CreateToolCatalog() => CreateToolCatalog([Assembly.GetCallingAssembly()]);
    public static GeneratedTool[] CreateTools(IEnumerable<Assembly> assemblies) => CreateToolCatalog(assemblies).CreatePrimaryTools();
    public static GeneratedToolCatalog CreateToolCatalog(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies); Assembly[] input = [.. assemblies]; if (input.Length == 0) return GeneratedToolCatalog.Empty; if (input.Any(assembly => assembly is null)) throw new ArgumentException("The assemblies collection cannot contain null entries.", nameof(assemblies)); string key = string.Join('|', input.Distinct().Select(assembly => $"{assembly.FullName}:{assembly.ManifestModule.ModuleVersionId}").OrderBy(value => value, StringComparer.Ordinal)); return Cache.GetOrAdd(key, _ => CreateCatalog(input));
    }
    public static GeneratedTool[] CreateTools(params Delegate[] delegates) => [.. delegates.SelectMany(CreateTools)];
    public static GeneratedTool[] CreateTools(IEnumerable<Assembly> assemblies, params Delegate[] delegates) => [.. CreateTools(assemblies), .. CreateTools(delegates)];
    private static GeneratedToolCatalog CreateCatalog(IEnumerable<Assembly> assemblies)
    {
        List<GeneratedTool> primary = []; List<GeneratedToolCategory> categories = [];
        foreach (Type type in assemblies.Distinct().SelectMany(GetTypes).Where(type => type.GetCustomAttribute<CompilerGeneratedAttribute>() is null).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            MethodInfo[] methods = [.. type.GetMethods(Flags).Where(method => method.GetCustomAttribute<CompilerGeneratedAttribute>() is null && method.GetCustomAttribute<ToolAttribute>() is { IsManuallyRegistered: false, IsDeprecated: false }).OrderBy(method => method.MetadataToken)]; if (methods.Length == 0) continue;
            ToolCategoryAttribute? category = type.GetCustomAttribute<ToolCategoryAttribute>(); if (category is null) { primary.AddRange(methods.SelectMany(method => CreateDiscoveredTools(type, method, null))); continue; }
            GeneratedTool[] categoryTools = [.. methods.SelectMany(method => CreateDiscoveredTools(type, method, category))]; GeneratedTool categoryTool = CreateCategoryTool(category, categoryTools); categories.Add(new GeneratedToolCategory(category.Name, category.Description, categoryTool, categoryTools)); primary.Add(categoryTool); primary.AddRange(categoryTools.Where(tool => tool.IsRequired || tool.IsAlwaysAvailable));
        }
        return new GeneratedToolCatalog(primary, categories);
    }
    private static IEnumerable<Type> GetTypes(Assembly assembly) { try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException exception) { return exception.Types.OfType<Type>(); } }
    private static IReadOnlyList<GeneratedTool> CreateTools(Delegate @delegate)
    {
        ToolAttribute tool = @delegate.Method.GetCustomAttribute<ToolAttribute>() ?? throw new ArgumentException("The delegate method is not decorated with ToolAttribute.", nameof(@delegate)); if (tool.IsDeprecated) return []; GeneratedTool primary = CreateTool(@delegate.Method.DeclaringType, @delegate.Method, tool, @delegate.Target, null); return [primary, .. CreateAliases(primary, tool)];
    }
    private static IReadOnlyList<GeneratedTool> CreateDiscoveredTools(Type type, MethodInfo method, ToolCategoryAttribute? category) { ToolAttribute tool = method.GetCustomAttribute<ToolAttribute>()!; GeneratedTool primary = CreateTool(type, method, tool, null, category); return [primary, .. CreateAliases(primary, tool)]; }
    private static GeneratedTool CreateTool(Type? type, MethodInfo method, ToolAttribute tool, object? instance, ToolCategoryAttribute? category) => new(CreateFunction(method, tool), method.IsStatic ? null : type, method, instance, method.IsDefined(typeof(ToolRequiredAttribute), true), tool.IsLongRunning, tool.IsAlwaysAvailable, false, category?.Name, category?.Description, null, tool.FriendlyUIDescription, CreateCategoryNames(tool));
    private static IReadOnlyList<GeneratedTool> CreateAliases(GeneratedTool primary, ToolAttribute attribute) => [.. (attribute.Aliases ?? []).Where(alias => !string.IsNullOrWhiteSpace(alias) && !string.Equals(alias, primary.ResponseTool.FunctionName, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Select(alias => new GeneratedTool(ResponseTool.CreateFunctionTool(alias, primary.ResponseTool.FunctionParameters, false, primary.ResponseTool.FunctionDescription), primary.TargetType, primary.MethodInfo, primary.TargetInstance, primary.IsRequired, primary.IsLongRunning, primary.IsAlwaysAvailable, primary.IsCategoryTool, primary.CategoryName, primary.CategoryDescription, primary.CategoryTools, primary.FriendlyUIDescription, primary.CategoryNames))];
    private static GeneratedTool CreateCategoryTool(ToolCategoryAttribute category, IReadOnlyList<GeneratedTool> tools) => new(ResponseTool.CreateFunctionTool(category.Name, BinaryData.FromString("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"), false, $"{category.Description} Calling this tool returns an array of tools related to this category."), null, ToolCategoryBuilder.CategoryToolMethod, null, false, false, false, true, category.Name, category.Description, tools);
    private static FunctionTool CreateFunction(MethodInfo method, ToolAttribute tool)
    {
        JsonObject properties = []; JsonArray required = []; ToolParameterAttribute[] methodAttributes = [.. method.GetCustomAttributes<ToolParameterAttribute>(true)];
        foreach (ParameterInfo parameter in method.GetParameters()) { if (ToolParameterMetadata.Skip(parameter)) continue; ToolParameterAttribute? attribute = ToolParameterMetadata.GetAttribute(methodAttributes, parameter); string name = ToolParameterMetadata.GetName(parameter, attribute); if (properties.ContainsKey(name)) throw new InvalidOperationException($"Tool method '{method.DeclaringType?.FullName}.{method.Name}' defines the parameter '{name}' more than once."); Type schemaType = ToolParameterMetadata.GetSchemaType(parameter, attribute); JsonObject schema = ToolJsonSchemaBuilder.CreateParameterTypeSchema(schemaType, ToolJsonSchemaBuilder.CreateNullabilityInfo(parameter)); string? description = ToolJsonSchemaBuilder.Join(attribute?.Description, ToolEnumSchemaMetadata.CreateEnumValueDescription(schemaType)); if (!string.IsNullOrWhiteSpace(description)) schema["description"] = description; if (parameter.HasDefaultValue) ToolJsonSchemaBuilder.AddDefaultValue(schema, schemaType, parameter.DefaultValue); properties[name] = schema; if (attribute?.IsRequired ?? !parameter.HasDefaultValue) required.Add(name); }
        return ResponseTool.CreateFunctionTool(tool.Name, BinaryData.FromString(new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false }.ToJsonString()), false, tool.Description);
    }
    private static string[] CreateCategoryNames(ToolAttribute tool) => [.. (tool.Categories ?? []).Where(categoryName => !string.IsNullOrWhiteSpace(categoryName)).Select(categoryName => categoryName!.Trim()).Distinct(StringComparer.Ordinal)];
}