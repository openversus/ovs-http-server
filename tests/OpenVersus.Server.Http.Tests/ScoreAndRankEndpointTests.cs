using System.Net;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The ranked score lookup as the game sends it (PUT with x-hydra-http-method: GET). This host has no Mongo, so it
/// answers the TS catch's empty body; the answers from real ratings were compared with the TS server's directly.
/// </summary>
public sealed class ScoreAndRankEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private static HttpRequestMessage Lookup(string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/leaderboards/bulk/score-and-rank/{id}") { Content = new StringContent("{}") };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(HydraMethodOverride.Header, "GET");
        return request;
    }

    [Fact]
    public async Task WithoutRatingsItAnswersTheTsCatch()
    {
        using var response = await factory.CreateGameClient().SendAsync(Lookup("0000000000000000000a0001"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"body":{},"metadata":null,"return_code":200}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NeedsTheSessionToken()
    {
        using var response = await factory.CreateClient().SendAsync(Lookup("0000000000000000000a0001"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
