using FastEndpoints;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Shared.Endpoints;

/// <summary>An endpoint whose answer is a <see cref="StaticResponses"/> file, the same for everyone.</summary>
public abstract class StaticEndpoint : EndpointWithoutRequest
{
    /// <summary>Sends Static/<paramref name="name"/>.json as JSON, or its Hydra encoding (made once) for a Hydra request.</summary>
    protected Task SendStaticAsync(string name, CancellationToken ct) =>
        HydraBodies.IsHydra(HttpContext)
            ? HydraBodies.WriteEncodedAsync(HttpContext, StaticResponses.Hydra(name), ct)
            : Send.StringAsync(StaticResponses.Json(name), contentType: "application/json; charset=utf-8", cancellation: ct);

    /// <summary>
    /// Answers as the TS server's catch-all did: for a path the route template matches but the TS server has no route
    /// for (it answers only literal "me" paths, say), which its catch-all answered after the token check.
    /// </summary>
    protected async Task SendTsCatchAllAsync(CancellationToken ct)
    {
        Logger.LogDebug("{Endpoint}: {Method} {Path}{Query} is not a path the TS server had a route for; answered as its catch-all did",
            GetType().Name, HttpContext.Request.Method, HttpContext.Request.Path, HttpContext.Request.QueryString);
        await Send.StringAsync(Js.Stringify(await TsCatchAll.AnswerAsync(HttpContext.RequestServices, ct)), contentType: "application/json; charset=utf-8", cancellation: ct);
    }
}
