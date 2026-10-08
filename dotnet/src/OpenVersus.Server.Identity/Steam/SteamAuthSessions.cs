using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Steam;
using StackExchange.Redis;

namespace OpenVersus.Server.Identity.Steam;

/// <summary>
/// The Steam identity service's heart: takes open requests (<see cref="SteamSessions.OpenQueue"/>), holds one auth
/// session per player with Steam through an <see cref="ISteamAuthClient"/>, and writes what Steam says where the other
/// services read it (<see cref="SteamSessions"/>). The first verdict on a session decides: OK is the proof (and presence,
/// steam:online); anything else refuses the ticket (its Steam id is dropped at the login for Steam:RefusalHoldMinutes
/// and the player is disconnected, as a ban does), except AuthTicketInvalidAlreadyUsed on a ticket this connection
/// itself opened, which is a lost reply and leaves the offline verdict standing. A verdict after OK (the game closed,
/// logged in elsewhere) ends presence and nothing more. No verdict within Steam:VerdictTimeoutMs, or no connection to
/// Steam: unavailable, the offline verdict stands. Sessions are keyed by the ticket's hash: the client sends the same
/// ticket more than once per launch, and a ticket is single-use per auth session, so a second open is a no-op and a new
/// ticket for the same Steam id replaces the old session.
/// <para>
/// Steam ends a session itself when the game closes (presence); the websocket never does: a game can lose its socket
/// and keep running. A Steam drop takes every held session with it (tickets cannot be reopened): they go unavailable
/// and each player's client is asked to register again (a reidentify notification) once Steam is back. At startup, what
/// an earlier process left in Redis is marked unavailable: nothing is held across a restart.
/// </para>
/// Every change is made under one gate, in the order the events arrive: Steam's thread, the Redis subscriptions and the
/// queue all post through it.
/// </summary>
public sealed class SteamAuthSessions(IServiceProvider services, ISteamAuthClient client, ServiceInstance instance, IOptionsMonitor<SteamSettings> settings,
    TimeProvider time, ILogger<SteamAuthSessions> log) : BackgroundService
{
    /// <summary>The notification the OpenVersus client gets when its Steam session was lost: mint a new ticket and POST /api/identify again.</summary>
    public const string ReidentifyNotification = "reidentify";

    /// <summary>An open request older than this is dropped: its game is most likely closed, and a new launch sent a new ticket.</summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan s_statusRefresh = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_idle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromSeconds(60);

    private sealed class Session(ulong steamId, SteamSessions.OpenRequest request, DateTimeOffset openedAt)
    {
        public ulong SteamId { get; } = steamId;
        public string SteamIdText { get; } = request.SteamId;
        public string PlayerId { get; set; } = request.PlayerId;
        public string Ip { get; } = request.Ip;
        public string Hash { get; } = request.Hash;
        public uint Crc { get; set; }
        public string State { get; set; } = SteamSessions.Pending;
        public string Response { get; set; } = "";
        public string Owner { get; set; } = "";
        public DateTimeOffset OpenedAt { get; } = openedAt;
        public DateTimeOffset? VerdictAt { get; set; }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Dictionary<ulong, Session> _sessions = [];
    private readonly HashSet<string> _opened = [];
    private readonly Dictionary<string, string> _reidentify = [];
    private readonly Dictionary<string, long> _verdicts = new() { ["ok"] = 0, ["refused"] = 0, ["canceled"] = 0, ["unavailable"] = 0, ["ignored"] = 0 };
    private bool _connected;
    private DateTimeOffset? _connectedSince;
    private DateTimeOffset _statusWritten = DateTimeOffset.MinValue;
    private string? _lastError;
    private bool _outageLogged;

    private IDatabase? Redis => services.GetService<IConnectionMultiplexer>()?.GetDatabase();

    // ── The loop ──────────────────────────────────────────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } multiplexer)
        {
            log.LogWarning("No Steam sessions are held: this service has no Redis (REDIS)");
            return;
        }

        var redis = multiplexer.GetDatabase();
        await SweepAsync(multiplexer, redis);
        var subscriber = multiplexer.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(SteamSessions.WakeChannel), (_, _) => _wake.Release());
        await subscriber.SubscribeAsync(RedisChannel.Literal(SteamSessions.EndChannel), (_, message) => Post(() => EndRequestedAsync(message.ToString())));
        client.Verdict += verdict => Post(() => VerdictAsync(verdict));
        client.Disconnected += reason => Post(() => DisconnectedAsync(reason));

        var backoff = TimeSpan.FromMilliseconds(settings.CurrentValue.ReconnectBackoffMs);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (!settings.CurrentValue.Enabled)
                    {
                        if (client.IsConnected)
                        {
                            log.LogInformation("Steam:Enabled is off: disconnecting from Steam; every held session ends");
                            await client.DisconnectAsync();
                            await DisconnectedAsync("Steam:Enabled off");
                        }

                        await WriteStatusAsync(redis, force: true);
                        await Task.Delay(TimeSpan.FromSeconds(1), time, stop);
                        continue;
                    }

                    if (!client.IsConnected)
                    {
                        try
                        {
                            await client.ConnectAsync(stop);
                            await ConnectedAsync();
                            backoff = TimeSpan.FromMilliseconds(settings.CurrentValue.ReconnectBackoffMs);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            _lastError = e.Message;
                            log.LogWarning("Could not connect to Steam ({Error}); next try in {Backoff}", e.Message, backoff);
                            await WriteStatusAsync(redis, force: true);
                            await Task.Delay(backoff, time, stop);
                            backoff = backoff * 2 > s_maxBackoff ? s_maxBackoff : backoff * 2;
                            continue;
                        }
                    }

                    await DrainAsync(redis);
                    await TickAsync();
                    await _wake.WaitAsync(s_idle, stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e) when (e is RedisException or TimeoutException)
                {
                    log.LogError(e, "Redis failed in the Steam sessions loop; retrying");
                    await Task.Delay(TimeSpan.FromSeconds(1), time, CancellationToken.None);
                }
            }
        }
        finally
        {
            await ShutdownAsync(redis);
        }
    }

    // A Steam or Redis event, applied under the gate in its own turn (the raising thread never waits on Redis).
    private void Post(Func<Task> handler) => _ = Task.Run(async () =>
    {
        try
        {
            await handler();
        }
        catch (Exception e)
        {
            log.LogError(e, "A Steam session event was not applied");
        }
    });

    // Every queued open request, oldest first.
    private async Task DrainAsync(IDatabase redis)
    {
        for (var value = await redis.ListRightPopAsync(SteamSessions.OpenQueue); value.HasValue; value = await redis.ListRightPopAsync(SteamSessions.OpenQueue))
        {
            if (SteamSessions.OpenRequest.Parse(value.ToString()) is { } request)
            {
                await OpenAsync(request);
            }
            else
            {
                log.LogWarning("Dropped an open request that is not one: {Request}", value.ToString());
            }
        }
    }

    // ── The events (public for the tests, which drive the machine without the loop) ──────────────────────────────

    /// <summary>An open request: holds the ticket and asks Steam, unless the same ticket is already held.</summary>
    public async Task OpenAsync(SteamSessions.OpenRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            if (Redis is not { } redis)
            {
                return;
            }

            var now = time.GetUtcNow();
            if (!ulong.TryParse(request.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) || !TryHex(request.Ticket, out byte[] authPart) || authPart.Length != 52)
            {
                log.LogWarning("Dropped an open request for Steam id {Steam} from {Ip}: not a Steam id or not a ticket's session part", request.SteamId, request.Ip);
                return;
            }

            if (now.ToUnixTimeMilliseconds() - request.RequestedAtMs > RequestLifetime.TotalMilliseconds)
            {
                log.LogInformation("Dropped an open request for {Steam} from {Ip}: {Age} old, its game is most likely closed", request.SteamId, request.Ip,
                    TimeSpan.FromMilliseconds(now.ToUnixTimeMilliseconds() - request.RequestedAtMs));
                return;
            }

            if (_sessions.TryGetValue(steamId, out var held))
            {
                if (held.Hash == request.Hash && held.State is SteamSessions.Pending or SteamSessions.Ok)
                {
                    if (held.PlayerId.Length == 0 && request.PlayerId.Length > 0)
                    {
                        held.PlayerId = request.PlayerId;
                        await WriteAsync(redis, held);
                    }

                    log.LogDebug("Open request for {Steam} repeats a held ticket ({State}): nothing to do", request.SteamId, held.State);
                    return;
                }

                // A new ticket: a new launch of the game. The old session is over on Steam's side (or will be).
                log.LogInformation("A new ticket for {Steam} replaces the session held since {Since:u} ({State})", request.SteamId, held.OpenedAt, held.State);
                client.End(steamId);
                _sessions.Remove(steamId);
            }

            var session = new Session(steamId, request, now);
            if (!client.IsConnected)
            {
                session.State = SteamSessions.Unavailable;
                session.Response = "not connected to Steam";
                await WriteAsync(redis, session);
                _reidentify[request.SteamId] = request.PlayerId;
                _verdicts["unavailable"]++;
                if (!_outageLogged)
                {
                    _outageLogged = true;
                    log.LogWarning("Not connected to Steam: the ticket of {Steam} is not checked online (the offline check stands); said once per outage", request.SteamId);
                }

                return;
            }

            _sessions[steamId] = session;
            _opened.Add(session.Hash);
            session.Crc = client.Open(steamId, authPart);
            await WriteAsync(redis, session);
            log.LogInformation("Asked Steam about the ticket of {Steam} (player {Player}, from {Ip}, crc {Crc})", request.SteamId, Dash(request.PlayerId), request.Ip, session.Crc);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Steam's verdict on a held ticket.</summary>
    public async Task VerdictAsync(SteamVerdict verdict)
    {
        await _gate.WaitAsync();
        try
        {
            if (Redis is not { } redis)
            {
                return;
            }

            if (!_sessions.TryGetValue(verdict.SteamId, out var session) || session.Crc != verdict.Crc)
            {
                _verdicts["ignored"]++;
                log.LogInformation("Ignored a verdict for Steam id {Steam} (crc {Crc}): {Response}; no such session is held", verdict.SteamId, verdict.Crc, verdict.Response);
                return;
            }

            var now = time.GetUtcNow();
            session.VerdictAt = now;
            session.Response = verdict.Response;
            session.Owner = verdict.OwnerSteamId == 0 ? "" : verdict.OwnerSteamId.ToString(CultureInfo.InvariantCulture);
            if (session.State == SteamSessions.Pending)
            {
                if (verdict.Ok)
                {
                    session.State = SteamSessions.Ok;
                    _verdicts["ok"]++;
                    // The request names no account on a first launch: the login's index may by now.
                    await PlayerIdAsync(redis, session);
                    await WriteAsync(redis, session);
                    await redis.HashSetAsync(SteamSessions.OnlineKey, session.SteamIdText, session.PlayerId);
                    await PublishAsync(redis, session);
                    log.LogInformation("Steam confirmed the ticket of {Steam} (player {Player}{Owner}): the session is held", session.SteamIdText, Dash(session.PlayerId),
                        session.Owner.Length > 0 && session.Owner != session.SteamIdText ? $", shared by {session.Owner}" : "");
                    return;
                }

                if (verdict.Response == SteamVerdict.AlreadyUsed && _opened.Contains(session.Hash))
                {
                    // Our own earlier open of this ticket is the one bound: the reply was lost. Nothing is known; the offline
                    // check stands.
                    session.State = SteamSessions.Unavailable;
                    _verdicts["unavailable"]++;
                    _sessions.Remove(session.SteamId);
                    client.End(session.SteamId);
                    await WriteAsync(redis, session);
                    log.LogWarning("Steam says the ticket of {Steam} is already in use, by this connection's earlier open: no verdict (the offline check stands)", session.SteamIdText);
                    return;
                }

                session.State = SteamSessions.Refused;
                _verdicts["refused"]++;
                _sessions.Remove(session.SteamId);
                client.End(session.SteamId);
                await WriteAsync(redis, session);
                await redis.HashDeleteAsync(SteamSessions.OnlineKey, session.SteamIdText);
                await PublishAsync(redis, session);
                string playerId = await PlayerIdAsync(redis, session);
                log.LogWarning("Steam REFUSED the ticket of {Steam} from {Ip} ({Response}); player {Player} is disconnected and the Steam id is a claim for {Hold} min",
                    session.SteamIdText, session.Ip, verdict.Response, Dash(playerId), settings.CurrentValue.RefusalHoldMinutes);
                if (playerId.Length > 0)
                {
                    await PlayerMessages.DisconnectAsync(redis, new JsonObject { ["playerId"] = playerId });
                }

                return;
            }

            if (session.State == SteamSessions.Ok)
            {
                if (verdict.Ok)
                {
                    // Steam judges every entry again each time the list is re-sent (another player's open or end) and
                    // answers OK again for a held one: not a change. Measured on the bench, 2026-10-08.
                    log.LogDebug("Steam confirmed the held ticket of {Steam} again", session.SteamIdText);
                    return;
                }

                // The game closed (AuthTicketCanceled), or Steam ended it for another reason: presence off, nothing more.
                session.State = SteamSessions.Canceled;
                _verdicts["canceled"]++;
                _sessions.Remove(session.SteamId);
                client.End(session.SteamId);
                await WriteAsync(redis, session);
                await redis.HashDeleteAsync(SteamSessions.OnlineKey, session.SteamIdText);
                await PublishAsync(redis, session);
                log.LogInformation("Steam ended the session of {Steam} (player {Player}): {Response}", session.SteamIdText, Dash(session.PlayerId), verdict.Response);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Logged on to Steam: presence can be held again; the clients whose sessions were lost are asked to register again.</summary>
    public async Task ConnectedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _connected = true;
            _connectedSince = time.GetUtcNow();
            _outageLogged = false;
            _lastError = null;
            log.LogInformation("Connected to Steam as an anonymous game server");
            if (Redis is not { } redis)
            {
                return;
            }

            await WriteStatusAsync(redis, force: true);
            var lost = _reidentify.ToList();
            _reidentify.Clear();
            foreach (var (steamId, playerId) in lost)
            {
                await ReidentifyAsync(redis, steamId, playerId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The connection to Steam dropped: every held session is gone; each becomes unavailable and its player is asked to register again once Steam is back.</summary>
    public async Task DisconnectedAsync(string reason)
    {
        await _gate.WaitAsync();
        try
        {
            bool wasConnected = _connected;
            _connected = false;
            _connectedSince = null;
            _lastError = reason;
            if (wasConnected)
            {
                log.LogWarning("Disconnected from Steam ({Reason}): {Count} held session(s) are lost until their clients register again", reason, _sessions.Count);
            }

            if (Redis is { } redis)
            {
                await LoseAllAsync(redis, "disconnected from Steam: " + reason);
                await WriteStatusAsync(redis, force: true);
            }
            else
            {
                _sessions.Clear();
                _opened.Clear();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A ban: the session held for the Steam id in the message ends now.</summary>
    public async Task EndRequestedAsync(string? message)
    {
        string steamIdText = "";
        try
        {
            steamIdText = (string?)(JsonNode.Parse(message ?? "")?["steamId"]) ?? "";
        }
        catch (System.Text.Json.JsonException)
        {
            // Not ours.
        }

        if (!ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId))
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(steamId, out var session) || Redis is not { } redis)
            {
                return;
            }

            client.End(steamId);
            _sessions.Remove(steamId);
            session.State = SteamSessions.Canceled;
            session.Response = "ended";
            session.VerdictAt = time.GetUtcNow();
            await WriteAsync(redis, session);
            await redis.HashDeleteAsync(SteamSessions.OnlineKey, session.SteamIdText);
            await PublishAsync(redis, session);
            log.LogInformation("Ended the Steam session of {Steam} (player {Player}) on request", session.SteamIdText, Dash(session.PlayerId));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Time passing: a session with no verdict within Steam:VerdictTimeoutMs is unavailable, and the status record is refreshed.</summary>
    public async Task TickAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Redis is not { } redis)
            {
                return;
            }

            var now = time.GetUtcNow();
            var timeout = TimeSpan.FromMilliseconds(settings.CurrentValue.VerdictTimeoutMs);
            foreach (var session in _sessions.Values.Where(s => s.State == SteamSessions.Pending && now - s.OpenedAt > timeout).ToList())
            {
                session.State = SteamSessions.Unavailable;
                session.Response = "no verdict";
                session.VerdictAt = now;
                _verdicts["unavailable"]++;
                _sessions.Remove(session.SteamId);
                client.End(session.SteamId);
                await WriteAsync(redis, session);
                log.LogWarning("No verdict from Steam on the ticket of {Steam} within {Timeout}: unavailable (the offline check stands)", session.SteamIdText, timeout);
            }

            await WriteStatusAsync(redis, force: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The state as the control API shows it.</summary>
    public async Task<SteamAuthStatus> StatusAsync()
    {
        await _gate.WaitAsync();
        try
        {
            long queued = 0;
            if (Redis is { } redis)
            {
                try
                {
                    queued = await redis.ListLengthAsync(SteamSessions.OpenQueue);
                }
                catch (Exception e) when (e is RedisException or TimeoutException)
                {
                    queued = -1;
                }
            }

            var byState = _sessions.Values.GroupBy(s => s.State).ToDictionary(g => g.Key, g => g.Count());
            var held = _sessions.Values.OrderBy(s => s.OpenedAt)
                .Select(s => new SteamAuthSessionView(s.SteamIdText, s.PlayerId, s.State, s.Response, s.Owner, s.OpenedAt.ToUnixTimeMilliseconds(), s.VerdictAt?.ToUnixTimeMilliseconds()))
                .ToList();
            return new SteamAuthStatus(settings.CurrentValue.Enabled, _connected, _connectedSince?.ToUnixTimeMilliseconds(), instance.Id, _lastError, queued,
                byState, new Dictionary<string, long>(_verdicts), held);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Startup and shutdown ──────────────────────────────────────────────────────────────────────────────────────

    // What an earlier process left: no session is held across a restart, so every ok or pending record is unavailable
    // now and nobody is online through Steam. Their clients are asked to register again once this process is connected.
    private async Task SweepAsync(IConnectionMultiplexer multiplexer, IDatabase redis)
    {
        try
        {
            await redis.KeyDeleteAsync(SteamSessions.OnlineKey);
            int swept = 0;
            var server = multiplexer.GetServers().FirstOrDefault(s => s.IsConnected) ?? multiplexer.GetServers().First();
            await foreach (var key in server.KeysAsync(redis.Database, SteamSessions.SessionKey("*"), pageSize: 500))
            {
                var values = await redis.HashGetAsync(key, ["state", "player_id"]);
                if (values[0].ToString() is SteamSessions.Ok or SteamSessions.Pending)
                {
                    string steamId = key.ToString()[SteamSessions.SessionKey("").Length..];
                    await redis.HashSetAsync(key, [new HashEntry("state", SteamSessions.Unavailable), new HashEntry("response", "service restarted"), new HashEntry("instance", instance.Id)]);
                    await redis.PublishAsync(RedisChannel.Literal(SteamSessions.PresenceChannel), SteamSessions.PresenceMessage(steamId, values[1].ToString(), SteamSessions.Unavailable, "service restarted"));
                    _reidentify[steamId] = values[1].ToString();
                    swept++;
                }
            }

            if (swept > 0)
            {
                log.LogInformation("{Count} Steam session(s) from before this start are unavailable now; their clients will be asked to register again", swept);
            }
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogError(e, "Could not sweep the Steam sessions of an earlier process");
        }
    }

    private async Task ShutdownAsync(IDatabase redis)
    {
        await _gate.WaitAsync();
        try
        {
            if (_sessions.Count > 0)
            {
                log.LogInformation("Stopping: {Count} held Steam session(s) end; their clients will be asked to register again by the next process", _sessions.Count);
            }

            try
            {
                await LoseAllAsync(redis, "service stopped");
                // The next process asks them; this one queues the notification now so a short restart loses nothing.
                foreach (var (steamId, playerId) in _reidentify.ToList())
                {
                    await ReidentifyAsync(redis, steamId, playerId);
                }

                _reidentify.Clear();
                await redis.KeyDeleteAsync(SteamSessions.StatusKey);
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                log.LogError(e, "Could not record the end of the held Steam sessions");
            }

            _connected = false;
            try
            {
                await client.DisconnectAsync();
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Disconnecting from Steam failed");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Every held session becomes unavailable (its player to be asked to register again); presence off for those that had it.
    private async Task LoseAllAsync(IDatabase redis, string response)
    {
        var now = time.GetUtcNow();
        foreach (var session in _sessions.Values.ToList())
        {
            bool wasOk = session.State == SteamSessions.Ok;
            session.State = SteamSessions.Unavailable;
            session.Response = response;
            session.VerdictAt = now;
            await WriteAsync(redis, session);
            if (wasOk)
            {
                await redis.HashDeleteAsync(SteamSessions.OnlineKey, session.SteamIdText);
                await PublishAsync(redis, session);
            }

            _reidentify[session.SteamIdText] = session.PlayerId;
        }

        _sessions.Clear();
        _opened.Clear();
    }

    // ── Redis ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task WriteAsync(IDatabase redis, Session session)
    {
        string key = SteamSessions.SessionKey(session.SteamIdText);
        await redis.HashSetAsync(key,
        [
            new HashEntry("state", session.State),
            new HashEntry("response", session.Response),
            new HashEntry("owner_steam_id", session.Owner),
            new HashEntry("player_id", session.PlayerId),
            new HashEntry("ip", session.Ip),
            new HashEntry("ticket_hash", session.Hash),
            new HashEntry("opened_at", session.OpenedAt.ToUnixTimeMilliseconds()),
            new HashEntry("verdict_at", session.VerdictAt is { } at ? at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : ""),
            new HashEntry("instance", instance.Id),
        ]);
        await redis.KeyExpireAsync(key, SteamSessions.SessionLifetime);
    }

    private static Task PublishAsync(IDatabase redis, Session session) =>
        redis.PublishAsync(RedisChannel.Literal(SteamSessions.PresenceChannel), SteamSessions.PresenceMessage(session.SteamIdText, session.PlayerId, session.State, session.Response));

    private async Task WriteStatusAsync(IDatabase redis, bool force)
    {
        var now = time.GetUtcNow();
        if (!force && now - _statusWritten < s_statusRefresh)
        {
            return;
        }

        _statusWritten = now;
        await redis.HashSetAsync(SteamSessions.StatusKey,
        [
            new HashEntry("connected", _connected ? "1" : ""),
            new HashEntry("since", _connectedSince?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? ""),
            new HashEntry("instance", instance.Id),
            new HashEntry("enabled", settings.CurrentValue.Enabled ? "1" : ""),
        ]);
        await redis.KeyExpireAsync(SteamSessions.StatusKey, SteamSessions.StatusLifetime);
    }

    // The player the session belongs to: the request named one, else the login's index (identity:steam:{id}).
    private static async Task<string> PlayerIdAsync(IDatabase redis, Session session)
    {
        if (session.PlayerId.Length > 0)
        {
            return session.PlayerId;
        }

        string indexed = (await redis.StringGetAsync($"identity:steam:{session.SteamIdText}")).ToString();
        if (indexed.Length > 0)
        {
            session.PlayerId = indexed;
        }

        return indexed;
    }

    private async Task ReidentifyAsync(IDatabase redis, string steamId, string playerId)
    {
        if (playerId.Length == 0)
        {
            playerId = (await redis.StringGetAsync($"identity:steam:{steamId}")).ToString();
        }

        if (playerId.Length == 0)
        {
            return;
        }

        await PlayerMessages.NotifyClientAsync(redis, playerId, ReidentifyNotification, "", "", new JsonObject { ["steamId"] = steamId }, time.GetUtcNow().ToUnixTimeMilliseconds());
        log.LogInformation("Asked the client of player {Player} ({Steam}) to register again", playerId, steamId);
    }

    private static bool TryHex(string text, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string Dash(string value) => value.Length > 0 ? value : "-";
}
