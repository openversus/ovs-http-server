using System.Net;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>The layouts the game asks for at login, answered from Static/layout-*.json.</summary>
public sealed class LayoutEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    // The variants the TS server answers (handlers/layout.ts).
    public static TheoryData<string> Variants() =>
    [
        "account-cosmetics-variant", "battlepass-variant", "currency-variant", "fighter-road-layout", "fighter-variant",
        "main-variant", "prestige-variant", "rift-variant", "skin-variant",
    ];

    private async Task<HttpResponseMessage> GetAsync(string path, bool hydra)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (hydra)
        {
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentType = new(HydraBodies.ContentType);
        }

        return await factory.CreateGameClient().SendAsync(request);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task EveryVariantAnswersItsFileForAnyId(string variant)
    {
        foreach (string id in new[] { "6abae8edf198da1e7ed993ff", "anything" })
        {
            using var hydra = await GetAsync($"/layout/dokken-layout-type/personalized/{variant}/{id}", hydra: true);
            Assert.Equal(HydraBodies.ContentType, hydra.Content.Headers.ContentType?.MediaType);
            // The cached encoding is what encoding the JSON on each request would give.
            Assert.Equal(HydraCodec.EncodeJson(StaticResponses.Json($"layout-{variant}")), await hydra.Content.ReadAsByteArrayAsync());
            using var json = await GetAsync($"/layout/dokken-layout-type/personalized/{variant}/{id}", hydra: false);
            Assert.Equal(StaticResponses.Json($"layout-{variant}"), await json.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    // The game asks for these too; the TS server has no route for them.
    [InlineData("/layout/dokken-layout-type/personalized/fighter-select-layout/x")]
    [InlineData("/layout/dokken-layout-type/personalized/fighter-bundle-content/x")]
    [InlineData("/layout/other-layout-type/personalized/main-variant/x")]
    public async Task OthersAreAnsweredAsTheTsCatchAllDid(string path)
    {
        using var response = await GetAsync(path, hydra: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(Stub.Header));
        Assert.Equal(HydraBodies.ContentType, response.Content.Headers.ContentType?.MediaType);
    }
}
