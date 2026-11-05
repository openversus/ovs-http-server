using System.Net;
using System.Text.Json.Nodes;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.TestSupport;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Social.Tests;

/// <summary>
/// The friends reads as the game calls them (JSON, with its session token). This host has no stores, so every read ends
/// in the TS catch's answer; tools/friends/friends_diff.mjs covers the answers from real data.
/// </summary>
public sealed class FriendsEndpointTests(ServiceFactory<Program> factory) : IClassFixture<ServiceFactory<Program>>
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

    [Theory]
    // The game's writes answer their fixed shape whatever happened (this host has no stores: every write fails inside).
    [InlineData("/friends/me/invitations/abc/accept", "PutFriendsMeInvitationsByIdAccept", """{"status":"ok"}""")]
    [InlineData("/friends/me/invitations/abc/decline", "PutFriendsMeInvitationsByIdDecline", """{"status":"ok"}""")]
    [InlineData("/friends/me/unfriend/some-public-id", "PutFriendsMeUnfriendById", """{"status":"ok"}""")]
    [InlineData("/social/me/block/abc", "PutSocialMeBlockById", "{}")]
    [InlineData("/social/me/unblock/abc", "PutSocialMeUnblockById", "{}")]
    [InlineData("/accounts/me/relationships/abc/block", "PutAccountsMeRelationshipsByIdBlock", "{}")]
    [InlineData("/accounts/me/relationships/abc/unblock", "PutAccountsMeRelationshipsByIdUnblock", "{}")]
    public async Task TheGamesWritesAnswerTheirShapeWhateverHappens(string path, string endpoint, string body)
    {
        using var response = await factory.CreateGameClient().PutAsync(path, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(endpoint, response.Headers.GetValues("X-OVS-Endpoint").Single());
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    // The OpenVersus client's writes: without a resolvable account (no stores here) they answer 401 as the TS routes did.
    [InlineData("POST", "/ovs/friends/accept", "PostOvsFriendsAccept")]
    [InlineData("POST", "/ovs/friends/decline", "PostOvsFriendsDecline")]
    [InlineData("POST", "/ovs/friends/block", "PostOvsFriendsBlock")]
    [InlineData("DELETE", "/ovs/friends/abc", "DeleteOvsFriendsByFriendId")]
    public async Task TheClientsWritesRefuseAnUnconnectedCaller(string method, string path, string endpoint)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("""{"requestId":"x","targetId":"y"}""", System.Text.Encoding.UTF8, "application/json") };
        using var response = await factory.CreateGameClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(endpoint, response.Headers.GetValues("X-OVS-Endpoint").Single());
        Assert.Equal("""{"error":"not_connected"}""", await response.Content.ReadAsStringAsync());
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
