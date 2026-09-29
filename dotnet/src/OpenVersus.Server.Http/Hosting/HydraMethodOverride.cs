namespace OpenVersus.Server.Http.Hosting;

/// <summary>
/// The Hydra SDK sends some GETs as a PUT carrying the real method in <c>x-hydra-http-method</c> (the bulk lookups,
/// and <c>/profiles/{id}/inventory</c>, which also has a real PUT on the same path: only this header tells the two
/// apart). Routing has to see the real method, so this runs before it. ASP.NET Core's own method-override
/// middleware only reads <c>X-HTTP-Method-Override</c>.
/// </summary>
public static class HydraMethodOverride
{
    public const string Header = "x-hydra-http-method";

    private static readonly HashSet<string> s_methods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get, HttpMethods.Put, HttpMethods.Post, HttpMethods.Delete, HttpMethods.Patch,
    };

    public static IApplicationBuilder UseHydraMethodOverride(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            string? method = context.Request.Headers[Header];
            if (method is not null && s_methods.Contains(method))
            {
                context.Request.Method = method.ToUpperInvariant();
            }

            return next(context);
        });
}
