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

    [Fact]
    public void EveryPathItGuardsIsThisServicesRoute()
    {
        Assert.All(ClientGameplayGate.Paths, p => Assert.Equal(KnownServices.Lobbies.Name, RouteTable.OwnerOfPath(p)));
    }

    // The test token has no registered identity, so the gate must ask for the toast, and without Redis it cannot.
    [Fact]
    public async Task WhenTheGateCannotBeEvaluatedItAnswers503WithItsAnswer()
    {
        using var response = await factory.CreateGameClient().PutAsync("/ssc/invoke/join_party_lobby", Empty());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(1, (int)body["return_code"]!);
        Assert.Equal("client_update_required", (string)body["body"]!["error"]!);
    }

    [Fact]
    public async Task APathItDoesNotCoverIsNotAsked()
    {
        using var response = await factory.CreateGameClient().PutAsync("/ssc/invoke/leave_player_lobby", Empty());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PutLeavePlayerLobby", response.Headers.GetValues(Stub.EndpointHeader).Single());
    }

    [Fact]
    public async Task WithTheCheckOffEverythingGoesThrough()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Clients:VersionCheck", "false"));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(HydraToken.Header, ServiceFactory<Program>.Token());
        using var response = await client.PutAsync("/ssc/invoke/join_party_lobby", Empty());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PutJoinPartyLobby", response.Headers.GetValues(Stub.EndpointHeader).Single());
    }

    [Fact]
    public async Task WithoutATokenTheTokenCheckAnswersFirst()
    {
        using var response = await factory.CreateClient().PutAsync("/ssc/invoke/join_party_lobby", Empty());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
