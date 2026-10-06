using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Lobbies.Tests;

/// <summary>
/// The gameplay gate in the service whose routes it guards, and what it answers when it cannot tell (these servers have
/// no Redis). Which paths it covers: OpenVersus.Server.Http.Tests.
/// </summary>
public sealed class ClientGameplayGateTests(ServiceFactory<Program> factory) : IClassFixture<ServiceFactory<Program>>
{
    private static StringContent Empty() => new("{}", System.Text.Encoding.UTF8, "application/json");

    // Each in a service that runs the gate: this one, or (the rift nodes) the HTTP service.
    [Fact]
    public void EveryPathItGuardsIsAPortedServicesRoute()
    {
        Assert.All(ClientGameplayGate.Paths, p => Assert.Contains(RouteTable.OwnerOfPath(p), new[] { KnownServices.Lobbies.Name, KnownServices.Http.Name }));
    }

    // The test token has no registered identity, so the gate must ask for the toast, and without Redis it cannot.
    [Fact]
    public async Task WhenTheGateCannotBeEvaluatedItAnswers503WithItsAnswer()
    {
        using var response = await factory.CreateGameClient().PutAsync("/ssc/invoke/start_custom_match", Empty());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(1, (int)body["return_code"]!);
        Assert.Equal("client_update_required", (string)body["body"]!["error"]!);
    }

    // A lobby join included: lobbies stay open to a player who must update.
    [Theory]
    [InlineData("/ssc/invoke/leave_player_lobby", "PutLeavePlayerLobby")]
    [InlineData("/ssc/invoke/join_party_lobby", "PutJoinPartyLobby")]
    public async Task APathItDoesNotCoverIsNotAsked(string path, string endpoint)
    {
        using var response = await factory.CreateGameClient().PutAsync(path, Empty());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(endpoint, response.Headers.GetValues(Stub.EndpointHeader).Single());
    }

    [Fact]
    public async Task WithTheCheckOffEverythingGoesThrough()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Clients:VersionCheck", "false"));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(HydraToken.Header, ServiceFactory<Program>.Token());
        // The route itself answers (it has no Redis here either: whatever it answers, not the gate's 503).
        using var response = await client.PutAsync("/ssc/invoke/start_custom_match", Empty());
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("PutStartCustomMatch", response.Headers.GetValues(Stub.EndpointHeader).Single());
    }

    [Fact]
    public async Task WithoutATokenTheTokenCheckAnswersFirst()
    {
        using var response = await factory.CreateClient().PutAsync("/ssc/invoke/join_party_lobby", Empty());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
