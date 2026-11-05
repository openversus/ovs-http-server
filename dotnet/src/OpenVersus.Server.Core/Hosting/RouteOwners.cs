using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;

namespace OpenVersus.Server.Core.Hosting;

/// <summary>
/// Which service answers a request: the owner docs/routes.json gives its route (tools/routes/sources/owners.tsv). A
/// request no route matches belongs to the HTTP service, where the router sends unknown paths. Where templates overlap
/// (<c>/accounts/{id}/{sub}</c> and <c>/accounts/{id}/relationships/followers</c>), the one with more literal segments
/// wins, and a route with the request's method beats one whose method is not known.
/// </summary>
public sealed class RouteOwners
{
    private static readonly Lazy<RouteOwners> s_routes = new(() =>
    {
        using var stream = typeof(RouteOwners).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.routes.json")
            ?? throw new InvalidOperationException("routes.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return new RouteOwners(doc.RootElement.EnumerateArray()
            .Select(r => (r.GetProperty("method").GetString()!, r.GetProperty("path").GetString()!, r.GetProperty("owner").GetString()!)));
    });

    private readonly (string Method, TemplateMatcher Matcher, int Literals, string Owner)[] _routes;

    /// <summary>The owners in docs/routes.json, as built into this binary.</summary>
    public static RouteOwners Routes => s_routes.Value;

    public RouteOwners(IEnumerable<(string Method, string Path, string Owner)> routes)
    {
        _routes = routes
            // The TS server's regex route (/.*/access) is not a template; it is the HTTP service's like anything unmatched.
            .Where(r => !r.Path.Contains(".*", StringComparison.Ordinal))
            .Select(r =>
            {
                var template = TemplateParser.Parse(r.Path);
                return (r.Method, new TemplateMatcher(template, []), template.Segments.Count(s => s.IsSimple && s.Parts[0].IsLiteral), r.Owner);
            })
            .ToArray();
    }

    /// <summary>The service name (<see cref="KnownServices"/>) that answers <paramref name="method"/> (the real one, after x-hydra-http-method) <paramref name="path"/>.</summary>
    public string OwnerOf(string method, PathString path)
    {
        var best = _routes
            .Where(r => (string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase) || r.Method == "?") && r.Matcher.TryMatch(path, new RouteValueDictionary()))
            .OrderByDescending(r => r.Literals)
            .ThenByDescending(r => r.Method != "?")
            .FirstOrDefault();
        return best.Owner ?? KnownServices.Http.Name;
    }
}
