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
/// PUT /batch: this service's sub-requests answered here, another service's through the router as requests of their
/// own; every answer passed on byte for byte, in the game's order; failures confined to their own items.
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

    private HttpClient Client(string routerUrl, params (string Key, string Value)[] settings)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Batch:EdgeUrl", routerUrl);
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

    // What the TS server's catch-all answers (the default CRC: no Mongo here), and so every route it never handled.
    private static JsonNode CatchAll => JsonNode.Parse("""{"body":{"Crc":1267552971,"MatchmakingCrc":2},"metadata":null,"return_code":200}""")!;

    [Fact]
    public async Task AnItemAnotherServiceOwnsGoesThroughTheRouterAsARequestOfItsOwn()
    {
        await using var router = await FakeRouter.StartAsync();
        var client = Client(router.Url);
        string token = GameAppFactory.Token();
        // /friends/me is the social service's, /accounts/{id}/relationships/followers too (more literal than the HTTP
        // service's /accounts/{id}/{sub}), the custom lobby's SSC the lobbies service's; /commerce/purchases/me is this
        // service's own.
        using var response = await PutAsync(client, Batch(
            Get("/friends/me?page=2", new JsonObject { ["x-hydra-http-method"] = "GET", ["x-custom"] = "kept" }),
            Get("/commerce/purchases/me"),
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
        Assert.True(JsonNode.DeepEquals(Static("commerce-purchases-me"), responses[1]!["body"]));
        Assert.Equal("GET /accounts/abc/relationships/followers", (string)responses[2]!["body"]!["routed"]!);
        Assert.Equal("PUT /ssc/invoke/create_custom_game_lobby", (string)responses[3]!["body"]!["routed"]!);
        // The owner's status is passed on as it came.
        Assert.Equal(418, Status(responses[4]));

        Assert.Equal(4, router.Routed.Count);
        var friends = router.Routed.Single(r => r.Target == "/friends/me?page=2");
        Assert.Equal(token, (string?)friends.Headers[HydraToken.Header]);
        Assert.Equal("203.0.113.9", (string?)friends.Headers["x-real-ip"]);
        Assert.Equal("kept", (string?)friends.Headers["x-custom"]);
        // The client's zstd capability header rides on the batch; every item inherits it (the hiss inside a login batch reads it).
        Assert.Equal("1", (string?)friends.Headers[HissZstd.Header]);
        var lobby = router.Routed.Single(r => r.Target == "/ssc/invoke/create_custom_game_lobby");
        Assert.Equal("1v1", (string)HydraDecoder.Decode(lobby.Body)!["Mode"]!);
    }

    [Fact]
    public async Task AnItemTheRouterCannotTakeAnswers502AndTheRestStillAnswers()
    {
        // Nothing listens on port 1.
        using var response = await PutAsync(Client("http://127.0.0.1:1"), Batch(Get("/friends/me"), Get("/commerce/purchases/me")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal(502, Status(responses[0]));
        Assert.Equal(200, Status(responses[1]));
    }

    [Fact]
    public async Task ItemsAnsweredHereAreInTheGamesOrder()
    {
        await using var router = await FakeRouter.StartAsync();
        var client = Client(router.Url);
        using var response = await PutAsync(client, Batch(
            Get("/commerce/products"),
            Get("/commerce/purchases/someone"),
            new JsonObject { ["verb"] = "PUT", ["url"] = "/commerce/purchases/me?count=25", ["headers"] = new JsonObject { ["x-hydra-http-method"] = "GET" } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HydraBodies.ContentType, response.Content.Headers.ContentType?.MediaType);
        // /commerce/purchases/someone is no route of the TS server's (only "me" is): answered here as its catch-all did.
        byte[] expected = HydraEncoder.Encode(new JsonObject
        {
            ["responses"] = new JsonArray(
                Item(200, Static("commerce-products")),
                Item(200, CatchAll),
                Item(200, Static("commerce-purchases-me"))),
        });
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(router.Routed);
    }

    [Fact]
    public async Task AnItemTheTsServerNeverHandledIsAnsweredHereAsItsCatchAll()
    {
        await using var router = await FakeRouter.StartAsync();
        // An SSC name the route map lacks (SscUnlisted) and one it has but the TS server never handled.
        using var response = await PutAsync(Client(router.Url), Batch(Get("/ssc/invoke/some_other_name"), Get("/ssc/invoke/get_preferred_currency")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal(2, responses.Count);
        foreach (var item in responses)
        {
            Assert.Equal(200, Status(item));
            Assert.True(JsonNode.DeepEquals(CatchAll, item!["body"]), item.ToJsonString());
        }

        Assert.Empty(router.Routed);
    }

    [Fact]
    public async Task AnOwnersBytesArePassedOnAsTheyCame()
    {
        await using var router = await FakeRouter.StartAsync();
        using var response = await PutAsync(Client(router.Url), Batch(Get("/friends/me?compressed=1")));
        byte[] answer = await response.Content.ReadAsByteArrayAsync();
        Assert.True(answer.AsSpan().IndexOf(s_compressed) >= 0, "the compressed value was re-encoded");
        Assert.NotEqual(s_compressed, HydraEncoder.Encode(HydraDecoder.Decode(s_compressed)));
    }

    [Fact]
    public async Task TheSessionTokenCarriesIntoEverySubRequest()
    {
        await using var router = await FakeRouter.StartAsync();
        // /commerce/products needs the token: without it the item would answer 401.
        using var response = await PutAsync(Client(router.Url), Batch(Get("/commerce/products")));
        var item = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]![0]!;
        Assert.Equal(200, Status(item));
        Assert.Empty(router.Routed);
    }

    [Fact]
    public async Task AnOwnerThatNeverAnswersIsGivenUpOnAfterTheTimeout()
    {
        await using var router = await FakeRouter.StartAsync(delay: TimeSpan.FromSeconds(20));
        var watch = Stopwatch.StartNew();
        using var response = await PutAsync(Client(router.Url, ("Batch:ForwardTimeoutSeconds", "1")), Batch(Get("/friends/me"), Get("/commerce/purchases/me")));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal(504, Status(responses[0]));
        Assert.Equal(200, Status(responses[1]));
    }

    [Fact]
    public async Task ANestedBatchAndAMalformedItemAnswer400AndTheRestAsUsual()
    {
        await using var router = await FakeRouter.StartAsync();
        using var response = await PutAsync(Client(router.Url), Batch(
            new JsonObject { ["verb"] = "PUT", ["url"] = "/batch", ["body"] = new JsonObject { ["requests"] = new JsonArray() } },
            new JsonObject { ["verb"] = "GET" },
            new JsonObject { ["verb"] = "GET", ["url"] = "http://elsewhere/x" },
            JsonValue.Create("not a request"),
            Get("/commerce/purchases/me")));
        var responses = HydraDecoder.Decode(await response.Content.ReadAsByteArrayAsync())!["responses"]!.AsArray();
        Assert.Equal([400, 400, 400, 400, 200], responses.Select(r => Status(r)));
        Assert.Empty(router.Routed);
    }

    [Fact]
    public async Task ABodyWithoutRequestsAnswers400()
    {
        await using var router = await FakeRouter.StartAsync();
        using var response = await PutAsync(Client(router.Url), HydraEncoder.Encode(new JsonObject { ["options"] = s_options.DeepClone() }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheBatchItselfNeedsTheToken()
    {
        await using var router = await FakeRouter.StartAsync();
        var client = Client(router.Url);
        client.DefaultRequestHeaders.Remove(HydraToken.Header);
        using var response = await PutAsync(client, Batch(Get("/friends/me")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(router.Routed);
    }

    /// <summary>
    /// Stands in for the router in front of the other services: records each request and answers which request it was
    /// (418 for a target holding "teapot"; for one holding "compressed", <see cref="s_compressed"/> as the body).
    /// </summary>
    private sealed class FakeRouter : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeRouter(WebApplication app, string url)
        {
            _app = app;
            Url = url;
        }

        public string Url { get; }

        public List<(string Method, string Target, IHeaderDictionary Headers, byte[] Body)> Routed { get; } = [];

        public static async Task<FakeRouter> StartAsync(TimeSpan? delay = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            FakeRouter? self = null;
            app.MapFallback(async (HttpContext context) =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body);
                string target = context.Request.Path + context.Request.QueryString;
                lock (self!.Routed)
                {
                    self.Routed.Add((context.Request.Method, target, new HeaderDictionary(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value)), body.ToArray()));
                }

                if (delay is { } wait)
                {
                    await Task.Delay(wait, context.RequestAborted);
                }

                context.Response.StatusCode = target.Contains("teapot", StringComparison.Ordinal) ? 418 : 200;
                context.Response.ContentType = HydraBodies.ContentType;
                await context.Response.Body.WriteAsync(target.Contains("compressed", StringComparison.Ordinal)
                    ? s_compressed
                    : HydraEncoder.Encode(new JsonObject { ["routed"] = context.Request.Method + " " + target }));
            });
            await app.StartAsync();
            string url = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
            self = new FakeRouter(app, url);
            return self;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
