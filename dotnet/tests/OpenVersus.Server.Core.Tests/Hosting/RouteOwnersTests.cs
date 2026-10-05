using Microsoft.AspNetCore.Http;
using OpenVersus.Server.Core.Hosting;

namespace OpenVersus.Server.Core.Tests.Hosting;

/// <summary>The owners built into the binary (docs/routes.json), as the batch fan-out and the services read them.</summary>
public sealed class RouteOwnersTests
{
    [Theory]
    [InlineData("GET", "/friends/me", "social")]
    [InlineData("PUT", "/ssc/invoke/create_custom_game_lobby", "lobbies")]
    [InlineData("PUT", "/ssc/invoke/create_rift_lobby", "lobbies")]
    [InlineData("PUT", "/ssc/invoke/perks_lock", "matchflow")]
    [InlineData("PUT", "/ssc/invoke/submit_end_of_match_stats", "http")]
    [InlineData("POST", "/ovs_end_match", "matchflow")]
    [InlineData("POST", "/access", "access")]
    [InlineData("GET", "/home", "web")]
    // Overlapping templates: the one with more literal segments.
    [InlineData("GET", "/accounts/abc/relationships/followers", "social")]
    [InlineData("GET", "/accounts/abc/something", "http")]
    // Nothing matches: the HTTP service's, where the router sends unknown paths.
    [InlineData("GET", "/no/such/route", "http")]
    [InlineData("PUT", "/ssc/invoke/no_such_function", "http")]
    public void EachRouteHasItsOwner(string method, string path, string owner)
    {
        Assert.Equal(owner, RouteOwners.Routes.OwnerOf(method, new PathString(path)));
    }

    [Fact]
    public void ARouteWithTheRequestsMethodBeatsOneWhoseMethodIsUnknown()
    {
        var owners = new RouteOwners([("?", "/things/{id}", "web"), ("GET", "/things/{id}", "social")]);

        Assert.Equal("social", owners.OwnerOf("GET", new PathString("/things/1")));
        Assert.Equal("web", owners.OwnerOf("PUT", new PathString("/things/1")));
    }
}
