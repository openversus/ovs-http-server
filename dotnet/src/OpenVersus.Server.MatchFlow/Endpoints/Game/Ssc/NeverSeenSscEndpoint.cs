using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// An SSC route known only by its name in the game's binary: never captured, never seen in a server log, and never
/// handled by the TS server, which answered it with its catch-all. Answers what the game has always got
/// (<see cref="TsCatchAll"/>), with any method, and logs the request (method, player, body) so the first real call
/// shows what the route carries.
/// </summary>
public abstract class NeverSeenSscEndpoint : JsonBodyEndpoint
{
    /// <summary>The route's name: /ssc/invoke/{Route}.</summary>
    protected abstract string Route { get; }

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes($"/ssc/invoke/{Route}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct);
        Logger.LogWarning("{Route}, never seen before, called: {Method} by {Account}, body {Body}; answered as the TS server did (its catch-all)",
            Route, HttpContext.Request.Method, HttpContext.Session()?.AccountId ?? "(no session)", body is null ? "(none)" : Js.Stringify(body));
        await SendJsonAsync(await TsCatchAll.AnswerAsync(TryResolve<IMongoDatabase>(), ct), ct);
    }
}
