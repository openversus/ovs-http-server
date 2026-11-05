using System.Net;
using System.Text.Json.Nodes;
using OpenVersus.Server.Http.Shared.Hosting;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The leaderboard screens: Hydra whatever was asked, and no token needed (the TS server answers them before its token
/// check). This host has no Mongo, so the boards are empty; real boards were compared with the TS server's directly.
/// </summary>
public sealed class LeaderboardEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    [Theory]
    [InlineData("/leaderboards/ranked_season5_1v1_all/show?count=100")]
    [InlineData("/leaderboards/ranked_season5_1v1_all/around/0000000000000000000a0001")]
    [InlineData("/leaderboards/ranked_season5_1v1_all/around/me")]
    public async Task AnswersHydraWithoutAToken(string path)
    {
        using var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HydraBodies.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HydraCodec.Encode(new JsonObject { ["leaders"] = new JsonArray() }), await response.Content.ReadAsByteArrayAsync());
    }
}
