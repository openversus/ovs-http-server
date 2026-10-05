using System.Text.Json;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Preferences;

namespace OpenVersus.Server.Http.Shared.Hosting;

/// <summary>
/// The TS server's recordGameplayPreferencesFromRequest (services/gameplayPreferences.ts, mounted after the token check):
/// the game sends its current GameplayPreferences (the player's input settings) with every party-lobby request, and it is
/// stored (<see cref="IGameplayPreferencesStore"/>) before the endpoint runs, so the endpoint and every match after it use
/// the player's current value. Never turns a request away. The paths are matched as Express's app.use matches them (the
/// path itself or anything below it, any letter case); a batch's sub-requests go through it too.
/// </summary>
public static class GameplayPreferencesRecorder
{
    public static readonly IReadOnlyList<string> Paths =
    [
        "/ssc/invoke/create_party_lobby",
        "/ssc/invoke/lock_lobby_loadout",
        "/ssc/invoke/set_ready_for_lobby",
        "/ssc/invoke/set_lobby_joinable",
        "/ssc/invoke/set_lobby_not_joinable",
        // Answered here, not by the TS server.
        "/ssc/invoke/create_rift_lobby",
        "/ssc/invoke/lock_rift_lobby_loadout",
    ];

    public static bool Covers(PathString path) => Paths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    public static IApplicationBuilder UseGameplayPreferencesRecorder(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // Only a request this service answers (as the client gate).
        if (Covers(context.Request.Path) && context.GetEndpoint() is not null && context.Session() is { } session)
        {
            try
            {
                // The endpoint reads the body again: a Hydra body is already a rewindable copy, a JSON one is buffered.
                context.Request.EnableBuffering();
                JsonNode? body = null;
                try
                {
                    body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                }
                catch (JsonException)
                {
                }
                finally
                {
                    context.Request.Body.Position = 0;
                }

                if (body is JsonObject request && request.TryGetPropertyValue("GameplayPreferences", out var raw))
                {
                    string? ip = session.Claims["current_ip"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
                    await context.RequestServices.GetRequiredService<IGameplayPreferencesStore>().SaveAsync(session.AccountId, raw, ip, context.RequestAborted);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GameplayPreferencesRecorder))
                    .LogError("Could not record GameplayPreferences from {Path}: {Error}", context.Request.Path, e.Message);
            }
        }

        await next(context);
    });
}
