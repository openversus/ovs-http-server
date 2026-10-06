using OpenVersus.Server.Core.Clients;

namespace OpenVersus.Server.Http.Shared.Hosting;

/// <summary>
/// The TS server's requireCurrentClientForGameplay (services/clientUpdateGate.ts, mounted in server.ts after the token
/// check), on what leads into a match (queueing, starting a custom match, entering Rifts or starting a node, accepting a
/// rematch): from a player whose client must update (<see cref="IClientUpdateGate"/>) it is turned away with the gate's
/// answer, HTTP 200 and return_code 1, and the player is sent the update toast. Login, the menus and lobbies stay open: creating and
/// joining a party or custom lobby are not gated (the TS server gated them too; the game has no failure path for creating
/// a custom lobby, and waits for it forever). Readying in a party lobby, the game's step before its matchmaking request
/// and the refusal it backs out of, is gated by the party service (PartyService.SetReadyAsync), the whole party at once.
/// <para>
/// The paths are matched as Express's app.use matches them: any method, any letter case, the path itself or anything
/// below it (/ssc/invoke/start_custom_match and /ssc/invoke/start_custom_match/x, not /ssc/invoke/start_custom_match_x).
/// A batch's sub-requests go through it too, as there. When the gate cannot be evaluated (no Redis), 503 with the same
/// answer, as there.
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
        "/ssc/invoke/start_custom_match",
        "/ssc/invoke/rematch_accept",
        // Rifts (not in the TS server's gate list): entering them, the game's first rift request and the refusal on that path
        // it backs out of (refused later, in the rift lobby or at the node, it waits on its loading screen); a node's match
        // behind it.
        "/ssc/invoke/get_or_create_rift_state",
        "/ssc/invoke/create_rift_lobby",
        "/ssc/invoke/start_rift_node",
        "/ssc/invoke/retry_current_rift_node",
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
