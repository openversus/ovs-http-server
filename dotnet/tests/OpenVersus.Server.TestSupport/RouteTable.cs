using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenVersus.Server.TestSupport;

/// <summary>A route of docs/routes.json.</summary>
public sealed record Route(string Method, string Path, string Kind, string Area, string Owner);

/// <summary>docs/routes.json, found from the test's output directory upwards.</summary>
public static class RouteTable
{
    public static readonly string[] AllVerbs = ["GET", "PUT", "POST", "DELETE"];

    public static IReadOnlyList<Route> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "routes.json")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new FileNotFoundException("docs/routes.json not found above " + AppContext.BaseDirectory);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "docs", "routes.json")));
        return doc.RootElement.EnumerateArray()
            .Select(r => new Route(r.GetProperty("method").GetString()!, r.GetProperty("path").GetString()!, r.GetProperty("kind").GetString()!,
                r.GetProperty("area").GetString()!, r.GetProperty("owner").GetString()!))
            // The TS server's regex route (/.*/access) has no concrete form.
            .Where(r => !r.Path.StartsWith("/.*", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// The owner of the routes <paramref name="path"/> is, whatever their method (as a middleware that matches paths only
    /// sees them); null when it is no route, "mixed" when they disagree.
    /// </summary>
    public static string? OwnerOfPath(string path)
    {
        var owners = Load().Where(r => Regex.IsMatch(path, "^" + Regex.Replace(Regex.Escape(r.Path), @"\\\{[^}]+\}", "[^/]+") + "$"))
            .Select(r => r.Owner).Distinct().ToList();
        return owners.Count switch { 0 => null, 1 => owners[0], _ => "mixed" };
    }

    /// <summary>A concrete path for a template: each parameter becomes a value, a trailing * one more segment.</summary>
    public static string Concrete(string template) =>
        Regex.Replace(template.EndsWith("/*", StringComparison.Ordinal) ? template[..^1] + "x9/y9" : template, @"\{[^}]+\}", "x1");
}
