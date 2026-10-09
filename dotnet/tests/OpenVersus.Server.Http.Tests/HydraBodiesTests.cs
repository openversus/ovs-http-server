using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using OpenVersus.Server.Core.Hydra;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;
using OpenVersus.Server.Http.Shared.Hosting;

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
        // A stub the gameplay gate does not cover (the matchmaking requests are gated; see ClientGameplayGateTests): a
        // route the TS server answers that is not ported yet. Move it when it is.
        var response = await _server.CreateGameClient().PostAsync("/virtual_commerce/purchases/x/toasts_gleamium", Hydra("""{ "a": 1 }"""));
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("PostVirtualCommercePurchasesByIdByItem", response.Headers.GetValues("X-OVS-Stub").Single());
    }

    // A route the TS server never handled answers what its catch-all did, in the request's form: Hydra for a Hydra
    // request, JSON otherwise (the TS server installed its Hydra encoder only on a Hydra request).
    [Fact]
    public async Task ARouteTheTsServerNeverHandledAnswersItsCatchAllInTheRequestsForm()
    {
        var client = _server.CreateGameClient();
        var hydra = await client.PostAsync("/ssc/invoke/debug_unlock_inventory_item", Hydra("""{ "a": 1 }"""));
        Assert.Equal(HttpStatusCode.OK, hydra.StatusCode);
        Assert.Equal(HydraBodies.ContentType, hydra.Content.Headers.ContentType?.MediaType);
        Assert.False(hydra.Headers.Contains("X-OVS-Stub"));
        Assert.Equal("AnyDebugUnlockInventoryItem", hydra.Headers.GetValues("X-OVS-Endpoint").Single());
        Assert.Equal(HydraCodec.EncodeJson(CatchAll), await hydra.Content.ReadAsByteArrayAsync());

        var json = await client.GetAsync("/ssc/invoke/get_preferred_currency");
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
        Assert.Equal(CatchAll, await json.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheGmLeaderboardsAnswerTheTsFallbackWithoutTheStores()
    {
        // The TS handler's fallback (its catch): empty lists, return_code 0. Not a stub, not the catch-all.
        var response = await _server.CreateGameClient().GetAsync("/ssc/invoke/get_gm_leaderboards");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("GetGetGmLeaderboards", response.Headers.GetValues("X-OVS-Endpoint").Single());
        Assert.Equal("""{"body":{"OneVsOne":[],"TwoVsTwo":[]},"metadata":null,"return_code":0}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LoadGameplayConfigIsAnEmptySuccess()
    {
        var response = await _server.CreateGameClient().GetAsync("/ssc/invoke/load_gameplay_config?MatchId=abc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("GetLoadGameplayConfig", response.Headers.GetValues("X-OVS-Endpoint").Single());
        Assert.Equal("""{"body":{},"metadata":null,"return_code":200}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/accounts/me/notifications", "AnyAccountsMeNotifications", """{"notifications":[],"total":0}""")]
    [InlineData("GET", "/accounts/me/notifications/bulk", "AnyAccountsMeNotificationsById", """{"notifications":[],"total":0}""")]
    [InlineData("POST", "/datarouter/api/v1/public/data/clients", "PostDatarouterApiV1PublicDataClients", "{}")]
    [InlineData("GET", "/global_configuration_types/eula/global_configurations/x", "GetGlobalConfigurationTypesByTypeGlobalConfigurationsById", "200")]
    // Without the stores the lookup answers {} as the TS catch does.
    [InlineData("GET", "/accounts/wb_network/abc", "GetAccountsByIdBySub", "{}")]
    // Methods and ids the TS server has no route for: its catch-all.
    [InlineData("PUT", "/accounts/me/notifications", "AnyAccountsMeNotifications", CatchAll)]
    [InlineData("GET", "/accounts/me/notifications/other", "AnyAccountsMeNotificationsById", CatchAll)]
    [InlineData("GET", "/accounts/me/notifications/bulk/x", "AnyAccountsMeNotificationsBulkById", CatchAll)]
    [InlineData("GET", "/global_configuration_types/other/global_configurations/x", "GetGlobalConfigurationTypesByTypeGlobalConfigurationsById", CatchAll)]
    [InlineData("GET", "/accounts/epic/abc", "GetAccountsByIdBySub", CatchAll)]
    public async Task SmallRoutesAnswerAsTheTsServerDoes(string method, string path, string endpoint, string body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        var response = await _server.CreateGameClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(endpoint, response.Headers.GetValues("X-OVS-Endpoint").Single());
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnAcknowledgedNotificationIs204()
    {
        var response = await _server.CreateGameClient().DeleteAsync("/accounts/me/notifications/bulk/abc");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("AnyAccountsMeNotificationsBulkById", response.Headers.GetValues("X-OVS-Endpoint").Single());
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    private const string CatchAll = """{"body":{"Crc":1267552971,"MatchmakingCrc":2},"metadata":null,"return_code":200}""";

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
