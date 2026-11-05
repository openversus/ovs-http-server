extern alias realtime;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;
using NodeProgram = realtime::Program;

namespace OpenVersus.Server.Edge.Tests;

/// <summary>A test edge or node must never reach a real Redis: the environment's REDIS is cleared before any host is built.</summary>
internal static class NoAmbientRedis
{
    [ModuleInitializer]
    internal static void Clear() => Environment.SetEnvironmentVariable("REDIS", null);
}

/// <summary>
/// The edge (OpenVersus.Server.Edge) in front of a real gateway node, both on real sockets and one Redis (database 12,
/// OVS_TEST_REDIS): the node registers itself and its address, the edge finds it there. What a game gets through the
/// edge is what it would get directly; closes go the way the link's rules say (GatewayEdge).
/// </summary>
public sealed class EdgeTests : IAsyncLifetime
{
    private const int TestRedisDb = 12;
    private const string Ip = "198.51.100.12";
    private const string EdgeSecret = "the-edges-and-the-nodes-share-this";
    private const int GiveUpMs = 3000;
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(8);

    private readonly string _player = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()[..12]);
    private ConnectionMultiplexer? _redis;
    private readonly List<NodeFactory> _nodes = [];
    private EdgeFactory? _edge;

    private IDatabase Redis => _redis!.GetDatabase(TestRedisDb);

    private static void UseStores(IWebHostBuilder builder)
    {
        string[] parts = s_redis!.Split(':');
        builder.UseSetting("REDIS", parts[0]);
        builder.UseSetting("REDIS_PORT", parts.Length > 1 ? parts[1] : "6379");
        builder.UseSetting("REDIS_USERNAME", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "");
        builder.UseSetting("REDIS_PW", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "");
        builder.UseSetting("REDIS_DB", TestRedisDb.ToString());
        builder.UseSetting("Control:Port", "0");
        builder.UseSetting("Control:Socket", "off");
    }

    private sealed class NodeFactory : WebApplicationFactory<NodeProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            UseStores(builder);
            builder.UseSetting("Access:JwtSecret", ServiceFactory<NodeProgram>.Secret);
            builder.UseSetting("WEBSOCKET_PORT", "0");
            builder.UseSetting("WEBSOCKET_ADVERTISE", "127.0.0.1");
            builder.UseSetting("Gateway:EdgeSecret", EdgeSecret);
        }
    }

    private sealed class EdgeFactory(string secret = EdgeSecret) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            UseStores(builder);
            builder.UseSetting("EDGE_PORT", "0");
            builder.UseSetting("Gateway:EdgeSecret", secret);
            builder.UseSetting("Edge:DrainTimeoutMs", "3000");
            builder.UseSetting("Edge:GiveUpMs", GiveUpMs.ToString());
            builder.UseSetting("Edge:DetachedPingMs", "1000");
        }
    }

    /// <summary>A fake game on the edge: every frame it gets is kept as it arrives; it answers pings.</summary>
    private sealed class Game
    {
        private readonly TaskCompletionSource<(WebSocketCloseStatus? Status, string? Reason)> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public required ClientWebSocket Socket { get; init; }
        public ConcurrentQueue<byte[]> Frames { get; } = new();

        /// <summary>How it was closed (null status: dropped with no close handshake).</summary>
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
                    Frames.Enqueue(frame);
                    if (frame is [GatewayProtocol.Ping])
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

        string[] parts = s_redis.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, AllowAdmin = true };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        // Only these tests use this database: earlier runs' instances and connections go.
        await _redis.GetServer(_redis.GetEndPoints()[0]).FlushDatabaseAsync(TestRedisDb);

        await StartNodeAsync();
        _edge = new EdgeFactory();
        _edge.UseKestrel(0);
        _edge.StartServer();
    }

    public async Task DisposeAsync()
    {
        if (_redis is null)
        {
            return;
        }

        foreach (var factory in new IAsyncDisposable?[] { _edge }.Concat(_nodes))
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }
        }

        await _redis.DisposeAsync();
    }

    // A gateway node, started and in the registry with its address.
    private async Task<NodeFactory> StartNodeAsync()
    {
        var node = new NodeFactory();
        node.UseKestrel(0);
        node.StartServer();
        _nodes.Add(node);
        await WaitForNodeAsync(node);
        return node;
    }

    // The node holding the player's connection now (realtime:conn names it), stopped as a node update stops it.
    private async Task<string> StopHolderAsync()
    {
        string holder = (await Redis.HashGetAsync(GatewayPresence.ConnectionKey(_player), "node")).ToString();
        var node = _nodes.Single(n => n.Services.GetRequiredService<ServiceInstance>().Id == holder);
        _nodes.Remove(node);
        await node.DisposeAsync();
        return holder;
    }

    // Until the node is in the registry, ready, with its address (written once its listener is bound).
    private async Task WaitForNodeAsync(NodeFactory node)
    {
        string id = node.Services.GetRequiredService<ServiceInstance>().Id;
        await UntilAsync(async () => (await InstanceRegistry.ReadAsync(Redis, DateTimeOffset.UtcNow)).Instances
            .Any(i => i.Instance == id && i.State == "Ready" && i.Address is not null));
    }

    private static string Address(IServiceProvider services) =>
        services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("http://", "ws://", StringComparison.Ordinal);

    // A game on the edge, its first frame sent; back once the node has its connection (or, with wait false, at once).
    private async Task<Game> ConnectAsync(EdgeFactory? edge = null, bool wait = true)
    {
        int connected = (await EventsAsync()).Count(e => e == "connected");
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("x-forwarded-for", Ip);
        await client.ConnectAsync(new Uri(Address((edge ?? _edge)!.Services)), CancellationToken.None);
        var game = new Game { Socket = client };
        _ = game.ReadAsync();
        await client.SendAsync(FirstFrame(), WebSocketMessageType.Binary, true, CancellationToken.None);
        if (wait)
        {
            await Until(() => game.Frames.Count >= 2);
            await UntilAsync(async () => (await EventsAsync()).Count(e => e == "connected") > connected);
        }

        return game;
    }

    // As the game's: 0x13 bytes, the token's length (u16, big-endian), the token, a 12-byte id.
    private byte[] FirstFrame()
    {
        byte[] token = Encoding.UTF8.GetBytes(AccessTokens.Sign(new JsonObject { ["id"] = _player }, ServiceFactory<NodeProgram>.Secret, TimeSpan.FromHours(1), DateTimeOffset.UtcNow));
        var frame = new byte[0x15 + token.Length + 12];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0x13), (ushort)token.Length);
        token.CopyTo(frame, 0x15);
        return frame;
    }

    private async Task<List<string>> EventsAsync() =>
        (await Redis.StreamRangeAsync(GatewayPresence.ConnectionsStream))
            .Where(e => e["player"] == _player)
            .Select(e => $"{e["type"]}{(e["reaped"] == "1" ? " reaped" : "")}")
            .ToList();

    private static byte[] Encoded(string cmd) => Core.Hydra.HydraEncoder.Encode(new JsonObject { ["cmd"] = cmd }, webSocket: true);

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow + s_wait;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(20);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> done)
    {
        var until = DateTime.UtcNow + s_wait;
        while (!await done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(20);
        }
    }

    [Fact]
    // The game gets exactly the frames a direct connection gets (the id frame and the ping as they are; the edge's own
    // position never reaches it), and a message sent to the player once; the node records the game's address and the
    // edge's connection id.
    public async Task Through_the_edge_a_game_gets_what_a_direct_game_gets()
    {
        if (_redis is null)
        {
            return;
        }

        var game = await ConnectAsync();
        await PlayerMessages.SendAsync(Redis, [_player], new JsonObject { ["cmd"] = "hello" });
        await Until(() => game.Frames.Any(f => f.SequenceEqual(Encoded("hello"))));
        await Task.Delay(300);

        var frames = game.Frames.ToList();
        Assert.Equal(GatewayProtocol.IdFrame, frames[0]);
        Assert.Equal([GatewayProtocol.Ping], frames[1]);
        Assert.Single(frames, f => f.SequenceEqual(Encoded("hello")));
        Assert.All(frames.Skip(2), f => Assert.True(f is [GatewayProtocol.Ping] || f.SequenceEqual(Encoded("hello"))));
        var connection = await Redis.HashGetAllAsync(GatewayPresence.ConnectionKey(_player));
        Assert.Equal(Ip, connection.Single(e => e.Name == "ip").Value.ToString());
        Assert.Matches("^[0-9a-f]{32}$", connection.Single(e => e.Name == "id").Value.ToString());
        Assert.Equal(_edge!.Services.GetRequiredService<ServiceInstance>().Id, connection.Single(e => e.Name == "edge").Value.ToString());
    }

    [Fact]
    // The node closing the game (a ws:disconnect) closes it at the edge with the node's code; without one, drops it.
    public async Task A_close_the_node_makes_reaches_the_game_with_its_code()
    {
        if (_redis is null)
        {
            return;
        }

        var game = await ConnectAsync();
        await PlayerMessages.DisconnectAsync(Redis, new JsonObject { ["playerId"] = _player, ["code"] = 4001, ["reason"] = "bye" });
        Assert.Equal(((WebSocketCloseStatus)4001, "bye"), await game.Closed.WaitAsync(s_wait));

        var dropped = await ConnectAsync();
        await PlayerMessages.DisconnectAsync(Redis, new JsonObject { ["playerId"] = _player });
        Assert.Null((await dropped.Closed.WaitAsync(s_wait)).Status);
    }

    [Fact]
    // The game's own close, or its socket dropping, is the session's end at the node at once (not a link the edge let go of).
    public async Task The_game_leaving_ends_the_session_at_the_node_at_once()
    {
        if (_redis is null)
        {
            return;
        }

        var game = await ConnectAsync();
        await game.Socket.CloseOutputAsync((WebSocketCloseStatus)4003, "leaving", CancellationToken.None);
        await UntilAsync(async () => (await EventsAsync()).Contains("disconnected"));
        Assert.False((await Redis.KeyExistsAsync(GatewayPresence.ConnectionKey(_player))));

        var dropped = await ConnectAsync();
        dropped.Socket.Abort();
        await UntilAsync(async () => (await EventsAsync()).Count(e => e == "disconnected") == 2);
        Assert.DoesNotContain("disconnected reaped", await EventsAsync());
    }

    [Fact]
    // No node to take the game: it is closed, "going away"; a node that refuses the edge (another secret) is a
    // configuration error: the game is closed with 1011.
    public async Task A_game_with_no_node_or_a_refused_link_is_closed()
    {
        if (_redis is null)
        {
            return;
        }

        var wrong = new EdgeFactory("not-the-secret-the-nodes-have-at-all");
        wrong.UseKestrel(0);
        wrong.StartServer();
        try
        {
            var refused = await ConnectAsync(wrong, wait: false);
            Assert.Equal(WebSocketCloseStatus.InternalServerError, (await refused.Closed.WaitAsync(s_wait)).Status);
        }
        finally
        {
            await wrong.DisposeAsync();
        }

        foreach (var node in _nodes.ToList())
        {
            _nodes.Remove(node);
            await node.DisposeAsync();
        }

        await UntilAsync(async () => !(await InstanceRegistry.ReadAsync(Redis, DateTimeOffset.UtcNow)).Instances.Any(i => i.Service == "ws" && i.State == "Ready"));
        var none = await ConnectAsync(wait: false);
        Assert.Equal((WebSocketCloseStatus.EndpointUnavailable, "going away"), await none.Closed.WaitAsync(s_wait));
        Assert.Empty(none.Frames);
    }

    [Fact]
    // A stopping edge keeps its games (they still get what they are sent) and shows as not ready in the registry.
    public async Task A_stopping_edge_keeps_its_games_and_is_not_ready()
    {
        if (_redis is null)
        {
            return;
        }

        var game = await ConnectAsync();
        string edge = _edge!.Services.GetRequiredService<ServiceInstance>().Id;
        _edge.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await UntilAsync(async () => (await InstanceRegistry.ReadAsync(Redis, DateTimeOffset.UtcNow)).Instances
            .Any(i => i.Instance == edge && i.State == "NotReady" && i.Checks.ContainsKey("draining")));
        await PlayerMessages.SendAsync(Redis, [_player], new JsonObject { ["cmd"] = "still here" });
        await Until(() => game.Frames.Any(f => f.SequenceEqual(Encoded("still here"))));
        Assert.False(game.Closed.IsCompleted);
    }

    private int Count(Game game, string cmd) => game.Frames.Count(f => f.SequenceEqual(Encoded(cmd)));

    private Task SendAsync(string cmd) => PlayerMessages.SendAsync(Redis, [_player], new JsonObject { ["cmd"] = cmd });

    [Fact]
    // The game's node stops: the edge moves the game to another node (a resume), the game sees no close and no second
    // id frame, and gets what was sent meanwhile once.
    public async Task When_its_node_goes_the_game_moves_to_another_and_misses_nothing()
    {
        if (_redis is null)
        {
            return;
        }

        await StartNodeAsync();
        var game = await ConnectAsync();
        await SendAsync("m1");
        await Until(() => Count(game, "m1") == 1);

        string lost = await StopHolderAsync();
        await SendAsync("m2");
        await UntilAsync(async () => (await EventsAsync()).Contains("resumed"));
        await Until(() => Count(game, "m2") == 1);
        await SendAsync("m3");
        await Until(() => Count(game, "m3") == 1);
        await Task.Delay(300);

        Assert.False(game.Closed.IsCompleted);
        Assert.Equal(1, game.Frames.Count(f => f.SequenceEqual(GatewayProtocol.IdFrame)));
        Assert.Equal([1, 1, 1], new[] { "m1", "m2", "m3" }.Select(m => Count(game, m)));
        Assert.NotEqual(lost, (await Redis.HashGetAsync(GatewayPresence.ConnectionKey(_player), "node")).ToString());
        Assert.Equal(1, (await EventsAsync()).Count(e => e == "connected"));
        Assert.DoesNotContain("disconnected", await EventsAsync());
    }

    [Fact]
    // Two nodes going one after the other: the game moves twice, and still gets everything once, in order.
    public async Task Two_nodes_going_in_a_row_lose_nothing_either()
    {
        if (_redis is null)
        {
            return;
        }

        await StartNodeAsync();
        await StartNodeAsync();
        var game = await ConnectAsync();
        for (int i = 1; i <= 2; i++)
        {
            int resumed = (await EventsAsync()).Count(e => e == "resumed");
            await StopHolderAsync();
            await SendAsync($"gap {i}");
            await UntilAsync(async () => (await EventsAsync()).Count(e => e == "resumed") > resumed);
            await Until(() => Count(game, $"gap {i}") == 1);
        }

        await SendAsync("after");
        await Until(() => Count(game, "after") == 1);
        var messages = game.Frames.Where(f => f.Length > 1 && !f.SequenceEqual(GatewayProtocol.IdFrame)).ToList();
        Assert.Equal([Encoded("gap 1"), Encoded("gap 2"), Encoded("after")], messages);
        Assert.False(game.Closed.IsCompleted);
    }

    [Fact]
    // No node for the game: the edge pings it itself (at once, then every Edge:DetachedPingMs) and closes it, "going
    // away", after Edge:GiveUpMs.
    public async Task A_game_with_no_node_left_is_pinged_then_closed_after_the_give_up_time()
    {
        if (_redis is null)
        {
            return;
        }

        var game = await ConnectAsync();
        int before = game.Frames.Count;
        var started = DateTime.UtcNow;
        await StopHolderAsync();

        var closed = await game.Closed.WaitAsync(s_wait);
        var after = game.Frames.Skip(before).ToList();
        Assert.Equal((WebSocketCloseStatus.EndpointUnavailable, "going away"), closed);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(GiveUpMs - 200));
        Assert.True(after.Count >= 3, $"{after.Count} pings");
        Assert.All(after, f => Assert.Equal([GatewayProtocol.Ping], f));
    }

    [Fact]
    // The next node refuses the resume (the player logged in again elsewhere meanwhile): the game is closed, as told.
    public async Task A_refused_resume_closes_the_game()
    {
        if (_redis is null)
        {
            return;
        }

        await StartNodeAsync();
        var game = await ConnectAsync();
        await Redis.HashSetAsync(GatewayPresence.ConnectionKey(_player), "id", "a-newer-login");
        await StopHolderAsync();

        Assert.Equal((WebSocketCloseStatus.NormalClosure, "resume refused"), await game.Closed.WaitAsync(s_wait));
    }

    [Fact]
    // A node that goes before the game got its id frame (here: one that sends the edge its position and drops the link)
    // leaves nothing to resume: the next node takes the game as new, and the game gets its id frame once.
    public async Task A_game_that_never_got_its_id_frame_is_taken_as_new_by_the_next_node()
    {
        if (_redis is null)
        {
            return;
        }

        foreach (var node in _nodes.ToList())
        {
            _nodes.Remove(node);
            await node.DisposeAsync();
        }

        await using var dropper = await StartDropperAsync();
        var game = await ConnectAsync(wait: false);
        await Task.Delay(700);
        Assert.Empty(game.Frames);

        await StartNodeAsync();
        await Until(() => game.Frames.Count >= 2);
        await UntilAsync(async () => (await EventsAsync()).Contains("connected"));
        Assert.Equal(GatewayProtocol.IdFrame, game.Frames.First());
        Assert.DoesNotContain("resumed", await EventsAsync());
        Assert.False(game.Closed.IsCompleted);
    }

    // A "node" in the registry that, once it has the game's first frame, sends a position and drops the link.
    private async Task<WebApplication> StartDropperAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.ReceiveAsync(new byte[65536], CancellationToken.None);
            await socket.SendAsync(GatewayEdge.Position(new StreamId(1, 0)), WebSocketMessageType.Binary, true, CancellationToken.None);
            await Task.Delay(100);
            socket.Abort();
        });
        await app.StartAsync();
        string address = Address(app.Services);
        string id = "dropper:" + Guid.NewGuid().ToString("N")[..6];
        var report = new InstanceReport("ws", id, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Ready", [], address);
        await Redis.StringSetAsync(InstanceRegistry.Key(id), System.Text.Json.JsonSerializer.Serialize(report, System.Text.Json.JsonSerializerOptions.Web), TimeSpan.FromMinutes(1));
        await Redis.SortedSetAddAsync(InstanceRegistry.Index, $"ws/{id}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return app;
    }
}
