using System.Net;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.TestSupport;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Lobbies.Tests;

/// <summary>
/// This service's SSC functions the TS server answers with a fixed answer and nothing written
/// (tools/ssc/constants_diff.mjs compares them with the TS server's, byte for byte).
/// </summary>
public sealed class SscConstantsEndpointTests(ServiceFactory<Program> factory) : IClassFixture<ServiceFactory<Program>>
{
    private const string Empty = """{"body":{},"metadata":null,"return_code":0}""";
    private readonly HttpClient _client = factory.CreateGameClient();

    [Theory]
    [InlineData("cancel_party_invite")]
    [InlineData("decline_party_invite")]
    [InlineData("update_party_game_modes")]
    public async Task AnswersTheTsServersFixedAnswer(string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/ssc/invoke/" + route);
        request.Content = new ByteArrayContent(HydraCodec.EncodeJson("""{"LobbyId":"x"}"""));
        request.Content.Headers.ContentType = new(HydraBodies.ContentType);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HydraBodies.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HydraCodec.EncodeJson(Empty), await response.Content.ReadAsByteArrayAsync());
    }
}
