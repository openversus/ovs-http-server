using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// Which paths the gameplay gate covers, and what it answers when it cannot tell (these servers have no Redis). Its
/// decisions against real sessions are checked against the TS server by tools/clients/gate_diff.mjs.
/// </summary>
public sealed class ClientGameplayGateTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    [Theory]
    [InlineData("/ssc/invoke/join_party_lobby", true)]
    [InlineData("/SSC/Invoke/Join_Party_Lobby", true)]
    [InlineData("/ssc/invoke/join_party_lobby/", true)]
    [InlineData("/ssc/invoke/join_party_lobby/extra", true)]
    [InlineData("/ssc/invoke/join_party_lobby_x", false)]
    [InlineData("/ssc/invoke/join_party", false)]
    [InlineData("/matches/matchmaking/2v2-retail/request", true)]
    [InlineData("/matches/matchmaking/2v2-retail/cancel", false)]
    [InlineData("/ssc/invoke/get_country_code", false)]
    public void CoversTheTsServersPathsAsExpressMountsThem(string path, bool covered) => Assert.Equal(covered, ClientGameplayGate.Covers(path));

    // A path the gate lists that no game route matches would gate nothing: a typo, or a route renamed.
    [Fact]
    public void EveryPathIsAGameRouteInTheRouteMap()
    {
        // (The map writes "?" for a method nobody has seen yet.)
        var game = RouteMapTests.LoadRoutes().Where(r => r.Kind == "game" && r.Method is "GET" or "PUT" or "POST" or "DELETE").ToList();
        var routes = new RouteList(string.Join(", ", game.Select(r => $"{r.Method} {r.Path}")));
        Assert.All(ClientGameplayGate.Paths, p => Assert.True(game.Select(r => r.Method).Distinct().Any(m => routes.Contains(m, p)), $"{p} is no game route"));
    }

    // The test token has no registered identity, so the gate must ask for the toast, and without Redis it cannot.
    [Fact]
    public async Task WhenTheGateCannotBeEvaluatedItAnswers503WithItsAnswer()
    {
        using var response = await factory.CreateGameClient().PostAsync("/ssc/invoke/join_party_lobby", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(1, (int)body["return_code"]!);
        Assert.Equal("client_update_required", (string)body["body"]!["error"]!);
    }

    [Fact]
    public async Task APathItDoesNotCoverIsNotAsked()
    {
        using var response = await factory.CreateGameClient().PostAsync("/ssc/invoke/join_party_lobby_x", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(new StubSettings().StatusCode, (int)response.StatusCode);
    }

    [Fact]
    public async Task WithTheCheckOffEverythingGoesThrough()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Clients:VersionCheck", "false"));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(HydraToken.Header, GameAppFactory.Token());
        using var response = await client.PostAsync("/ssc/invoke/join_party_lobby", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(new StubSettings().StatusCode, (int)response.StatusCode);
        Assert.True(response.Headers.Contains(Stub.Header));
    }

    [Fact]
    public async Task WithoutATokenTheTokenCheckAnswersFirst()
    {
        using var response = await factory.CreateClient().PostAsync("/ssc/invoke/join_party_lobby", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
