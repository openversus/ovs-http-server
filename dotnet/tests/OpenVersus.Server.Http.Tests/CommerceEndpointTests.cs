using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>The commerce routes the game calls at login, answered from Static/.</summary>
public sealed class CommerceEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private readonly HttpClient _client = factory.CreateGameClient();

    private async Task<HttpResponseMessage> GetHydraAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new(HydraBodies.ContentType);
        return await _client.SendAsync(request);
    }

    [Theory]
    [InlineData("/commerce/products", "commerce-products")]
    [InlineData("/commerce/products?partial_response=1", "commerce-products-partial")]
    [InlineData("/commerce/products?count=-1&fields=slug&fields=prices&partial_response=1", "commerce-products-partial")]
    [InlineData("/commerce/products?partial_response=", "commerce-products")]
    [InlineData("/commerce/purchases/me?count=25&page=1&state=transient_failure", "commerce-purchases-me")]
    [InlineData("/commerce/steam/mtx_user_info/me", "commerce-steam-mtx-user-info-me")]
    public async Task AnswersTheStaticFileAsHydra(string path, string name)
    {
        using var response = await GetHydraAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HydraBodies.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HydraCodec.EncodeJson(StaticResponses.Json(name)), await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/commerce/purchases/someone")]
    [InlineData("/commerce/steam/mtx_user_info/someone")]
    public async Task AnotherIdIsAnsweredAsTheTsCatchAllSinceTheTsServerHasNoRouteForIt(string path)
    {
        using var response = await GetHydraAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(Stub.Header));
        Assert.Equal(HydraCodec.EncodeJson("""{"body":{"Crc":1267552971,"MatchmakingCrc":2},"metadata":null,"return_code":200}"""), await response.Content.ReadAsByteArrayAsync());
    }
}
