using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The Hydra body middleware on a small app whose endpoint echoes the JSON it is given: the game's binary bodies reach
/// endpoints as JSON and their JSON answers go back as Hydra, byte for byte what the codec writes. With
/// OVS_TEST_HYDRA_CORPUS set, every captured game request is echoed and must come back as the same bytes.
/// </summary>
public sealed class HydraBodiesTests : IAsyncLifetime, IClassFixture<GameAppFactory>
{
    private readonly GameAppFactory _server;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public HydraBodiesTests(GameAppFactory server)
    {
        _server = server;
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseHydraBodies();
        _app.UseRouting();
        _app.MapPost("/echo", (JsonNode? body) => Results.Json(body));
        _app.MapPost("/text", () => Results.Text("plain"));
        _app.MapPost("/empty", () => Results.StatusCode(501));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static ByteArrayContent Hydra(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(HydraBodies.ContentType);
        return content;
    }

    private static ByteArrayContent Hydra(string json) => Hydra(HydraEncoder.Encode(JsonNode.Parse(json)));

    [Fact]
    public async Task AHydraRequestIsAnsweredInHydra()
    {
        string json = """{ "a": 1, "when": { "_hydra_unix_date": 1790640000 }, "name": { "localizations": { "en": "Shaggy" } }, "list": [true, null, -5, 1.5] }""";
        var response = await _client.PostAsync("/echo", Hydra(json));
        Assert.Equal(HydraBodies.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("openversus", response.Headers.GetValues("X-Hydra-Info").Single());
        Assert.True(double.Parse(response.Headers.GetValues("X-Hydra-Server-Time").Single(), System.Globalization.CultureInfo.InvariantCulture) > 1_700_000_000);
        Assert.Equal(HydraEncoder.Encode(JsonNode.Parse(json)), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AWholeNumberDoubleTheGameSentStaysADouble()
    {
        // The game sends some whole numbers as DOUBLE (445 of its 801 doubles in the captures); they must not come back as integers.
        byte[] sent = HydraEncoder.Encode(JsonNode.Parse("""{ "ratio": { "_hydra_double": 2 } }"""));
        var response = await _client.PostAsync("/echo", Hydra(sent));
        Assert.Equal(sent, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AJsonRequestIsAnsweredInJson()
    {
        var response = await _client.PostAsync("/echo", new StringContent("""{"a":1}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"a":1}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ABodyThatIsNotHydraBecomesAnEmptyObject()
    {
        var response = await _client.PostAsync("/echo", Hydra([0xEE, 0x01]));
        Assert.Equal(HydraEncoder.Encode(new JsonObject()), await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/text", HttpStatusCode.OK, "text/plain")]
    [InlineData("/empty", HttpStatusCode.NotImplemented, null)]
    public async Task AnAnswerThatIsNotJsonPassesThrough(string path, HttpStatusCode status, string? type)
    {
        var response = await _client.PostAsync(path, Hydra("{}"));
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheRealServersStubsAnswerHydraRequestsUnchanged()
    {
        var response = await _server.CreateGameClient().PostAsync("/matches/matchmaking/1v1-retail/request", Hydra("""{ "a": 1 }"""));
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("PostMatchesMatchmakingByCriteriaRequest", response.Headers.GetValues("X-OVS-Stub").Single());
    }

    [SkippableFact]
    public async Task EveryCapturedGameRequestSurvivesTheTripThroughJson()
    {
        string? corpus = Environment.GetEnvironmentVariable("OVS_TEST_HYDRA_CORPUS");
        Skip.If(string.IsNullOrEmpty(corpus), "set OVS_TEST_HYDRA_CORPUS to run");
        var files = Directory.GetFiles(Path.Combine(corpus!, "req"), "*.bin");
        Assert.NotEmpty(files);
        var differ = new List<string>();
        int changedByCodec = 0;
        foreach (var file in files)
        {
            byte[] sent = File.ReadAllBytes(file);
            byte[] back = await (await _client.PostAsync("/echo", Hydra(sent))).Content.ReadAsByteArrayAsync();
            // Not the sent bytes themselves: the game uses integer map keys (AllMultiplayParams: 1, 2, ...), which JSON
            // turns into "1", "2", exactly as the TS server's decoder does (and the game accepts them back as strings).
            // What must hold is that passing through JSON and an endpoint adds nothing to what the codec does.
            byte[] expected = HydraEncoder.Encode(HydraDecoder.Decode(sent));
            if (!expected.SequenceEqual(sent))
            {
                changedByCodec++;
            }

            if (!back.AsSpan().SequenceEqual(expected))
            {
                differ.Add(Path.GetFileName(file));
            }
        }

        Assert.True(differ.Count == 0, $"{differ.Count} of {files.Length} came back different: {string.Join(", ", differ.Take(10))}");
        // Only the integer-keyed bodies change at all; anything more is a new kind of difference to look at.
        Assert.True(changedByCodec <= files.Count(f => f.Contains("create_party_lobby", StringComparison.Ordinal)), $"{changedByCodec} bodies change in the codec itself");
    }
}
