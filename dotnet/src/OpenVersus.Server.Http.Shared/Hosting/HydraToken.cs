using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Http.Shared.Hosting;

/// <summary>Endpoint metadata: the request must carry a valid game session token (x-hydra-access-token).</summary>
public sealed class RequiresHydraToken
{
    public static readonly RequiresHydraToken Instance = new();

    private RequiresHydraToken()
    {
    }
}

/// <summary>
/// A game endpoint the TS server answers without a token (it registers them before its token check): the login, the
/// SDK's token exchange, the leaderboard views. With <see cref="RouteValue"/> set, only when that route value is
/// <see cref="Value"/> (the TS route is narrower than the C# template).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NoHydraTokenAttribute : Attribute
{
    public string? RouteValue { get; init; }

    public string? Value { get; init; }
}

/// <summary>
/// A route that is not the game's but sits behind the TS server's hydraTokenMiddleware all the same (mounted after it in
/// server.ts): it requires the session token as a game endpoint does.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class HydraTokenRequiredAttribute : Attribute
{
}

/// <summary>Who the request is from: the verified game session token.</summary>
public sealed record HydraSession(string RawToken, JsonObject Claims)
{
    /// <summary>The player's account id (the token's id claim).</summary>
    public string AccountId => Claims["id"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string)Claims["id"]! : "";
}

/// <summary>
/// The TS server's hydraTokenMiddleware: a game route answers only a request with a valid x-hydra-access-token (the
/// /access token), else 401 with <c>{"error": "Missing access token"}</c> or <c>{"error": "Invalid access token"}</c>
/// (Hydra-encoded for a Hydra request, as there). Which endpoints: every game endpoint unless it carries
/// <see cref="NoHydraTokenAttribute"/>, and the fallback (the TS server checks unknown paths too). Runs after routing.
/// <para>
/// The TS server also skips the check, always, when the Host header's name is WB_DOMAIN; here only with
/// <see cref="AccessSettings.SkipTokenCheckForDomainHost"/>, which is off by default. Such a request has no session.
/// </para>
/// </summary>
public static class HydraToken
{
    public const string Header = "x-hydra-access-token";

    public static IApplicationBuilder UseHydraToken(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<RequiresHydraToken>() is null || Exempt(context, endpoint.Metadata.GetMetadata<NoHydraTokenAttribute>()))
        {
            await next(context);
            return;
        }

        if (DomainHost(context))
        {
            await next(context);
            return;
        }

        var headers = context.Request.Headers;
        if (!headers.TryGetValue(Header, out var values) || values.Count == 0)
        {
            await RefuseAsync(context, "Missing access token");
            return;
        }

        string? secret = context.RequestServices.GetRequiredService<IOptionsMonitor<AccessSettings>>().CurrentValue.JwtSecret;
        if (string.IsNullOrEmpty(secret))
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HydraToken))
                .LogError("Refusing {Method} {Path}: this service has no JWT secret (JWT_SECRET)", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // Node joins a repeated header with ", ", which then is not a token.
        string token = values.Count == 1 ? values[0]! : string.Join(", ", values.ToArray());
        try
        {
            context.Features.Set(new HydraSession(token, AccessTokens.Verify(token, secret, DateTimeOffset.UtcNow)));
        }
        catch (AccessTokenException e)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HydraToken))
                .LogDebug("Rejected an invalid access token on {Method} {Path}: {Error}", context.Request.Method, context.Request.Path, e.Message);
            await RefuseAsync(context, "Invalid access token");
            return;
        }

        await next(context);
    });

    /// <summary>
    /// The request's verified session, or null: an endpoint that requires one has it unless the request came in under
    /// <see cref="AccessSettings.SkipTokenCheckForDomainHost"/>.
    /// </summary>
    public static HydraSession? Session(this HttpContext context) => context.Features.Get<HydraSession>();

    // Express's req.hostname without trust proxy: the Host header's name, port removed, compared exactly.
    private static bool DomainHost(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptionsMonitor<AccessSettings>>().CurrentValue.SkipTokenCheckForDomainHost
        && string.Equals(context.Request.Host.Host, context.RequestServices.GetRequiredService<IOptionsMonitor<RealtimeSettings>>().CurrentValue.Domain, StringComparison.Ordinal);

    private static bool Exempt(HttpContext context, NoHydraTokenAttribute? exemption) =>
        exemption is not null && (exemption.RouteValue is null || string.Equals(context.GetRouteValue(exemption.RouteValue)?.ToString(), exemption.Value, StringComparison.Ordinal));

    private static Task RefuseAsync(HttpContext context, string error)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(new JsonObject { ["error"] = error }.ToJsonString());
    }
}
