using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Perks;

// PUT /ssc/invoke/perks_lock {ContainerMatchId, Perks}, ported from the TS server's handleSsc_invoke_perks_lock
// (handlers/ssc.ts): the player's perks for the match are stored; once every player of the match (match:{match}, its
// tickets' players: the bots are locked at launch by MatchLauncher, spectators are in no ticket) has locked,
// perks:notifications {containerMatchId, playerIds (every ticket player, in ticket order)} is published. The TS websocket
// then puts each player's perks into the match config it sent them and sends it again (PerksLockedNotification).
// Answers {body: {}, metadata: null, return_code: 0}, whatever happened.
//
// Redis, written  match:{match}:perks:{player} (the Perks as sent) EX 20 min; match:{match}:perks_locked EX 20 min
// Redis, read     match:{match}; match:{match}:perks:{every other ticket player}
// Published       perks:notifications
//
// Unlike there:
//   no ContainerMatchId (or not text): nothing is stored (TS stores match:undefined:perks:{player}).
//   Perks missing or not a list: stored as [] (missing: the TS Redis write throws and the request is never answered).
//   the TS check that the match's status is not "locked" is left out: nothing writes that status, so it always passed.
//   published once per match (match:{match}:perks_locked, SET NX): two players locking at the same moment each see the
//   other's perks, and TS published (and the websocket resent every config) twice.

public interface IPerksLock
{
    /// <summary>Stores <paramref name="accountId"/>'s perks for the match in <paramref name="body"/>; publishes when all have.</summary>
    Task LockAsync(string accountId, JsonObject body, CancellationToken ct);
}

internal sealed class PerksLock(IServiceProvider services, ILogger<PerksLock> log) : IPerksLock
{
    public const string Channel = "perks:notifications";
    private static readonly TimeSpan s_ttl = TimeSpan.FromMinutes(20);

    public async Task LockAsync(string accountId, JsonObject body, CancellationToken ct)
    {
        if (body["ContainerMatchId"] is not JsonValue v || !v.TryGetValue(out string? matchId) || matchId.Length == 0)
        {
            log.LogWarning("perks_lock from {Player} without a ContainerMatchId; nothing stored", accountId);
            return;
        }

        var perks = body["Perks"] as JsonArray;
        if (perks is null)
        {
            log.LogWarning("perks_lock from {Player} in {Match} without a Perks list; locked with none", accountId, matchId);
        }

        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } db)
        {
            log.LogError("perks_lock from {Player} in {Match}: this service has no Redis (REDIS); nothing stored", accountId, matchId);
            return;
        }

        await db.StringSetAsync($"match:{matchId}:perks:{accountId}", Js.Stringify(perks ?? []), s_ttl);

        if (await db.StringGetAsync($"match:{matchId}") is not { HasValue: true } stored || Json(stored.ToString()) is not JsonObject match)
        {
            log.LogInformation("perks_lock from {Player}: match {Match} is not (or no longer) known", accountId, matchId);
            return;
        }

        var players = (match["tickets"] as JsonArray ?? [])
            .SelectMany(t => t?["players"] as JsonArray ?? [])
            .Select(p => p?["id"] is JsonValue id && id.TryGetValue(out string? s) ? s : null)
            .OfType<string>()
            .ToList();
        foreach (string other in players.Where(p => p != accountId))
        {
            if (!await db.KeyExistsAsync($"match:{matchId}:perks:{other}"))
            {
                return;
            }
        }

        if (!await db.StringSetAsync($"match:{matchId}:perks_locked", accountId, s_ttl, When.NotExists))
        {
            return;
        }

        await db.PublishAsync(RedisChannel.Literal(Channel), Js.Stringify(new JsonObject
        {
            ["containerMatchId"] = matchId,
            ["playerIds"] = new JsonArray([.. players.Select(p => (JsonNode)p)]),
        }));
        log.LogInformation("All perks locked {Match}, players, ({Players})", matchId, string.Join(",", players));
    }

    private static JsonNode? Json(string text)
    {
        try
        {
            return Js.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

public static class PerksLockHosting
{
    public static WebApplicationBuilder AddPerksLock(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IPerksLock, PerksLock>();
        return builder;
    }
}
