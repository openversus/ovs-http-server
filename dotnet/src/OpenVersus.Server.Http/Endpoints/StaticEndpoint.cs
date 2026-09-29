using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints;

/// <summary>An endpoint whose answer is a <see cref="StaticResponses"/> file, the same for everyone.</summary>
public abstract class StaticEndpoint : EndpointWithoutRequest
{
    /// <summary>Sends Static/<paramref name="name"/>.json as JSON, or its Hydra encoding (made once) for a Hydra request.</summary>
    protected Task SendStaticAsync(string name, CancellationToken ct) =>
        HydraBodies.IsHydra(HttpContext)
            ? HydraBodies.WriteEncodedAsync(HttpContext, StaticResponses.Hydra(name), ct)
            : Send.StringAsync(StaticResponses.Json(name), contentType: "application/json; charset=utf-8", cancellation: ct);

    /// <summary>
    /// Answers as a stub would: for a path the route template matches but the TS server does not answer (it answers
    /// only literal "me" paths, say).
    /// </summary>
    protected Task SendNotPortedAsync()
    {
        HttpContext.Response.Headers[Stub.Header] = GetType().Name;
        return Send.ResultAsync(Results.StatusCode(Resolve<IOptionsMonitor<StubSettings>>().CurrentValue.StatusCode));
    }
}
