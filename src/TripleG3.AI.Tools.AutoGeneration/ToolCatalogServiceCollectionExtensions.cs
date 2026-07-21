using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TripleG3.Cis;

namespace TripleG3.AI.Tools.AutoGeneration;

/// <summary>Registers explicitly supplied assemblies for annotated tool discovery.</summary>
public static class ToolServiceCollectionExtensions
{
    public static IServiceCollection AddToolAssembly(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        services.AddSingleton(new ToolScanAssembly(assembly));
        services.TryAddSingleton<ToolCatalogService>();
        services.TryAddSingleton<IToolCatalogService>(provider => provider.GetRequiredService<ToolCatalogService>());
        services.TryAddSingleton<IStateService<GeneratedToolCatalog>>(provider => provider.GetRequiredService<ToolCatalogService>());
        return services;
    }

    public static IServiceCollection AddToolAssembly<TMarker>(this IServiceCollection services) =>
        services.AddToolAssembly(typeof(TMarker).Assembly);

    public static IServiceCollection AddToolAssemblies(this IServiceCollection services, IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (Assembly assembly in assemblies)
        {
            services.AddToolAssembly(assembly);
        }

        return services;
    }
}

public interface IToolCatalogService : IStateService<GeneratedToolCatalog>
{
    ValueTask<State<GeneratedToolCatalog>> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed class ToolScanAssembly(Assembly assembly)
{
    public Assembly Assembly { get; } = assembly ?? throw new ArgumentNullException(nameof(assembly));
}

public sealed class ToolCatalogService : StateService<GeneratedToolCatalog>, IToolCatalogService
{
    private readonly Lazy<GeneratedToolCatalog> catalog;

    public ToolCatalogService(IEnumerable<ToolScanAssembly> scanAssemblies)
    {
        ArgumentNullException.ThrowIfNull(scanAssemblies);
        Assembly[] assemblies = [.. scanAssemblies.Select(item => item.Assembly).Distinct()];
        catalog = new Lazy<GeneratedToolCatalog>(
            () => ResponseToolDiscovery.CreateToolCatalog(assemblies),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ValueTask<State<GeneratedToolCatalog>> LoadAsync(CancellationToken cancellationToken = default) =>
        SetAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(catalog.Value);
        }, cancellationToken);
}