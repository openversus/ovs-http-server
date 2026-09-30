using System.Net;
using OpenVersus.Server.Http.Hosting;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The SSC functions the TS server answers with a fixed answer and nothing written (tools/ssc/constants_diff.mjs
/// compares them with the TS server's, byte for byte).
/// </summary>
public sealed class SscConstantsEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private const string Empty = """{"body":{},"metadata":null,"return_code":0}""";
    private readonly HttpClient _client = factory.CreateGameClient();

    private async Task<HttpResponseMessage> SendHydraAsync(HttpMethod method, string route)
    {
        using var request = new HttpRequestMessage(method, "/ssc/invoke/" + route);
        request.Content = new ByteArrayContent(HydraCodec.EncodeJson("""{"LobbyId":"x"}"""));
        request.Content.Headers.ContentType = new(HydraBodies.ContentType);
        return await _client.SendAsync(request);
    }

    [Theory]
    [InlineData("PUT", "game_install", Empty)]
    [InlineData("PUT", "cancel_party_invite", Empty)]
    [InlineData("PUT", "decline_party_invite", Empty)]
    [InlineData("PUT", "update_party_game_modes", Empty)]
    [InlineData("POST", "claim_mission_rewards", """{"body":{"MissionControllerContainers":{},"ClaimLocks":{}},"metadata":null,"return_code":0}""")]
    public async Task AnswersTheTsServersFixedAnswer(string method, string route, string json)
    {
        using var response = await SendHydraAsync(new HttpMethod(method), route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HydraBodies.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HydraCodec.EncodeJson(json), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task TheLaunchEventIsAnsweredWithNothing()
    {
        // The TS server's res.send(""): text/html and no body, even to a Hydra request.
        using var response = await SendHydraAsync(HttpMethod.Put, "game_launch_event");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }
}
