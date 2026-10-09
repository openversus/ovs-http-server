using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Shared.Stubs;

/// <summary>
/// A route the TS server never handled: it answered with its catch-all (200, {body: {Crc, MatchmakingCrc}, metadata:
/// null, return_code: 200}, Hydra for a Hydra request, after the token check), and so does this, with whichever
/// methods the subclass declares. That is what the game has always got for these routes, so it is the port of them.
/// The first hit of each route is logged as a warning with the method, the player and the body, so the first real call
/// shows what the route carries; later hits at debug. Not a stub: no X-OVS-Stub header, so a batch answers it here
/// instead of sending it to the TS server.
/// </summary>
public abstract class TsCatchAllEndpoint : JsonBodyEndpoint
{
    private static readonly ConcurrentDictionary<string, int> s_hits = new();

    public override async Task HandleAsync(CancellationToken ct)
    {
        int hits = s_hits.AddOrUpdate(GetType().Name, 1, static (_, n) => n + 1);
        if (hits == 1)
        {
            var body = await ReadBodyAsync(ct);
            Logger.LogWarning("{Endpoint}, called for the first time: {Method} {Path}{Query} by {Account}, body {Body}; answered as the TS server did (its catch-all)",
                GetType().Name, HttpContext.Request.Method, HttpContext.Request.Path, HttpContext.Request.QueryString,
                HttpContext.Session()?.AccountId ?? "(no session)", body is null ? "(none)" : Js.Stringify(body));
        }
        else
        {
            Logger.LogDebug("{Endpoint}: {Method} {Path}{Query} (hit {Hits}); answered as the TS server did (its catch-all)",
                GetType().Name, HttpContext.Request.Method, HttpContext.Request.Path, HttpContext.Request.QueryString, hits);
        }

        await SendJsonAsync(await TsCatchAll.AnswerAsync(HttpContext.RequestServices, ct), ct);
    }
}
