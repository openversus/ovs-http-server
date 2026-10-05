using System.Text.Json.Nodes;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>A custom lobby route (<see cref="ICustomLobbyService"/>): PUT /ssc/invoke/{Route}.</summary>
public abstract class CustomLobbyEndpoint : JsonBodyEndpoint
{
    /// <summary>The route's name: /ssc/invoke/{Route}.</summary>
    protected abstract string Route { get; }

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes($"/ssc/invoke/{Route}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session() is not { } session || TryResolve<IConnectionMultiplexer>() is null)
        {
            Logger.LogWarning("{Route} with no session or no Redis; answered empty", Route);
            await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 1 }, ct);
            return;
        }

        var body = (await ReadBodyAsync(ct)) as JsonObject;
        var request = new PartyRequest(session.AccountId, session.Claims, ClientAddress.Of(HttpContext, stripMapped: true), body);
        await SendJsonAsync(await Resolve<ICustomLobbyService>().AnswerAsync(Route, request, ct), ct);
    }
}
