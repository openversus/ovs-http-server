using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// A party lobby route (<see cref="IPartyService"/>). One the game also uses inside a custom lobby (<see cref="Shared"/>)
/// hands those requests to the TS server, which still answers custom lobbies (<see cref="TsForwarder"/>).
/// </summary>
public abstract class PartyEndpoint : JsonBodyEndpoint
{
    /// <summary>The route's name: /ssc/invoke/{Route}.</summary>
    protected abstract string Route { get; }

    /// <summary>The route also serves custom lobbies.</summary>
    protected virtual bool Shared => false;

    protected abstract Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct);

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
            await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
            return;
        }

        HttpContext.Request.EnableBuffering();
        var body = (await ReadBodyAsync(ct)) as JsonObject;
        HttpContext.Request.Body.Position = 0;
        var request = new PartyRequest(session.AccountId, session.Claims, ClientAddress.Of(HttpContext, stripMapped: true), body);
        var party = Resolve<IPartyService>();
        if (Shared && await party.CustomLobbyAsync(Route, request) is { } custom)
        {
            Logger.LogInformation("{Route}: custom lobby {Lobby}: answered by the TS server", Route, custom);
            await TsForwarder.ForwardAsync(HttpContext, ct);
            return;
        }

        await SendJsonAsync(await AnswerAsync(party, request, ct), ct);
    }
}
