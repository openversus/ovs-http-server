using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.TestSupport;

/// <summary>
/// What a service's container does not check when it is built, though it validates every constructor: an options class
/// nothing binds (IOptions&lt;T&gt; always resolves, to T's defaults), and a service an endpoint looks up while answering
/// (Resolve&lt;T&gt;), which no constructor names. An executable that leaves out an Add... call then runs on defaults or
/// fails on every request, and only a request finds out.
/// </summary>
public static class Registrations
{
    // Present only when the service is configured with them (REDIS, MONGO_URI); ServiceStores and readiness cover them.
    private static readonly HashSet<string> s_stores = ["IConnectionMultiplexer", "IDatabase", "IMongoClient", "IMongoDatabase"];

    private static readonly Type[] s_optionsInterfaces = [typeof(IOptions<>), typeof(IOptionsMonitor<>), typeof(IOptionsSnapshot<>)];

    private static readonly Regex s_class = new(@"\bclass\s+(?<name>\w+)", RegexOptions.Compiled);

    private static readonly Regex s_lookup = new(@"(?<!\w)(?:Resolve|GetRequiredService)<(?<type>[\w.]+(?:<[\w.]+>)?)>\(", RegexOptions.Compiled);

    /// <summary>
    /// Each options class a constructor of an OpenVersus service in <paramref name="services"/> takes that has no binding
    /// (no IConfigureOptions&lt;T&gt;: AddSetting, Configure or Bind was never called for it).
    /// </summary>
    public static IReadOnlyList<string> UnboundOptions(IServiceCollection services, IServiceProvider provider)
    {
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in services)
        {
            var type = descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;
            if (type is null || type.IsGenericTypeDefinition || type.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) != true)
            {
                continue;
            }

            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                if (OptionsOf(parameter.ParameterType) is { } options && !IsBound(provider, options))
                {
                    problems.Add($"{type.Name} takes {Name(parameter.ParameterType)}, and nothing binds {options.Name}");
                }
            }
        }

        return [.. problems];
    }

    /// <summary>
    /// Each Resolve&lt;T&gt; or GetRequiredService&lt;T&gt; that <paramref name="provider"/> cannot answer (an unregistered
    /// service, or options nothing binds), in the code this service runs: its own project's source; Http.Shared's, whose
    /// pipeline every game HTTP service runs; and the Core files that declare a class <paramref name="registered"/> names.
    /// The stores are left out: whether a service has them is configuration. TryResolve is left out: it is written for an
    /// absent service.
    /// </summary>
    public static IReadOnlyList<string> UnresolvableLookups(IServiceProvider provider, Assembly program, IServiceCollection registered)
    {
        var isService = provider.GetRequiredService<IServiceProviderIsService>();
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var registeredNames = registered.Select(d => d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType)
            .OfType<Type>().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        string own = ProjectDirectory(program.GetName().Name!);
        var sources = SourceFiles(own).Select(f => (Root: own, File: f));
        if (program.GetReferencedAssemblies().Any(a => a.Name == "OpenVersus.Server.Http.Shared"))
        {
            string shared = ProjectDirectory("OpenVersus.Server.Http.Shared");
            sources = sources.Concat(SourceFiles(shared).Select(f => (Root: shared, File: f)));
        }

        string core = ProjectDirectory("OpenVersus.Server.Core");
        sources = sources.Concat(SourceFiles(core)
            .Where(f => s_class.Matches(File.ReadAllText(f)).Any(m => registeredNames.Contains(m.Groups["name"].Value)))
            .Select(f => (Root: core, File: f)));

        foreach (var (root, file) in sources)
        {
            string relative = Path.Combine(Path.GetFileName(root), Path.GetRelativePath(root, file));
            foreach (Match match in s_lookup.Matches(File.ReadAllText(file)))
            {
                string text = match.Groups["type"].Value;
                string? problem = Check(text);
                if (problem is not null)
                {
                    problems.Add($"{relative}: {match.Value.TrimEnd('(')}: {problem}");
                }
            }
        }

        return [.. problems];

        string? Check(string text)
        {
            var generic = Regex.Match(text, @"^(?:[\w.]+\.)?(?<outer>IOptions|IOptionsMonitor|IOptionsSnapshot)<(?<inner>[\w.]+)>$");
            if (generic.Success)
            {
                var options = Find(generic.Groups["inner"].Value);
                return options.Count == 0 ? "no such type is loaded"
                    : options.Any(t => IsBound(provider, t)) ? null : "nothing binds it";
            }

            // Any other generic (ILogger<T>): the open type closed over the argument, as the container would build it.
            var closed = Regex.Match(text, @"^(?:[\w.]+\.)?(?<outer>\w+)<(?:[\w.]+\.)?(?<inner>\w+)>$");
            if (closed.Success)
            {
                var built = Find(closed.Groups["outer"].Value + "`1", definitions: true)
                    .SelectMany(o => Find(closed.Groups["inner"].Value).Select(i => o.MakeGenericType(i))).ToList();
                return built.Count == 0 ? "no such type is loaded" : built.Any(isService.IsService) ? null : "not registered";
            }

            string name = text[(text.LastIndexOf('.') + 1)..];
            if (s_stores.Contains(name))
            {
                return null;
            }

            var types = Find(name);
            return types.Count == 0 ? "no such type is loaded"
                : types.Any(isService.IsService) ? null : "not registered";
        }
    }

    private static Type? OptionsOf(Type type) =>
        type.IsGenericType && s_optionsInterfaces.Contains(type.GetGenericTypeDefinition()) ? type.GetGenericArguments()[0] : null;

    private static bool IsBound(IServiceProvider provider, Type options) =>
        provider.GetServices(typeof(IConfigureOptions<>).MakeGenericType(options)).Any();

    private static string Name(Type type) =>
        type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>" : type.Name;

    private static List<Type> Find(string name, bool definitions = false) =>
        AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).SelectMany(Types)
            .Where(t => t.Name == name && t.IsGenericTypeDefinition == definitions).ToList();

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f =>
        {
            string relative = Path.GetRelativePath(root, f);
            return !relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        });

    // src/{project}, found from the test's output directory upwards.
    private static string ProjectDirectory(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"src/{name} not found above {AppContext.BaseDirectory}");
    }
}
