using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Tests;

/// <summary>POST /sessions/auth/token over HTTP: JSON in, the Express-shaped JSON out.</summary>
public sealed class SessionTokenEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> PostAsync(string? json) =>
        _client.PostAsync("/sessions/auth/token?options=account&options=sdk&", json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task EchoesTheCodeInTheWbAccount()
    {
        using var response = await PostAsync("""{"code":"abc","grant_type":"authorization_code"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("PostSessionsAuthToken", response.Headers.GetValues(Stub.EndpointHeader).Single());
        Assert.Equal(SessionTokenResponse.Build(System.Text.Json.Nodes.JsonNode.Parse("""{"code":"abc"}"""), new WbNetworkSettings()), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnEmptyBodyIsNoCode()
    {
        using var response = await PostAsync(null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("access_token", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIs400()
    {
        using var response = await PostAsync("{nope");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
