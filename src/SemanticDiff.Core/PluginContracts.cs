namespace SemanticDiff.Core;

public interface IPluginModule
{
    string Id { get; }

    void Register(IPluginRegistry registry);
}

public interface IPluginRegistry
{
    void Add<TService>(TService service)
        where TService : notnull;

    IReadOnlyList<TService> GetServices<TService>()
        where TService : notnull;
}

public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<Type, List<object>> services = new();

    public void Add<TService>(TService service)
        where TService : notnull
    {
        var serviceType = typeof(TService);

        if (!services.TryGetValue(serviceType, out var registeredServices))
        {
            registeredServices = [];
            services.Add(serviceType, registeredServices);
        }

        registeredServices.Add(service);
    }

    public IReadOnlyList<TService> GetServices<TService>()
        where TService : notnull
    {
        return services.TryGetValue(typeof(TService), out var registeredServices)
            ? registeredServices.Cast<TService>().ToArray()
            : [];
    }
}