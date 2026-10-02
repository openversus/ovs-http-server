using System.Text.Json.Nodes;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// A party lobby route (<see cref="IPartyService"/>). One the game also uses inside a custom lobby (<see cref="Shared"/>)
/// answers those requests from the custom lobby (<see cref="ICustomLobbyService.SharedAsync"/>), or as a party route when
/// that has no answer (the TS server fell through to its party route the same way).
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

        var body = (await ReadBodyAsync(ct)) as JsonObject;
        var request = new PartyRequest(session.AccountId, session.Claims, ClientAddress.Of(HttpContext, stripMapped: true), body);
        var party = Resolve<IPartyService>();
        if (Shared && await party.CustomLobbyAsync(Route, request) is { } custom
            && await Resolve<ICustomLobbyService>().SharedAsync(Route, request, custom, ct) is { } answer)
        {
            await SendJsonAsync(answer, ct);
            return;
        }

        await SendJsonAsync(await AnswerAsync(party, request, ct), ct);
    }
}
