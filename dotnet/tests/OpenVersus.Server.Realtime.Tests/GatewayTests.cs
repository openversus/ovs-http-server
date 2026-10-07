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
    private ConnectionMultiplexer? _redis;

    // The test's own connection: it outlives the node (a test stops it).
    private IDatabase Redis => _redis!.GetDatabase(TestRedisDb);

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
            builder.UseSetting("Gateway:EdgeSecret", EdgeSecret);
            builder.UseSetting("Gateway:EdgeDetachGraceMs", GraceMs.ToString());
        }
    }

    private const string EdgeSecret = "the-edges-and-the-nodes-share-this";
    private const int GraceMs = 1500;

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
        string[] parts = s_redis.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } } };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        await Redis.KeyDeleteAsync(GatewayPresence.ConnectionsStream);
    }

    public async Task DisposeAsync()
    {
        if (_factory is null)
        {
            return;
        }

        await Redis.KeyDeleteAsync([GatewayPresence.ConnectionKey(_player), $"rejoin_pending:{_player}", $"active_ip_accounts:{Ip}", GatewayPresence.ConnectionsStream,
            PlayerMessages.LogKey(_player)]);
        await Redis.SetRemoveAsync(GatewayPresence.OnlinePlayers, _player);
        await Redis.SortedSetRemoveAsync(GatewayPresence.Heartbeats, _player);
        await _factory.DisposeAsync();
        await _redis!.DisposeAsync();
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

    /// <summary>A fake edge's link for one game: every envelope the node sends is kept; it answers pings for the game.</summary>
    private sealed class EdgeLink
    {
        private readonly TaskCompletionSource<(WebSocketCloseStatus? Status, string? Reason)> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public required ClientWebSocket Socket { get; init; }
        public List<GatewayEdge.Envelope> Frames { get; } = [];

        /// <summary>How the node ended the link (null status: dropped with no close handshake).</summary>
        public Task<(WebSocketCloseStatus? Status, string? Reason)> Closed => _closed.Task;

        public GatewayEdge.Envelope[] Snapshot()
        {
            lock (Frames)
            {
                return [.. Frames];
            }
        }

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

                    var envelope = GatewayEdge.Read(buffer.AsSpan(0, result.Count));
                    lock (Frames)
                    {
                        Frames.Add(envelope);
                    }

                    if (envelope is { Kind: GatewayEdge.Kind.Unlogged, Frame: [GatewayProtocol.Ping] })
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

    // An edge's link for this test's player, named connectionId, with the game's first frame sent and the handshake done.
    private async Task<EdgeLink> ConnectEdgeAsync(string connectionId, string secret = EdgeSecret, string? resumeAfter = null)
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("x-forwarded-for", Ip);
        client.Options.SetRequestHeader(GatewayEdge.SecretHeader, secret);
        client.Options.SetRequestHeader(GatewayEdge.ConnectionIdHeader, connectionId);
        if (resumeAfter is not null)
        {
            client.Options.SetRequestHeader(GatewayEdge.ResumeAfterHeader, resumeAfter);
        }

        string address = _factory!.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        await client.ConnectAsync(new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal)), CancellationToken.None);
        var link = new EdgeLink { Socket = client };
        _ = link.ReadAsync();
        int connected = (await EventsAsync()).Count(e => e.StartsWith("connected "));
        await client.SendAsync(FirstFrame(), WebSocketMessageType.Binary, true, CancellationToken.None);
        await Until(() => link.Snapshot().Length >= 3);
        await UntilAsync(async () => (await EventsAsync()).Count(e => e.StartsWith("connected ")) > connected);
        return link;
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

    [Fact]
    public async Task An_edge_link_needs_the_shared_secret_and_a_connection_id()
    {
        if (_factory is null)
        {
            return;
        }

        await Assert.ThrowsAsync<WebSocketException>(() => ConnectEdgeAsync("edge-1", secret: "not-the-secret"));
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectEdgeAsync("not an id"));
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    // The edge gets the head of the player's log first (what was sent before this link: never the game's), then the id
    // frame and the ping as they are; a message sent through the log comes with its entry's id, one published without it
    // as it is.
    public async Task An_edge_link_gets_its_position_first_and_each_message_with_its_place_in_the_log()
    {
        if (_factory is null)
        {
            return;
        }

        await PlayerMessages.SendAsync(Redis, [_player], new JsonObject { ["cmd"] = "before" });
        string head = (await Redis.StreamRangeAsync(PlayerMessages.LogKey(_player))).Last().Id!;

        var link = await ConnectEdgeAsync("edge-conn-1");

        var first = link.Snapshot();
        Assert.Equal(GatewayEdge.Kind.Position, first[0].Kind);
        Assert.Equal(head, first[0].Id.ToString());
        Assert.Equal(GatewayEdge.Kind.Unlogged, first[1].Kind);
        Assert.Equal(GatewayProtocol.IdFrame, first[1].Frame);
        Assert.Equal(GatewayEdge.Kind.Unlogged, first[2].Kind);
        Assert.Equal([GatewayProtocol.Ping], first[2].Frame);
        Assert.Equal("edge-conn-1", await CurrentConnectionAsync());
        Assert.False((await Redis.HashGetAsync(GatewayPresence.ConnectionKey(_player), "attach")).IsNullOrEmpty);

        var hello = new JsonObject { ["cmd"] = "hello" };
        byte[] expected = Core.Hydra.HydraEncoder.Encode(hello.DeepClone(), webSocket: true);
        await PlayerMessages.SendAsync(Redis, [_player], hello);
        await Redis.PublishAsync(RedisChannel.Literal(GatewayChannels.Send), $$$"""{"playerIds":["{{{_player}}}"],"message":{"cmd":"hello"}}""");
        await Until(() => link.Snapshot().Count(f => f.Frame.SequenceEqual(expected)) == 2);

        var hellos = link.Snapshot().Where(f => f.Frame.SequenceEqual(expected)).ToList();
        Assert.Equal(GatewayEdge.Kind.Logged, hellos[0].Kind);
        Assert.Equal((await Redis.StreamRangeAsync(PlayerMessages.LogKey(_player))).Last().Id!, hellos[0].Id.ToString());
        Assert.Equal(GatewayEdge.Kind.Unlogged, hellos[1].Kind);
    }

    [Fact]
    // A close the node makes for the game is an instruction to the edge (the code, 0 for a drop), then the link's normal
    // close; the session ends at once.
    public async Task Closing_the_game_is_an_instruction_to_the_edge_and_ends_the_session()
    {
        if (_factory is null)
        {
            return;
        }

        var link = await ConnectEdgeAsync("edge-conn-2");
        await PlayerMessages.DisconnectAsync(Redis, new JsonObject { ["playerId"] = _player, ["code"] = 4001, ["reason"] = "bye" });

        Assert.Equal(WebSocketCloseStatus.NormalClosure, (await link.Closed.WaitAsync(s_wait)).Status);
        var close = Assert.Single(link.Snapshot(), f => f.Kind == GatewayEdge.Kind.Close);
        Assert.Equal((4001, "bye"), (close.Code, close.Reason));
        await UntilAsync(async () => (await EventsAsync()).Contains("disconnected edge-conn-2"));
        Assert.Null(await CurrentConnectionAsync());
        Assert.DoesNotContain(await Redis.StreamRangeAsync(GatewayPresence.ConnectionsStream), e => e["player"] == _player && e["reaped"] == "1");

        // A drop (the ops command: no code) is code 0.
        var again = await ConnectEdgeAsync("edge-conn-3");
        await PlayerMessages.DisconnectAsync(Redis, new JsonObject { ["playerId"] = _player });
        await again.Closed.WaitAsync(s_wait);
        Assert.Equal(0, Assert.Single(again.Snapshot(), f => f.Kind == GatewayEdge.Kind.Close).Code);
    }

    [Fact]
    // The edge's close on the link is the game's end (it closed, or dropped): the session ends at once, as a direct close.
    public async Task The_edge_s_close_ends_the_session_at_once()
    {
        if (_factory is null)
        {
            return;
        }

        var link = await ConnectEdgeAsync("edge-conn-4");
        await link.Socket.CloseOutputAsync((WebSocketCloseStatus)GatewayEdge.GameDroppedCode, "the game dropped", CancellationToken.None);

        await UntilAsync(async () => (await EventsAsync()).Contains("disconnected edge-conn-4"));
        Assert.Null(await CurrentConnectionAsync());
        Assert.DoesNotContain(await Redis.StreamRangeAsync(GatewayPresence.ConnectionsStream), e => e["player"] == _player && e["reaped"] == "1");
    }

    [Fact]
    // A link that ends with no close (the edge let go of this node, or died) keeps the player until the grace has passed,
    // then lets them go as a server failure (reaped); unless another socket has taken the connection meanwhile.
    public async Task A_link_that_ends_without_a_close_is_let_go_of_only_after_the_grace_and_only_if_nobody_took_it()
    {
        if (_factory is null)
        {
            return;
        }

        var link = await ConnectEdgeAsync("edge-conn-5");
        link.Socket.Abort();
        await Task.Delay(GraceMs / 2);
        Assert.Equal("edge-conn-5", await CurrentConnectionAsync());
        Assert.DoesNotContain("disconnected edge-conn-5", await EventsAsync());

        await UntilAsync(async () => (await EventsAsync()).Contains("disconnected edge-conn-5"));
        Assert.Null(await CurrentConnectionAsync());
        Assert.Contains(await Redis.StreamRangeAsync(GatewayPresence.ConnectionsStream), e => e["player"] == _player && e["type"] == "disconnected" && e["reaped"] == "1");

        // Taken by another socket during the grace (as a resume on another node writes it): left alone.
        var taken = await ConnectEdgeAsync("edge-conn-6");
        taken.Socket.Abort();
        await Task.Delay(100);
        await Redis.HashSetAsync(GatewayPresence.ConnectionKey(_player), [new("node", "another-node"), new("attach", "another-socket")]);
        await Task.Delay(GraceMs + 500);
        Assert.Equal("edge-conn-6", await CurrentConnectionAsync());
        Assert.DoesNotContain("disconnected edge-conn-6", await EventsAsync());
    }

    [Fact]
    // A node that stops leaves its edge links' players for another node (no release, no disconnected event).
    public async Task A_stopping_node_leaves_its_edge_links_players_for_another_node()
    {
        if (_factory is null)
        {
            return;
        }

        var link = await ConnectEdgeAsync("edge-conn-7");
        _factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();

        Assert.Null((await link.Closed.WaitAsync(s_wait)).Status);
        await Task.Delay(300);
        Assert.Equal("edge-conn-7", await CurrentConnectionAsync());
        Assert.DoesNotContain("disconnected edge-conn-7", await EventsAsync());
    }

    [Fact]
    // A connection entry an older node wrote (no attach) is still let go of by its id.
    public async Task An_entry_without_an_attachment_is_released_by_its_id()
    {
        if (_factory is null)
        {
            return;
        }

        var game = await ConnectAsync();
        string? id = await CurrentConnectionAsync();
        await Redis.HashDeleteAsync(GatewayPresence.ConnectionKey(_player), "attach");
        await game.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        await UntilAsync(async () => (await EventsAsync()).Contains($"disconnected {id}"));
        Assert.Null(await CurrentConnectionAsync());
    }
}
