using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;

namespace OpenVersus.Server.Http.Shared.Stubs;

/// <summary>Stub settings, changeable while the server runs (the control API, and so the CLI).</summary>
public sealed class StubSettings
{
    [Description("The status every endpoint that is not ported yet answers with. The game tends to fall over on answers it does not expect, so this is one setting for all of them. A route the TS server never handled is not a stub: it answers as the TS catch-all did (TsCatchAllEndpoint), and so does the fallback.")]
    [Range(100, 599)]
    public int StatusCode { get; set; } = StatusCodes.Status501NotImplemented;
}

/// <summary>
/// What a route that has not been ported yet answers: <see cref="StubSettings.StatusCode"/>, read on every request so
/// a change applies at once. A route that needs something else overrides <see cref="StubEndpoint.StubStatusCode"/>. A route
/// the TS server never handled is <see cref="TsCatchAllEndpoint"/> instead.
/// </summary>
public static class Stub
{
    /// <summary>The response header naming the stub that answered, so a capture or a test can tell which one it was.</summary>
    public const string Header = "X-OVS-Stub";

    /// <summary>The response header naming the endpoint that answered, ported or not.</summary>
    public const string EndpointHeader = "X-OVS-Endpoint";

    /// <summary>The fallback's name in <see cref="Header"/>: no endpoint claimed the request.</summary>
    public const string FallbackName = "Fallback";

    /// <summary>
    /// Answers a request no endpoint claims as the TS server's catch-all did (it answered every path it had no route
    /// for the same way, after the token check), and logs it: the route map may be missing the route.
    /// </summary>
    public static async Task FallbackAsync(HttpContext context)
    {
        var log = context.RequestServices.GetRequiredService<ILogger<StubEndpoint>>();
        log.LogWarning("No endpoint: {Method} {Path}{Query}; answered as the TS server did (its catch-all)", context.Request.Method, context.Request.Path, context.Request.QueryString);
        context.Response.Headers[EndpointHeader] = FallbackName;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(Js.Stringify(await TsCatchAll.AnswerAsync(context.RequestServices, context.RequestAborted)), context.RequestAborted);
    }
}

/// <summary>
/// An endpoint that has not been ported yet: logs the request and answers <see cref="StubStatusCode"/>. Porting a
/// route means deriving its class from a real endpoint type instead and writing its handler.
/// </summary>
public abstract class StubEndpoint : EndpointWithoutRequest
{
    /// <summary>The status this stub answers with: the <see cref="StubSettings.StatusCode"/> setting unless a route needs something else.</summary>
    protected virtual int StubStatusCode => Resolve<IOptionsMonitor<StubSettings>>().CurrentValue.StatusCode;

    // Hits per stub. The first is a warning; the rest are debug, or a route the game polls (the client mod asks for
    // /ovs/notifications every 2 s) would bury everything else in the log.
    private static readonly ConcurrentDictionary<string, int> s_hits = new();

    public override Task HandleAsync(CancellationToken ct)
    {
        int hits = s_hits.AddOrUpdate(GetType().Name, 1, static (_, n) => n + 1);
        Logger.Log(hits == 1 ? LogLevel.Warning : LogLevel.Debug, "Not ported: {Method} {Path}{Query} -> {Endpoint} (hit {Hits})",
            HttpContext.Request.Method, HttpContext.Request.Path, HttpContext.Request.QueryString, GetType().Name, hits);
        HttpContext.Response.Headers[Stub.Header] = GetType().Name;
        return Send.ResultAsync(Results.StatusCode(StubStatusCode));
    }
}
