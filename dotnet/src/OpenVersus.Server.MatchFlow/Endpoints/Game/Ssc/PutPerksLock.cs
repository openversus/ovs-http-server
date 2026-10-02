using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/perks_lock {ContainerMatchId, Perks}: the player's perks for the match (<see cref="IPerksLock"/>).
/// Answers {body: {}} whatever happened, as the TS server did (with no session too: there it was never answered).
/// Seen in: binary ssc name; captured 9x; TS server: PUT /ssc/invoke/perks_lock.
/// Ssc: server/capture.
/// </summary>
public sealed class PutPerksLock : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/perks_lock");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session() is { AccountId.Length: > 0 } session)
        {
            await Resolve<IPerksLock>().LockAsync(session.AccountId, await ReadBodyAsync(ct) as JsonObject ?? [], ct);
        }
        else
        {
            Logger.LogWarning("perks_lock with no session; answered empty");
        }

        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}
