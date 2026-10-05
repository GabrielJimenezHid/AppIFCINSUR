using System.Reflection;
using System.Runtime.Loader;
using Insur.Plugins.Abstractions;

namespace Insur.Host;

public sealed class PluginLoader
{
    private readonly Dictionary<string, IIfcPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IIfcPlugin> Plugins => _plugins.Values;

    public IIfcPlugin? Find(string id) => _plugins.GetValueOrDefault(id);

    public void LoadFrom(string directory, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogWarning("Carpeta de plugins no encontrada: {Dir}", directory);
            return;
        }

        foreach (var pluginDir in Directory.GetDirectories(directory))
        {
            var dll = Path.Combine(pluginDir, Path.GetFileName(pluginDir) + ".dll");
            if (!File.Exists(dll)) continue;

            try
            {
                var context = new PluginLoadContext(dll);
                var assembly = context.LoadFromAssemblyPath(dll);
                foreach (var type in assembly.GetTypes()
                             .Where(t => typeof(IIfcPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    var plugin = (IIfcPlugin)Activator.CreateInstance(type)!;
                    _plugins[plugin.Manifest.Id] = plugin;
                    logger.LogInformation("Plugin cargado: {Name} {Version}", plugin.Manifest.Name, plugin.Manifest.Version);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo cargar el plugin {Dll}", dll);
            }
        }
    }

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(IIfcPlugin).Assembly.GetName().Name) return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
