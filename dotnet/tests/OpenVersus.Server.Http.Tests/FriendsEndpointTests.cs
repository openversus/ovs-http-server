using System.Net;
using System.Text.Json.Nodes;
using OpenVersus.Server.Http.Hosting;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The friends reads as the game calls them (JSON, with its session token). This host has no stores, so every read ends
/// in the TS catch's answer; tools/friends/friends_diff.mjs covers the answers from real data.
/// </summary>
public sealed class FriendsEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    public static readonly TheoryData<string> Paths =
    [
        "/friends/me?page_size=1000&",
        "/friends/me/invitations/incoming?page_size=1000&state=open&",
        "/friends/me/invitations/outgoing?page_size=1000&state=open&",
        "/social/me/blocked",
    ];

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task AnswersJsonWithTwoHundredWhateverHappens(string path)
    {
        using var response = await factory.CreateGameClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("""{"total":0,"page":1,"page_size":1000,"results":[]}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(Paths))]
    // /social/me/* is the game's own social layer on the OpenVersus host: it needs the token too.
    public async Task NeedsTheSessionToken(string path)
    {
        using var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AHydraRequestGetsHydra()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/friends/me") { Content = new ByteArrayContent([]) };
        request.Content.Headers.ContentType = new(HydraBodies.ContentType);
        using var response = await factory.CreateGameClient().SendAsync(request);
        Assert.Equal(HydraCodec.Encode(JsonNode.Parse("""{"total":0,"page":1,"page_size":1000,"results":[]}""")), await response.Content.ReadAsByteArrayAsync());
    }
}
