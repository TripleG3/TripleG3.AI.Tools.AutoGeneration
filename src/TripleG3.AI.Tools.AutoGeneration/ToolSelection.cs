namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>Describes which generated tools should be exposed to a model or transport.</summary>
public sealed record ToolSelection
{
    private ToolSelection(
        bool includeAll,
        IReadOnlySet<string> categories,
        bool includeRequiredTools,
        bool includeToolboxSelectors)
    {
        IncludeAll = includeAll;
        Categories = categories;
        IncludeRequiredTools = includeRequiredTools;
        IncludeToolboxSelectors = includeToolboxSelectors;
    }

    public bool IncludeAll { get; }

    /// <summary>Capability categories matched using an ordinal, case-sensitive comparison.</summary>
    public IReadOnlySet<string> Categories { get; }

    public bool IncludeRequiredTools { get; }

    public bool IncludeToolboxSelectors { get; }

    public static ToolSelection All { get; } = new(
        includeAll: true,
        categories: new HashSet<string>(StringComparer.Ordinal),
        includeRequiredTools: true,
        includeToolboxSelectors: false);

    public static ToolSelection ForCategories(
        IEnumerable<string> categories,
        bool includeRequiredTools = true,
        bool includeToolboxSelectors = false)
    {
        ArgumentNullException.ThrowIfNull(categories);

        HashSet<string> normalized = new(StringComparer.Ordinal);
        foreach (string category in categories)
        {
            if (!string.IsNullOrWhiteSpace(category))
            {
                normalized.Add(category.Trim());
            }
        }

        return new(
            includeAll: false,
            categories: normalized,
            includeRequiredTools,
            includeToolboxSelectors);
    }

    public static ToolSelection ForCategories(
        params string[] categories) =>
        ForCategories(categories, includeRequiredTools: true, includeToolboxSelectors: false);

    public ToolSelection WithOptions(
        bool? includeRequiredTools = null,
        bool? includeToolboxSelectors = null) =>
        new(
            IncludeAll,
            Categories,
            includeRequiredTools ?? IncludeRequiredTools,
            includeToolboxSelectors ?? IncludeToolboxSelectors);
}
