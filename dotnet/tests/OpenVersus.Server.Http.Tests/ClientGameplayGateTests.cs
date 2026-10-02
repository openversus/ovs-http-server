using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// Which paths the gameplay gate covers (all of them the lobbies service's; what it answers there:
/// OpenVersus.Server.Lobbies.Tests). Its decisions against real sessions are checked against the TS server by
/// tools/clients/gate_diff.mjs.
/// </summary>
public sealed class ClientGameplayGateTests
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
}
