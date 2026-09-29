using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;

namespace OpenVersus.Server.Core.Hosting;

/// <summary>
/// A list of routes written as <c>METHOD /path, METHOD /path, ...</c>, the route being a template as a C# endpoint
/// declares it (<c>GET /profiles/{id}/inventory</c>): the proxy's Proxy:PortedRoutes and the http service's
/// Batch:ForwardRoutes.
/// </summary>
public sealed class RouteList
{
    private readonly (string Method, TemplateMatcher Matcher)[] _routes;

    /// <exception cref="FormatException">An entry is not <c>METHOD /path</c>.</exception>
    public RouteList(string? routes)
    {
        _routes = Parse(routes).Select(r => (r.Method, new TemplateMatcher(TemplateParser.Parse(r.Path), []))).ToArray();
    }

    /// <summary>"POST /access, GET /profiles/{id}" as (method, path) pairs; anything else is refused.</summary>
    public static IReadOnlyList<(string Method, string Path)> Parse(string? routes)
    {
        var parsed = new List<(string, string)>();
        foreach (string entry in (routes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].ToUpperInvariant() is not ("GET" or "PUT" or "POST" or "DELETE") || !parts[1].StartsWith('/'))
            {
                throw new FormatException($"\"{entry}\" is not METHOD /path");
            }

            parsed.Add((parts[0].ToUpperInvariant(), parts[1]));
        }

        return parsed;
    }

    /// <summary>Whether a request with this method (the real one, after x-hydra-http-method) and path is listed.</summary>
    public bool Contains(string method, PathString path) =>
        _routes.Any(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase) && r.Matcher.TryMatch(path, []));
}
