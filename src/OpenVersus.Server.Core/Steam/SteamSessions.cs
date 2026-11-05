using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Steam;

/// <summary>
/// What the Steam identity service (OpenVersus.Server.Identity.Steam) shares with the services that ask it to open a
/// session or read what Steam said: the Redis keys and channels, the states, and the readers. The service is the only
/// writer of the session records; everything here reads them or queues a request.
/// <list type="bullet">
/// <item><c>steam:auth:open</c> (list): open requests, <see cref="OpenRequest"/> as JSON, LPUSHed by /api/identify and
/// taken by the service (RPOP); <c>steam:auth:wake</c> is published after each push so the service looks at once. A
/// request outlives a service restart: the list is durable, a channel is not.</item>
/// <item><c>steam:auth:end</c> (channel): {steamId}, a ban: the service ends the session it holds for that Steam id.</item>
/// <item><c>steam:session:{steamId}</c> (hash, 24 h): state, response (Steam's answer), owner_steam_id (Family
/// Sharing: the lender; recorded, never a ban's concern), player_id, ip, ticket_hash, opened_at, verdict_at
/// (Unix ms), instance (the service replica holding it).</item>
/// <item><c>steam:online</c> (hash): Steam id -> player id while the session is ok: the game runs under that Steam
/// account (the title screen included), which is not "connected to our server" (online_players).</item>
/// <item><c>steam:presence</c> (channel): {steamId, playerId, state, response} on every change.</item>
/// <item><c>steam:status</c> (hash, 10 s): {connected, since, instance}, refreshed by the service while it runs: absent,
/// the service is down and nothing here is trusted (identify does not wait; presence falls through).</item>
/// </list>
/// </summary>
public static class SteamSessions
{
    public const string OpenQueue = "steam:auth:open";
    public const string WakeChannel = "steam:auth:wake";
    public const string EndChannel = "steam:auth:end";
    public const string PresenceChannel = "steam:presence";
    public const string OnlineKey = "steam:online";
    public const string StatusKey = "steam:status";

    public const string Pending = "pending";
    public const string Ok = "ok";
    public const string Refused = "refused";
    public const string Canceled = "canceled";
    public const string Unavailable = "unavailable";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(10);

    public static string SessionKey(string steamId) => $"steam:session:{steamId}";

    /// <summary>A request to open an auth session: the ticket's first 52 bytes (its session part, hex), the whole ticket's SHA-256, and whose it is.</summary>
    /// <param name="PlayerId">The account the registration resolved to, or "" on a first launch.</param>
    public sealed record OpenRequest(string SteamId, string PlayerId, string Ip, string Ticket, string Hash, long RequestedAtMs)
    {
        public string ToJson() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

        public static OpenRequest? Parse(string? json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                var request = JsonSerializer.Deserialize<OpenRequest>(json, JsonSerializerOptions.Web);
                return request is { SteamId.Length: > 0, Ticket.Length: > 0, Hash.Length: > 0 } ? request : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>A session record as stored; null fields were never written.</summary>
    public sealed record SessionView(string State, string Response, string OwnerSteamId, string PlayerId, string TicketHash, long? OpenedAtMs, long? VerdictAtMs, string Instance);

    /// <summary>Queues an open request for the service and wakes it.</summary>
    public static async Task QueueOpenAsync(IDatabase redis, OpenRequest request)
    {
        await redis.ListLeftPushAsync(OpenQueue, request.ToJson());
        await redis.PublishAsync(RedisChannel.Literal(WakeChannel), request.SteamId);
    }

    /// <summary>Tells the service to end the session it holds for <paramref name="steamId"/> (a ban).</summary>
    public static Task PublishEndAsync(IDatabase redis, string steamId) =>
        redis.PublishAsync(RedisChannel.Literal(EndChannel), new JsonObject { ["steamId"] = steamId }.ToJsonString());

    /// <summary>Whether a Steam identity service is running and connected to Steam right now.</summary>
    public static async Task<bool> ConnectedAsync(IDatabase redis) =>
        (await redis.HashGetAsync(StatusKey, "connected")).ToString() == "1";

    public static async Task<SessionView?> ReadAsync(IDatabase redis, string steamId)
    {
        var values = await redis.HashGetAsync(SessionKey(steamId), ["state", "response", "owner_steam_id", "player_id", "ticket_hash", "opened_at", "verdict_at", "instance"]);
        if (values[0].IsNullOrEmpty)
        {
            return null;
        }

        return new SessionView(values[0].ToString(), values[1].ToString(), values[2].ToString(), values[3].ToString(), values[4].ToString(),
            Ms(values[5]), Ms(values[6]), values[7].ToString());
    }

    /// <summary>Whether Steam refused a ticket for <paramref name="steamId"/> within <paramref name="hold"/>: the id is then a claim, not an identity.</summary>
    public static async Task<bool> RefusedRecentlyAsync(IDatabase redis, string steamId, TimeSpan hold, DateTimeOffset now)
    {
        if (hold <= TimeSpan.Zero)
        {
            return false;
        }

        var values = await redis.HashGetAsync(SessionKey(steamId), ["state", "verdict_at"]);
        return values[0].ToString() == Refused && Ms(values[1]) is { } at && now.ToUnixTimeMilliseconds() - at <= hold.TotalMilliseconds;
    }

    /// <summary>
    /// What Steam says about each player's presence, for the readers that paint an online indicator: true, the game
    /// runs under their Steam account (the session is ok); false, Steam saw the game close or refused it within
    /// <paramref name="overrideFor"/> (the websocket's online_players lags a closed game by the reaper's timeout, and a
    /// refused player is being disconnected); null, no say (no session, pending, unavailable, an older verdict, another
    /// player's session under a shared Steam id, or no service running): the caller's own truth decides. Players
    /// without a Steam id get null.
    /// </summary>
    public static async Task<bool?[]> PresenceAsync(IDatabase redis, IReadOnlyList<(string PlayerId, string SteamId)> players, TimeSpan overrideFor, DateTimeOffset now)
    {
        var result = new bool?[players.Count];
        if (players.Count == 0 || !await ConnectedAsync(redis))
        {
            return result;
        }

        var batch = redis.CreateBatch();
        var reads = new Task<RedisValue[]>?[players.Count];
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].SteamId.Length > 0)
            {
                reads[i] = batch.HashGetAsync(SessionKey(players[i].SteamId), ["state", "player_id", "verdict_at"]);
            }
        }

        batch.Execute();
        long nowMs = now.ToUnixTimeMilliseconds();
        for (int i = 0; i < players.Count; i++)
        {
            if (reads[i] is not { } read)
            {
                continue;
            }

            var values = await read;
            if (values[1].ToString() != players[i].PlayerId)
            {
                continue;
            }

            string state = values[0].ToString();
            if (state == Ok)
            {
                result[i] = true;
            }
            else if (state is Canceled or Refused && Ms(values[2]) is { } at && nowMs - at <= overrideFor.TotalMilliseconds)
            {
                result[i] = false;
            }
        }

        return result;
    }

    private static long? Ms(RedisValue value) =>
        value.HasValue && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) ? ms : null;

    /// <summary>The presence message for <see cref="PresenceChannel"/>.</summary>
    public static string PresenceMessage(string steamId, string playerId, string state, string response) =>
        Js.Stringify(new JsonObject { ["steamId"] = steamId, ["playerId"] = playerId, ["state"] = state, ["response"] = response });
}
