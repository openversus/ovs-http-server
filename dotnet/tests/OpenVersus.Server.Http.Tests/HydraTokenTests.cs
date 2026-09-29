using System.Net;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json.Nodes;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>The session token check, as the TS server's hydraTokenMiddleware: which routes, and what a refusal looks like.</summary>
public sealed class HydraTokenTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? token, bool hydra = true)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (hydra)
        {
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentType = new(HydraBodies.ContentType);
        }

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(HydraToken.Header, token);
        }

        return await _client.SendAsync(request);
    }

    [Theory]
    [InlineData(null, "Missing access token")]
    [InlineData("not.a.token", "Invalid access token")]
    public async Task AGameRouteRefusesWithoutAValidToken(string? token, string error)
    {
        using var response = await SendAsync("GET", "/commerce/products", token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Hydra-encoded for a Hydra request, as the TS server's res.json is by then.
        Assert.Equal(HydraCodec.Encode(new JsonObject { ["error"] = error }), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ARefusalToAJsonRequestIsJson()
    {
        using var response = await SendAsync("GET", "/commerce/products", null, hydra: false);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"Missing access token"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ATokenFromAnotherSecretOrExpiredIsInvalid()
    {
        using var other = await SendAsync("GET", "/commerce/products", GameAppFactory.Token("another-secret-0123456789abcdef0123456789"));
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        using var expired = await SendAsync("GET", "/commerce/products", GameAppFactory.Token(lifetime: TimeSpan.FromSeconds(-60)));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task AValidTokenReachesTheRoute()
    {
        using var response = await SendAsync("GET", "/commerce/products", GameAppFactory.Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    // The routes the TS server registers before its token check.
    [InlineData("POST", "/access")]
    [InlineData("DELETE", "/access")]
    [InlineData("POST", "/sessions/auth/token")]
    [InlineData("GET", "/leaderboards/x/show")]
    [InlineData("GET", "/leaderboards/x/around/me")]
    [InlineData("GET", "/leaderboards/x/around/0000000000000000000a0002")]
    [InlineData("GET", "/global_configuration_types/eula/global_configurations/x")]
    // Not game routes: they do their own checks.
    [InlineData("GET", "/ovs/notifications")]
    [InlineData("GET", "/health/live")]
    public async Task RoutesTheTsServerAnswersWithoutAToken(string method, string path)
    {
        using var response = await SendAsync(method, path, null, hydra: path != "/sessions/auth/token");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    // Only the eula configurations are open there.
    [InlineData("/global_configuration_types/other/global_configurations/x")]
    [InlineData("/global_configuration_types/eula/global_configurations")]
    // The TS server checks the token before it knows a path does not exist.
    [InlineData("/definitely/not/a/route")]
    public async Task EverythingElseNeedsAToken(string path)
    {
        using var response = await SendAsync("GET", path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var withToken = await SendAsync("GET", path, GameAppFactory.Token());
        Assert.NotEqual(HttpStatusCode.Unauthorized, withToken.StatusCode);
        Assert.True(withToken.Headers.Contains(Stub.Header) || withToken.Headers.Contains(Stub.EndpointHeader));
    }

    [Theory]
    // The TS server's req.hostname: the Host header's name, without its port.
    [InlineData(true, "http://game.example/commerce/products", false)]
    [InlineData(true, "http://game.example:8000/commerce/products", false)]
    [InlineData(true, "http://other.example/commerce/products", true)]
    // Off by default: the TS server's bypass is a testing aid.
    [InlineData(null, "http://game.example/commerce/products", true)]
    public async Task TheDomainHostSkipsTheCheckOnlyWhenEnabled(bool? skip, string url, bool refused)
    {
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Realtime:Domain", "game.example");
            if (skip is { } value)
            {
                b.UseSetting("Access:SkipTokenCheckForDomainHost", value.ToString());
            }
        });
        using var response = await app.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
        Assert.Equal(refused, response.StatusCode == HttpStatusCode.Unauthorized);
    }
}
