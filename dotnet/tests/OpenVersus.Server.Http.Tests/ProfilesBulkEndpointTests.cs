using System.Net;
using System.Text.Json.Nodes;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The profile lookups as the game sends them: PUT with x-hydra-http-method: GET and a Hydra body. This host has no
/// stores, so they answer the TS catch's []; tools/friends/friends_diff.mjs covers the answers from real data.
/// </summary>
public sealed class ProfilesBulkEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private static HttpRequestMessage Lookup(string path, JsonNode? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new ByteArrayContent(body is null ? [] : HydraCodec.Encode(body)) };
        request.Content.Headers.ContentType = new(HydraBodies.ContentType);
        request.Headers.Add("x-hydra-http-method", "GET");
        return request;
    }

    [Theory]
    [InlineData("/accounts/wb_network/bulk")]
    [InlineData("/profiles/bulk")]
    public async Task AnswersHydraFromThePortedEndpoint(string path)
    {
        using var response = await factory.CreateGameClient().SendAsync(Lookup(path, new JsonObject { ["ids"] = new JsonArray("0000000000000000000a0001") }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HydraCodec.Encode(new JsonArray()), await response.Content.ReadAsByteArrayAsync());
        Assert.False(response.Headers.Contains(Stub.Header));
    }

    [Theory]
    // The game's shape is {ids}; the TS server also takes a bare array, and anything else answers [].
    [InlineData("[\"0000000000000000000a0001\"]")]
    [InlineData("\"ids\"")]
    [InlineData("{\"ids\":\"abc\"}")]
    public async Task OtherBodiesAnswerAnEmptyList(string json)
    {
        using var response = await factory.CreateGameClient().SendAsync(Lookup("/accounts/wb_network/bulk", JsonNode.Parse(json)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HydraCodec.Encode(new JsonArray()), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnotherNetworkIsAnsweredAsTheTsCatchAllDid()
    {
        // The TS server routes only wb_network; any other network fell to its catch-all.
        using var response = await factory.CreateGameClient().SendAsync(Lookup("/accounts/epic/bulk", new JsonObject { ["ids"] = new JsonArray() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(Stub.Header));
    }

    [Theory]
    [InlineData("/accounts/wb_network/bulk")]
    [InlineData("/profiles/bulk")]
    public async Task NeedsTheSessionToken(string path)
    {
        using var response = await factory.CreateClient().SendAsync(Lookup(path, new JsonObject { ["ids"] = new JsonArray() }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
