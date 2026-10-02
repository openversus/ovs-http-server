using OpenVersus.Server.Core.Clients;

namespace OpenVersus.Server.Http.Shared.Hosting;

/// <summary>
/// The TS server's requireCurrentClientForGameplay (services/clientUpdateGate.ts, mounted in server.ts after the token
/// check): a gameplay transition (queueing, joining or starting a lobby or custom match, readying up, a rematch) from a
/// player whose client must update (<see cref="IClientUpdateGate"/>) is turned away with the gate's answer, HTTP 200 and
/// return_code 1, and the websocket is asked to show the player the update toast. Login and the menus stay open.
/// <para>
/// The paths are the TS server's, matched as Express's app.use matches them: any method, any letter case, the path
/// itself or anything below it (/ssc/invoke/join_party_lobby and /ssc/invoke/join_party_lobby/x, not
/// /ssc/invoke/join_party_lobby_x). A batch's sub-requests go through it too, as there. When the gate cannot be
/// evaluated (no Redis), 503 with the same answer, as there.
/// </para>
/// </summary>
public static class ClientGameplayGate
{
    public static readonly IReadOnlyList<string> Paths =
    [
        "/matches/matchmaking/1v1-retail/request",
        "/matches/matchmaking/ranked-1v1-retail/request",
        "/matches/matchmaking/2v2-retail/request",
        // The Casual queue, which the TS server never answered (its gate list had no entry for it).
        "/matches/matchmaking/casual-retail/request",
        "/ssc/invoke/casual_queue",
        "/ssc/invoke/create_custom_game_lobby",
        "/ssc/invoke/join_custom_game_lobby",
        "/ssc/invoke/start_custom_match",
        "/ssc/invoke/join_party_lobby",
        "/ssc/invoke/autoparty_join",
        "/ssc/invoke/set_ready_for_lobby",
        "/ssc/invoke/rematch_accept",
    ];

    public static bool Covers(PathString path) => Paths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    public static IApplicationBuilder UseClientGameplayGate(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // Only a request this service answers (an endpoint here, the fallback included): another service's route is
        // its owner's to gate, and here it is a 404.
        if (!Covers(context.Request.Path) || context.GetEndpoint() is null)
        {
            await next(context);
            return;
        }

        var gate = context.RequestServices.GetRequiredService<IClientUpdateGate>();
        var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ClientGameplayGate));
        ClientUpdateState state;
        bool modalRequested = false;
        try
        {
            state = await gate.ForRequestAsync(AccountLookups.From(context), context.Session()?.Claims);
            if (state.Required)
            {
                modalRequested = await gate.RequestModalsAsync([state.AccountId]) is [true];
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            log.LogError("Failed to evaluate gameplay gate: {Error}", e.Message);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await HydraBodies.WriteAsync(context, gate.FailureBody(), context.RequestAborted);
            return;
        }

        // Outside the try: the endpoint's own failures are not the gate's (in TS, Express catches them inside next()).
        if (!state.Required)
        {
            await next(context);
            return;
        }

        if (modalRequested)
        {
            var settings = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ClientSettings>>().CurrentValue;
            log.LogWarning("Blocked {Method} {Path}{Query} for {Account} (version={Version}, identity={Identity}, minimum={Minimum}); requested reward-free update toast",
                context.Request.Method, context.Request.Path, context.Request.QueryString, state.AccountId.Length > 0 ? state.AccountId : "unresolved",
                state.ClientVersion.Length > 0 ? state.ClientVersion : "legacy", state.IdentityRegistered ? "registered" : "missing",
                settings.MinimumVersion.Length > 0 ? settings.MinimumVersion : "disabled");
        }

        await HydraBodies.WriteAsync(context, gate.FailureBody(), context.RequestAborted);
    });
}
