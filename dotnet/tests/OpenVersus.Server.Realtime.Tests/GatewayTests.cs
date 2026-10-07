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
    private byte[] FirstFrame(TimeSpan? lifetime = null, string? secret = null)
    {
        byte[] token = Encoding.UTF8.GetBytes(AccessTokens.Sign(new JsonObject { ["id"] = _player }, secret ?? ServiceFactory<Program>.Secret,
            lifetime ?? TimeSpan.FromHours(1), DateTimeOffset.UtcNow));
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
        public required byte[] FirstFrame { get; init; }
        public List<GatewayEdge.Envelope> Frames { get; } = [];

        public GatewayEdge.Envelope[] Of(GatewayEdge.Kind kind) => [.. Snapshot().Where(f => f.Kind == kind)];

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
    private async Task<EdgeLink> ConnectEdgeAsync(string connectionId, string secret = EdgeSecret, byte[]? frame = null)
    {
        int connected = (await EventsAsync()).Count(e => e.StartsWith("connected "));
        var link = await OpenEdgeAsync(connectionId, frame ?? FirstFrame(), secret, null);
        await Until(() => link.Snapshot().Length >= 3);
        await UntilAsync(async () => (await EventsAsync()).Count(e => e.StartsWith("connected ")) > connected);
        return link;
    }

    // A resume of connectionId after the stream id the game got (the game's first frame as it was): back once the node has
    // answered (a resumed event, or a close).
    private async Task<EdgeLink> ResumeEdgeAsync(string connectionId, string after, byte[] frame)
    {
        int resumed = (await EventsAsync()).Count(e => e == $"resumed {connectionId}");
        var link = await OpenEdgeAsync(connectionId, frame, EdgeSecret, after);
        await UntilAsync(async () => link.Closed.IsCompleted || link.Of(GatewayEdge.Kind.Close).Length > 0
            || (await EventsAsync()).Count(e => e == $"resumed {connectionId}") > resumed);
        return link;
    }

    private async Task<EdgeLink> OpenEdgeAsync(string connectionId, byte[] frame, string secret, string? resumeAfter)
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
        var link = new EdgeLink { Socket = client, FirstFrame = frame };
        _ = link.ReadAsync();
        await client.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        return link;
    }

    private GatewayNode Node => _factory!.Services.GetRequiredService<GatewayNode>();

    private async Task<string> LastLoggedAsync() => (await Redis.StreamRangeAsync(PlayerMessages.LogKey(_player))).Last().Id!;

    private static byte[] Encoded(string cmd) => Core.Hydra.HydraEncoder.Encode(new JsonObject { ["cmd"] = cmd }, webSocket: true);

    private Task SendAsync(string cmd) => PlayerMessages.SendAsync(Redis, [_player], new JsonObject { ["cmd"] = cmd });

    // The edge's link let go of (as an edge does when it moves the game): the node has dropped it once a message for the
    // player no longer reaches it.
    private async Task DetachAsync(EdgeLink link)
    {
        link.Socket.Abort();
        await UntilAsync(() => Task.FromResult(!Node.TryGet(_player, out _)));
    }

    private async Task<List<string>> EventsAsync() =>
        (await Redis.StreamRangeAsync(GatewayPresence.ConnectionsStream))
            .Where(e => e["player"] == _player)
            .Select(e => $"{e["type"]} {e["connection"]}")
            .ToList();

    private async Task<string?> CurrentConnectionAsync() => await Redis.HashGetAsync(GatewayPresence.ConnectionKey(_player), "id");

    private static async Task Until(Func<bool> done, TimeSpan? wait = null)
    {
        var until = DateTime.UtcNow + (wait ?? s_wait);
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

    [Fact]
    // A resume takes the connection on the new socket (the same id; a resumed event, no connected, no id frame) and replays
    // only what the game missed, after the last entry it got; the old socket's grace then lets nothing go; delivery is
    // live again after.
    public async Task A_resume_moves_the_connection_and_replays_only_what_the_game_missed()
    {
        if (_factory is null)
        {
            return;
        }

        var first = await ConnectEdgeAsync("edge-r1");
        await SendAsync("m1");
        await Until(() => first.Of(GatewayEdge.Kind.Logged).Length == 1);
        string got = first.Of(GatewayEdge.Kind.Logged)[0].Id.ToString();
        string? attach = await Redis.HashGetAsync(GatewayPresence.ConnectionKey(_player), "attach");
        await DetachAsync(first);
        await SendAsync("m2");
        await SendAsync("m3");

        var second = await ResumeEdgeAsync("edge-r1", got, first.FirstFrame);
        await Until(() => second.Of(GatewayEdge.Kind.Logged).Length == 2);

        var log = (await Redis.StreamRangeAsync(PlayerMessages.LogKey(_player))).Select(e => e.Id.ToString()).ToList();
        var replayed = second.Of(GatewayEdge.Kind.Logged);
        Assert.Equal([Encoded("m2"), Encoded("m3")], replayed.Select(f => f.Frame));
        Assert.Equal(log[1..], replayed.Select(f => f.Id.ToString()));
        Assert.Empty(second.Of(GatewayEdge.Kind.Position));
        Assert.DoesNotContain(second.Snapshot(), f => f.Frame.SequenceEqual(GatewayProtocol.IdFrame));
        Assert.Equal("edge-r1", await CurrentConnectionAsync());
        Assert.NotEqual(attach, (string?)await Redis.HashGetAsync(GatewayPresence.ConnectionKey(_player), "attach"));
        var events = await EventsAsync();
        Assert.Equal(1, events.Count(e => e == "connected edge-r1"));
        Assert.Contains("resumed edge-r1", events);

        await Task.Delay(GraceMs + 500);
        Assert.Equal("edge-r1", await CurrentConnectionAsync());
        Assert.DoesNotContain("disconnected edge-r1", await EventsAsync());

        await SendAsync("m4");
        await Until(() => second.Of(GatewayEdge.Kind.Logged).Length == 3);
        Assert.Equal(Encoded("m4"), second.Of(GatewayEdge.Kind.Logged)[2].Frame);

        // ws:send can reach the node after the replay read the same entry from the log: an entry at or below the last
        // one sent is dropped, a later one is not.
        var channel = RedisChannel.Literal(GatewayChannels.Send);
        await Redis.PublishAsync(channel, $$$"""{"playerIds":["{{{_player}}}"],"message":{"cmd":"m2"},"seqs":{"{{{_player}}}":"{{{log[1]}}}"}}""");
        await Redis.PublishAsync(channel, $$$"""{"playerIds":["{{{_player}}}"],"message":{"cmd":"later"},"seqs":{"{{{_player}}}":"99999999999999-0"}}""");
        await Until(() => second.Of(GatewayEdge.Kind.Logged).Length == 4);
        await Task.Delay(200);
        Assert.Equal([Encoded("m2"), Encoded("m3"), Encoded("m4"), Encoded("later")], second.Of(GatewayEdge.Kind.Logged).Select(f => f.Frame));
    }

    [Fact]
    // What is sent while the log is being replayed arrives once and in order: one sent before the log is read (in the
    // log, and delivered live while held) once, one sent after it is read after the replayed ones.
    public async Task What_is_sent_during_a_replay_arrives_once_and_in_order()
    {
        if (_factory is null)
        {
            return;
        }

        var first = await ConnectEdgeAsync("edge-r2");
        string got = first.Of(GatewayEdge.Kind.Position)[0].Id.ToString();
        await DetachAsync(first);
        await SendAsync("missed");
        Node.ReplayPaused = async phase =>
        {
            await SendAsync(phase == "joined" ? "while joining" : "after the read");
            await Task.Delay(300);
        };

        var second = await ResumeEdgeAsync("edge-r2", got, first.FirstFrame);
        await Until(() => second.Of(GatewayEdge.Kind.Logged).Length >= 3);
        await Task.Delay(300);

        Assert.Equal([Encoded("missed"), Encoded("while joining"), Encoded("after the read")], second.Of(GatewayEdge.Kind.Logged).Select(f => f.Frame));
        Assert.Equal((await Redis.StreamRangeAsync(PlayerMessages.LogKey(_player))).Select(e => e.Id.ToString()),
            second.Of(GatewayEdge.Kind.Logged).Select(f => f.Id.ToString()));
    }

    [Fact]
    // A resume is refused (the edge is told to close the game; nothing changes here) unless the connection is still the
    // player's current one, opened with the same session.
    public async Task A_resume_is_refused_unless_the_connection_is_still_current()
    {
        if (_factory is null)
        {
            return;
        }

        var first = await ConnectEdgeAsync("edge-r3");
        string got = first.Of(GatewayEdge.Kind.Position)[0].Id.ToString();
        await DetachAsync(first);
        string key = GatewayPresence.ConnectionKey(_player);

        // Replaced by a newer login.
        await Redis.HashSetAsync(key, "id", "a-newer-login");
        var replaced = await ResumeEdgeAsync("edge-r3", got, first.FirstFrame);
        Assert.Equal((1000, "resume refused"), (Assert.Single(replaced.Of(GatewayEdge.Kind.Close)).Code, replaced.Of(GatewayEdge.Kind.Close)[0].Reason));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, (await replaced.Closed.WaitAsync(s_wait)).Status);
        Assert.Equal("a-newer-login", await CurrentConnectionAsync());

        // Another session under the same id.
        await Redis.HashSetAsync(key, [new("id", "edge-r3"), new("token", "another-session")]);
        Assert.Single((await ResumeEdgeAsync("edge-r3", got, first.FirstFrame)).Of(GatewayEdge.Kind.Close));

        // Let go of already.
        await Redis.KeyDeleteAsync(key);
        Assert.Single((await ResumeEdgeAsync("edge-r3", got, first.FirstFrame)).Of(GatewayEdge.Kind.Close));
        Assert.Null(await CurrentConnectionAsync());
        Assert.DoesNotContain("resumed edge-r3", await EventsAsync());
    }

    [Fact]
    // A resume checks the token's signature, not its expiry (the session outlives it); a bad signature is refused. The old
    // link is still attached here (the edge gave up on a node that is still up): the resume takes the connection from
    // it, and its own close then lets nothing go.
    public async Task A_resume_takes_an_expired_token_but_not_a_bad_one()
    {
        if (_factory is null)
        {
            return;
        }

        long signedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var first = await ConnectEdgeAsync("edge-r4", frame: FirstFrame(TimeSpan.FromSeconds(2)));
        string got = first.Of(GatewayEdge.Kind.Position)[0].Id.ToString();
        await Until(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= signedAt + 3, TimeSpan.FromSeconds(5));

        var resumed = await ResumeEdgeAsync("edge-r4", got, first.FirstFrame);
        Assert.Empty(resumed.Of(GatewayEdge.Kind.Close));
        Assert.Contains("resumed edge-r4", await EventsAsync());
        Assert.Equal(("replaced", 1000), (Assert.Single(first.Of(GatewayEdge.Kind.Close)).Reason, first.Of(GatewayEdge.Kind.Close)[0].Code));
        await first.Closed.WaitAsync(s_wait);
        await Task.Delay(300);
        Assert.Equal("edge-r4", await CurrentConnectionAsync());
        Assert.DoesNotContain("disconnected edge-r4", await EventsAsync());

        var forged = await ResumeEdgeAsync("edge-r4", got, FirstFrame(secret: "a-different-secret-entirely-0123456789"));
        Assert.Single(forged.Of(GatewayEdge.Kind.Close));
        await Assert.ThrowsAsync<WebSocketException>(() => ResumeEdgeAsync("edge-r4", "not-an-id", first.FirstFrame));
    }

    [Fact]
    // The window's rule, not a reach-back check: a game whose last entry is older than everything in the log (trimmed by
    // the sends after it) is replayed the whole log, and a close in the log that applies to it closes it there.
    public async Task A_resume_replays_from_its_position_whatever_the_log_still_holds_and_applies_a_close_in_it()
    {
        if (_factory is null)
        {
            return;
        }

        var first = await ConnectEdgeAsync("edge-r5");
        await DetachAsync(first);
        await SendAsync("n1");
        await PlayerMessages.DisconnectAsync(Redis, new JsonObject { ["playerId"] = _player, ["connectionId"] = "another-connection", ["code"] = 4003 });
        await SendAsync("n2");
        await PlayerMessages.DisconnectAsync(Redis, new JsonObject { ["playerId"] = _player, ["code"] = 4002, ["reason"] = "closed in the gap" });
        await SendAsync("n3");

        var second = await ResumeEdgeAsync("edge-r5", "1-0", first.FirstFrame);
        await second.Closed.WaitAsync(s_wait);

        Assert.Equal([Encoded("n1"), Encoded("n2")], second.Of(GatewayEdge.Kind.Logged).Select(f => f.Frame));
        var close = Assert.Single(second.Of(GatewayEdge.Kind.Close));
        Assert.Equal((4002, "closed in the gap"), (close.Code, close.Reason));
        await UntilAsync(async () => (await EventsAsync()).Contains("disconnected edge-r5"));
    }

    [Fact]
    // A close that arrives while the log is being replayed is held with the messages, and applied after the replayed
    // ones, in its place.
    public async Task A_close_that_arrives_during_a_replay_is_applied_after_the_replayed_messages()
    {
        if (_factory is null)
        {
            return;
        }

        var first = await ConnectEdgeAsync("edge-r6");
        string got = first.Of(GatewayEdge.Kind.Position)[0].Id.ToString();
        await DetachAsync(first);
        await SendAsync("missed");
        Node.ReplayPaused = async phase =>
        {
            if (phase == "read")
            {
                await PlayerMessages.DisconnectAsync(Redis, new JsonObject { ["playerId"] = _player, ["code"] = 4004, ["reason"] = "held close" });
                await Task.Delay(300);
            }
        };

        var second = await ResumeEdgeAsync("edge-r6", got, first.FirstFrame);
        await second.Closed.WaitAsync(s_wait);

        var frames = second.Snapshot().Where(f => f.Kind is GatewayEdge.Kind.Logged or GatewayEdge.Kind.Close).ToList();
        Assert.Equal([GatewayEdge.Kind.Logged, GatewayEdge.Kind.Close], frames.Select(f => f.Kind));
        Assert.Equal(Encoded("missed"), frames[0].Frame);
        Assert.Equal((4004, "held close"), (frames[1].Code, frames[1].Reason));
    }
}
