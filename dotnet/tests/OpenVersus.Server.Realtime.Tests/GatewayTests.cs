using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;

namespace OpenVersus.Server.Realtime.Tests;

/// <summary>
/// What only the C# gateway does: a second login closes the first connection and the old one's close changes nothing; a
/// close during a post-match rejoin keeps the player online; a game that stops answering is dropped, and one that never
/// sends its first frame is closed; a close request names its connection. Parity with the TS websocket (frames, closes,
/// presence) is tools/realtime/gateway_diff.mjs. Real Redis, database 13 (OVS_TEST_REDIS).
/// </summary>
public sealed class GatewayTests : IAsyncLifetime
{
    private const int TestRedisDb = 13;
    private const string Ip = "198.51.100.13";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(5);

    private readonly string _player = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()[..12]);
    private Factory? _factory;

    private IDatabase Redis => _factory!.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            string[] parts = s_redis!.Split(':');
            builder.UseSetting("REDIS", parts[0]);
            builder.UseSetting("REDIS_PORT", parts.Length > 1 ? parts[1] : "6379");
            builder.UseSetting("REDIS_USERNAME", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "");
            builder.UseSetting("REDIS_PW", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "");
            builder.UseSetting("REDIS_DB", TestRedisDb.ToString());
            builder.UseSetting("Access:JwtSecret", ServiceFactory<Program>.Secret);
            // Any free port, and no control listeners (their fixed ports may be taken).
            builder.UseSetting("WEBSOCKET_PORT", "0");
            builder.UseSetting("Control:Port", "0");
            builder.UseSetting("Control:Socket", "off");
            builder.UseSetting("Gateway:PingIntervalMs", "200");
            builder.UseSetting("Gateway:SilenceCutoffMs", "1000");
            builder.UseSetting("Gateway:HandshakeTimeoutMs", "1000");
        }
    }

    /// <summary>A fake game: its first frame carries a session token; every frame it gets is kept; it answers pings while told to.</summary>
    private sealed class Game
    {
        private readonly TaskCompletionSource<(WebSocketCloseStatus? Status, string? Reason)> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public required WebSocket Socket { get; init; }
        public List<byte[]> Frames { get; } = [];
        public bool AnswersPings { get; set; } = true;

        /// <summary>How the server closed it (null status: dropped with no close handshake).</summary>
        public Task<(WebSocketCloseStatus? Status, string? Reason)> Closed => _closed.Task;

        public async Task ReadAsync()
        {
            var buffer = new byte[65536];
            try
            {
                while (true)
                {
                    var result = await Socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _closed.TrySetResult((result.CloseStatus, result.CloseStatusDescription));
                        await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }

                    byte[] frame = buffer[..result.Count];
                    lock (Frames)
                    {
                        Frames.Add(frame);
                    }

                    if (AnswersPings && frame is [GatewayProtocol.Ping])
                    {
                        await Socket.SendAsync(new[] { GatewayProtocol.Pong }, WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                _closed.TrySetResult((null, null));
            }
        }
    }

    public async Task InitializeAsync()
    {
        if (s_redis is null)
        {
            return;
        }

        // On a real socket: the in-memory test server does not pass a server's abort on to its client.
        _factory = new Factory();
        _factory.UseKestrel(0);
        _factory.StartServer();
        await Redis.KeyDeleteAsync(GatewayPresence.ConnectionsStream);
    }

    public async Task DisposeAsync()
    {
        if (_factory is null)
        {
            return;
        }

        await Redis.KeyDeleteAsync([GatewayPresence.ConnectionKey(_player), $"rejoin_pending:{_player}", $"active_ip_accounts:{Ip}", GatewayPresence.ConnectionsStream]);
        await Redis.SetRemoveAsync(GatewayPresence.OnlinePlayers, _player);
        await Redis.SortedSetRemoveAsync(GatewayPresence.Heartbeats, _player);
        await _factory.DisposeAsync();
    }

    private async Task<Game> ConnectAsync(bool sendFirstFrame = true)
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("x-forwarded-for", Ip);
        string address = _factory!.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        await client.ConnectAsync(new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal)), CancellationToken.None);
        var game = new Game { Socket = client };
        _ = game.ReadAsync();
        if (sendFirstFrame)
        {
            int connected = (await EventsAsync()).Count(e => e.StartsWith("connected "));
            await game.Socket.SendAsync(FirstFrame(), WebSocketMessageType.Binary, true, CancellationToken.None);
            // The id frame and the first ping, then the handshake's presence (its connected event last).
            await Until(() => game.Frames.Count >= 2);
            await UntilAsync(async () => (await EventsAsync()).Count(e => e.StartsWith("connected ")) > connected);
        }

        return game;
    }

    // As the game's: 0x13 bytes, the token's length (u16, big-endian), the token, a 12-byte id.
    private byte[] FirstFrame()
    {
        byte[] token = Encoding.UTF8.GetBytes(AccessTokens.Sign(new JsonObject { ["id"] = _player }, ServiceFactory<Program>.Secret, TimeSpan.FromHours(1), DateTimeOffset.UtcNow));
        var frame = new byte[0x15 + token.Length + 12];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0x13), (ushort)token.Length);
        token.CopyTo(frame, 0x15);
        return frame;
    }

    private async Task<List<string>> EventsAsync() =>
        (await Redis.StreamRangeAsync(GatewayPresence.ConnectionsStream))
            .Where(e => e["player"] == _player)
            .Select(e => $"{e["type"]} {e["connection"]}")
            .ToList();

    private async Task<string?> CurrentConnectionAsync() => await Redis.HashGetAsync(GatewayPresence.ConnectionKey(_player), "id");

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow + s_wait;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(20);
        }
    }

    private async Task UntilAsync(Func<Task<bool>> done)
    {
        var until = DateTime.UtcNow + s_wait;
        while (!await done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_second_login_closes_the_first_and_the_first_one_s_close_leaves_the_player_online()
    {
        if (_factory is null)
        {
            return;
        }

        var first = await ConnectAsync();
        string? firstId = await CurrentConnectionAsync();
        var second = await ConnectAsync();
        string? secondId = await CurrentConnectionAsync();

        Assert.Equal((WebSocketCloseStatus.NormalClosure, "replaced"), await first.Closed.WaitAsync(s_wait));
        Assert.NotNull(firstId);
        Assert.NotEqual(firstId, secondId);
        await UntilAsync(async () => (await EventsAsync()).Count == 3);
        Assert.Equal([$"connected {firstId}", $"replaced {firstId}", $"connected {secondId}"], await EventsAsync());
        Assert.Equal(secondId, await CurrentConnectionAsync());
        Assert.True(await Redis.SetContainsAsync(GatewayPresence.OnlinePlayers, _player));
        Assert.NotNull(await Redis.SortedSetScoreAsync(GatewayPresence.Heartbeats, _player));

        // What is sent to the player reaches the second connection only.
        int before = second.Frames.Count;
        await Redis.PublishAsync(RedisChannel.Literal(GatewayChannels.Send), $$$"""{"playerIds":["{{{_player}}}"],"message":{"cmd":"hello"}}""");
        await Until(() => second.Frames.Count > before);

        await second.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await UntilAsync(async () => !await Redis.SetContainsAsync(GatewayPresence.OnlinePlayers, _player));
        Assert.Null(await Redis.SortedSetScoreAsync(GatewayPresence.Heartbeats, _player));
        Assert.Null(await CurrentConnectionAsync());
        Assert.Equal($"disconnected {secondId}", (await EventsAsync())[^1]);
    }

    [Fact]
    public async Task A_close_during_a_post_match_rejoin_keeps_the_player_online_but_not_their_heartbeat()
    {
        if (_factory is null)
        {
            return;
        }

        await Redis.StringSetAsync($"rejoin_pending:{_player}", "1", TimeSpan.FromSeconds(45));
        var game = await ConnectAsync();
        string? id = await CurrentConnectionAsync();

        await game.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await UntilAsync(async () => (await EventsAsync()).Contains($"disconnected {id}"));

        Assert.True(await Redis.SetContainsAsync(GatewayPresence.OnlinePlayers, _player));
        Assert.True((await Redis.SortedSetRangeByRankAsync($"active_ip_accounts:{Ip}")).Contains(_player));
        // The matchmaker's sign of a live game: a party ticket holding this player is dropped at once.
        Assert.Null(await Redis.SortedSetScoreAsync(GatewayPresence.Heartbeats, _player));
        Assert.Null(await CurrentConnectionAsync());
    }

    [Fact]
    public async Task A_game_that_stops_answering_is_dropped_and_one_that_answers_is_not()
    {
        if (_factory is null)
        {
            return;
        }

        var game = await ConnectAsync();
        // Answering (pings every 200 ms): still there after more than the cut-off.
        await Task.Delay(1500);
        Assert.False(game.Closed.IsCompleted);
        Assert.True(game.Frames.Count(f => f is [GatewayProtocol.Ping]) >= 5);

        game.AnswersPings = false;
        Assert.Equal((null, null), await game.Closed.WaitAsync(s_wait));
        await UntilAsync(async () => !await Redis.SetContainsAsync(GatewayPresence.OnlinePlayers, _player));
    }

    [Fact]
    public async Task A_socket_that_never_sends_its_first_frame_is_closed()
    {
        if (_factory is null)
        {
            return;
        }

        var game = await ConnectAsync(sendFirstFrame: false);

        await game.Closed.WaitAsync(s_wait);
        Assert.Empty(game.Frames);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task A_close_request_for_another_connection_is_ignored_and_one_for_this_connection_is_done()
    {
        if (_factory is null)
        {
            return;
        }

        var game = await ConnectAsync();
        string? id = await CurrentConnectionAsync();
        var channel = RedisChannel.Literal(GatewayChannels.Disconnect);

        await Redis.PublishAsync(channel, $$"""{"playerId":"{{_player}}","connectionId":"another","code":4000,"reason":"not this one"}""");
        await Redis.PublishAsync(channel, $$"""{"playerId":"{{_player}}","except":"{{id}}","code":4000,"reason":"not this one either"}""");
        await Task.Delay(300);
        Assert.False(game.Closed.IsCompleted);

        await Redis.PublishAsync(channel, $$"""{"playerId":"{{_player}}","connectionId":"{{id}}","code":4000,"reason":"this one"}""");
        Assert.Equal(((WebSocketCloseStatus)4000, "this one"), await game.Closed.WaitAsync(s_wait));
    }
}
