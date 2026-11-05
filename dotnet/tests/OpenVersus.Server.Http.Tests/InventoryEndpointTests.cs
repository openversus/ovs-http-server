using System.Net;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The inventory route as the game sends it. The answers were compared with the TS server's
/// (tools/inventory/inventory_diff.mjs, and 200 real accounts); here, the routing.
/// </summary>
public sealed class InventoryEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private static HttpRequestMessage Request(string? overrideMethod)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/profiles/0000000000000000000a0001/inventory") { Content = new StringContent("{}") };
        request.Content.Headers.ContentType = new("application/json");
        if (overrideMethod is not null)
        {
            request.Headers.Add(HydraMethodOverride.Header, overrideMethod);
        }

        return request;
    }

    [Fact]
    public async Task TheLookupAndThePlainPutBothReachTheInventory()
    {
        using var lookup = await factory.CreateGameClient().SendAsync(Request("GET"));
        Assert.Equal("GetProfilesByIdInventory", lookup.Headers.GetValues(Stub.EndpointHeader).Single());
        // This host has no Mongo.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lookup.StatusCode);
        // The plain PUT (modifications) is the same TS handler: the inventory, nothing applied.
        using var put = await factory.CreateGameClient().SendAsync(Request(null));
        Assert.Equal("PutProfilesByIdInventory", put.Headers.GetValues(Stub.EndpointHeader).Single());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, put.StatusCode);
    }

    [Fact]
    public async Task NeedsTheSessionToken()
    {
        using var response = await factory.CreateClient().SendAsync(Request("GET"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
