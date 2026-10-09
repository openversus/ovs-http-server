using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Http.Batch;
using OpenVersus.Server.Http.Shared.Hosting;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// PUT /batch: ported sub-requests answered here, the rest sent to the TS server as one batch and its answers passed on
/// byte for byte, in the game's order; failures of either part confined to their own items.
/// </summary>
public sealed class BatchEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private static JsonObject Get(string url, JsonObject? headers = null) =>
        new() { ["verb"] = "GET", ["url"] = url, ["headers"] = headers ?? new JsonObject() };

    private static readonly JsonObject s_options = new() { ["allow_failures"] = true, ["parallel"] = true };

    private static byte[] Batch(params JsonNode[] requests) =>
        HydraEncoder.Encode(new JsonObject { ["options"] = s_options.DeepClone(), ["requests"] = new JsonArray(requests) });

    // The decoder keeps the smallest integer type the number came as.
    private static int Status(JsonNode? item) => Convert.ToInt32(item!["status_code"]!.AsValue().GetValue<object>());

    private static JsonObject Item(int status, JsonNode? body) => new() { ["status_code"] = status, ["headers"] = new JsonObject(), ["body"] = body };

    // A compressed value as a compressor other than this codec's writes it (smallest size, not fastest): decoding and
    // encoding it again would give other bytes, so finding these bytes in the answer shows they were passed on as they were.
    private static readonly byte[] s_compressed = Compressed(HydraEncoder.Encode(new JsonObject { ["region"] = "US", ["filler"] = new string('x', 300) }));

    private static byte[] Compressed(byte[] inner)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(inner);
        }

        byte[] data = buffer.ToArray();
        return [HydraCode.Compressed, 1, HydraCode.Bytes8, (byte)data.Length, .. data];
    }

    private HttpClient Client(string tsUrl, params (string Key, string Value)[] settings)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Batch:TsUrl", tsUrl);
            // The fake answers the routed sub-requests too.
            b.UseSetting("Batch:EdgeUrl", tsUrl);
            b.UseSetting("Batch:ForwardRoutes", $"GET {Unported}");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(HydraToken.Header, GameAppFactory.Token());
        return client;
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, byte[] body, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/batch") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new(HydraBodies.ContentType);
        configure?.Invoke(request);
        return await client.SendAsync(request);
    }

    private static JsonNode Static(string name) => JsonNode.Parse(StaticResponses.Json(name))!;

    // The sub-request these tests send on to the TS server: this service has no stub left, so Client() lists it in
    // Batch:ForwardRoutes, the bridge's other way to the TS server. When the bridge goes, so do these tests.
    private const string Unported = "/ssc/invoke/no_such_function";

    // What the TS server's catch-all answers (the default CRC: no Mongo here), and so every route it never handled.
    private static JsonNode CatchAll => JsonNode.Parse("""{"body":{"Crc":1267552971,"MatchmakingCrc":2},"metadata":null,"return_code":200}""")!;

    [Fact]
    public async Task AnItemAnotherServiceOwnsGoesThroughTheRouterAsARequestOfItsOwn()
    {
        await using var ts = await FakeTs.StartAsync();
        var client = Client(ts.Url);
        string token = GameAppFactory.Token();
        // /friends/me is the social service's, /accounts/{id}/relationships/followers too (more literal than the HTTP
        // service's /accounts/{id}/{sub}), the custom lobby's SSC the lobbies service's; the unported one is this
        // service's own, for the TS server.
        using var response = await PutAsync(client, Batch(
            Get("/friends/me?page=2", new JsonObject { ["x-hydra-http-method"] = "GET", ["x-custom"] = "kept" }),
            Get(Unported),
            Get("/accounts/abc/relationships/followers"),
            new JsonObject { ["verb"] = "PUT", ["url"] = "/ssc/invoke/create_custom_game_lobby", ["body"] = new JsonObject { ["Mode"] = "1v1" } },
            Get("/friends/me/invitations/incoming?teapot=1")),
            r =>
            {
                r.Headers.Add("x-real-ip", "203.0.113.9");
                r.Headers.Add(HissZstd.Header, "1");
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal("GET /friends/me?page=2", (string)responses[0]!["body"]!["routed"]!);
        Assert.Equal(Unported, (string)responses[1]!["body"]!["url"]!);
        Assert.Equal("GET /accounts/abc/relationships/followers", (string)responses[2]!["body"]!["routed"]!);
        Assert.Equal("PUT /ssc/invoke/create_custom_game_lobby", (string)responses[3]!["body"]!["routed"]!);
        // The owner's status is passed on as it came.
        Assert.Equal(418, Status(responses[4]));

        Assert.Equal(4, ts.Routed.Count);
        var friends = ts.Routed.Single(r => r.Target == "/friends/me?page=2");
        Assert.Equal(token, (string?)friends.Headers[HydraToken.Header]);
        Assert.Equal("203.0.113.9", (string?)friends.Headers["x-real-ip"]);
        Assert.Equal("kept", (string?)friends.Headers["x-custom"]);
        // The client's zstd capability header rides on the batch; every item inherits it (the hiss inside a login batch reads it).
        Assert.Equal("1", (string?)friends.Headers[HissZstd.Header]);
        var lobby = ts.Routed.Single(r => r.Target == "/ssc/invoke/create_custom_game_lobby");
        Assert.Equal("1v1", (string)HydraDecoder.Decode(lobby.Body)!["Mode"]!);
        // Only this service's unported item went to the TS server.
        Assert.Equal([Unported], HydraDecoder.Decode(Assert.Single(ts.Requests).Body)!["requests"]!.AsArray().Select(r => (string)r!["url"]!));
    }

    [Fact]
    public async Task AnItemTheRouterCannotTakeAnswers502AndTheRestStillAnswers()
    {
        await using var ts = await FakeTs.StartAsync();
        // Nothing listens on port 1.
        var client = Client(ts.Url, ("Batch:EdgeUrl", "http://127.0.0.1:1"));
        using var response = await PutAsync(client, Batch(Get("/friends/me"), Get(Unported)));

        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal(502, Status(responses[0]));
        Assert.Equal(Unported, (string)responses[1]!["body"]!["url"]!);
    }

    [Fact]
    public async Task PortedItemsAnswerHereAndTheOthersGoToTheTsServerAsOneBatchInTheGamesOrder()
    {
        await using var ts = await FakeTs.StartAsync();
        var client = Client(ts.Url);
        using var response = await PutAsync(client, Batch(
            Get("/commerce/products"),
            Get(Unported),
            Get("/commerce/purchases/someone"),
            new JsonObject { ["verb"] = "PUT", ["url"] = "/commerce/purchases/me?count=25", ["headers"] = new JsonObject { ["x-hydra-http-method"] = "GET" } }),
            r =>
            {
                r.Headers.Add("X-OVS-Identity", "identity-value");
                r.Headers.Add("x-install-id", "install-value");
                r.Headers.Add("X-Forwarded-For", "203.0.113.9");
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HydraBodies.ContentType, response.Content.Headers.ContentType?.MediaType);
        var forwarded = Assert.Single(ts.Requests);
        var body = HydraDecoder.Decode(forwarded.Body)!;
        Assert.True(JsonNode.DeepEquals(s_options, body["options"]));
        // /commerce/purchases/someone is no route of the TS server's (only "me" is): answered here as its catch-all did.
        Assert.Equal([Unported], body["requests"]!.AsArray().Select(r => (string)r!["url"]!));
        // The batch's headers go with it, as the game sent them, and the client address as this service worked it out.
        Assert.Equal(client.DefaultRequestHeaders.GetValues(HydraToken.Header).Single(), forwarded.Headers[HydraToken.Header]);
        Assert.Equal("identity-value", forwarded.Headers["X-OVS-Identity"]);
        Assert.Equal("install-value", forwarded.Headers["x-install-id"]);
        Assert.Equal("203.0.113.9", forwarded.Headers["x-real-ip"]);
        Assert.Equal(HydraBodies.ContentType, forwarded.Headers.ContentType);

        byte[] expected = HydraEncoder.Encode(new JsonObject
        {
            ["responses"] = new JsonArray(
                Item(200, Static("commerce-products")),
                HydraRaw.Node(FakeTs.ItemFor(Unported)),
                Item(200, CatchAll),
                Item(200, Static("commerce-purchases-me"))),
        });
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnItemTheTsServerNeverHandledIsAnsweredHereAsItsCatchAllAndNotForwarded()
    {
        await using var ts = await FakeTs.StartAsync();
        // An SSC name the route map lacks (SscUnlisted; not the one Client() lists for forwarding) and one it has but the TS server never handled.
        using var response = await PutAsync(Client(ts.Url), Batch(Get("/ssc/invoke/some_other_name"), Get("/ssc/invoke/get_preferred_currency")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal(2, responses.Count);
        foreach (var item in responses)
        {
            Assert.Equal(200, Status(item));
            Assert.True(JsonNode.DeepEquals(CatchAll, item!["body"]), item.ToJsonString());
        }

        Assert.Empty(ts.Requests);
    }

    [Fact]
    public async Task TheTsServersBytesArePassedOnAsTheyCame()
    {
        await using var ts = await FakeTs.StartAsync();
        // An SSC name the map lacks is answered here now; listing it sends it to the TS server as a ported route would be.
        using var response = await PutAsync(Client(ts.Url, ("Batch:ForwardRoutes", "GET /ssc/invoke/compressed")), Batch(Get("/ssc/invoke/compressed")));
        byte[] answer = await response.Content.ReadAsByteArrayAsync();
        Assert.True(answer.AsSpan().IndexOf(s_compressed) >= 0, "the compressed value was re-encoded");
        Assert.NotEqual(s_compressed, HydraEncoder.Encode(HydraDecoder.Decode(s_compressed)));
    }

    [Fact]
    public async Task TheSessionTokenCarriesIntoEverySubRequest()
    {
        await using var ts = await FakeTs.StartAsync();
        // /commerce/products needs the token: without it the item would answer 401.
        using var response = await PutAsync(Client(ts.Url), Batch(Get("/commerce/products")));
        var item = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]![0]!;
        Assert.Equal(200, Status(item));
        Assert.Empty(ts.Requests);
    }

    [Fact]
    public async Task ForwardRoutesSendsAPortedRouteToTheTsServer()
    {
        await using var ts = await FakeTs.StartAsync();
        using var response = await PutAsync(Client(ts.Url, ("Batch:ForwardRoutes", "GET /commerce/products")),
            Batch(Get("/commerce/products?partial_response=1"), Get("/commerce/purchases/me")));
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal(["/commerce/products?partial_response=1"], HydraDecoder.Decode(Assert.Single(ts.Requests).Body)!["requests"]!.AsArray().Select(r => (string)r!["url"]!));
        Assert.Equal("/commerce/products?partial_response=1", (string)responses[0]!["body"]!["url"]!);
        Assert.True(JsonNode.DeepEquals(Static("commerce-purchases-me"), responses[1]!["body"]));
    }

    [Fact]
    public async Task WithTheTsServerDownOnlyItsPartAnswers502()
    {
        using var response = await PutAsync(Client("http://127.0.0.1:9"), Batch(Get(Unported), Get("/commerce/purchases/me")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal(502, Status(responses[0]));
        Assert.Equal(200, Status(responses[1]));
    }

    [Fact]
    public async Task ATsServerThatNeverAnswersIsGivenUpOnAfterTheTimeout()
    {
        await using var ts = await FakeTs.StartAsync(delay: TimeSpan.FromSeconds(20));
        var watch = Stopwatch.StartNew();
        using var response = await PutAsync(Client(ts.Url, ("Batch:ForwardTimeoutSeconds", "1")), Batch(Get(Unported), Get("/commerce/purchases/me")));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal(504, Status(responses[0]));
        Assert.Equal(200, Status(responses[1]));
    }

    [Fact]
    public async Task ANestedBatchAndAMalformedItemAnswer400AndTheRestAsUsual()
    {
        await using var ts = await FakeTs.StartAsync();
        using var response = await PutAsync(Client(ts.Url), Batch(
            new JsonObject { ["verb"] = "PUT", ["url"] = "/batch", ["body"] = new JsonObject { ["requests"] = new JsonArray() } },
            new JsonObject { ["verb"] = "GET" },
            new JsonObject { ["verb"] = "GET", ["url"] = "http://elsewhere/x" },
            JsonValue.Create("not a request"),
            Get("/commerce/purchases/me")));
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal([400, 400, 400, 400, 200], responses.Select(r => Status(r)));
        Assert.Empty(ts.Requests);
    }

    [Fact]
    public async Task ABodyWithoutRequestsAnswers400()
    {
        await using var ts = await FakeTs.StartAsync();
        using var response = await PutAsync(Client(ts.Url), HydraEncoder.Encode(new JsonObject { ["options"] = s_options.DeepClone() }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheForwardedBatchIsMarkedAndAMarkedBatchIsRefused()
    {
        await using var ts = await FakeTs.StartAsync();
        var client = Client(ts.Url);
        using (var forwarding = await PutAsync(client, Batch(Get(Unported))))
        {
            Assert.Equal("1", Assert.Single(ts.Requests).Headers[BatchRunner.ForwardedHeader]);
        }

        // Batch:TsUrl pointing back at this service (or the proxy): the forwarded batch arrives here marked.
        using var loop = await PutAsync(client, Batch(Get(Unported)), r => r.Headers.Add(BatchRunner.ForwardedHeader, "1"));
        Assert.Equal(HttpStatusCode.LoopDetected, loop.StatusCode);
        Assert.Single(ts.Requests);
    }

    [Fact]
    public async Task TheBatchItselfNeedsTheToken()
    {
        await using var ts = await FakeTs.StartAsync();
        var client = Client(ts.Url);
        client.DefaultRequestHeaders.Remove(HydraToken.Header);
        using var response = await PutAsync(client, Batch(Get("/commerce/purchases/me")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(ts.Requests);
    }

    /// <summary>
    /// Stands in for the TS server's /batch: records each request and answers every sub-request with
    /// <see cref="ItemFor"/>, the item for /ssc/invoke/compressed holding <see cref="s_compressed"/>.
    /// </summary>
    private sealed class FakeTs : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeTs(WebApplication app, string url)
        {
            _app = app;
            Url = url;
        }

        public string Url { get; }

        public List<(IHeaderDictionary Headers, byte[] Body)> Requests { get; } = [];

        /// <summary>What came as a plain request, as the router would pass it to another service.</summary>
        public List<(string Method, string Target, IHeaderDictionary Headers, byte[] Body)> Routed { get; } = [];

        public static byte[] ItemFor(string url) => url == "/ssc/invoke/compressed"
            ? HydraEncoder.Encode(new JsonObject { ["status_code"] = 200, ["headers"] = new JsonObject(), ["body"] = HydraRaw.Node(s_compressed) })
            : HydraEncoder.Encode(new JsonObject { ["status_code"] = 200, ["headers"] = new JsonObject(), ["body"] = new JsonObject { ["url"] = url } });

        public static async Task<FakeTs> StartAsync(TimeSpan? delay = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            FakeTs? self = null;
            app.MapPut("/batch", async (HttpContext context) =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body);
                lock (self!.Requests)
                {
                    self.Requests.Add((new HeaderDictionary(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value)), body.ToArray()));
                }

                if (delay is { } wait)
                {
                    await Task.Delay(wait, context.RequestAborted);
                }

                var items = HydraDecoder.Decode(body.ToArray())!["requests"]!.AsArray().Select(r => HydraRaw.Node(ItemFor((string)r!["url"]!))).ToArray();
                byte[] answer = HydraEncoder.Encode(new JsonObject { ["responses"] = new JsonArray(items) });
                context.Response.ContentType = HydraBodies.ContentType;
                await context.Response.Body.WriteAsync(answer);
            });
            // Anything else: a sub-request of another service's, through the router. Answers which request it was.
            app.MapFallback(async (HttpContext context) =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body);
                string target = context.Request.Path + context.Request.QueryString;
                lock (self!.Routed)
                {
                    self.Routed.Add((context.Request.Method, target, new HeaderDictionary(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value)), body.ToArray()));
                }

                context.Response.StatusCode = target.Contains("teapot", StringComparison.Ordinal) ? 418 : 200;
                context.Response.ContentType = HydraBodies.ContentType;
                await context.Response.Body.WriteAsync(HydraEncoder.Encode(new JsonObject { ["routed"] = context.Request.Method + " " + target }));
            });
            await app.StartAsync();
            string url = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
            self = new FakeTs(app, url);
            return self;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
